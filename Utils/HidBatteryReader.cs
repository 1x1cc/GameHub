using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace GameHub.Utils
{
    /// <summary>一个 HID 接口的电量读数。</summary>
    public sealed class HidBatteryReading
    {
        /// <summary>稳定标识（HID 接口路径）。</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>设备名（优先 HID 产品字符串，其次厂商字符串）。</summary>
        public string Name { get; init; } = string.Empty;

        public ushort VendorId { get; init; }
        public ushort ProductId { get; init; }

        /// <summary>电量百分比；null = 声明支持但这次没读到。</summary>
        public int? Percent { get; init; }

        /// <summary>数据来自哪种报告（Feature / Input）。</summary>
        public string ReportKind { get; init; } = string.Empty;

        /// <summary>读不到时的原因（读到则为空）。</summary>
        public string Note { get; init; } = string.Empty;
    }

    /// <summary>
    /// HID 标准电量通道（跨厂商通用、零型号适配）。
    ///
    /// HID 规范定义了一个通用上报项：
    ///   · 用法页 Generic Device Controls = 0x06
    ///   · 用法     Battery Strength       = 0x20
    /// 任何厂商只要按规范上报电量，同一套代码就能读出来 —— 不看品牌、不建型号表、
    /// 未来新设备也自动支持。这是「所有无线设备都能显示」里能通用覆盖的那一半。
    ///
    /// 实现上刻意避开了一个坑：**不解析 HIDP_VALUE_CAPS 结构体**。
    /// 那个结构体尾部是 union、布尔量是 1 字节，手工排布极易错位，一旦步长算错
    /// 后续条目全成垃圾，会得出「没有电量项」的假结论。
    /// 这里改用 <c>HidP_GetSpecificValueCaps</c> 按用法页+用法直接过滤，
    /// 只需要「匹配条数」这一个信息，结构体布局就完全无关了。
    ///
    /// 局限（必须说清）：只对**按 HID 规范上报**的设备有效。
    /// 完全私有协议的接收器（如雷蛇）压根不上报这一项 —— 那不是本通道的缺陷，
    /// 而是设备没按标准来，只能靠厂商插件。
    /// </summary>
    public static class HidBatteryReader
    {
        private const ushort UsagePageGenericDeviceControls = 0x06;
        private const ushort UsageBatteryStrength = 0x20;

        /// <summary>阳性对照用：鼠标 X 轴（Generic Desktop / X）。</summary>
        public const ushort UsagePageGenericDesktop = 0x01;
        public const ushort UsageX = 0x30;

        private const int HidpInput = 0;
        private const int HidpFeature = 2;

        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const uint GenericRead = 0x80000000;
        private const int DigcfPresent = 0x02;
        private const int DigcfDeviceInterface = 0x10;
        private const int HidpStatusSuccess = 0x00110000;
        private const int ErrorIoPending = 997;

        private const int InputReadTimeoutMs = 250;

        /// <summary>
        /// 是否读「输入报告」取电量；默认开启。
        /// 多数设备的电量项就在输入报告里，关掉会漏一大半。
        /// 只对**已声明电量项**的接口尝试（很罕见），且有 250ms 超时，
        /// 不会拖慢普遍扫描；读输入报告使用独立读句柄，不会从系统抢走输入。
        /// </summary>
        public static bool AllowInputReportRead { get; set; } = true;

        private const int ResultCacheMs = 2500;
        private static readonly object Gate = new();
        private static List<HidBatteryReading>? _lastResult;
        private static DateTime _lastResultAtUtc = DateTime.MinValue;

        /// <summary>读取所有「按 HID 规范上报电量」的设备。同轮扫描结果复用。</summary>
        public static List<HidBatteryReading> ReadAll()
        {
            lock (Gate)
            {
                if (_lastResult != null &&
                    (DateTime.UtcNow - _lastResultAtUtc).TotalMilliseconds < ResultCacheMs)
                {
                    return new List<HidBatteryReading>(_lastResult);
                }

                var result = QueryAll();
                _lastResult = result;
                _lastResultAtUtc = DateTime.UtcNow;
                return new List<HidBatteryReading>(result);
            }
        }

        private static List<HidBatteryReading> QueryAll()
        {
            var result = new List<HidBatteryReading>();

            foreach (string path in EnumeratePaths())
            {
                try
                {
                    var reading = QueryOne(path);
                    if (reading != null)
                        result.Add(reading);
                }
                catch (Exception ex)
                {
                    AppLog.Write($"HID 标准电量：读取接口失败 {path}", ex);
                }
            }

            return result;
        }

        private static HidBatteryReading? QueryOne(string path)
        {
            IntPtr handle = Open(path, 0);
            if (handle == new IntPtr(-1))
                return null;

            try
            {
                if (!HidD_GetAttributes(handle, out var attributes))
                    return null;

                if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
                    return null;

                try
                {
                    string name = ReadDeviceName(handle);
                    if (name.Length == 0)
                        name = $"HID {attributes.VendorID:X4}:{attributes.ProductID:X4}";

                    bool hasFeature =
                        CountUsages(preparsed, HidpFeature, UsagePageGenericDeviceControls, UsageBatteryStrength) > 0;

                    if (hasFeature)
                    {
                        int? percent = ReadFeatureBattery(handle, preparsed);
                        if (percent.HasValue)
                        {
                            return new HidBatteryReading
                            {
                                Id = path,
                                Name = name,
                                VendorId = attributes.VendorID,
                                ProductId = attributes.ProductID,
                                Percent = percent,
                                ReportKind = "Feature"
                            };
                        }

                        // 特征报告声明了电量但这次没取到，仍列入（设备可能休眠）
                        return new HidBatteryReading
                        {
                            Id = path,
                            Name = name,
                            VendorId = attributes.VendorID,
                            ProductId = attributes.ProductID,
                            Percent = null,
                            ReportKind = "Feature",
                            Note = "特征报告有电量项但未取到值（设备可能休眠）"
                        };
                    }

                    bool hasInput =
                        CountUsages(preparsed, HidpInput, UsagePageGenericDeviceControls, UsageBatteryStrength) > 0;
                    if (!hasInput)
                        return null;   // 不按标准上报，本通道不管

                    int? inputPercent = AllowInputReportRead
                        ? ReadInputBattery(path, preparsed)
                        : null;

                    return new HidBatteryReading
                    {
                        Id = path,
                        Name = name,
                        VendorId = attributes.VendorID,
                        ProductId = attributes.ProductID,
                        Percent = inputPercent,
                        ReportKind = "Input",
                        Note = inputPercent.HasValue
                            ? string.Empty
                            : (AllowInputReportRead ? "输入报告未取到值（设备可能休眠）" : "需读输入报告（未开启）")
                    };
                }
                finally
                {
                    HidD_FreePreparsedData(preparsed);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        // ---------- 用法探测（不解析结构体） ----------

        /// <summary>
        /// 某接口的指定报告里，符合「用法页+用法」的值项有几条。
        /// 用 HidP_GetSpecificValueCaps 过滤，只看条数，因此与结构体布局无关。
        /// </summary>
        private static int CountUsages(IntPtr preparsed, int reportType, ushort usagePage, ushort usage)
        {
            // 缓冲给足；返回值只用条数
            var buffer = new byte[128 * 64];
            ushort length = 0;

            int status = HidP_GetSpecificValueCaps(reportType, usagePage, 0, usage,
                buffer, ref length, preparsed);

            if (status == HidpStatusSuccess)
                return length;

            // 缓冲不足时 length 会被填成所需条数
            if (length > 0)
                return length;

            return 0;
        }

        /// <summary>诊断/验证用：探测任意用法的条数（阳性对照靠它）。</summary>
        public static int ProbeUsage(string path, ushort usagePage, ushort usage)
        {
            IntPtr handle = Open(path, 0);
            if (handle == new IntPtr(-1))
                return -1;

            try
            {
                if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
                    return -1;

                try
                {
                    int feature = CountUsages(preparsed, HidpFeature, usagePage, usage);
                    int input = CountUsages(preparsed, HidpInput, usagePage, usage);
                    return feature + input;
                }
                finally
                {
                    HidD_FreePreparsedData(preparsed);
                }
            }
            finally
            {
                CloseHandle(handle);
            }
        }

        // ---------- 取值 ----------

        private static int? ReadFeatureBattery(IntPtr handle, IntPtr preparsed)
        {
            var caps = new HidpCaps { Reserved = new ushort[17] };
            if (HidP_GetCaps(preparsed, ref caps) != HidpStatusSuccess)
                return null;
            if (caps.FeatureReportByteLength <= 0)
                return null;

            var report = new byte[caps.FeatureReportByteLength];
            report[0] = 0x00;
            if (!HidD_GetFeature(handle, report, report.Length))
                return null;

            return Normalize(ExtractBatteryValue(preparsed, HidpFeature, report));
        }

        private static int? ReadInputBattery(string path, IntPtr preparsed)
        {
            var caps = new HidpCaps { Reserved = new ushort[17] };
            if (HidP_GetCaps(preparsed, ref caps) != HidpStatusSuccess)
                return null;
            if (caps.InputReportByteLength <= 0)
                return null;

            IntPtr handle = Open(path, GenericRead);
            if (handle == new IntPtr(-1))
                return null;

            IntPtr evt = IntPtr.Zero;
            try
            {
                HidD_FlushQueue(handle);

                evt = CreateEventW(IntPtr.Zero, true, false, null);
                if (evt == IntPtr.Zero)
                    return null;

                var overlapped = new NativeOverlapped { EventHandle = evt };
                var report = new byte[caps.InputReportByteLength];
                int read;

                if (!ReadFile(handle, report, report.Length, out read, ref overlapped))
                {
                    if (Marshal.GetLastWin32Error() != ErrorIoPending)
                        return null;

                    if (WaitForSingleObject(evt, InputReadTimeoutMs) != 0)
                    {
                        CancelIo(handle);
                        return null;
                    }

                    if (!GetOverlappedResult(handle, ref overlapped, out read, false))
                        return null;
                }

                if (read <= 0)
                    return null;

                return Normalize(ExtractBatteryValue(preparsed, HidpInput, report));
            }
            finally
            {
                if (evt != IntPtr.Zero)
                    CloseHandle(evt);
                CloseHandle(handle);
            }
        }

        private static uint? ExtractBatteryValue(IntPtr preparsed, int reportType, byte[] report)
        {
            int status = HidP_GetUsageValue(reportType, UsagePageGenericDeviceControls, 0,
                UsageBatteryStrength, out uint value, preparsed, report, (uint)report.Length);

            return status == HidpStatusSuccess ? value : null;
        }

        /// <summary>
        /// 归一化到 0–100。
        /// 不用 HIDP_VALUE_CAPS 里的量程（那要解析结构体），改用约定：
        /// 电量强度高于 100 说明设备用的是 0–255 量程，否则就是直接的百分比。
        /// 这两种覆盖了绝大多数量产设备。
        /// </summary>
        private static int? Normalize(uint? raw)
        {
            if (!raw.HasValue)
                return null;

            double value = raw.Value;
            int percent = value > 100
                ? (int)Math.Round(value * 100.0 / 255.0)
                : (int)Math.Round(value);

            if (percent < 0)
                percent = 0;
            if (percent > 100)
                percent = 100;
            return percent;
        }

        // ---------- 诊断 ----------

        /// <summary>所有 HID 接口路径（诊断用）。</summary>
        public static List<string> EnumeratePaths() => EnumerateHidInterfacePaths();

        /// <summary>列出所有 HID 接口及其顶层用法、报告长度、是否声明标准电量。</summary>
        public static List<string> DescribeAll()
        {
            var lines = new List<string>();

            foreach (string path in EnumerateHidInterfacePaths())
            {
                IntPtr handle = Open(path, 0);
                if (handle == new IntPtr(-1))
                    continue;

                try
                {
                    if (!HidD_GetAttributes(handle, out var attributes))
                        continue;

                    if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
                        continue;

                    try
                    {
                        var caps = new HidpCaps { Reserved = new ushort[17] };
                        if (HidP_GetCaps(preparsed, ref caps) != HidpStatusSuccess)
                            continue;

                        int batteryFeature = CountUsages(preparsed, HidpFeature,
                            UsagePageGenericDeviceControls, UsageBatteryStrength);
                        int batteryInput = CountUsages(preparsed, HidpInput,
                            UsagePageGenericDeviceControls, UsageBatteryStrength);

                        // 阳性对照：鼠标 X 轴
                        int xAxisInput = CountUsages(preparsed, HidpInput, UsagePageGenericDesktop, UsageX);

                        lines.Add(
                            $"{attributes.VendorID:X4}:{attributes.ProductID:X4}  " +
                            $"顶层={caps.UsagePage:X2}/{caps.Usage:X2}  " +
                            $"报告长度 F={caps.FeatureReportByteLength} I={caps.InputReportByteLength}  " +
                            $"电量项(特征={batteryFeature} 输入={batteryInput})  " +
                            $"对照X轴={xAxisInput}  " +
                            $"{ReadDeviceName(handle)}");
                    }
                    finally
                    {
                        HidD_FreePreparsedData(preparsed);
                    }
                }
                catch (Exception ex)
                {
                    lines.Add($"{path}  → 异常 {ex.Message}");
                }
                finally
                {
                    CloseHandle(handle);
                }
            }

            return lines;
        }

        // ---------- 结构体 / P/Invoke ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
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
        private struct HiddAttributes
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        private static IntPtr Open(string path, uint desiredAccess)
            => CreateFileW(path, desiredAccess, FileShareRead | FileShareWrite,
                IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);

        private static string ReadDeviceName(IntPtr handle)
        {
            string product = ReadHidString(handle, true);
            return product.Length > 0 ? product : ReadHidString(handle, false);
        }

        private static string ReadHidString(IntPtr handle, bool isProduct)
        {
            var buffer = new byte[256 * 2];
            bool ok = isProduct
                ? HidD_GetProductString(handle, buffer, buffer.Length)
                : HidD_GetManufacturerString(handle, buffer, buffer.Length);
            if (!ok)
                return string.Empty;

            return System.Text.Encoding.Unicode.GetString(buffer).TrimEnd('\0').Trim();
        }

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

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool HidD_GetProductString(IntPtr handle, byte[] buffer, int bufferLength);

        [DllImport("hid.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool HidD_GetManufacturerString(IntPtr handle, byte[] buffer, int bufferLength);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

        [DllImport("hid.dll")]
        private static extern int HidP_GetSpecificValueCaps(int reportType, ushort usagePage,
            ushort linkCollection, ushort usage, byte[] valueCaps, ref ushort valueCapsLength,
            IntPtr preparsedData);

        [DllImport("hid.dll")]
        private static extern int HidP_GetUsageValue(int reportType, ushort usagePage, ushort linkCollection,
            ushort usage, out uint usageValue, IntPtr preparsedData, byte[] report, uint reportLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(IntPtr handle, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FlushQueue(IntPtr handle);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool ReadFile(IntPtr handle, byte[] buffer, int bytesToRead,
            out int bytesRead, ref NativeOverlapped overlapped);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool GetOverlappedResult(IntPtr handle, ref NativeOverlapped overlapped,
            out int bytesTransferred, bool wait);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateEventW(IntPtr securityAttributes, bool manualReset,
            bool initialState, string? name);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern uint WaitForSingleObject(IntPtr handle, int milliseconds);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CancelIo(IntPtr handle);

        private static List<string> EnumerateHidInterfacePaths()
        {
            var paths = new List<string>();

            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                DigcfPresent | DigcfDeviceInterface);
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
    }
}
