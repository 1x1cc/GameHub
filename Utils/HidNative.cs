using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GameHub.Utils
{
    /// <summary>
    /// Win32 HID 底层操作的共享封装：接口枚举、句柄打开、属性/能力查询、
    /// feature report 读写、带超时的重叠(overlapped)输入/输出报告读写。
    ///
    /// 抽出来给各厂商电量读取器（雷蛇/前行者/ROG/WLMouse…）复用，
    /// 避免每个读取器各维护一份 P/Invoke。
    ///
    /// 并发模型（来自社区项目 2.4G-battery-viewer 踩坑记录，务必遵守）：
    ///   · 绝不要在同一句柄上「阻塞 ReadFile + 并发 WriteFile」——实测会直接杀掉进程；
    ///   · 正确做法是 FILE_FLAG_OVERLAPPED 句柄 + 事件超时 + CancelIoEx；
    ///   · 鼠标/键盘/触摸屏等系统独占的顶层集合只能以 access=0 打开
    ///     （能读属性和 feature report，不能发指令），打开失败要静默跳过。
    /// </summary>
    internal static class HidNative
    {
        internal const uint GenericRead = 0x80000000;
        internal const uint GenericWrite = 0x40000000;
        internal const uint FileShareRead = 0x00000001;
        internal const uint FileShareWrite = 0x00000002;
        internal const uint OpenExisting = 3;
        internal const uint FileFlagOverlapped = 0x40000000;
        internal const int ErrorIoPending = 997;

        [StructLayout(LayoutKind.Sequential)]
        internal struct HiddAttributes
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeOverlapped
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public int OffsetLow;
            public int OffsetHigh;
            public IntPtr EventHandle;
        }

        // ---------- DllImports ----------

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, int memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
            int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(IntPtr handle, out HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(IntPtr handle, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(IntPtr handle, byte[] reportBuffer, int reportBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr handle, byte[] buffer, uint numberOfBytesToWrite,
            out uint numberOfBytesWritten, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr handle, byte[] buffer, uint numberOfBytesToRead,
            out uint numberOfBytesRead, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr handle, ref NativeOverlapped overlapped,
            out uint numberOfBytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr handle, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEventW(IntPtr eventAttributes, bool manualReset, bool initialState, string? eventName);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern int WaitForSingleObject(IntPtr handle, int milliseconds);

        /// <summary>枚举本机所有 HID 设备接口路径（形如 \\?\hid#vid_xxxx...）。</summary>
        internal static List<string> EnumerateInterfacePaths()
        {
            var paths = new List<string>();
            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x02 | 0x10);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
                return paths;

            try
            {
                var data = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                for (int i = 0; ; i++)
                {
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data))
                        break;

                    IntPtr buffer = Marshal.AllocHGlobal(4096);
                    try
                    {
                        // 64 位下 cbSize 固定 8；路径字符串从偏移 4 开始
                        Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, 4096, out _, IntPtr.Zero))
                        {
                            string path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? string.Empty;
                            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                                paths.Add(path);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return paths;
        }

        /// <summary>打开一个 HID 接口。失败（系统独占集合等）返回 <see cref="IntPtr.Zero"/>，
        /// 调用方应静默跳过。</summary>
        internal static IntPtr Open(string path, uint desiredAccess, bool overlapped)
        {
            uint flags = overlapped ? FileFlagOverlapped : 0;
            IntPtr handle = CreateFileW(path, desiredAccess, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, flags, IntPtr.Zero);
            return handle == new IntPtr(-1) ? IntPtr.Zero : handle;
        }

        internal static void Close(IntPtr handle)
        {
            if (handle != IntPtr.Zero && handle != new IntPtr(-1))
                CloseHandle(handle);
        }

        internal static bool TryGetAttributes(IntPtr handle, out HiddAttributes attributes)
        {
            attributes = new HiddAttributes { Size = Marshal.SizeOf<HiddAttributes>() };
            return HidD_GetAttributes(handle, out attributes);
        }

        internal static bool TryGetCaps(IntPtr handle, out HidpCaps caps)
        {
            caps = default;
            if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
                return false;

            try
            {
                var c = new HidpCaps { Reserved = new ushort[17] };
                if (HidP_GetCaps(preparsed, ref c) != 0x00110000)   // HIDP_STATUS_SUCCESS
                    return false;
                caps = c;
                return true;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        internal static bool SetFeature(IntPtr handle, byte[] report)
            => HidD_SetFeature(handle, report, report.Length);

        internal static bool GetFeature(IntPtr handle, byte[] report)
            => HidD_GetFeature(handle, report, report.Length);

        /// <summary>重叠写一个输出报告；超时或失败返回 false。</summary>
        internal static bool WriteReport(IntPtr handle, byte[] report, int timeoutMs)
        {
            IntPtr evt = CreateEventW(IntPtr.Zero, true, false, null);
            if (evt == IntPtr.Zero)
                return false;

            var ov = new NativeOverlapped { EventHandle = evt };
            try
            {
                if (!WriteFile(handle, report, (uint)report.Length, out _, ref ov))
                {
                    if (Marshal.GetLastWin32Error() != ErrorIoPending)
                        return false;
                }

                if (WaitForSingleObject(evt, timeoutMs) != 0)
                {
                    CancelIoEx(handle, ref ov);
                    return false;
                }

                return GetOverlappedResult(handle, ref ov, out _, false);
            }
            finally
            {
                CloseHandle(evt);
            }
        }

        /// <summary>重叠读一个输入报告；返回读到的字节数，超时/失败返回 -1。</summary>
        internal static int ReadReport(IntPtr handle, byte[] buffer, int timeoutMs)
        {
            IntPtr evt = CreateEventW(IntPtr.Zero, true, false, null);
            if (evt == IntPtr.Zero)
                return -1;

            var ov = new NativeOverlapped { EventHandle = evt };
            try
            {
                if (!ReadFile(handle, buffer, (uint)buffer.Length, out _, ref ov))
                {
                    if (Marshal.GetLastWin32Error() != ErrorIoPending)
                        return -1;
                }

                if (WaitForSingleObject(evt, timeoutMs) != 0)
                {
                    CancelIoEx(handle, ref ov);
                    return -1;
                }

                return GetOverlappedResult(handle, ref ov, out uint n, false) ? (int)n : -1;
            }
            finally
            {
                CloseHandle(evt);
            }
        }

        /// <summary>从接口路径解析 USB 接口号（mi_NN）；解析不到返回 -1。</summary>
        internal static int InterfaceNumberOf(string path)
        {
            var m = System.Text.RegularExpressions.Regex.Match(path,
                @"mi_(\d+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? int.Parse(m.Groups[1].Value) : -1;
        }

        /// <summary>
        /// 从接口路径解析 HID 集合名（col01…，统一小写、不带下划线）；解析不到返回 null。
        ///
        /// ⚠ 实机踩坑：Windows 给 HID 接口的路径形如
        ///   \\?\hid#vid_0b05&amp;pid_1ace&amp;mi_02&amp;col01#…
        /// 集合名是 **col01**（没有下划线）。这里早期写成 @"col_(\d+)"，
        /// 于是 OMNI 接收器的 col01/col02/col03 一个都匹配不上：
        /// RogBatteryReader 里拿到 null 就 continue，整条 ROG OMNI 分支
        /// （配对列表 / 无线键盘电量）静默失效但日志一片干净 —— 表现为
        /// 「软件认不出 ROG 夜魔 X / AZOTH X 键盘」，有线直连却正常。
        /// 现在同时兼容 col01 与 col_01 两种写法，并保留十六进制后缀
        /// （雷柏设备用 col01..col0a，只取 \d+ 会把 col0a 误截成 col0）。
        /// </summary>
        internal static string? CollectionNameOf(string path)
        {
            var m = System.Text.RegularExpressions.Regex.Match(path,
                @"col_?([0-9a-f]+)", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            return m.Success ? "col" + m.Groups[1].Value.ToLowerInvariant() : null;
        }

        /// <summary>
        /// 同一物理 USB 实例（同一个接收器）的所有集合共享的路径片段，
        /// 例如 ...&mi_02&col01#8&amp;2a1b3c&amp;0&amp;0000#... 里的 "8&amp;2a1b3c&amp;0"。
        /// 用于区分插在两个 USB 口上的两台同型号接收器。
        /// </summary>
        internal static string InstanceKeyOf(string path)
        {
            var segments = path.Split('#');
            if (segments.Length < 3)
                return path.ToLowerInvariant();

            string instance = segments[2];
            int ampersand = instance.LastIndexOf('&');
            if (ampersand > 0)
                instance = instance[..ampersand];
            return instance.ToLowerInvariant();
        }

        // ---------- 设备自身文字信息（逆向时用于判断方案厂商） ----------

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetManufacturerString(IntPtr handle, byte[] buffer, int bufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetProductString(IntPtr handle, byte[] buffer, int bufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetSerialNumberString(IntPtr handle, byte[] buffer, int bufferLength);

        /// <summary>
        /// 报告描述符。由它可读出每个集合实际使用的 **Report ID**
        /// —— 发厂商命令前必须先知道对方认哪个 report id，
        /// 猜错设备会直接不理你。
        /// </summary>
        internal const int IoctlHidGetReportDescriptor = 0x000B0004;

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool DeviceIoControl(IntPtr handle, int ioctlCode, byte[]? inBuffer, int inSize,
            byte[]? outBuffer, int outSize, out int bytesReturned, IntPtr overlapped);

        private static string ReadHidString(Func<IntPtr, byte[], int, bool> getter, IntPtr handle)
        {
            var buffer = new byte[256];
            if (!getter(handle, buffer, buffer.Length))
                return string.Empty;

            string text = System.Text.Encoding.Unicode.GetString(buffer);
            int nul = text.IndexOf('\0');
            return (nul >= 0 ? text.Substring(0, nul) : text).Trim();
        }

        internal static string ManufacturerOf(IntPtr handle)
            => ReadHidString(HidD_GetManufacturerString, handle);

        internal static string ProductOf(IntPtr handle)
            => ReadHidString(HidD_GetProductString, handle);

        internal static string SerialOf(IntPtr handle)
            => ReadHidString(HidD_GetSerialNumberString, handle);

        internal static byte[]? ReportDescriptorOf(IntPtr handle, int maxBytes = 1024)
        {
            var buffer = new byte[maxBytes];
            if (!DeviceIoControl(handle, IoctlHidGetReportDescriptor, null, 0,
                    buffer, buffer.Length, out int returned, IntPtr.Zero))
                return null;

            if (returned <= 0)
                return null;

            var result = new byte[returned];
            Array.Copy(buffer, result, returned);
            return result;
        }
    }
}
