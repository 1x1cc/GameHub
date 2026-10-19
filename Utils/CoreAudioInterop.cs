using System;
using System.Runtime.InteropServices;

namespace GameHub.Utils
{
    /// <summary>
    /// Windows Core Audio (WASAPI) 互操作层。
    /// 提供枚举音频端点与设置默认设备的能力，对应 SoundSwitch 的核心实现方式。
    /// </summary>
    internal static class CoreAudioInterop
    {
        public enum EDataFlow
        {
            eRender = 0,
            eCapture = 1,
            eAll = 2
        }

        public enum ERole
        {
            eConsole = 0,
            eMultimedia = 1,
            eCommunications = 2
        }

        public const int DEVICE_STATE_ACTIVE = 0x00000001;
        public const int STGM_READ = 0x00000000;

        // CLSID for CPolicyConfigClient (Windows 7+)
        private static readonly Guid CLSID_POLICY_CONFIG = new Guid("870af99c-171d-4f9e-af0d-e63df40c2bc9");
        // CLSID for CPolicyConfigVistaClient (Windows Vista/8/10/11)
        private static readonly Guid CLSID_POLICY_CONFIG_VISTA = new Guid("294935ce-f637-4e7c-a41b-ab255460b862");
        // IID for IPolicyConfig
        private static readonly Guid IID_IPOLICYCONFIG = new Guid("f8679f50-850a-41cf-9c72-430f290290c8");

        // PKEY_Device_FriendlyName
        private static readonly PROPERTYKEY PKEY_Device_FriendlyName = new PROPERTYKEY
        {
            fmtid = new Guid("a45c254e-df1c-4efd-8020-67d146a850e0"),
            pid = 14
        };

