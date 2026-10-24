using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;

namespace RapooCli
{
    /// <summary>
    /// 雷柏（Rapoo, VID 0x24AE）2.4G 外设电量独立读取 CLI。
    ///
    /// 为什么独立成进程：Windows HID 输入报告只会投递给「最先打开该集合句柄」的一方，
    /// GameHub 主进程内其它 HID 读取器会抢占这些 vendor 集合，导致进程内永远读不到
    /// （写触发帧成功、读却持续超时）。独立进程则完全没有竞争。
    ///
    /// 协议（来自商店版 EasyBluetooth 3.5.8 反编译 + 本机实机验证 2026-10-24）：
    ///   ① 枚举 VID 0x24AE 且 usage page = 0xFF00 的 vendor 集合；
    ///   ② 打开全部候选集合句柄；
    ///   ③ 向「输出报告长度最大」的集合写触发帧 [6, A5, A3, 00, 00, 00, 00, 00, 00]；
    ///   ④ 按输入报告长度升序逐个读，解析：
    ///        len >= 19 && buf[0] == 0x07 -> 电量 = buf[8]
    ///        len >=  7 && buf[0] == 0xBB -> 电量 = buf[6]
    ///      只接受 1..100。
    ///
    /// 输出：stdout 一行 JSON 数组，例如
    ///   [{"pid":5218,"name":"雷柏 2.4G 设备 (PID 0x1462)","channel":"col09","percent":82}]
    /// 退出码 0 = 成功（可能无设备）；1 = 异常。
    /// </summary>
    internal static class Program
    {
        private const ushort RapooVendorId = 0x24AE;
        private const int VendorUsagePage = 0xFF00;
        private const int MaxInputReportLength = 128;   // 超过视为 bulk 噪声（实测 mi_02 是 513 字节全 0）
        private const int ReadSliceMs = 200;             // 单次读切片（与探针 V3 一致，短切片反复读比一次性长等更稳）
        private const int TotalWindowMs = 2000;          // 单条集合读总窗口

        private static int Main(string[] args)
        {
            Console.OutputEncoding = Encoding.UTF8;
            try
            {
                var devices = ReadRapooDevices();
                Console.WriteLine(JsonSerializer.Serialize(devices));
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("RAPOO_CLI_ERROR: " + ex.Message);
                return 1;
            }
        }

        private sealed class RapooDevice
        {
            public ushort pid { get; set; }
            public string name { get; set; } = "";
            public string channel { get; set; } = "";
            public int? percent { get; set; }
        }

