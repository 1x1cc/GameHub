using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

// ============================================================================
//  HID 抓包工具 —— 逆向 2.4G 无线外设电量协议用
//
//  用法:  HidCapture <VID十六进制> <PID十六进制> [秒数=90]
//  示例:  HidCapture 249A 5C2F 120
//  无参数运行则进入交互模式（默认前行者 GS02 的 249A:5C2F）。
//
//  做三件事（全部只读，不给设备发任何写命令）：
//    1) 枚举该 VID:PID 的所有 HID 接口，把能力（usage/报告长度）记进日志；
//    2) 对有 feature report 的接口做 GET_FEATURE 只读探测（report id 0..15）；
//    3) 每个接口一个线程持续监听输入报告，全部带时间戳写日志。
//
//  日志: %TEMP%\hidcapture\HidCapture-<VID>-<PID>-<时间戳>.log
//  逆向方法（A/B 电量对比法）:
//    · 在电量 X% 时抓一份（正常使用设备：移动/点击/按遍按键）
//    · 电量变化到 Y% 后再抓一份；充电时再抓一份
//    · 对比三份日志里「随电量变化」的字节 → 那就是电量字段
// ============================================================================

internal static class Program
{
    private static string _logPath = string.Empty;
    private static readonly object Gate = new();
    private static long _total;

    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint ShareReadWrite = 0x00000003;
    private const uint OpenExisting = 3;
    private static readonly IntPtr Invalid = new(-1);