        [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
        internal class MMDeviceEnumerator
        {
        }

        [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceEnumerator
        {
            [PreserveSig] int EnumAudioEndpoints(EDataFlow dataFlow, int dwStateMask, out IMMDeviceCollection ppDevices);
            [PreserveSig] int GetDefaultAudioEndpoint(EDataFlow dataFlow, ERole role, out IMMDevice ppEndpoint);
            [PreserveSig] int GetDevice([MarshalAs(UnmanagedType.LPWStr)] string pwstrId, out IMMDevice ppDevice);
            [PreserveSig] int RegisterEndpointNotificationCallback(IntPtr pClient);
            [PreserveSig] int UnregisterEndpointNotificationCallback(IntPtr pClient);
        }

        [ComImport, Guid("0BD7A1BE-7A1A-44DB-8397-CC5392387B5E"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDeviceCollection
        {
            [PreserveSig] int GetCount(out int pcDevices);
            [PreserveSig] int Item(int nDevice, out IMMDevice ppDevice);
        }

        [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IMMDevice
        {
            [PreserveSig] int Activate(ref Guid iid, int dwClsCtx, IntPtr pActivationParams,
                [MarshalAs(UnmanagedType.IUnknown)] out object ppInterface);
            [PreserveSig] int OpenPropertyStore(int stgmAccess, out IPropertyStore ppProperties);
            [PreserveSig] int GetId([MarshalAs(UnmanagedType.LPWStr)] out string ppstrId);
            [PreserveSig] int GetState(out int pdwState);
        }

        [ComImport, Guid("886d8eeb-8cf2-4446-8d02-cdba1dbdcf99"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPropertyStore
        {
            [PreserveSig] int GetCount(out int cProps);
            [PreserveSig] int GetAt(int iProp, out PROPERTYKEY pkey);
            [PreserveSig] int GetValue(ref PROPERTYKEY key, out PROPVARIANT pv);
            [PreserveSig] int SetValue(ref PROPERTYKEY key, ref PROPVARIANT propvar);
            [PreserveSig] int Commit();
        }

        [ComImport, Guid("f8679f50-850a-41cf-9c72-430f290290c8"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        internal interface IPolicyConfig
        {
            [PreserveSig] int GetMixFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr ppFormat);
            [PreserveSig] int GetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr ppFormat);
            [PreserveSig] int ResetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName);
            [PreserveSig] int SetDeviceFormat([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pEndpointFormat, IntPtr mixFormat);
            [PreserveSig] int GetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bDefault, IntPtr pmftDefaultPeriod, IntPtr pmftMinimumPeriod);
            [PreserveSig] int SetProcessingPeriod([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pmftPeriod);
            [PreserveSig] int GetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr pMode);
            [PreserveSig] int SetShareMode([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, IntPtr mode);
            [PreserveSig] int GetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
            [PreserveSig] int SetPropertyValue([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bFxStore, IntPtr key, IntPtr pv);
            [PreserveSig] int SetDefaultEndpoint([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, ERole role);
            [PreserveSig] int SetEndpointVisibility([MarshalAs(UnmanagedType.LPWStr)] string pszDeviceName, bool bVisible);
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct PROPERTYKEY
        {
            public Guid fmtid;
            public int pid;
        }

        [StructLayout(LayoutKind.Explicit)]
        internal struct PROPVARIANT
        {
            [FieldOffset(0)] public ushort vt;
            [FieldOffset(2)] public ushort wReserved1;
            [FieldOffset(4)] public ushort wReserved2;
            [FieldOffset(6)] public ushort wReserved3;
            [FieldOffset(8)] public IntPtr pointerValue;
        }

        [DllImport("ole32.dll")]
        internal static extern int PropVariantClear(ref PROPVARIANT pvar);

        [DllImport("ole32.dll", CharSet = CharSet.Unicode)]
        internal static extern int CoCreateInstance(
            ref Guid rclsid,
            IntPtr pUnkOuter,
            uint dwClsCtx,
            ref Guid riid,
            // 必须显式声明为 IUnknown，否则默认按 VARIANT 封送，成功返回时会抛
            // InvalidOleVariantTypeException
            [MarshalAs(UnmanagedType.IUnknown)] out object ppv);

        [DllImport("ole32.dll")]
        internal static extern uint CoInitializeEx(IntPtr pvReserved, uint dwCoInit);

        [DllImport("ole32.dll")]
        internal static extern void CoUninitialize();

        /// <summary>读取设备友好名称。</summary>
        public static string GetFriendlyName(IMMDevice device)
        {
            IPropertyStore? store = null;
            try
            {
                if (device.OpenPropertyStore(STGM_READ, out store) != 0 || store == null)
                    return "(未知设备)";

                var key = PKEY_Device_FriendlyName;
                if (store.GetValue(ref key, out var pv) != 0)
                    return "(未知设备)";

                try
                {
                    // VT_LPWSTR = 31
                    if (pv.vt == 31 && pv.pointerValue != IntPtr.Zero)
                        return Marshal.PtrToStringUni(pv.pointerValue) ?? "(未知设备)";
                    return "(未知设备)";
                }
                finally
                {
                    PropVariantClear(ref pv);
                }
            }
            catch
            {
                return "(未知设备)";
            }
            finally
            {
                if (store != null)
                    Marshal.ReleaseComObject(store);
            }
        }

        /// <summary>通过 CoCreateInstance 创建 IPolicyConfig COM 对象。</summary>
        private static IPolicyConfig? CreatePolicyConfig()
        {
            // 确保 COM 已初始化
            CoInitializeEx(IntPtr.Zero, 0);

            // 使用局部变量，因为 static readonly 不能用作 ref/out
            var clsid = CLSID_POLICY_CONFIG_VISTA;
            var iid = IID_IPOLICYCONFIG;

            object? raw = null;
            // CLSCTX_ALL(0x17)：必须包含 CLSCTX_INPROC_SERVER(0x1)。
            // 原先误用 0x0C（LOCAL_SERVER|INPROC_SERVER16），漏掉 INPROC_SERVER，
            // 而 PolicyConfig 是进程内组件，导致 CoCreateInstance 返回 REGDB_E_CLASSNOTREG(0x80040154)。
            const uint CLSCTX_ALL = 0x00000017;

            int hr = CoCreateInstance(ref clsid, IntPtr.Zero,
                CLSCTX_ALL,
                ref iid, out raw);

            if (hr != 0)
            {
                // 回退到 Windows 7 版本
                clsid = CLSID_POLICY_CONFIG;
                hr = CoCreateInstance(ref clsid, IntPtr.Zero,
                    CLSCTX_ALL,
                    ref iid, out raw);
            }

            if (hr != 0 || raw == null)
            {
                int failHr = hr != 0 ? hr : unchecked((int)0x80004005); // E_FAIL
                AppLog.Write($"CreatePolicyConfig 失败：hr=0x{failHr:X8}（无法创建 IPolicyConfig，切换默认设备将失败）");
                return null;
            }

            try
            {
                // 直接用 CoCreateInstance 返回的对象，它已经实现了 IPolicyConfig
                return (IPolicyConfig)raw;
            }
            catch
            {
                Marshal.ReleaseComObject(raw);
                return null;
            }
        }

        /// <summary>设置默认音频端点（多媒体/通信角色）。</summary>
        public static bool SetDefaultEndpoint(string deviceId, ERole role)
        {
            IPolicyConfig? policy = null;
            try
            {
                policy = CreatePolicyConfig();
                if (policy == null)
                    return false;

                int hr = policy.SetDefaultEndpoint(deviceId, role);
                if (hr != 0)
                    AppLog.Write($"SetDefaultEndpoint 失败：hr=0x{hr:X8}，role={role}，device={deviceId}");
                return hr == 0;
            }
            catch (Exception ex)
            {
                AppLog.Write($"SetDefaultEndpoint 异常：role={role}，device={deviceId}", ex);
                return false;
            }
            finally
            {
                if (policy != null)
                    Marshal.ReleaseComObject(policy);
            }
        }
    }
}
