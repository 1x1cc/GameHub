using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using GameHub.Utils;

namespace GameHub.Modules.Dronesee.Audio
{
    /// <summary>
    /// WASAPI 音频捕捉（思路参考开源项目 OBS Studio 的「音频输出捕获」）。
    ///
    /// 能力：
    ///   1) 系统输出环回捕捉（AUDCLNT_STREAMFLAGS_LOOPBACK）——录到的就是耳机/扬声器
    ///      正在播放的内容，包含游戏声、音乐声，与系统输出完全一致；
    ///   2) 麦克风捕捉（默认输入设备）；
    ///   3) 两路软件混音 → 48kHz / 立体声 / 16bit PCM。
    ///
    /// 关键约束（按需求）：
    ///   · 只读环回数据，**不安装任何虚拟声卡、不修改系统声音设置、不修改游戏设置**；
    ///   · 设备名自动识别（IPropertyStore → PKEY_Device_FriendlyName）；
    ///   · 识别不到 / 初始化失败 → 该路自动关闭，并通过 Errors 告知调用方去通知用户。
    /// </summary>
    public sealed class WasapiAudioCapture : IDisposable
    {
        /// <summary>统一输出格式：48kHz / 2ch / 16bit（FFmpeg 与 WAV 都直接吃）。</summary>
        public const int TargetSampleRate = 48000;
        public const int TargetChannels = 2;
        public const int TargetBitsPerSample = 16;
        public const int BytesPerFrame = TargetChannels * TargetBitsPerSample / 8;

        /// <summary>系统输出（耳机/扬声器/游戏声）是否成功开启。</summary>
        public bool SystemAudioActive { get; private set; }

        /// <summary>麦克风是否成功开启。</summary>
        public bool MicrophoneActive { get; private set; }

        public string SystemAudioDeviceName { get; private set; } = "(未检测到)";
        public string MicrophoneDeviceName { get; private set; } = "(未检测到)";

        public string? SystemAudioError { get; private set; }
        public string? MicrophoneError { get; private set; }

        /// <summary>被自动关闭的那一路的说明（用于通知用户）。</summary>
        public List<string> Errors { get; } = new();

        public bool HasAnySource => SystemAudioActive || MicrophoneActive;

        private EndpointCapture? _system;
        private EndpointCapture? _mic;
        private Thread? _pump;
        private volatile bool _running;
        private Stream? _sink;