        private static List<RapooDevice> ReadRapooDevices()
        {
            var result = new List<RapooDevice>();

            // ① 发现：枚举 vendor 集合候选（临时句柄取 caps 后关闭）
            var candidates = new List<(string path, ushort pid, int inLen, int outLen)>();
            foreach (string path in EnumerateHidPaths())
            {
                if (path.IndexOf("vid_24ae", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                IntPtr h = Open(path);
                if (h == IntPtr.Zero || h == new IntPtr(-1))
                    continue;
                try
                {
                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(h, ref attr))
                        continue;
                    if (attr.VendorID != RapooVendorId)
                        continue;
                    if (!TryGetCaps(h, out ushort usagePage, out _, out int inLen, out int outLen))
                        continue;
                    if (usagePage != VendorUsagePage)
                        continue;
                    if (inLen < 7 || inLen > MaxInputReportLength)
                        continue;

                    candidates.Add((path, attr.ProductID, inLen, outLen));
                }
                finally { Close(h); }
            }

            // ② 按物理实例分组（同一接收器的多个集合共享实例号）
            foreach (var group in candidates.GroupBy(c => InstanceKey(c.path)))
            {
                var dev = ProbeInstance(group.ToList());
                if (dev != null)
                    result.Add(dev);
            }

            return result;
        }

        private static RapooDevice? ProbeInstance(
            List<(string path, ushort pid, int inLen, int outLen)> group)
        {
            var opened = new List<(string path, ushort pid, int inLen, int outLen, string channel, IntPtr handle)>();

            try
            {
                // 先全部打开（关键：输入报告只会进给「到达时已打开」的句柄）
                foreach (var c in group)
                {
                    IntPtr h = Open(c.path);
                    if (h == IntPtr.Zero || h == new IntPtr(-1))
                        continue;
                    opened.Add((c.path, c.pid, c.inLen, c.outLen, ChannelName(c.path), h));
                }

                if (opened.Count == 0)
                    return null;

                // 【关键】写触发帧前，对写句柄取一次 caps（与探针 V3 一致）。
                // 这个同步 IO 会“预热/同步”句柄：实测少这一步，写成功却读不到应答。
                foreach (var o in opened)
                    TryGetCaps(o.handle, out _, out _, out _, out _);

                // 写触发帧：向输出报告长度最大的集合（本机是 33 字节的 col05）
                foreach (var o in opened.Where(x => x.outLen >= 9).OrderByDescending(x => x.outLen))
                {
                    var packet = new byte[o.outLen];
                    packet[0] = 6;
                    packet[1] = 0xA5;
                    packet[2] = 0xA3;
                    if (Write(o.handle, packet, 300))
                        break;   // 一次就够，多发会干扰应答
                }

                // 读：输入报告长度升序（本机电量在 19 字节的 col09）
                foreach (var o in opened.OrderBy(x => x.inLen))
                {
                    var buffer = new byte[Math.Max(o.inLen, 64)];
                    long deadline = Environment.TickCount64 + TotalWindowMs;

                    while (Environment.TickCount64 < deadline)
                    {
                        int left = (int)Math.Min(ReadSliceMs, Math.Max(1, deadline - Environment.TickCount64));
                        int n = Read(o.handle, buffer, left);
                        if (n <= 0)
                            continue;

                        if (!TryParseBattery(buffer, n, out int percent))
                            continue;

                        return new RapooDevice
                        {
                            pid = o.pid,
                            name = $"雷柏 2.4G 设备 (PID 0x{o.pid:X4})",
                            channel = o.channel,
                            percent = percent
                        };
                    }
                }

                // 设备在，但本轮没收到电量帧（休眠/无线丢包）
                var first = group.OrderBy(x => x.inLen).First();
                return new RapooDevice
                {
                    pid = first.pid,
                    name = $"雷柏 2.4G 设备 (PID 0x{first.pid:X4})",
                    channel = ChannelName(first.path),
                    percent = null
                };
            }
            finally
            {
                foreach (var o in opened)
                    Close(o.handle);
            }
        }

        private static bool TryParseBattery(byte[] buffer, int length, out int percent)
        {
            percent = -1;
            if (buffer == null || length <= 0)
                return false;
            if (length >= 19 && buffer[0] == 0x07)
                percent = buffer[8];
            else if (length >= 7 && buffer[0] == 0xBB)
                percent = buffer[6];
            else
                return false;
            return percent > 0 && percent <= 100;
        }

        private static string ChannelName(string path)
        {
            var m = Regex.Match(path, @"col_?([0-9a-f]+)", RegexOptions.IgnoreCase);
            return m.Success ? "col" + m.Groups[1].Value.ToLowerInvariant() : "?";
        }

        private static string InstanceKey(string path)
        {
            var m = Regex.Match(path, @"#([0-9a-fA-F]+&[0-9a-fA-F]+&0)");
            return m.Success ? m.Groups[1].Value : path;
        }

        // ---------- HID 枚举与原生互操作 ----------

        private static Guid HidInterfaceGuid = new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");

        private static List<string> EnumerateHidPaths()
        {
            var result = new List<string>();
            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
                return result;

            try
            {
                var did = new SP_DEVICE_INTERFACE_DATA { cbSize = Marshal.SizeOf<SP_DEVICE_INTERFACE_DATA>() };
                for (int i = 0; ; i++)
                {
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, (uint)i, ref did))
                        break;

                    IntPtr buffer = Marshal.AllocHGlobal(4096);
                    try
                    {
                        Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref did, buffer, 4096, out _, IntPtr.Zero))
                        {
                            string p = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? string.Empty;
                            if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
                                result.Add(p);
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }

            return result;
        }

