using System;
using System.Runtime.InteropServices;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 系统输出的峰值电平表（0..1），给桌面上那个拾音块用。
    ///
    /// 用 CoreAudio 自带的 IAudioMeterInformation：它读的是渲染端点（耳机/扬声器）
    /// 正在播的实际音量，**只读**，不装虚拟声卡、不改任何系统声音设置
    /// （与 Dronesee 环回采集同一口径）。
    /// </summary>
    public sealed class AudioLevelMeter : IDisposable
    {
        private const int eRender = 0;
        private const int eConsole = 0;
        private const int CLSCTX_ALL = 23;

        private static readonly Guid IID_IAudioMeterInformation =
            new("C02216F6-8C67-4B5B-9D00-D008E73E0064");

        private static readonly Guid CLSID_MMDeviceEnumerator =
            new("BCDE0395-E52F-467C-8E3D-C4579291692E");

        private IMMDeviceEnumerator? _enumerator;
        private IMMDevice? _device;
        private IAudioMeterInformation? _meter;

        /// <summary>电平表是否可用。</summary>
        public bool Ready => _meter != null;

        /// <summary>失败原因（诊断用）。</summary>
        public string? LastError { get; private set; }

        /// <summary>打开默认输出端点的电平表；失败返回 false（不影响其它功能）。</summary>
        public bool Open()
        {
            if (_meter != null)
                return true;

            try
            {
                // 注意：同一个 CLSID 在本项目里还被 CoreAudioInterop / AudioSessionMute 声明过，
                // 再 new 一个 ComImport 包装类会触发 COM interop 类型缓存冲突（InvalidCastException），
                // 所以统一用 Type.GetTypeFromCLSID + Activator（和 Dronesee 侧同样的绕法）。
                var type = Type.GetTypeFromCLSID(CLSID_MMDeviceEnumerator);
                if (type == null)
                {
                    LastError = "MMDeviceEnumerator CLSID 未注册";
                    return false;
                }

                _enumerator = (IMMDeviceEnumerator)Activator.CreateInstance(type)!;

                int hr = _enumerator.GetDefaultAudioEndpoint(eRender, eConsole, out var device);
                if (hr != 0 || device == null)
                {
                    LastError = $"GetDefaultAudioEndpoint 失败 (0x{hr:X8})";
                    return false;
                }

                _device = device;

                var iid = IID_IAudioMeterInformation;
                hr = device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out object? iface);
                if (hr != 0 || iface == null)
                {
                    LastError = $"Activate(IAudioMeterInformation) 失败 (0x{hr:X8})";
                    return false;
                }

                _meter = (IAudioMeterInformation)iface;
                return true;
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                return false;
            }
        }

        /// <summary>当前峰值（0..1）；读不到就是 0。</summary>
        public float Peak()
        {
            try
            {
                if (_meter == null)
                    return 0f;

                int hr = _meter.GetPeakValue(out float value);
                if (hr != 0 || float.IsNaN(value) || float.IsInfinity(value))
                    return 0f;

                return Math.Max(0f, Math.Min(1f, value));
            }
            catch
            {
                return 0f;
            }
        }

        public void Dispose()
        {
            if (_meter != null) { try { Marshal.ReleaseComObject(_meter); } catch { } _meter = null; }
            if (_device != null) { try { Marshal.ReleaseComObject(_device); } catch { } _device = null; }
            if (_enumerator != null) { try { Marshal.ReleaseComObject(_enumerator); } catch { } _enumerator = null; }
        }

        // ---------------- COM 声明（只声明用得到的方法，顺序必须与 vtable 一致） ----------------

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDeviceEnumerator
        {
            int EnumAudioEndpoints(int dataFlow, int stateMask, out IntPtr devices);
            int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice endpoint);
            int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string id, out IMMDevice device);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IMMDevice
        {
            // vtable 第一位就是 Activate，后面几个用不到就不声明
            int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                         [MarshalAs(UnmanagedType.IUnknown)] out object? interfacePointer);
        }

        [ComImport, Guid("C02216F6-8C67-4B5B-9D00-D008E73E0064"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IAudioMeterInformation
        {
            int GetPeakValue(out float peak);
        }
    }
}
