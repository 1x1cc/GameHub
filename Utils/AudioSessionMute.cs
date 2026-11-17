using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GameHub.Utils
{
    /// <summary>
    /// 蓝牙音频接收（A2DP Sink）会话的静音控制。
    ///
    /// 背景：手机播放的声音进入电脑后，会作为一个**独立的 WASAPI 音频会话**挂在某个渲染
    /// 端点上（本机实测：默认输出设备“扬声器 (Realtek(R) Audio)”上出现显示名为
    /// “线路 (2- &lt;手机名&gt; A2DP SNK)”的会话，宿主进程是蓝牙音频服务的 svchost）。
    /// 因此“暂停手机声音从电脑输出”可以只静音这一个会话：
    ///   · 电脑上其它声音完全不受影响；
    ///   · 手机与电脑的连接保持不变，恢复是瞬时的；
    ///   · 不需要（也无法安全地）释放 AudioPlaybackConnection。
    ///
    /// 本文件只新增接口，不改动 CoreAudioInterop（设备切换模块在用）。
    /// </summary>
    internal static class AudioSessionMute
    {
        // 会话识别：主判据是显示名里含 “A2DP”（协议名，不随系统语言变化）；
        // 备用判据是会话标识里含蓝牙音频服务使用的会话上下文 GUID。
        private const string A2dpToken = "A2DP";
        private const string BluetoothAudioServiceSessionGuid = "CC0101F5-2D8B-4662-9AE8-6560BE813712";

        private const int EDataFlow_Render = 0;
        private const int DEVICE_STATE_ACTIVE = 0x00000001;
        private const int CLSCTX_ALL = 0x00000017;

        /// <summary>最近一次判定的错误信息（诊断用，正常时为空）。</summary>
        public static string LastError { get; private set; } = string.Empty;

        /// <summary>最近一次判定的过程摘要（诊断用）：端点几个、命中进程的会话处于什么状态。</summary>
        public static string LastDetail { get; private set; } = string.Empty;

        /// <summary>
        /// MMDeviceEnumerator 的 CLSID。
        ///
        /// 必须走 CLSID + Activator，不能写 new MMDeviceEnumeratorComObject()：
        /// 同一 CLSID 在项目里被 CoreAudioInterop / WasapiAudioCapture / AudioLevelMeter
        /// 各用一份 ComImport 声明过，RCW 会退化成 System.__ComObject，转包装类就抛
        /// InvalidCastException —— 异常若被 catch 吞掉，外界只能看到“永远不在播放”。
        /// （AudioDeviceService 今天因同一原因修过，这里是同一个坑的第二、三处。）
        /// </summary>
        private static readonly Guid CLSID_MMDeviceEnumerator = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");

        /// <summary>IAudioSessionControl.GetState 的取值：1 = 正在输出声音。</summary>
        private const int AudioSessionState_Active = 1;

        // ---------- COM 定义 ----------

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        private class MMDeviceEnumeratorComObject
        {
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(int dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
            [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice ppEndpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int pcDevices);
            [PreserveSig] int Item(int nDevice, out IMMDevice ppDevice);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object? ppInterface);
            [PreserveSig] int OpenPropertyStore(int stgmAccess, out IntPtr ppProperties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            [PreserveSig] int GetState(out int pdwState);
        }

        [ComImport, Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionManager2
        {
            // IAudioSessionManager
            [PreserveSig] int GetAudioSessionControl(ref Guid AudioSessionGuid, int StreamFlags, out IntPtr SessionControl);
            [PreserveSig] int GetSimpleAudioVolume(ref Guid AudioSessionGuid, int StreamFlags, out IntPtr AudioVolume);
            // IAudioSessionManager2
            [PreserveSig] int GetSessionEnumerator(out IAudioSessionEnumerator SessionEnum);
            [PreserveSig] int RegisterSessionNotification(IntPtr SessionNotification);
            [PreserveSig] int UnregisterSessionNotification(IntPtr SessionNotification);
            [PreserveSig] int RegisterDuckNotification([MarshalAs(UnmanagedType.LPWStr)] string sessionID, IntPtr duckNotification);
            [PreserveSig] int UnregisterDuckNotification(IntPtr rtNotification);
        }

        [ComImport, Guid("E2F5BB11-0570-40CA-ACDD-3AA01277DEE8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionEnumerator
        {
            [PreserveSig] int GetCount(out int SessionCount);
            [PreserveSig] int GetSession(int SessionCount, out IAudioSessionControl Session);
        }

        [ComImport, Guid("F4B1A599-7266-4319-A8CA-E70ACB11E8CD"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl
        {
            [PreserveSig] int GetState(out int pRetVal);
            [PreserveSig] int GetDisplayName(out IntPtr pRetVal);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            [PreserveSig] int GetIconPath(out IntPtr pRetVal);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            [PreserveSig] int GetGroupingParam(out Guid pRetVal);
            [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
        }

        [ComImport, Guid("BFB7FF88-7239-4FC9-8FA2-07C950BE9C6D"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioSessionControl2
        {
            // IAudioSessionControl
            [PreserveSig] int GetState(out int pRetVal);
            [PreserveSig] int GetDisplayName(out IntPtr pRetVal);
            [PreserveSig] int SetDisplayName([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            [PreserveSig] int GetIconPath(out IntPtr pRetVal);
            [PreserveSig] int SetIconPath([MarshalAs(UnmanagedType.LPWStr)] string Value, ref Guid EventContext);
            [PreserveSig] int GetGroupingParam(out Guid pRetVal);
            [PreserveSig] int SetGroupingParam(ref Guid Override, ref Guid EventContext);
            [PreserveSig] int RegisterAudioSessionNotification(IntPtr NewNotifications);
            [PreserveSig] int UnregisterAudioSessionNotification(IntPtr NewNotifications);
            // IAudioSessionControl2
            [PreserveSig] int GetSessionIdentifier(out IntPtr pRetVal);
            [PreserveSig] int GetSessionInstanceIdentifier(out IntPtr pRetVal);
            [PreserveSig] int GetProcessId(out uint pRetVal);
            [PreserveSig] int IsSystemSoundsSession();
            [PreserveSig] int SetDuckingPreference(bool optOut);
        }

        [ComImport, Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface ISimpleAudioVolume
        {
            [PreserveSig] int SetMasterVolume(float fLevel, ref Guid EventContext);
            [PreserveSig] int GetMasterVolume(out float pfLevel);
            [PreserveSig] int SetMute([MarshalAs(UnmanagedType.Bool)] bool bMute, ref Guid EventContext);
            [PreserveSig] int GetMute([MarshalAs(UnmanagedType.Bool)] out bool pbMute);
        }

        private static readonly Guid IID_IAudioSessionManager2 =
            new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");

        [DllImport("ole32.dll")]
        private static extern uint CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        // ---------- 对外能力 ----------

        /// <summary>返回（蓝牙音频会话总数, 其中处于静音的数量）。</summary>
        public static (int total, int muted) GetState()
        {
            int total = 0;
            int muted = 0;

            ForEachSession(volume =>
            {
                total++;
                if (volume.GetMute(out bool isMuted) == 0 && isMuted)
                    muted++;
            });

            return (total, muted);
        }

        /// <summary>对全部蓝牙音频会话设置静音；返回实际修改成功的会话数。</summary>
        public static int SetMute(bool mute)
        {
            int changed = 0;
            var eventContext = Guid.Empty;

            ForEachSession(volume =>
            {
                if (volume.SetMute(mute, ref eventContext) == 0)
                    changed++;
            });

            return changed;
        }

        /// <summary>会话显示名（用于排查；无会话时返回空列表）。</summary>
        public static List<string> GetSessionNames()
        {
            var names = new List<string>();
            ForEachSession((volume, name) =>
            {
                names.Add(name);
            });
            return names;
        }

        /// <summary>
        /// 指定的进程当前是不是正在输出声音（存在属于它、且处于 Active 的渲染会话）。
        ///
        /// 用途：像网易云这种不注册媒体会话（SMTC）的播放器，
        /// 「到底在不在播」只能靠它自己的音频会话状态 —— 播放中 = Active。
        ///
        /// 注意：**不新声明 COM 接口**。同一个 CLSID 在项目里多一份 ComImport 声明，
        /// RCW 就会退化成 System.__ComObject，转换时抛 InvalidCastException
        /// （AudioDeviceService 就是这么坏掉的），所以这里复用本文件已有的声明。
        /// </summary>
        public static bool IsProcessPlaying(int processId)
        {
            bool playing = false;
            LastError = string.Empty;
            LastDetail = string.Empty;

            try { CoInitializeEx(IntPtr.Zero, 0); } catch { }

            IMMDeviceEnumerator? enumerator = null;
            IMMDeviceCollection? collection = null;

            try
            {
                Type? enumType = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
                if (enumType == null)
                {
                    LastError = "MMDeviceEnumerator CLSID 未注册";
                    return false;
                }

                enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumType)!;

                if (enumerator.EnumAudioEndpoints(EDataFlow_Render, DEVICE_STATE_ACTIVE, out collection) != 0 ||
                    collection == null)
                    return false;

                collection.GetCount(out int endpointCount);
                LastDetail = $"端点{endpointCount}";

                for (int i = 0; i < endpointCount && !playing; i++)
                {
                    IMMDevice? device = null;
                    object? managerObject = null;
                    IAudioSessionEnumerator? sessions = null;

                    try
                    {
                        if (collection.Item(i, out device) != 0 || device == null)
                            continue;

                        var iid = IID_IAudioSessionManager2;
                        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out managerObject) != 0 ||
                            managerObject is not IAudioSessionManager2 manager)
                            continue;

                        if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null)
                            continue;

                        sessions.GetCount(out int sessionCount);

                        for (int s = 0; s < sessionCount && !playing; s++)
                        {
                            IAudioSessionControl? control = null;
                            try
                            {
                                if (sessions.GetSession(s, out control) != 0 || control == null)
                                    continue;

                                if (control is not IAudioSessionControl2 control2)
                                    continue;

                                if (control2.GetProcessId(out uint pid) != 0 || (int)pid != processId)
                                    continue;

                                if (control2.GetState(out int state) != 0)
                                    continue;

                                // 命中目标进程的会话：记下状态（1=Active 正在出声）
                                LastDetail += $" | pid{processId} state={state}";

                                if (state == AudioSessionState_Active)
                                    playing = true;
                            }
                            catch
                            {
                                // 单个会话读失败不影响其它会话
                            }
                            finally
                            {
                                Release(ref control);
                            }
                        }
                    }
                    catch
                    {
                        // 单个端点失败不影响其它端点
                    }
                    finally
                    {
                        Release(ref sessions);
                        if (managerObject != null)
                        {
                            try { Marshal.ReleaseComObject(managerObject); } catch { }
                        }
                        Release(ref device);
                    }
                }
            }
            catch (Exception ex)
            {
                // 以前这里是空 catch：异常被静默吞掉，外界只能看到“永远未播放”。
                LastError = ex.GetType().Name + ": " + ex.Message;
            }
            finally
            {
                Release(ref collection);
                Release(ref enumerator);
            }

            LastDetail += $" | 结果={(playing ? "Active" : "静默")}";
            return playing;
        }

        // ---------- 内部实现 ----------

        private static void ForEachSession(Action<ISimpleAudioVolume> action)
        {
            ForEachSession((volume, _) => action(volume));
        }

        private static void ForEachSession(Action<ISimpleAudioVolume, string> action)
        {
            // 线程可能已按其它模式初始化过 COM（WPF UI 线程是 STA），
            // 返回值不可作为失败判据，真正失败会在后续调用中体现。
            try { CoInitializeEx(IntPtr.Zero, 0); } catch { }

            IMMDeviceEnumerator? enumerator = null;
            IMMDeviceCollection? collection = null;

            try
            {
                var enumType2 = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
                if (enumType2 == null)
                    return;

                // 同 IsProcessPlaying：不能用 new 包装类（同 CLSID 多处 ComImport → InvalidCastException）
                enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(enumType2)!;

                if (enumerator.EnumAudioEndpoints(EDataFlow_Render, DEVICE_STATE_ACTIVE, out collection) != 0 ||
                    collection == null)
                    return;

                collection.GetCount(out int endpointCount);
                for (int i = 0; i < endpointCount; i++)
                {
                    IMMDevice? device = null;
                    object? managerObject = null;
                    IAudioSessionEnumerator? sessions = null;

                    try
                    {
                        if (collection.Item(i, out device) != 0 || device == null)
                            continue;

                        var iid = IID_IAudioSessionManager2;
                        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out managerObject) != 0 ||
                            managerObject is not IAudioSessionManager2 manager)
                            continue;

                        if (manager.GetSessionEnumerator(out sessions) != 0 || sessions == null)
                            continue;

                        sessions.GetCount(out int sessionCount);
                        for (int s = 0; s < sessionCount; s++)
                        {
                            IAudioSessionControl? control = null;
                            try
                            {
                                if (sessions.GetSession(s, out control) != 0 || control == null)
                                    continue;

                                if (control is not IAudioSessionControl2 control2)
                                    continue;

                                if (!IsBluetoothAudioSession(control2, out string displayName))
                                    continue;

                                if (control is ISimpleAudioVolume volume)
                                    action(volume, displayName);
                            }
                            catch
                            {
                                // 单个会话失败不影响其它会话
                            }
                            finally
                            {
                                Release(ref control);
                            }
                        }
                    }
                    catch
                    {
                        // 单个端点失败不影响其它端点
                    }
                    finally
                    {
                        Release(ref sessions);
                        if (managerObject != null)
                        {
                            try { Marshal.ReleaseComObject(managerObject); } catch { }
                        }
                        Release(ref device);
                    }
                }
            }
            catch
            {
                // 蓝牙/音频服务不可用时静默返回
            }
            finally
            {
                Release(ref collection);
                Release(ref enumerator);
            }
        }

        private static bool IsBluetoothAudioSession(IAudioSessionControl2 control, out string displayName)
        {
            displayName = string.Empty;

            try
            {
                if (control.GetDisplayName(out IntPtr namePtr) == 0)
                    displayName = TakeString(namePtr);
            }
            catch
            {
                // 忽略
            }

            if (displayName.Contains(A2dpToken, StringComparison.OrdinalIgnoreCase))
                return true;

            try
            {
                if (control.GetSessionIdentifier(out IntPtr idPtr) == 0)
                {
                    string identifier = TakeString(idPtr);
                    if (identifier.Contains(BluetoothAudioServiceSessionGuid, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
                // 忽略
            }

            return false;
        }

        /// <summary>取出由 COM 分配的 LPWSTR 并释放它。</summary>
        private static string TakeString(IntPtr pointer)
        {
            if (pointer == IntPtr.Zero)
                return string.Empty;

            try
            {
                return Marshal.PtrToStringUni(pointer) ?? string.Empty;
            }
            finally
            {
                Marshal.FreeCoTaskMem(pointer);
            }
        }

        private static void Release<T>(ref T? comObject) where T : class
        {
            if (comObject == null)
                return;

            try { Marshal.ReleaseComObject(comObject); } catch { }
            comObject = null;
        }
    }
}