        private static IntPtr Open(string path)
        {
            return CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        }

        private static void Close(IntPtr h) => CloseHandle(h);

        private static bool TryGetCaps(IntPtr handle, out ushort usagePage, out ushort usage, out int inLen, out int outLen)
        {
            usagePage = usage = 0;
            inLen = outLen = 0;
            if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
                return false;
            try
            {
                var caps = new HIDP_CAPS { Reserved = new ushort[17] };
                if (HidP_GetCaps(preparsed, ref caps) != 0x00110000)  // HIDP_STATUS_SUCCESS
                    return false;
                usagePage = caps.UsagePage;
                usage = caps.Usage;
                inLen = caps.InputReportByteLength;
                outLen = caps.OutputReportByteLength;
                return true;
            }
            finally { HidD_FreePreparsedData(preparsed); }
        }

        private static bool Write(IntPtr h, byte[] data, int timeoutMs)
        {
            IntPtr evt = CreateEventW(IntPtr.Zero, true, false, null);
            if (evt == IntPtr.Zero) return false;
            try
            {
                var ov = new NativeOverlapped { EventHandle = evt };
                if (!WriteFile(h, data, (uint)data.Length, out _, ref ov))
                {
                    if (Marshal.GetLastWin32Error() != 997) return false;  // 997 = ERROR_IO_PENDING
                }
                if (WaitForSingleObject(evt, timeoutMs) != 0)
                {
                    CancelIoEx(h, ref ov);
                    return false;
                }
                return GetOverlappedResult(h, ref ov, out _, false);
            }
            finally { CloseHandle(evt); }
        }

        private static int Read(IntPtr h, byte[] buffer, int timeoutMs)
        {
            IntPtr evt = CreateEventW(IntPtr.Zero, true, false, null);
            if (evt == IntPtr.Zero) return -1;
            try
            {
                var ov = new NativeOverlapped { EventHandle = evt };
                if (!ReadFile(h, buffer, (uint)buffer.Length, out _, ref ov))
                {
                    if (Marshal.GetLastWin32Error() != 997) return -1;
                }
                if (WaitForSingleObject(evt, timeoutMs) != 0)
                {
                    CancelIoEx(h, ref ov);
                    return 0;
                }
                return GetOverlappedResult(h, ref ov, out uint n, false) ? (int)n : -1;
            }
            finally { CloseHandle(evt); }
        }

        // ---------- 常量 / 结构体 / P/Invoke ----------

        private const uint GENERIC_READ = 0x80000000;
        private const uint GENERIC_WRITE = 0x40000000;
        private const uint FILE_SHARE_READ = 0x1;
        private const uint FILE_SHARE_WRITE = 0x2;
        private const uint OPEN_EXISTING = 3;
        private const uint FILE_FLAG_OVERLAPPED = 0x40000000;
        private const uint DIGCF_PRESENT = 0x2;
        private const uint DIGCF_DEVICEINTERFACE = 0x10;

        [StructLayout(LayoutKind.Sequential)]
        private struct SP_DEVICE_INTERFACE_DATA
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDD_ATTRIBUTES
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HIDP_CAPS
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)] public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeOverlapped
        {
            public IntPtr Internal;
            public IntPtr InternalHigh;
            public int Offset;
            public int OffsetHigh;
            public IntPtr EventHandle;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, uint flags);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode, IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool WriteFile(IntPtr handle, byte[] buffer, uint numberOfBytesToWrite, out uint numberOfBytesWritten, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr handle, byte[] buffer, uint numberOfBytesToRead, out uint numberOfBytesRead, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr handle, ref NativeOverlapped overlapped, out uint numberOfBytesTransferred, bool wait);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIoEx(IntPtr handle, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr CreateEventW(IntPtr attributes, bool manualReset, bool initialState, string name);

        [DllImport("kernel32.dll")]
        private static extern int WaitForSingleObject(IntPtr handle, int milliseconds);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetAttributes(IntPtr handle, ref HIDD_ATTRIBUTES attributes);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);
    }
}