        /// <summary>
        /// 打开设备（自动识别默认输出/输入设备名）。返回是否存在可用音源。
        /// </summary>
        public bool Open(bool enableSystemAudio, bool enableMicrophone)
        {
            Errors.Clear();

            if (enableSystemAudio)
            {
                try
                {
                    string name = Interop.GetDefaultDeviceName(Interop.eRender);
                    SystemAudioDeviceName = string.IsNullOrWhiteSpace(name) ? "(未检测到)" : name;

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Errors.Add("未检测到系统输出设备（耳机/扬声器），已自动关闭系统声音录制");
                    }
                    else
                    {
                        _system = EndpointCapture.TryCreate(Interop.eRender, loopback: true, out string? err);
                        if (_system != null)
                        {
                            SystemAudioActive = true;
                            AppLog.Write($"Dronesee：系统声音捕捉已开启 → {SystemAudioDeviceName}");
                        }
                        else
                        {
                            SystemAudioError = err;
                            Errors.Add($"系统声音录制已自动关闭：{err}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    SystemAudioError = ex.Message;
                    Errors.Add($"系统声音录制已自动关闭：{ex.Message}");
                }
            }

            if (enableMicrophone)
            {
                try
                {
                    string name = Interop.GetDefaultDeviceName(Interop.eCapture);
                    MicrophoneDeviceName = string.IsNullOrWhiteSpace(name) ? "(未检测到)" : name;

                    if (string.IsNullOrWhiteSpace(name))
                    {
                        Errors.Add("未检测到麦克风设备，已自动关闭麦克风录制");
                    }
                    else
                    {
                        _mic = EndpointCapture.TryCreate(Interop.eCapture, loopback: false, out string? err);
                        if (_mic != null)
                        {
                            MicrophoneActive = true;
                            AppLog.Write($"Dronesee：麦克风捕捉已开启 → {MicrophoneDeviceName}");
                        }
                        else
                        {
                            MicrophoneError = err;
                            Errors.Add($"麦克风录制已自动关闭：{err}");
                        }
                    }
                }
                catch (Exception ex)
                {
                    MicrophoneError = ex.Message;
                    Errors.Add($"麦克风录制已自动关闭：{ex.Message}");
                }
            }

            if (!HasAnySource)
            {
                DisposeCaptures();
                return false;
            }

            return true;
        }

        /// <summary>把混音后的 PCM 持续写入 <paramref name="sink"/>（通常是 WAV 文件流）。</summary>
        public void Start(Stream sink)
        {
            if (!HasAnySource) return;
            if (_running) return;

            _sink = sink ?? throw new ArgumentNullException(nameof(sink));

            _system?.Start();
            _mic?.Start();

            _running = true;
            _pump = new Thread(PumpLoop)
            {
                IsBackground = true,
                Name = "GameHub.Dronesee.AudioPump",
                Priority = ThreadPriority.AboveNormal
            };
            _pump.Start();
        }

        /// <summary>停止捕捉（不释放设备）。</summary>
        public void Stop()
        {
            _running = false;

            try { _pump?.Join(800); } catch { }
            _pump = null;

            _system?.Stop();
            _mic?.Stop();
            _sink = null;
        }

        /// <summary>PCM 每秒字节数（48kHz × 2ch × 16bit）。</summary>
        private static readonly int BytesPerSec = TargetSampleRate * BytesPerFrame;

        /// <summary>
        /// 混音主循环：按墙钟实时推进，每一路独立消费。
        ///
        /// 两个关键点：
        ///  1) 必须按实时速率持续输出 PCM：WASAPI 环回在系统没在播放声音时不产生数据，
        ///     若停止写入，FFmpeg 的 MPEG-TS 复用器会因音频流缺包而停摆（视频也一起停）。
        ///  2) 每路独立消费，互不阻塞（旧实现取两路长度的 Math.Min：系统声音一空闲，
        ///     麦克风数据就被卡住写不出去，积压后一次性涌出 → 回放里麦克风一顿一顿的）。
        /// </summary>
        private void PumpLoop()
        {
            var sysBuf = new List<byte>(1 << 16);
            var micBuf = new List<byte>(1 << 16);
            var clock = System.Diagnostics.Stopwatch.StartNew();
            long produced = 0;

            while (_running)
            {
                try
                {
                    // 采集异常绝不能连累实时推进：某一路抛错就当它这段时间静音
                    try { _system?.Drain(sysBuf); _mic?.Drain(micBuf); }
                    catch (Exception exDrain)
                    {
                        AppLog.Write("Dronesee：音频采集读取异常（该段按静音处理）", exDrain);
                    }

                    // 防积压：单路超过 250ms 就丢最旧的（设备暂停/异常时避免无限增长，
                    // 也避免恢复后“补播”一大段旧音频）
                    TrimBacklog(sysBuf);
                    TrimBacklog(micBuf);

                    // 按墙钟推进：本循环应写出多少（上限 50ms，避免调度间隙大时突发）
                    long expected = (long)(clock.Elapsed.TotalSeconds * BytesPerSec);
                    long toWrite = expected - produced;
                    long cap = BytesPerSec / 20;
                    if (toWrite > cap) toWrite = cap;
                    if (toWrite < BytesPerFrame) { Thread.Sleep(4); continue; }

                    int n = (int)(toWrite - toWrite % BytesPerFrame);
                    if (n <= 0) { Thread.Sleep(4); continue; }

                    WriteMixed(sysBuf, micBuf, n);
                    produced += n;
                }
                catch (Exception ex)
                {
                    AppLog.Write("Dronesee：音频混音循环异常", ex);
                }

                Thread.Sleep(4);
            }
        }

        /// <summary>单路最多积压 250ms。</summary>
        private static readonly int MaxBacklogBytes = BytesPerSec / 4;

        private static void TrimBacklog(List<byte> buf)
        {
            if (buf.Count > MaxBacklogBytes)
                buf.RemoveRange(0, buf.Count - MaxBacklogBytes);
        }

        private byte[]? _mixBuf;

        /// <summary>
        /// 写出 n 字节混音：每一路独立消费，不足的那一路按静音参与混音。
        /// 单路时直接透传（不衰减）；双路时各取 50% 相加避免削波。
        /// </summary>
        private void WriteMixed(List<byte> sys, List<byte> mic, int n)
        {
            if (_mixBuf == null || _mixBuf.Length < n) _mixBuf = new byte[n];
            var outBuf = _mixBuf;

            bool hasSys = _system != null;
            bool hasMic = _mic != null;
            bool both = hasSys && hasMic;

            int si = 0, mi = 0;
            for (int i = 0; i < n; i += 2)
            {
                short s = 0;
                if (si + 1 < sys.Count) { s = (short)(sys[si] | (sys[si + 1] << 8)); si += 2; }

                short m = 0;
                if (mi + 1 < mic.Count) { m = (short)(mic[mi] | (mic[mi + 1] << 8)); mi += 2; }

                int mixed = both ? (s / 2) + (m / 2) : s + m;
                if (mixed > short.MaxValue) mixed = short.MaxValue;
                else if (mixed < short.MinValue) mixed = short.MinValue;

                outBuf[i] = (byte)(mixed & 0xFF);
                outBuf[i + 1] = (byte)((mixed >> 8) & 0xFF);
            }

            if (si > 0) sys.RemoveRange(0, si);
            if (mi > 0) mic.RemoveRange(0, mi);

            WriteToSink(outBuf, n);
        }

        /// <summary>
        /// 写入 sink 并立即 Flush。
        /// 不 Flush 的话，PCM 会卡在 .NET 的缓冲流里，FFmpeg 长时间收不到音频。
        /// </summary>
        private void WriteToSink(byte[] data, int count)
        {
            try
            {
                _sink?.Write(data, 0, count);
                _sink?.Flush();
            }
            catch
            {
                // FFmpeg 已退出等：忽略，主流程会在别处发现进程已结束
            }
        }

        // Flush / MixInto 已由 WriteMixed 取代（每路独立消费 + 不足补静音）

        private void DisposeCaptures()
        {
            try { _system?.Dispose(); } catch { }
            try { _mic?.Dispose(); } catch { }
            _system = null;
            _mic = null;
            SystemAudioActive = false;
            MicrophoneActive = false;
        }

        public void Dispose()
        {
            Stop();
            DisposeCaptures();
        }

        // =====================================================================
        //  单个端点（环回或麦克风）
        // =====================================================================

        private sealed class EndpointCapture : IDisposable
        {
            private Interop.IAudioClient? _client;
            private Interop.IAudioCaptureClient? _capture;

            public static EndpointCapture? TryCreate(int dataFlow, bool loopback, out string? error)
            {
                error = null;
                var cap = new EndpointCapture();
                try
                {
                    if (!cap.Initialize(dataFlow, loopback, out error))
                    {
                        cap.Dispose();
                        return null;
                    }
                    return cap;
                }
                catch (Exception ex)
                {
                    error = ex.Message;
                    cap.Dispose();
                    return null;
                }
            }

            private bool Initialize(int dataFlow, bool loopback, out string? error)
            {
                error = null;

                var enumerator = Interop.CreateDeviceEnumerator();
                try
                {
                    int hr = enumerator.GetDefaultAudioEndpoint(dataFlow, Interop.eConsole, out Interop.IMMDevice device);
                    if (hr != 0 || device == null)
                    {
                        error = $"GetDefaultAudioEndpoint 失败 (0x{hr:X8})";
                        return false;
                    }

                    try
                    {
                        var iid = Interop.IID_IAudioClient;
                        hr = device.Activate(ref iid, Interop.CLSCTX_ALL, IntPtr.Zero, out object? obj);
                        if (hr != 0 || obj == null)
                        {
                            error = $"Activate(IAudioClient) 失败 (0x{hr:X8})";
                            return false;
                        }

                        _client = (Interop.IAudioClient)obj;

                        // 统一请求 48kHz/2ch/16bit；AUTOCONVERTPCM 让 WASAPI 自己转换，
                        // 这样环回与麦克风格式一致，混音时不需要重采样。
                        var fmt = new Interop.WAVEFORMATEX
                        {
                            wFormatTag = Interop.WAVE_FORMAT_PCM,
                            nChannels = TargetChannels,
                            nSamplesPerSec = TargetSampleRate,
                            wBitsPerSample = TargetBitsPerSample,
                            nBlockAlign = (ushort)BytesPerFrame,
                            nAvgBytesPerSec = TargetSampleRate * (uint)BytesPerFrame,
                            cbSize = 0
                        };

                        int flags = Interop.AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM
                                    | Interop.AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY;
                        if (loopback) flags |= Interop.AUDCLNT_STREAMFLAGS_LOOPBACK;

                        // 1 秒缓冲，避免游戏瞬间卡顿导致丢帧
                        hr = _client.Initialize(Interop.AUDCLNT_SHAREMODE_SHARED, flags,
                                                10_000_000L, 0, ref fmt, IntPtr.Zero);
                        if (hr != 0)
                        {
                            error = $"IAudioClient.Initialize 失败 (0x{hr:X8})";
                            return false;
                        }

                        var iidCap = Interop.IID_IAudioCaptureClient;
                        hr = _client.GetService(ref iidCap, out object? svc);
                        if (hr != 0 || svc == null)
                        {
                            error = $"GetService(IAudioCaptureClient) 失败 (0x{hr:X8})";
                            return false;
                        }

                        _capture = (Interop.IAudioCaptureClient)svc;
                        return true;
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(device);
                    }
                }
                finally
                {
                    Marshal.ReleaseComObject(enumerator);
                }
            }

            public void Start()
            {
                try { _client?.Start(); } catch { }
            }

            public void Stop()
            {
                try { _client?.Stop(); } catch { }
            }

            /// <summary>把当前可读的所有数据追加到 <paramref name="dest"/>。</summary>
            public void Drain(List<byte> dest)
            {
                if (_capture == null) return;

                while (true)
                {
                    int hr = _capture.GetNextPacketSize(out int packetFrames);
                    if (hr != 0 || packetFrames <= 0) return;

                    hr = _capture.GetBuffer(out IntPtr data, out int frames, out int flags, out _, out _);
                    if (hr != 0) return;

                    bool silent = (flags & Interop.AUDCLNT_BUFFERFLAGS_SILENT) != 0;
                    int bytes = frames * BytesPerFrame;

                    if (silent || data == IntPtr.Zero)
                    {
                        for (int i = 0; i < bytes; i++) dest.Add(0);
                    }
                    else
                    {
                        var tmp = new byte[bytes];
                        Marshal.Copy(data, tmp, 0, bytes);
                        dest.AddRange(tmp);
                    }

                    _capture.ReleaseBuffer(frames);
                }
            }

            public void Dispose()
            {
                Stop();

                if (_capture != null)
                {
                    try { Marshal.ReleaseComObject(_capture); } catch { }
                    _capture = null;
                }

                if (_client != null)
                {
                    try { Marshal.ReleaseComObject(_client); } catch { }
                    _client = null;
                }
            }
        }

        // =====================================================================
        //  WASAPI 互操作
        // =====================================================================

        private static class Interop
        {
            public const int eRender = 0;
            public const int eCapture = 1;
            public const int eConsole = 0;
            public const int CLSCTX_ALL = 23;
            public const int AUDCLNT_SHAREMODE_SHARED = 0;
            public const int AUDCLNT_STREAMFLAGS_LOOPBACK = 0x00020000;
            public const int AUDCLNT_STREAMFLAGS_AUTOCONVERTPCM = unchecked((int)0x80000000);
            public const int AUDCLNT_STREAMFLAGS_SRC_DEFAULT_QUALITY = 0x08000000;
            public const int AUDCLNT_BUFFERFLAGS_SILENT = 0x2;
            public const ushort WAVE_FORMAT_PCM = 1;

            public static readonly Guid IID_IAudioClient =
                new("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");

            public static readonly Guid IID_IAudioCaptureClient =
                new("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

            private static readonly PROPERTYKEY PKEY_Device_FriendlyName =
                new() { fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"), pid = 14 };

            /// <summary>取默认设备的友好名称；取不到返回空串（调用方据此自动关闭该路）。</summary>
            public static string GetDefaultDeviceName(int dataFlow)
            {
                try
                {
                    var enumerator = CreateDeviceEnumerator();
                    try
                    {
                        if (enumerator.GetDefaultAudioEndpoint(dataFlow, eConsole, out IMMDevice device) != 0
                            || device == null)
                            return string.Empty;

                        try
                        {
                            if (device.OpenPropertyStore(0 /* STGM_READ */, out IPropertyStore store) != 0
                                || store == null)
                                return string.Empty;

                            try
                            {
                                var key = PKEY_Device_FriendlyName;
                                if (store.GetValue(ref key, out PROPVARIANT value) != 0)
                                    return string.Empty;

                                try
                                {
                                    // VT_LPWSTR = 31
                                    if (value.vt == 31 && value.pointerValue != IntPtr.Zero)
                                        return Marshal.PtrToStringUni(value.pointerValue) ?? string.Empty;
                                    return string.Empty;
                                }
                                finally
                                {
                                    PropVariantClear(ref value);
                                }
                            }
                            finally
                            {
                                Marshal.ReleaseComObject(store);
                            }
                        }
                        finally
                        {
                            Marshal.ReleaseComObject(device);
                        }
                    }
                    finally
                    {
                        Marshal.ReleaseComObject(enumerator);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("Dronesee：读取音频设备名失败", ex);
                    return string.Empty;
                }
            }

            [DllImport("ole32.dll", ExactSpelling = true)]
            public static extern int PropVariantClear(ref PROPVARIANT pvar);

            // 注意：同一 CLSID(BCDE0395-...) 在本项目里也被 AudioSessionMute.cs / CoreAudioInterop.cs
            // 声明为不同名称的 ComImport 类。若这里再 new 一个包装类，会触发 .NET COM interop 的
            // 类型缓存冲突（InvalidCastException）。改用 Type.GetTypeFromCLSID + Activator 绕开。
            private static readonly Guid CLSID_MMDeviceEnumerator = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

            public static IMMDeviceEnumerator CreateDeviceEnumerator()
            {
                Type? type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
                if (type == null)
                    throw new InvalidOperationException("MMDeviceEnumerator CLSID 未注册");
                object obj = Activator.CreateInstance(type)!;
                return (IMMDeviceEnumerator)obj;
            }

            [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
             InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IMMDeviceEnumerator
            {
                int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
                int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
                int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
                int RegisterEndpointNotificationCallback(IntPtr client);
                int UnregisterEndpointNotificationCallback(IntPtr client);
            }

            [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
             InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IMMDevice
            {
                int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                             [MarshalAs(UnmanagedType.IUnknown)] out object? interfacePointer);
                int OpenPropertyStore(int access, out IPropertyStore properties);
                int GetId([MarshalAs(UnmanagedType.LPWStr)] out string id);
                int GetState(out int state);
            }

            [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
             InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IPropertyStore
            {
                int GetCount(out int count);
                int GetAt(int index, out PROPERTYKEY key);
                int GetValue(ref PROPERTYKEY key, out PROPVARIANT value);
                int SetValue(ref PROPERTYKEY key, ref PROPVARIANT value);
                int Commit();
            }

            [ComImport, Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2"),
             InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IAudioClient
            {
                int Initialize(int shareMode, int streamFlags, long hnsBufferDuration,
                               long hnsPeriodicity, ref WAVEFORMATEX format, IntPtr audioSessionGuid);
                int GetBufferSize(out int bufferFrameCount);
                int GetStreamLatency(out long latency);
                int GetCurrentPadding(out int currentPadding);
                int IsFormatSupported(int shareMode, ref WAVEFORMATEX format, out IntPtr closestMatch);
                int GetMixFormat(out IntPtr deviceFormat);
                int GetDevicePeriod(out long defaultPeriod, out long minimumPeriod);
                int Start();
                int Stop();
                int Reset();
                int SetEventHandle(IntPtr eventHandle);
                int GetService(ref Guid iid, [MarshalAs(UnmanagedType.IUnknown)] out object? service);
            }

            [ComImport, Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317"),
             InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
            public interface IAudioCaptureClient
            {
                int GetBuffer(out IntPtr data, out int numFramesRead, out int flags,
                              out long devicePosition, out long qpcPosition);
                int ReleaseBuffer(int numFramesRead);
                int GetNextPacketSize(out int numFramesInNextPacket);
            }

            [StructLayout(LayoutKind.Sequential, Pack = 1)]
            public struct WAVEFORMATEX
            {
                public ushort wFormatTag;
                public ushort nChannels;
                public uint nSamplesPerSec;
                public uint nAvgBytesPerSec;
                public ushort nBlockAlign;
                public ushort wBitsPerSample;
                public ushort cbSize;
            }

            [StructLayout(LayoutKind.Sequential)]
            public struct PROPERTYKEY
            {
                public Guid fmtid;
                public int pid;
            }

            [StructLayout(LayoutKind.Explicit)]
            public struct PROPVARIANT
            {
                [FieldOffset(0)] public ushort vt;
                [FieldOffset(2)] public ushort wReserved1;
                [FieldOffset(4)] public ushort wReserved2;
                [FieldOffset(6)] public ushort wReserved3;
                [FieldOffset(8)] public IntPtr pointerValue;
                [FieldOffset(8)] public long longValue;
            }
        }
    }
}