    [StructLayout(LayoutKind.Sequential)]
    private struct Attr
    {
        public int Size;
        public ushort Vid;
        public ushort Pid;
        public ushort Ver;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Caps
    {
        public ushort Usage;
        public ushort UsagePage;
        public ushort InLen;
        public ushort OutLen;
        public ushort FeatLen;
        [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
        public ushort[] Reserved;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IfaceData
    {
        public int cbSize;
        public Guid Guid;
        public int Flags;
        public IntPtr Reserved;
    }

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr e, IntPtr parent, int flags);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiEnumDeviceInterfaces(IntPtr set, IntPtr dev, ref Guid guid, int index, ref IfaceData data);

    [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr set, ref IfaceData data, IntPtr detail, int size, out int need, IntPtr dev);

    [DllImport("setupapi.dll", SetLastError = true)]
    private static extern bool SetupDiDestroyDeviceInfoList(IntPtr set);

    [DllImport("hid.dll")]
    private static extern void HidD_GetHidGuid(out Guid guid);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetAttributes(IntPtr h, out Attr a);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetPreparsedData(IntPtr h, out IntPtr pp);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_FreePreparsedData(IntPtr pp);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern int HidP_GetCaps(IntPtr pp, ref Caps caps);

    [DllImport("hid.dll", SetLastError = true)]
    private static extern bool HidD_GetFeature(IntPtr h, byte[] buf, int len);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetManufacturerString(IntPtr h, byte[] buf, int len);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetProductString(IntPtr h, byte[] buf, int len);

    [DllImport("hid.dll", CharSet = CharSet.Unicode)]
    private static extern bool HidD_GetSerialNumberString(IntPtr h, byte[] buf, int len);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool DeviceIoControl(IntPtr h, int code, byte[]? inBuf, int inSize,
        byte[]? outBuf, int outSize, out int returned, IntPtr overlapped);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(string name, uint access, uint share, IntPtr sa, uint disp, uint flags, IntPtr tmpl);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ReadFile(IntPtr h, byte[] buf, uint len, out uint got, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool WriteFile(IntPtr h, byte[] buf, uint len, out uint written, IntPtr ov);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(IntPtr h);

    private sealed class Target
    {
        public string Path = string.Empty;
        public string Label = string.Empty;
        public ushort UsagePage;
        public ushort Usage;
        public ushort InLen;
        public ushort OutLen;
        public ushort FeatLen;
    }

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;

        ushort vid;
        ushort pid;
        int seconds;

        if (args.Length >= 2)
        {
            vid = Convert.ToUInt16(args[0], 16);
            pid = Convert.ToUInt16(args[1], 16);
            seconds = args.Length >= 3 && int.TryParse(args[2], out var s) ? s : 90;
        }
        else
        {
            Console.WriteLine("HID 抓包工具 —— 逆向 2.4G 外设电量协议");
            Console.WriteLine("已知设备: 249A:5C2F = 前行者 GS02（默认）");
            Console.Write("VID 十六进制（回车 = 249A）: ");
            var v = Console.ReadLine();
            Console.Write("PID 十六进制（回车 = 5C2F）: ");
            var p = Console.ReadLine();
            Console.Write("抓包秒数（回车 = 90）: ");
            var sec = Console.ReadLine();
            vid = string.IsNullOrWhiteSpace(v) ? (ushort)0x249A : Convert.ToUInt16(v.Trim(), 16);
            pid = string.IsNullOrWhiteSpace(p) ? (ushort)0x5C2F : Convert.ToUInt16(p.Trim(), 16);
            seconds = string.IsNullOrWhiteSpace(sec) ? 90 : int.Parse(sec.Trim());
        }

        if (args.Length >= 3 && args[2].StartsWith("probe", StringComparison.OrdinalIgnoreCase))
        {
            return RunProbe(vid, pid, stage2: args[2].EndsWith("2", StringComparison.Ordinal));
        }

        var dir = Path.Combine(Path.GetTempPath(), "hidcapture");
        Directory.CreateDirectory(dir);
        _logPath = Path.Combine(dir, $"HidCapture-{vid:X4}-{pid:X4}-{DateTime.Now:yyyyMMdd-HHmmss}.log");

        var targets = FindInterfaces(vid, pid);
        if (targets.Count == 0)
        {
            Console.WriteLine($"未找到 VID={vid:X4} PID={pid:X4} 的 HID 设备，请确认设备/接收器已插好。");
            return 1;
        }

        Console.WriteLine($"找到 {targets.Count} 个 HID 接口，抓包 {seconds} 秒。");
        Console.WriteLine("抓包期间请正常使用设备：移动、点击、按遍所有按键（DPI 键 / 侧键 / 滚轮）。");
        Console.WriteLine($"日志: {_logPath}");

        Log($"# HidCapture VID={vid:X4} PID={pid:X4}");
        Log($"# 开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss}  接口数 {targets.Count}");

        foreach (var t in targets)
        {
            Log($"# 接口 {t.Label} usage=0x{t.UsagePage:X2}/0x{t.Usage:X2} 输入={t.InLen} 输出={t.OutLen} 特征={t.FeatLen}");
        }

        // 设备自身信息 + 报告描述符：逆向时用来判断方案家族和实际 Report ID
        DumpIdentity(targets);

        foreach (var t in targets)
        {
            ProbeFeatures(t);
            var th = new Thread(() => Listen(t)) { IsBackground = true };
            th.Start();
        }

        Thread.Sleep(seconds * 1000);
        Log($"# 结束 {DateTime.Now:HH:mm:ss}  共捕获 {_total} 条报告");
        Console.WriteLine($"完成：共 {_total} 条报告。");
        Console.WriteLine($"日志已保存: {_logPath}");
        Console.WriteLine("抓完告诉 Agent 即可，它会直接读取这个日志做协议分析。");
        Environment.Exit(0);
        return 0;
    }

    private static List<Target> FindInterfaces(ushort vid, ushort pid)
    {
        var result = new List<Target>();
        HidD_GetHidGuid(out var hidGuid);
        IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero, 0x12);
        if (set == IntPtr.Zero || set == Invalid)
            return result;

        try
        {
            var data = new IfaceData { cbSize = Marshal.SizeOf<IfaceData>() };
            for (int i = 0; ; i++)
            {
                if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data))
                    break;

                IntPtr buffer = Marshal.AllocHGlobal(4096);
                try
                {
                    Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                    if (!SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, 4096, out _, IntPtr.Zero))
                        continue;

                    string path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? string.Empty;
                    if (!path.StartsWith(@"\\?\", StringComparison.Ordinal))
                        continue;

                    // access=0 打开：读属性/能力足够，兼容所有集合
                    IntPtr h = CreateFileW(path, 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                    if (h == Invalid)
                        continue;

                    try
                    {
                        if (!HidD_GetAttributes(h, out var a) || a.Vid != vid || a.Pid != pid)
                            continue;

                        var target = new Target { Path = path };
                        if (HidD_GetPreparsedData(h, out var pp))
                        {
                            try
                            {
                                var caps = new Caps { Reserved = new ushort[17] };
                                if (HidP_GetCaps(pp, ref caps) == 0x00110000)
                                {
                                    target.UsagePage = caps.UsagePage;
                                    target.Usage = caps.Usage;
                                    target.InLen = caps.InLen;
                                    target.OutLen = caps.OutLen;
                                    target.FeatLen = caps.FeatLen;
                                }
                            }
                            finally
                            {
                                HidD_FreePreparsedData(pp);
                            }
                        }

                        target.Label = LabelOf(path);
                        result.Add(target);
                    }
                    finally
                    {
                        CloseHandle(h);
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

        return result;
    }

    /// <summary>
    /// 输出设备厂商/产品字符串与报告描述符。
    /// 厂商字符串常能直接指向方案商（如 SONiX / SINO WEALTH / Telink），
    /// 报告描述符里的 0x85 项就是实际使用的 Report ID ——
    /// 发厂商命令前必须知道这个，猜错设备完全不搭理。
    /// </summary>
    private static void DumpIdentity(List<Target> targets)
    {
        foreach (var t in targets)
        {
            var h = CreateFileW(t.Path, 0, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
            if (h == new IntPtr(-1))
            {
                Log($"# {t.Label} 打开失败，无法读设备信息");
                continue;
            }

            try
            {
                string man = ReadString(h, HidD_GetManufacturerString);
                string prod = ReadString(h, HidD_GetProductString);
                string ser = ReadString(h, HidD_GetSerialNumberString);

                if (man.Length > 0 || prod.Length > 0 || ser.Length > 0)
                {
                    Log($"# 设备信息 {t.Label}: 厂商=\"{man}\" 产品=\"{prod}\" 序列号=\"{ser}\"");
                }

                var desc = ReportDescriptor(h);
                if (desc == null || desc.Length == 0)
                {
                    // 部分设备要求句柄具备读权限才允许取报告描述符
                    var hr = CreateFileW(t.Path, 0x80000000, 3, IntPtr.Zero, 3, 0, IntPtr.Zero);
                    if (hr != new IntPtr(-1))
                    {
                        try { desc = ReportDescriptor(hr); }
                        finally { CloseHandle(hr); }
                    }
                }

                if (desc == null || desc.Length == 0)
                {
                    Log($"# 报告描述符 {t.Label}: 取不到（设备不允许）");
                    continue;
                }

                Log($"# 报告描述符 {t.Label}（{desc.Length} 字节）: {Hex(desc, Math.Min(desc.Length, 192))}");

                var ids = new List<string>();
                for (int i = 0; i < desc.Length - 1; i++)
                {
                    if (desc[i] == 0x85)
                        ids.Add("0x" + desc[i + 1].ToString("X2"));
                }

                Log($"# Report ID: {(ids.Count == 0 ? "（无，仅 report id 0）" : string.Join(",", ids))}");
            }
            finally
            {
                CloseHandle(h);
            }
        }
    }

    private static string ReadString(IntPtr handle, Func<IntPtr, byte[], int, bool> getter)
    {
        var buf = new byte[256];
        if (!getter(handle, buf, buf.Length))
            return string.Empty;

        string text = Encoding.Unicode.GetString(buf);
        int nul = text.IndexOf('\0');
        return (nul >= 0 ? text.Substring(0, nul) : text).Trim();
    }

    private static byte[]? ReportDescriptor(IntPtr handle)
    {
        var buf = new byte[1024];
        if (!DeviceIoControl(handle, 0x000B0004, null, 0, buf, buf.Length, out int got, IntPtr.Zero) || got <= 0)
            return null;

        var result = new byte[got];
        Array.Copy(buf, result, got);
        return result;
    }

    private static string LabelOf(string path)
    {
        var m = System.Text.RegularExpressions.Regex.Match(path,
            @"mi_(\d+)(?:&col(\d+))?", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        if (!m.Success)
            return "if";
        return m.Groups[2].Success
            ? $"mi_{m.Groups[1].Value}&col{m.Groups[2].Value}"
            : $"mi_{m.Groups[1].Value}";
    }

    /// <summary>GET_FEATURE 只读探测：有些方案的电量就藏在 feature report 里。</summary>
    private static void ProbeFeatures(Target t)
    {
        if (t.FeatLen == 0)
            return;

        IntPtr h = CreateFileW(t.Path, 0, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h == Invalid)
            return;

        try
        {
            for (byte id = 0; id < 16; id++)
            {
                var buf = new byte[t.FeatLen];
                buf[0] = id;
                if (HidD_GetFeature(h, buf, buf.Length))
                    Log($"{Now()} | {t.Label} | FEAT id=0x{id:X2} | {Hex(buf)}");
            }
        }
        finally
        {
            CloseHandle(h);
        }
    }

    /// <summary>
    /// 主动探测。被动监听对这个设备无效（鼠标/键盘集合被系统独占、
    /// 厂商集合不自发上报），只能主动发输出报告去“问”。
    ///
    /// stage2 = false：只发**全零无害帧**，只用来确定
    ///   （a）设备接受哪个 report id，（b）发完会不会应答。
    ///   全零帧不含任何有意义的命令，不会改设备设置。
    ///
    /// stage2 = true：再试真实电量命令帧（来自前行者官方网页驱动
    ///   的逆向结果 + ATK/VXE 公开协议），**这一步有改动设备设置的风险**。
    /// </summary>
    private static int RunProbe(ushort vid, ushort pid, bool stage2)
    {
        var targets = FindInterfaces(vid, pid);
        Target? vendor = null;
        foreach (var t in targets)
        {
            if (t.UsagePage >= 0xFF00 && t.OutLen > 1)
                vendor = t;
        }

        if (vendor == null)
        {
            Console.WriteLine("没有找到可写的厂商集合（usage page >= 0xFF00 且有输出报告）。");
            return 1;
        }

        int payloadLen = vendor.OutLen - 1;   // 输出报告长度含 report id 字节
        Console.WriteLine($"厂商集合: {vendor.Label} usage=0x{vendor.UsagePage:X2}/0x{vendor.Usage:X2}");
        Console.WriteLine($"输出报告 {vendor.OutLen} 字节（report id + {payloadLen} 字节载荷），输入 {vendor.InLen} 字节");
        Console.WriteLine(stage2
            ? "阶段 2：将发送真实电量命令帧（有改动设备设置的风险）。"
            : "阶段 1：只发送全零无害帧，不会改动设备设置。");
        Console.WriteLine();

        // 必须用**两个独立句柄**：同一个 HID 句柄上并发「阻塞读 + 写」会互相锁死
        // （实测本机卡死 4 分钟无输出）。读句柄归后台线程，写句柄归主线程。
        IntPtr hWrite = CreateFileW(vendor.Path, GenericWrite, ShareReadWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (hWrite == Invalid)
        {
            Console.WriteLine($"打不开厂商集合写句柄（错误码 {Marshal.GetLastWin32Error()}）—— 可能被厂商软件占用。");
            return 1;
        }

        IntPtr hRead = CreateFileW(vendor.Path, GenericRead, ShareReadWrite,
            IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (hRead == Invalid)
            Console.WriteLine($"提示：读句柄打开失败（错误码 {Marshal.GetLastWin32Error()}），只能发不能收。");

        try
        {
            var hReadLocal = hRead;
            var label = vendor.Label;
            int inLen = vendor.InLen > 0 ? vendor.InLen : 64;
            var reader = new Thread(() =>
            {
                if (hReadLocal == Invalid)
                    return;

                var buf = new byte[inLen];
                while (true)
                {
                    if (ReadFile(hReadLocal, buf, (uint)buf.Length, out uint got, IntPtr.Zero) && got > 0)
                    {
                        lock (Gate)
                        {
                            _total++;
                            Log($"{Now()} | {label} | IN len={got} | {Hex(buf, (int)got)}");
                        }
                        Console.WriteLine($"<<< 收到应答 {got} 字节: {Hex(buf, (int)got)}");
                    }
                }
            })
            { IsBackground = true };
            reader.Start();

            Log($"# 主动探测 stage{(stage2 ? 2 : 1)} 开始 {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

            for (int rid = 0; rid <= 16; rid++)
            {
                byte[] payload = stage2 ? BatteryCommandFor(rid) : new byte[payloadLen];
                var frame = BuildFrame((byte)rid, payload, payloadLen);

                var before = _total;
                bool ok = WriteFile(hWrite, frame, (uint)frame.Length, out uint written, IntPtr.Zero);
                int err = ok ? 0 : Marshal.GetLastWin32Error();

                Thread.Sleep(300);
                bool replied = _total > before;

                Log($"# rid=0x{rid:X2} 写入={(ok ? "成功" : "失败(" + err + ")")} 写入字节={written} 应答={replied}");
                Console.WriteLine($"rid=0x{rid:X2}  写入={(ok ? "成功" : "失败(" + err + ")")}  字节={written}  应答={(replied ? "有" : "无")}");

                if (ok)
                    Thread.Sleep(150);
            }

            Log($"# 主动探测结束 共收到 {_total} 条应答");
            Console.WriteLine();
            Console.WriteLine($"探测完成，共收到 {_total} 条应答。日志: {_logPath}");

            // 后台读线程还阻塞在 ReadFile 上，句柄未关就退不出去；
            // 这里直接强制退出，避免命令行挂住（实测会卡死）。
            CloseHandle(hWrite);
            if (hRead != Invalid)
                CloseHandle(hRead);
            Environment.Exit(0);
            return 0;
        }
        finally
        {
            CloseHandle(hWrite);
            if (hRead != Invalid)
                CloseHandle(hRead);
        }
    }

    /// <summary>
    /// 构造输出报告：byte[0] = report id，其后是载荷。
    /// 校验字节放在载荷**最后一字节** = 255 - (report_id + 前面各字节之和) % 256
    /// —— 这是前行者官方网页驱动 crc_sum 的算法，
    /// 它把 report id 也计入和值，这点很容易漏。
    /// </summary>
    private static byte[] BuildFrame(byte reportId, byte[] payload, int payloadLen)
    {
        var frame = new byte[payloadLen + 1];
        frame[0] = reportId;

        int sum = reportId;
        for (int i = 0; i < payloadLen - 1; i++)
        {
            byte value = i < payload.Length ? payload[i] : (byte)0;
            frame[i + 1] = value;
            sum += value;
        }

        frame[payloadLen] = (byte)(255 - (sum % 256));
        return frame;
    }

    /// <summary>阶段 2 的候选电量命令（取自公开协议与官方网页驱动逆向结果）。</summary>
    private static byte[] BatteryCommandFor(int reportId)
    {
        switch (reportId % 4)
        {
            case 0: return new byte[] { 0x87, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00 };
            case 1: return new byte[] { 0x4A, 0x00, 0x00, 0x00 };
            case 2: return new byte[] { 0x87, 0x00, 0x00, 0x01, 0x00, 0x02, 0x00 };
            default: return new byte[] { 0x7D, 0x72, 0x02, 0x00, 0x01, 0x07, 0x01 };
        }
    }

    private static void Listen(Target t)
    {
        // GENERIC_READ 打开：鼠标/键盘顶层集合会被系统拒绝（记录后跳过），
        // 厂商/消费者集合可以打开并收到设备自发上报
        IntPtr h = CreateFileW(t.Path, GenericRead, ShareReadWrite, IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
        if (h == Invalid)
        {
            Log($"# {t.Label} 以读权限打开失败（系统独占集合，跳过监听）");
            return;
        }

        try
        {
            int len = t.InLen > 0 ? t.InLen : 64;
            var buf = new byte[len];
            while (true)
            {
                if (ReadFile(h, buf, (uint)buf.Length, out uint got, IntPtr.Zero) && got > 0)
                {
                    lock (Gate)
                    {
                        _total++;
                        Log($"{Now()} | {t.Label} | IN id=0x{buf[0]:X2} len={got} | {Hex(buf, (int)got)}");
                    }
                }
            }
        }
        catch
        {
            // 进程退出时线程被终止，忽略
        }
        finally
        {
            CloseHandle(h);
        }
    }

    private static string Now() => DateTime.Now.ToString("HH:mm:ss.fff");

    private static string Hex(byte[] data) => Hex(data, data.Length);

    private static string Hex(byte[] data, int count)
    {
        var sb = new StringBuilder(count * 3);
        for (int i = 0; i < count; i++)
        {
            sb.Append(data[i].ToString("X2"));
            if (i < count - 1)
                sb.Append(' ');
        }
        return sb.ToString();
    }

    private static void Log(string line)
    {
        lock (Gate)
        {
            try
            {
                File.AppendAllText(_logPath, line + Environment.NewLine, Encoding.UTF8);
            }
            catch
            {
                // 日志写不进去就算了
            }
        }
    }
}
