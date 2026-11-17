using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace RapooProbe
{
    /// <summary>
    /// 雷柏（Rapoo, VID 0x24AE）2.4G 设备电量读取探针。
    ///
    /// 协议来源：商店版 EasyBluetooth 3.5.8 反编译出的 RapooBatteryProviderPidSingle
    ///   · 目标集合：report descriptor 里 usage page = 0xFF00（vendor-defined），
    ///     usage 命中 {14, 15} 的更优先；
    ///   · 先写触发帧：report id 6 + 负载 A5 A3 00 00 00 00 00 00；
    ///   · 然后被动读输入报告（该协议不发查询、等设备推送）：
    ///       len >= 19 && buf[0] == 0x07  -> 电量 = buf[8]
    ///       len >= 7  && buf[0] == 0xBB  -> 电量 = buf[6]
    ///     只接受 1..100 的值。
    ///
    /// 本程序只用原生 P/Invoke，便于在真机上直接验证协议是否成立。
    /// </summary>
    internal static class Program
    {
        // 注意：不能是 static readonly —— P/Invoke 需要 ref 传入，静态只读字段无法取 ref。
        private static Guid HidInterfaceGuid =
            new Guid("4d1e55b2-f16f-11cf-88cb-001111000030");

        private static bool m_verbose;

        private const ushort RapooVendorId = 0x24AE;
        private const int VendorUsagePage = 0xFF00;
        private static readonly HashSet<int> PreferredUsages = new HashSet<int> { 14, 15 };

        private static int Main(string[] args)
        {
            int listenSeconds = 15;
            if (args.Length > 0 && int.TryParse(args[0], out int s) && s > 0)
                listenSeconds = s;
            m_verbose = args.Length > 1 && args[1] == "-v";

            Console.OutputEncoding = Encoding.UTF8;

            // 定向实验：只在 col05（触发帧可写的那条）写，只在 col09（实测出电量的那条）听
            if (args.Length > 1 && args[1] == "trigger")
                return TriggerExperiment(listenSeconds);

            // 时序对照实验：定位“写成功后却收不到应答”的原因
            if (args.Length > 1 && args[1] == "matrix")
                return MatrixExperiment(listenSeconds);

            Console.WriteLine($"=== 雷柏 2.4G 电量探针（监听 {listenSeconds}s）===");

            var paths = EnumerateHidPaths();
            Console.WriteLine($"HID 接口总数: {paths.Count}");

            var targets = new List<string>();
            int opened = 0, rapooSeen = 0, attrFail = 0;
            foreach (string path in paths)
            {
                IntPtr h = Open(path);
                if (h == IntPtr.Zero || h == new IntPtr(-1))
                {
                    Console.WriteLine($"  [打开失败 err={Marshal.GetLastWin32Error()}] {path}");
                    continue;
                }
                opened++;
                try
                {
                    // 关键：HIDD_ATTRIBUTES.Size 必须先填 sizeof，否则 HidD_GetAttributes 直接失败
                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    if (!HidD_GetAttributes(h, ref attr))
                    {
                        attrFail++;
                        Console.WriteLine($"  [取属性失败 err={Marshal.GetLastWin32Error()}] {path}");
                        continue;
                    }

                    string product = GetProductString(h) ?? "";
                    bool hasCaps = TryGetCaps(h, out ushort usagePage, out ushort usage, out int inLen, out int outLen);

                    if (attr.VendorID == RapooVendorId) rapooSeen++;
                    Console.WriteLine($"  [0x{attr.VendorID:X4}:0x{attr.ProductID:X4}] usagePage=0x{usagePage:X4} usage={usage} in={inLen} out={outLen}  {path}");
                    if (!string.IsNullOrEmpty(product))
                        Console.WriteLine($"      product=\"{product}\"");

                    if (attr.VendorID == RapooVendorId && usagePage == VendorUsagePage)
                        targets.Add(path);
                }
                finally { Close(h); }
            }
            Console.WriteLine($"成功打开 {opened}/{paths.Count}，取属性失败 {attrFail}，雷柏设备 {rapooSeen} 个");

            if (targets.Count == 0)
            {
                Console.WriteLine("未找到 usage page 0xFF00 的雷柏 vendor 集合——请确认设备已连接/唤醒。");
                return 2;
            }

            var threads = new List<Thread>();
            foreach (string path in targets)
            {
                string captured = path;
                var t = new Thread(() => Probe(captured, listenSeconds)) { IsBackground = true };
                t.Start();
                threads.Add(t);
            }
            foreach (var t in threads) t.Join();
            return 0;
        }

        private static void Probe(string path, int seconds)
        {
            string tag0 = ShortName(path);
            IntPtr h = Open(path);
            if (h == IntPtr.Zero || h == new IntPtr(-1))
            {
                Console.WriteLine($"[{tag0}] 打开失败 err={Marshal.GetLastWin32Error()}");
                return;
            }

            try
            {
                if (!TryGetCaps(h, out _, out _, out int inLen, out int outLen)) return;

                // 触发帧：report id 6 + A5 A3 00 00 00 00 00 00
                var trigger = new byte[outLen];
                trigger[0] = 6;
                byte[] payload = { 0xA5, 0xA3, 0, 0, 0, 0, 0, 0 };
                Array.Copy(payload, 0, trigger, 1, Math.Min(payload.Length, outLen - 1));
                Console.WriteLine($"[{tag0}] 写触发帧: {Hex(trigger)} -> {(Write(h, trigger, 500) ? "OK" : "失败")}");

                var buf = new byte[inLen];
                long deadline = Environment.TickCount64 + seconds * 1000L;
                int seen = 0, hits = 0;

                while (Environment.TickCount64 < deadline)
                {
                    int n = Read(h, buf, 300);
                    if (n <= 0) continue;
                    seen++;

                    string tag = "";
                    int percent = -1;
                    if (n >= 19 && buf[0] == 0x07) { percent = buf[8]; tag = "规则A(buf[0]=07,buf[8])"; }
                    else if (n >= 7 && buf[0] == 0xBB) { percent = buf[6]; tag = "规则B(buf[0]=BB,buf[6])"; }

                    if (seen <= 25 || (percent >= 0 && percent <= 100))
                        Console.WriteLine($"[{tag0}] #{seen} len={n} {Hex(buf, Math.Min(n, 24))} {tag}");

                    if (percent > 0 && percent <= 100)
                    {
                        hits++;
                        Console.WriteLine($"[{tag0}] >>> 电量 = {percent}%");
                    }
                }
                Console.WriteLine($"[{tag0}] 小结: 收到 {seen} 个报告, 命中电量 {hits} 次");
                if (hits == 0 && seen > 0)
                    Console.WriteLine($"[{tag0}] （有报告但无电量帧）");
                if (seen == 0)
                    Console.WriteLine($"[{tag0}] （无输入报告）");
            }
            finally { Close(h); }
        }

        /// <summary>
        /// 读取 report caps。注意：HidP_GetCaps 的成功码是 0x00110000（HIDP_STATUS_SUCCESS），
        /// 不是 0；且 ByValArray 字段必须先实例化，否则封送会失败。
        /// 这两点照 GameHub.Utils.HidNative.TryGetCaps。
        /// </summary>
        private static bool TryGetCaps(IntPtr handle, out ushort usagePage, out ushort usage, out int inLen, out int outLen)
        {
            usagePage = usage = 0;
            inLen = outLen = 0;
            if (!HidD_GetPreparsedData(handle, out IntPtr preparsed)) return false;
            try
            {
                var caps = new HIDP_CAPS { Reserved = new ushort[17] };
                if (HidP_GetCaps(preparsed, ref caps) != 0x00110000) return false;
                usagePage = caps.UsagePage;
                usage = caps.Usage;
                inLen = caps.InputReportByteLength;
                outLen = caps.OutputReportByteLength;
                return true;
            }
            finally { HidD_FreePreparsedData(preparsed); }
        }

        /// <summary>
        /// 定向实验：验证“写 col05 的触发帧”能否让 col09 吐电量。
        /// 若能，雷柏就从“被动推送”变成“确定性查询”，实现可以稳定得多。
        /// </summary>
        private static int TriggerExperiment(int seconds)
        {
            var paths = EnumerateHidPaths();
            string writeTarget = null, listenTarget = null;
            foreach (string p in paths)
            {
                if (p.IndexOf("vid_24ae", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (p.IndexOf("col05", StringComparison.OrdinalIgnoreCase) >= 0) writeTarget = p;
                if (p.IndexOf("col09", StringComparison.OrdinalIgnoreCase) >= 0) listenTarget = p;
            }

            Console.WriteLine($"写目标(col05): {writeTarget ?? "未找到"}");
            Console.WriteLine($"听目标(col09): {listenTarget ?? "未找到"}");
            if (listenTarget == null) return 1;

            IntPtr hListen = Open(listenTarget);
            if (hListen == IntPtr.Zero || hListen == new IntPtr(-1))
            {
                Console.WriteLine($"打开 col09 失败 err={Marshal.GetLastWin32Error()}");
                return 1;
            }

            try
            {
                if (TryGetCaps(hListen, out _, out _, out int inLen, out _))
                    Console.WriteLine($"col09 输入报告长度 = {inLen}");

                for (int round = 1; round <= 3; round++)
                {
                    if (writeTarget != null)
                    {
                        IntPtr hw = Open(writeTarget);
                        if (hw != IntPtr.Zero && hw != new IntPtr(-1))
                        {
                            try
                            {
                                if (TryGetCaps(hw, out _, out _, out _, out int outLen) && outLen >= 9)
                                {
                                    var pkt = new byte[outLen];
                                    pkt[0] = 6;
                                    pkt[1] = 0xA5; pkt[2] = 0xA3;
                                    Console.WriteLine($"  第 {round} 轮：向 col05 写触发帧 -> {(Write(hw, pkt, 300) ? "OK" : "失败(err=" + Marshal.GetLastWin32Error() + ")")}");
                                }
                            }
                            finally { Close(hw); }
                        }
                    }

                    var buf = new byte[64];
                    long deadline = Environment.TickCount64 + seconds * 1000L;
                    int got = 0;
                    while (Environment.TickCount64 < deadline)
                    {
                        int n = Read(hListen, buf, 200);
                        if (n <= 0) continue;
                        got++;
                        Console.WriteLine($"    收到 len={n} {Hex(buf, Math.Min(n, 20))}");
                        if (TryDecode(buf, n, out int pct))
                        {
                            Console.WriteLine($"    >>> 电量 = {pct}%");
                            return 0;   // 实验成功：写触发帧确实能换来电量
                        }
                    }
                    Console.WriteLine($"  第 {round} 轮：只听 col09，{seconds}s 内收到 {got} 个报告");
                }
            }
            finally { Close(hListen); }
            return 2;
        }

        private static string ShortName(string path)
        {
            string lower = path.ToLowerInvariant();
            int mi = lower.IndexOf("mi_");
            if (mi < 0) return "?";
            int end = lower.IndexOf('#', mi);
            return end > mi ? path.Substring(mi, end - mi) : path.Substring(mi);
        }

        /// <summary>
        /// 时序对照实验。三种变体，逐步向 GameHub 的实际流程靠拢：
        ///   V1 基线：开 col09 → 开 col05 写 → **关 col05** → 读 col09（已知 3/3 成功）
        ///   V2：开 col09 + col05 → 写 col05（**保持开着**）→ 读 col09
        ///   V3：开全部 4 条 → 写 col05 → 读 col09（= GameHub 实际流程）
        /// </summary>
        private static int MatrixExperiment(int seconds)
        {
            var paths = EnumerateHidPaths();
            string p05 = null, p08 = null, p09 = null, p0a = null;
            foreach (string p in paths)
            {
                if (p.IndexOf("vid_24ae", StringComparison.OrdinalIgnoreCase) < 0) continue;
                if (p.Contains("col05")) p05 = p;
                if (p.Contains("col08")) p08 = p;
                if (p.Contains("col09")) p09 = p;
                if (p.Contains("col0a")) p0a = p;
            }

            var buf = new byte[64];

            int Read09(IntPtr h, int sec)
            {
                long dl = Environment.TickCount64 + sec * 1000L;
                while (Environment.TickCount64 < dl)
                {
                    int n = Read(h, buf, 200);
                    if (n <= 0) continue;
                    Console.WriteLine($"      收到 len={n} {Hex(buf, Math.Min(n, 20))}");
                    if (TryDecode(buf, n, out int pc)) return pc;
                }
                return -1;
            }

            void WriteTrigger(IntPtr h, int outLen)
            {
                var pkt = new byte[outLen];
                pkt[0] = 6;
                pkt[1] = 0xA5; pkt[2] = 0xA3;
                Console.WriteLine($"      写触发帧 -> {(Write(h, pkt, 300) ? "OK" : "失败")}");
            }

            // ---- V1 基线 ----
            Console.WriteLine("V1 基线（开 col09 → 开 col05 写 → 关 col05 → 读 col09）");
            IntPtr l1 = Open(p09);
            try
            {
                TryGetCaps(l1, out _, out _, out _, out _);
                IntPtr w1 = Open(p05);
                if (w1 != IntPtr.Zero && w1 != new IntPtr(-1))
                {
                    TryGetCaps(w1, out _, out _, out _, out int ol);
                    WriteTrigger(w1, ol);
                    Close(w1);
                }
                Console.WriteLine($"      V1 结果: 电量 = {Read09(l1, seconds)}");
            }
            finally { Close(l1); }

            // ---- V2：写句柄保持打开 ----
            Console.WriteLine("V2（开 col09 + col05 → 写 col05 保持打开 → 读 col09）");
            IntPtr l2 = Open(p09);
            IntPtr w2 = Open(p05);
            try
            {
                TryGetCaps(w2, out _, out _, out _, out int ol2);
                WriteTrigger(w2, ol2);
                Console.WriteLine($"      V2 结果: 电量 = {Read09(l2, seconds)}   （col05 句柄全程未关）");
            }
            finally { Close(w2); Close(l2); }

            // ---- V3：全部 4 条都开着 ----
            Console.WriteLine("V3（开 col05/col08/col09/col0a → 写 col05 → 读 col09）= GameHub 实际流程");
            IntPtr h05 = Open(p05), h08 = Open(p08), h09 = Open(p09), h0a = Open(p0a);
            try
            {
                TryGetCaps(h05, out _, out _, out _, out int ol3);
                WriteTrigger(h05, ol3);
                Console.WriteLine($"      V3 结果: 电量 = {Read09(h09, seconds)}   （4 条句柄全程开着）");
            }
            finally { Close(h0a); Close(h09); Close(h08); Close(h05); }

            // ---- V4：先做一遍“发现”（开/取属性/取 caps/关），再跑 V3 流程 = GameHub 真实前置步骤 ----
            Console.WriteLine("V4（先发现一遍再开→写→读）= GameHub 的完整前置步骤");
            foreach (string p in new[] { p05, p08, p09, p0a })
            {
                IntPtr hd = Open(p);
                if (hd == IntPtr.Zero || hd == new IntPtr(-1)) continue;
                try
                {
                    var attr = new HIDD_ATTRIBUTES { Size = Marshal.SizeOf<HIDD_ATTRIBUTES>() };
                    HidD_GetAttributes(hd, ref attr);
                    TryGetCaps(hd, out _, out _, out _, out _);
                    GetProductString(hd);
                }
                finally { Close(hd); }
            }
            IntPtr m05 = Open(p05), m08 = Open(p08), m09 = Open(p09), m0a = Open(p0a);
            try
            {
                TryGetCaps(m05, out _, out _, out _, out int ol4);
                WriteTrigger(m05, ol4);
                Console.WriteLine($"      V4 结果: 电量 = {Read09(m09, seconds)}");
            }
            finally { Close(m0a); Close(m09); Close(m08); Close(m05); }

            return 0;
        }

        /// <summary>参考实现的解析规则（下标含 report id 占位）。</summary>
        private static bool TryDecode(byte[] buffer, int length, out int percent)
        {
            percent = -1;
            if (buffer == null || length <= 0) return false;
            if (length >= 19 && buffer[0] == 0x07)
                percent = buffer[8];
            else if (length >= 7 && buffer[0] == 0xBB)
                percent = buffer[6];
            else
                return false;
            return percent > 0 && percent <= 100;
        }

        private static string Hex(byte[] b, int len = -1)
        {
            if (len < 0) len = b.Length;
            var sb = new StringBuilder(len * 3);
            for (int i = 0; i < len; i++) sb.Append(b[i].ToString("X2")).Append(i == len - 1 ? "" : "-");
            return sb.ToString();
        }

        // ---------- 枚举 HID 接口 ----------

        private static List<string> EnumerateHidPaths()
        {
            var result = new List<string>();
            // 与 GameHub.Utils.HidNative.EnumerateInterfacePaths 保持一致（本机实测可用）：
            // cbSize 在 x64 固定 8，但路径字符串从偏移 4 开始。
            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                DIGCF_PRESENT | DIGCF_DEVICEINTERFACE);
            if (set == IntPtr.Zero || set == new IntPtr(-1)) return result;

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
                        if (SetupDiGetDeviceInterfaceDetailWrapper(set, ref did, buffer, 4096, out _, IntPtr.Zero))
                        {
                            string p = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? string.Empty;
                            if (p.StartsWith(@"\\?\", StringComparison.Ordinal))
                                result.Add(p);
                            else if (!string.IsNullOrEmpty(p))
                                Console.WriteLine($"  [路径格式意外: {p}]");
                        }
                    }
                    finally { Marshal.FreeHGlobal(buffer); }
                }
            }
            finally { SetupDiDestroyDeviceInfoList(set); }
            return result;
        }

        // ---------- HID 原生互操作 ----------

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

        // VS_VERSION_INFO 的 DeviceInterfaceDetail 首字段在 x64 上是 8 字节（含 4 字节对齐）
        private static IntPtr Open(string path)
        {
            return CreateFileW(path, GENERIC_READ | GENERIC_WRITE,
                FILE_SHARE_READ | FILE_SHARE_WRITE, IntPtr.Zero, OPEN_EXISTING,
                FILE_FLAG_OVERLAPPED, IntPtr.Zero);
        }

        private static void Close(IntPtr h) => CloseHandle(h);

        private static string GetProductString(IntPtr h)
        {
            var sb = new StringBuilder(256);
            if (HidD_GetProductString(h, sb, sb.Capacity * 2)) return sb.ToString();
            return null;
        }

        private static bool Write(IntPtr h, byte[] data, int timeoutMs)
        {
            IntPtr evt = CreateEventW(IntPtr.Zero, true, false, null);
            try
            {
                var ov = new NativeOverlapped { EventHandle = evt };
                if (!WriteFile(h, data, (uint)data.Length, out _, ref ov))
                {
                    if (Marshal.GetLastWin32Error() != 997) return false;
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
            try
            {
                var ov = new NativeOverlapped { EventHandle = evt };
                if (!ReadFile(h, buffer, (uint)buffer.Length, out _, ref ov))
                {
                    int err = Marshal.GetLastWin32Error();
                    if (err != 997) return -1;   // 997 = ERROR_IO_PENDING
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

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true, EntryPoint = "SetupDiGetDeviceInterfaceDetailW")]
        private static extern bool SetupDiGetDeviceInterfaceDetailWrapper(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int size, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("hid.dll")]
        private static extern bool HidD_GetHidGuid(out Guid hidGuid);
        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData, ref Guid interfaceClassGuid, uint memberIndex, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetailW(IntPtr deviceInfoSet, ref SP_DEVICE_INTERFACE_DATA deviceInterfaceData, IntPtr deviceInterfaceDetailData, int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

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

        [DllImport("hid.dll", CharSet = CharSet.Unicode)]
        private static extern bool HidD_GetProductString(IntPtr handle, StringBuilder buffer, int bufferLength);

        [DllImport("hid.dll")]
        private static extern int HidP_GetCaps(IntPtr preparsedData, ref HIDP_CAPS capabilities);
    }
}
