using System;
using System.Collections.Generic;
using System.Linq;

namespace GameHub.Utils
{
    /// <summary>ROG（华硕）设备的一次电量读数。</summary>
    public sealed class RogBatteryReading
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public ushort ProductId { get; init; }
        public bool IsWireless { get; init; }

        /// <summary>电量百分比；null = 读不到（设备休眠/关机）。</summary>
        public int? Percent { get; init; }

        public bool IsCharging { get; init; }

        /// <summary>夜魔家族额外回报的 0–10 档位（原样透传，未换算成百分比）。</summary>
        public int Gauge { get; init; } = -1;

        /// <summary>读不到电量时的原因说明。</summary>
        public string Note { get; init; } = string.Empty;
    }

    /// <summary>
    /// 华硕 ROG 无线外设电量读取（厂商私有 HID 协议）。
    ///
    /// 协议来源：开源项目 SteveWang92/peripheral-battery-tray 的 rog-battery 模块
    /// （Rust/Windows，hidapi；其协议事实整理自 G-Helper）。本项目按公开结论独立实现：
    ///
    ///   · VID 0x0B05；查询为输出报告 [report_id, 0x12, 子命令]，回复走输入报告
    ///     （同一集合），500ms 内应答。下标按「回复首字节是 report id」计：
    ///       - 键盘子命令 0x01：回复回显 [1]=0x12 [2]=0x01；百分比在 [6]
    ///         （Azoth 等）或 [11]（夜魔家族，[6] 是 0–10 档位表）；充电中 [9]==1；
    ///       - 鼠电子命令 0x07：回复回显 [1]=0x12 [2]=0x07；百分比在 [5]
    ///         （老鼠标是 0–4 档位，×25 换算）；充电中 [10]!=0；
    ///   · 接收器代答「设备无应答」标记：[1]=0xFF [2]=0xAA（设备休眠/关机）；
    ///   · OMNI 多设备接收器（PID 0x1ACE）：配对列表命令 [0x01, 0xA0, 0x00] 发到
    ///     mi_02/col01，回复 [5..] 按 4 字节槽位存配对设备 PID（小端 u16，遇 0 结束）；
    ///     配对键盘走 mi_02/col02（report id 0x02）、鼠标走 mi_02/col03（report id 0x03）。
    ///
    /// 未收录型号（如 2025 年末的新品，夜魔 X 等）出现在 OMNI 配对列表里时，会按
    /// 「键盘（夜魔家族布局优先）→ 鼠标」的顺序用只读查询试探并推断布局，读数带
    /// 「推断」标注，回复原始字节写入日志，方便日后精确收录。
    /// </summary>
    public static class RogBatteryReader
    {
        private const ushort VendorId = 0x0B05;
        private const ushort OmniReceiverPid = 0x1ACE;

        private const byte CmdGet = 0x12;
        private const byte SubKeyboardBattery = 0x01;
        private const byte SubMouseBattery = 0x07;
        private const byte CmdOmniPairedList = 0xA0;

        private const int ReplyTimeoutMs = 500;

        private enum ChannelKind { Interface, VendorPage, Collection }

        private sealed class Channel
        {
            public ChannelKind Kind = ChannelKind.Interface;
            public int Number;              // mi_NN（Interface / Collection 用）
            public string Collection = "";  // col01/col02/col03（Collection 用）
            public byte ReportId;
        }

        private static Channel Mi(int n) => new() { Kind = ChannelKind.Interface, Number = n };

        private static Channel VendorPage() => new() { Kind = ChannelKind.VendorPage };

        private static Channel Col(int n, string collection, byte reportId)
            => new() { Kind = ChannelKind.Collection, Number = n, Collection = collection, ReportId = reportId };

        private sealed class Model
        {
            public ushort Pid;
            public string Name = string.Empty;
            public bool Wireless;
            public bool IsMouse;

            /// <summary>OMNI 接收器上的配对设备（Pid 是配对列表里的设备 ID，不是 USB PID）。</summary>
            public bool IsOmni;

            public Channel Channel = Mi(1);

            /// <summary>回复里电量所在下标（含 report id 占位）。</summary>
            public int BatteryIndex;

            /// <summary>鼠标档位 → 百分比换算（老鼠标 0–4 档 ×25）。</summary>
            public byte Scale = 1;

            /// <summary>夜魔家族：[6] 是 0–10 档位表，百分比在 [11]。</summary>
            public bool IsFalchion;
        }

        private static readonly Channel KeyboardMi1 = Mi(1);
        private static readonly Channel KeyboardVendor = VendorPage();
        private static readonly Channel KeyboardOmni = Col(2, "col02", 0x02);
        private static readonly Channel MouseMi0 = Mi(0);
        private static readonly Channel MouseMi1 = Mi(1);
        private static readonly Channel MouseMi2 = Mi(2);
        private static readonly Channel MouseCol03 = Col(2, "col03", 0x03);

        private static Model M(ushort pid, string name, bool wireless, bool isMouse, bool isOmni,
            Channel channel, int batteryIndex, byte scale = 1, bool falchion = false)
            => new()
            {
                Pid = pid,
                Name = name,
                Wireless = wireless,
                IsMouse = isMouse,
                IsOmni = isOmni,
                Channel = channel,
                BatteryIndex = batteryIndex,
                Scale = scale,
                IsFalchion = falchion
            };

        private static readonly Model[] Models = CreateModels();

        private static Model[] CreateModels()
        {
            return new[]
            {
                // —— 键盘：自带 2.4G 接收器 / 有线 ——
                M(0x193E, "ROG 夜魔 Falchion (2.4G)", true, false, false, KeyboardMi1, 11, falchion: true),
                M(0x193C, "ROG 夜魔 Falchion (有线)", false, false, false, KeyboardMi1, 11, falchion: true),
                M(0x1B04, "ROG 夜魔 Falchion RX (有线)", false, false, false, KeyboardVendor, 11, falchion: true),
                M(0x1A85, "ROG Azoth (2.4G)", true, false, false, KeyboardMi1, 6),
                M(0x1A83, "ROG Azoth (有线)", false, false, false, KeyboardMi1, 6),
                M(0x1B42, "ROG Azoth Extreme (2.4G)", true, false, false, KeyboardMi1, 6),
                M(0x1B3F, "ROG Azoth Extreme (有线)", false, false, false, KeyboardMi1, 6),
                M(0x1C25, "ROG Azoth X (2.4G)", true, false, false, KeyboardMi1, 6),
                M(0x1C24, "ROG Azoth X (有线)", false, false, false, KeyboardMi1, 6),
                M(0x1CEF, "ROG Azoth Extreme SE (有线)", false, false, false, KeyboardMi1, 6),
                M(0x196B, "ROG Claymore II (有线)", false, false, false, KeyboardVendor, 6),
                M(0x1AAE, "ROG Strix Scope II 96 (有线)", false, false, false, KeyboardMi1, 6),
                M(0x1B78, "ROG Strix Scope II 96 RX (有线)", false, false, false, KeyboardMi1, 6),
                M(0x1A07, "ROG Strix Scope RX TKL (2.4G)", true, false, false, KeyboardVendor, 6),

                // —— 键盘：OMNI 多设备接收器 ——
                M(0x1A85, "ROG Azoth (OMNI)", true, false, true, KeyboardOmni, 6),
                M(0x1B42, "ROG Azoth Extreme (OMNI)", true, false, true, KeyboardOmni, 6),
                M(0x1CF1, "ROG Azoth Extreme SE (OMNI)", true, false, true, KeyboardOmni, 6),
                M(0x1B06, "ROG 夜魔 Falchion RX 低剖版 (OMNI)", true, false, true, KeyboardOmni, 11, falchion: true),
                M(0x1AB0, "ROG Strix Scope II 96 (OMNI)", true, false, true, KeyboardOmni, 6),
                M(0x1B7A, "ROG Strix Scope II 96 RX (OMNI)", true, false, true, KeyboardOmni, 6),

                // —— 鼠标：自带 2.4G 接收器 / 有线（电量在 [5]；老款 0–4 档位 ×25）——
                M(0x18E5, "ROG Chakram (2.4G)", true, true, false, MouseMi0, 5, scale: 25),
                M(0x18E3, "ROG Chakram (有线)", false, true, false, MouseMi0, 5, scale: 25),
                M(0x1A1A, "ROG Chakram X (2.4G)", true, true, false, MouseMi0, 5),
                M(0x1A18, "ROG Chakram X (有线)", false, true, false, MouseMi0, 5),
                M(0x18A0, "ROG Gladius II Wireless (2.4G)", true, true, false, MouseMi2, 5, scale: 25),
                M(0x197F, "ROG Gladius III Wireless (2.4G)", true, true, false, MouseMi0, 5),
                M(0x197D, "ROG Gladius III Wireless (有线)", false, true, false, MouseMi0, 5),
                M(0x1A72, "ROG Gladius III Aimpoint (2.4G)", true, true, false, MouseMi0, 5),
                M(0x1A70, "ROG Gladius III Aimpoint (有线)", false, true, false, MouseMi0, 5),
                M(0x1B0C, "ROG Gladius III Aimpoint EVA-02 (2.4G)", true, true, false, MouseMi0, 5),
                M(0x1B0A, "ROG Gladius III Aimpoint EVA-02 (有线)", false, true, false, MouseMi0, 5),
                M(0x1A94, "ROG Harpe Ace (2.4G)", true, true, false, MouseMi0, 5),
                M(0x1A92, "ROG Harpe Ace (有线)", false, true, false, MouseMi0, 5),
                M(0x1B67, "ROG Harpe Ace Extreme (有线)", false, true, false, MouseMi0, 5),
                M(0x1B63, "ROG Harpe Ace Mini (有线)", false, true, false, MouseMi0, 5),

                // 自带接收器但通道为 col03 形态（参考实现如此标注）
                M(0x1AD0, "ROG Harpe II Ace (2.4G)", true, true, false, MouseCol03, 5),
                M(0x1C69, "ROG Harpe II Ace (有线)", false, true, false, MouseMi0, 5),
                M(0x1960, "ROG Keris Wireless (2.4G)", true, true, false, MouseMi0, 5, scale: 25),
                M(0x195E, "ROG Keris Wireless (有线)", false, true, false, MouseMi0, 5, scale: 25),
                M(0x1A59, "ROG Keris Wireless EVA (2.4G)", true, true, false, MouseMi0, 5, scale: 25),
                M(0x1A57, "ROG Keris Wireless EVA (有线)", false, true, false, MouseMi0, 5, scale: 25),
                M(0x1A68, "ROG Keris Wireless Aimpoint (2.4G)", true, true, false, MouseMi0, 5),
                M(0x1A66, "ROG Keris Wireless Aimpoint (有线)", false, true, false, MouseMi0, 5),
                M(0x1B16, "ROG Keris II Ace (有线)", false, true, false, MouseMi0, 5),
                M(0x1C0C, "ROG Keris II Origin (有线)", false, true, false, MouseMi0, 5),
                M(0x1D4C, "ROG Keris II Origin KJP (有线)", false, true, false, MouseMi0, 5),
                M(0x1908, "ROG Pugio II (2.4G)", true, true, false, MouseMi0, 5, scale: 25),
                M(0x1906, "ROG Pugio II (有线)", false, true, false, MouseMi0, 5, scale: 25),
                M(0x1979, "ROG Spatha X (2.4G)", true, true, false, MouseMi0, 5),
                M(0x1977, "ROG Spatha X (有线)", false, true, false, MouseMi0, 5),
                M(0x18B4, "ROG Strix Carry (2.4G)", true, true, false, MouseMi1, 7, scale: 25),
                M(0x1949, "ROG Strix Impact II Wireless (2.4G)", true, true, false, MouseMi0, 5, scale: 25),
                M(0x1947, "ROG Strix Impact II Wireless (有线)", false, true, false, MouseMi0, 5, scale: 25),

                // —— 鼠标：OMNI 多设备接收器 ——
                M(0x1A72, "ROG Gladius III Aimpoint (OMNI)", true, true, true, MouseCol03, 5),
                M(0x1A94, "ROG Harpe Ace (OMNI)", true, true, true, MouseCol03, 5),
                M(0x1B68, "ROG Harpe Ace Extreme (OMNI)", true, true, true, MouseCol03, 5),
                M(0x1B69, "ROG Harpe Ace Extreme (OMNI)", true, true, true, MouseCol03, 5),
                M(0x1B65, "ROG Harpe Ace Mini (OMNI)", true, true, true, MouseCol03, 5),
                M(0x1A68, "ROG Keris Wireless Aimpoint (OMNI)", true, true, true, MouseCol03, 5),
                M(0x1A6A, "ROG Keris Wireless Aimpoint (OMNI)", true, true, true, MouseCol03, 5),
                M(0x1B1A, "ROG Keris II Ace (OMNI)", true, true, true, MouseCol03, 5),
                M(0x1B18, "ROG Keris II Ace (OMNI)", true, true, true, MouseCol03, 5),
                M(0x1C0E, "ROG Keris II Origin (OMNI)", true, true, true, MouseCol03, 5),
                M(0x1D4E, "ROG Keris II Origin KJP (OMNI)", true, true, true, MouseCol03, 5),
                M(0x1AD7, "ROG Strix Impact III Wireless (OMNI)", true, true, true, MouseCol03, 5),
            };
        }

        private const int ResultCacheMs = 2500;

        private static readonly object Gate = new();
        private static List<RogBatteryReading>? _cached;
        private static DateTime _cachedAtUtc = DateTime.MinValue;

        public static List<RogBatteryReading> ReadAll()
        {
            lock (Gate)
            {
                if (_cached != null &&
                    (DateTime.UtcNow - _cachedAtUtc).TotalMilliseconds < ResultCacheMs)
                {
                    return new List<RogBatteryReading>(_cached);
                }

                var result = QueryAll();
                _cached = result;
                _cachedAtUtc = DateTime.UtcNow;
                return new List<RogBatteryReading>(result);
            }
        }

        private static List<RogBatteryReading> QueryAll()
        {
            var result = new Dictionary<string, RogBatteryReading>(StringComparer.OrdinalIgnoreCase);

            var omniPaths = new List<string>();
            var directPaths = new List<(string Path, ushort Pid)>();

            foreach (string path in HidNative.EnumerateInterfacePaths())
            {
                if (path.IndexOf("vid_0b05", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                IntPtr handle = HidNative.Open(path,
                    HidNative.GenericRead | HidNative.GenericWrite, overlapped: true);
                if (handle == IntPtr.Zero)
                    continue;

                try
                {
                    if (!HidNative.TryGetAttributes(handle, out var attrs) ||
                        attrs.VendorID != VendorId)
                    {
                        continue;
                    }

                    if (attrs.ProductID == OmniReceiverPid)
                        omniPaths.Add(path);
                    else
                        directPaths.Add((path, attrs.ProductID));
                }
                finally
                {
                    HidNative.Close(handle);
                }
            }

            try
            {
                QueryOmniReceivers(result, omniPaths);
                QueryDirectDevices(result, directPaths);
            }
            catch (Exception ex)
            {
                AppLog.Write("EasyBluetooth/ROG：扫描异常", ex);
            }

            return result.Values.ToList();
        }

        // ---------- OMNI 多设备接收器 ----------

        private static void QueryOmniReceivers(
            Dictionary<string, RogBatteryReading> result, List<string> omniPaths)
        {
            foreach (var group in omniPaths.GroupBy(HidNative.InstanceKeyOf))
            {
                string instance = group.Key;
                string? col01 = group.FirstOrDefault(p => IsCollection(p, 2, "col01"));
                string? col02 = group.FirstOrDefault(p => IsCollection(p, 2, "col02"));
                string? col03 = group.FirstOrDefault(p => IsCollection(p, 2, "col03"));

                if (col01 == null)
                    continue;

                List<ushort>? paired = ReadOmniPairedList(col01);
                if (paired == null)
                {
                    AppLog.Write($"EasyBluetooth/ROG：OMNI 接收器 {instance} 配对列表读取失败");
                    continue;
                }

                AppLog.Write($"EasyBluetooth/ROG：OMNI 接收器 {instance} 配对 {paired.Count} 台设备：" +
                             string.Join(", ", paired.Select(p => $"0x{p:X4}")));

                foreach (ushort pid in paired)
                {
                    string key = $"omni:{instance}:{pid:X4}";
                    var model = Models.FirstOrDefault(m => m.IsOmni && m.Pid == pid);
                    RogBatteryReading? reading;

                    if (model != null)
                    {
                        string? channelPath = model.IsMouse ? col03 : col02;
                        reading = channelPath != null ? QueryOnPath(channelPath, model) : null;
                    }
                    else
                    {
                        reading = InferOmniReading(col02, col03, pid);
                    }

                    if (reading != null)
                        result[key] = reading;
                }
            }
        }

        /// <summary>读 OMNI 接收器的配对设备列表；失败返回 null。</summary>
        private static List<ushort>? ReadOmniPairedList(string path)
        {
            IntPtr handle = HidNative.Open(path,
                HidNative.GenericRead | HidNative.GenericWrite, overlapped: true);
            if (handle == IntPtr.Zero)
                return null;

            try
            {
                if (!HidNative.TryGetCaps(handle, out var caps) ||
                    caps.OutputReportByteLength < 8 || caps.InputReportByteLength < 12)
                {
                    return null;
                }

                var kind = Transact(handle, caps, 0x01, CmdOmniPairedList, 0x00, out byte[]? reply);
                if (kind != ReplyKind.Reply || reply == null)
                    return null;

                var pids = new List<ushort>();
                for (int offset = 5; offset + 1 < reply.Length; offset += 4)
                {
                    ushort pid = (ushort)(reply[offset] | (reply[offset + 1] << 8));
                    if (pid == 0)
                        break;
                    pids.Add(pid);
                }

                return pids;
            }
            finally
            {
                HidNative.Close(handle);
            }
        }

        /// <summary>
        /// OMNI 上未收录型号的推断：先按键盘（夜魔家族布局优先）查询，
        /// 通道完全不应答时再按鼠标查询。都是只读命令，最坏情况是设备不理你。
        /// </summary>
        private static RogBatteryReading InferOmniReading(string? col02, string? col03, ushort pid)
        {
            if (col02 != null)
            {
                var kind = TransactOnPath(col02, 0x02, SubKeyboardBattery, out byte[]? reply);

                if (kind == ReplyKind.NoAnswer)
                    return Asleep(col02, pid, $"ROG 键盘 (OMNI, PID 0x{pid:X4})",
                        "设备未应答（可能休眠或关机）");

                if (kind == ReplyKind.Reply && reply != null)
                {
                    string hex = BitConverter.ToString(reply, 0, Math.Min(16, reply.Length));
                    AppLog.Write($"EasyBluetooth/ROG：未收录型号 PID 0x{pid:X4} 键盘回复 {hex}");

                    // 充电位应是 0/1；夜魔家族：[6] 是 0–10 档位表、[11] 是百分比
                    if (reply[9] is 0 or 1)
                    {
                        if (reply[6] <= 10 && reply[11] <= 100)
                        {
                            int percent = reply[11];
                            bool charging = reply[9] == 1;
                            if (percent == 0 && !charging)
                                return Asleep(col02, pid, $"ROG 键盘 (OMNI, PID 0x{pid:X4})",
                                    "电量 0 且未充电，视为休眠/关机");

                            AppLog.Write($"EasyBluetooth/ROG：未收录型号 PID 0x{pid:X4}" +
                                         $"推断为夜魔家族键盘，电量 {percent}%（充电={charging}）");
                            return new RogBatteryReading
                            {
                                Id = col02,
                                Name = $"ROG 键盘 (OMNI, PID 0x{pid:X4})",
                                ProductId = pid,
                                IsWireless = true,
                                Percent = percent,
                                IsCharging = charging,
                                Gauge = reply[6],
                                Note = "未收录型号：按夜魔家族布局推断，读数仅供参考"
                            };
                        }

                        if (reply[6] <= 100)
                        {
                            int percent = reply[6];
                            bool charging = reply[9] == 1;
                            if (percent == 0 && !charging)
                                return Asleep(col02, pid, $"ROG 键盘 (OMNI, PID 0x{pid:X4})",
                                    "电量 0 且未充电，视为休眠/关机");

                            AppLog.Write($"EasyBluetooth/ROG：未收录型号 PID 0x{pid:X4}" +
                                         $"推断为标准布局键盘，电量 {percent}%（充电={charging}）");
                            return new RogBatteryReading
                            {
                                Id = col02,
                                Name = $"ROG 键盘 (OMNI, PID 0x{pid:X4})",
                                ProductId = pid,
                                IsWireless = true,
                                Percent = percent,
                                IsCharging = charging,
                                Note = "未收录型号：按标准键盘布局推断，读数仅供参考"
                            };
                        }
                    }

                    return new RogBatteryReading
                    {
                        Id = col02,
                        Name = $"ROG 设备 (OMNI, PID 0x{pid:X4})",
                        ProductId = pid,
                        IsWireless = true,
                        Percent = null,
                        Note = "未收录型号：回复布局未能识别（原始字节已写入日志）"
                    };
                }
            }

            if (col03 != null)
            {
                var kind = TransactOnPath(col03, 0x03, SubMouseBattery, out byte[]? reply);

                if (kind == ReplyKind.NoAnswer)
                    return Asleep(col03, pid, $"ROG 鼠标 (OMNI, PID 0x{pid:X4})",
                        "设备未应答（可能休眠或关机）");

                if (kind == ReplyKind.Reply && reply != null)
                {
                    string hex = BitConverter.ToString(reply, 0, Math.Min(16, reply.Length));
                    AppLog.Write($"EasyBluetooth/ROG：未收录型号 PID 0x{pid:X4} 鼠标回复 {hex}");

                    int percent = reply[5];
                    bool charging = reply[10] != 0;

                    if (percent <= 100)
                    {
                        if (percent == 0 && !charging)
                            return Asleep(col03, pid, $"ROG 鼠标 (OMNI, PID 0x{pid:X4})",
                                "电量 0 且未充电，视为休眠/关机");

                        AppLog.Write($"EasyBluetooth/ROG：未收录型号 PID 0x{pid:X4}" +
                                     $"推断为鼠标，电量 {percent}%（充电={charging}）");
                        return new RogBatteryReading
                        {
                            Id = col03,
                            Name = $"ROG 鼠标 (OMNI, PID 0x{pid:X4})",
                            ProductId = pid,
                            IsWireless = true,
                            Percent = percent,
                            IsCharging = charging,
                            Note = "未收录型号：按鼠标布局推断，读数仅供参考"
                        };
                    }
                }
            }

            return new RogBatteryReading
            {
                Id = col02 ?? col03 ?? string.Empty,
                Name = $"ROG 设备 (OMNI, PID 0x{pid:X4})",
                ProductId = pid,
                IsWireless = true,
                Percent = null,
                Note = "未能识别（PID 已写入日志，可反馈给开发者收录）"
            };
        }

        // ---------- 直连设备（自带接收器 / 有线） ----------

        private static void QueryDirectDevices(
            Dictionary<string, RogBatteryReading> result,
            List<(string Path, ushort Pid)> paths)
        {
            var byPid = new Dictionary<ushort, RogBatteryReading>();

            foreach ((string path, ushort pid) in paths)
            {
                IntPtr handle = HidNative.Open(path,
                    HidNative.GenericRead | HidNative.GenericWrite, overlapped: true);
                if (handle == IntPtr.Zero)
                    continue;

                try
                {
                    if (!HidNative.TryGetCaps(handle, out var caps))
                        continue;

                    foreach (var model in Models.Where(m => !m.IsOmni && m.Pid == pid))
                    {
                        if (byPid.TryGetValue(model.Pid, out var existing) && existing.Percent != null)
                            break;

                        if (!ChannelMatches(model.Channel, path, caps))
                            continue;

                        if (caps.OutputReportByteLength < 8 || caps.InputReportByteLength < 12)
                            continue;

                        var reading = QueryModel(handle, path, model, caps);
                        if (reading == null)
                            continue;

                        if (!byPid.TryGetValue(model.Pid, out var old) ||
                            (old.Percent == null && reading.Percent != null))
                        {
                            byPid[model.Pid] = reading;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("EasyBluetooth/ROG：读取设备失败", ex);
                }
                finally
                {
                    HidNative.Close(handle);
                }
            }

            foreach (var pair in byPid)
                result[$"pid:{pair.Key:X4}"] = pair.Value;
        }

        // ---------- 查询与解析 ----------

        private static RogBatteryReading? QueryOnPath(string path, Model model)
        {
            IntPtr handle = HidNative.Open(path,
                HidNative.GenericRead | HidNative.GenericWrite, overlapped: true);
            if (handle == IntPtr.Zero)
                return null;

            try
            {
                if (!HidNative.TryGetCaps(handle, out var caps) ||
                    caps.OutputReportByteLength < 8 || caps.InputReportByteLength < 12)
                {
                    return null;
                }

                return QueryModel(handle, path, model, caps);
            }
            finally
            {
                HidNative.Close(handle);
            }
        }

        private static RogBatteryReading? QueryModel(
            IntPtr handle, string path, Model model, HidNative.HidpCaps caps)
        {
            byte sub = model.IsMouse ? SubMouseBattery : SubKeyboardBattery;
            var kind = Transact(handle, caps, model.Channel.ReportId, CmdGet, sub, out byte[]? reply);

            if (kind == ReplyKind.NoAnswer)
                return Asleep(path, model, "设备未应答（可能休眠或关机）");

            if (kind != ReplyKind.Reply || reply == null)
                return Asleep(path, model, "设备未应答（可能休眠，动一下设备）");

            if (model.IsMouse)
            {
                int percent = Math.Min(reply[model.BatteryIndex] * model.Scale, 100);
                bool charging = reply[10] != 0;

                if (percent == 0 && !charging)
                    return Asleep(path, model, "电量 0 且未充电，视为休眠/关机");

                AppLog.Write($"EasyBluetooth/ROG：{model.Name} 电量 {percent}%（充电={charging}）");
                return new RogBatteryReading
                {
                    Id = path,
                    Name = model.Name,
                    ProductId = model.Pid,
                    IsWireless = model.Wireless,
                    Percent = percent,
                    IsCharging = charging
                };
            }
            else
            {
                int percent = reply[model.BatteryIndex];
                bool charging = reply[9] == 1;

                if (percent > 100)
                    return Asleep(path, model, "回复形状不符（可能不是控制集合）");

                if (percent == 0 && !charging)
                    return Asleep(path, model, "电量 0 且未充电，视为休眠/关机");

                int gauge = model.IsFalchion ? reply[6] : -1;

                AppLog.Write($"EasyBluetooth/ROG：{model.Name} 电量 {percent}%（充电={charging}）");
                return new RogBatteryReading
                {
                    Id = path,
                    Name = model.Name,
                    ProductId = model.Pid,
                    IsWireless = model.Wireless,
                    Percent = percent,
                    IsCharging = charging,
                    Gauge = gauge
                };
            }
        }

        private enum ReplyKind { Reply, NoAnswer, Timeout }

        /// <summary>
        /// 发 [reportId, cmd, sub] 并等回复。回复首字节是 report id（无编号报告为 0），
        /// 命令回显在 [1]/[2]；接收器代答的无应答标记是 [1]=0xFF [2]=0xAA。
        /// </summary>
        private static ReplyKind Transact(
            IntPtr handle, HidNative.HidpCaps caps, byte reportId, byte cmd, byte sub,
            out byte[]? reply)
        {
            reply = null;

            var packet = new byte[caps.OutputReportByteLength];
            packet[0] = reportId;
            packet[1] = cmd;
            packet[2] = sub;

            if (!HidNative.WriteReport(handle, packet, ReplyTimeoutMs))
                return ReplyKind.Timeout;

            var buffer = new byte[caps.InputReportByteLength];
            long deadline = Environment.TickCount64 + ReplyTimeoutMs;

            while (Environment.TickCount64 < deadline)
            {
                int left = (int)(deadline - Environment.TickCount64);
                int n = HidNative.ReadReport(handle, buffer, left);
                if (n <= 0)
                    break;
                if (n < 12)
                    continue;   // 别的输入报告共用这个接口，跳过

                if (buffer[1] == 0xFF && buffer[2] == 0xAA)
                    return ReplyKind.NoAnswer;

                if (buffer[1] == cmd && buffer[2] == sub)
                {
                    var match = new byte[buffer.Length];
                    Array.Copy(buffer, match, buffer.Length);
                    reply = match;
                    return ReplyKind.Reply;
                }
            }

            // 超时收尾：尽力清掉残留数据，避免影响同一句柄的下一次查询
            HidNative.ReadReport(handle, buffer, 1);
            return ReplyKind.Timeout;
        }

        private static ReplyKind TransactOnPath(
            string path, byte reportId, byte sub, out byte[]? reply)
        {
            IntPtr handle = HidNative.Open(path,
                HidNative.GenericRead | HidNative.GenericWrite, overlapped: true);
            if (handle == IntPtr.Zero)
            {
                reply = null;
                return ReplyKind.Timeout;
            }

            try
            {
                if (!HidNative.TryGetCaps(handle, out var caps) ||
                    caps.OutputReportByteLength < 8 || caps.InputReportByteLength < 12)
                {
                    reply = null;
                    return ReplyKind.Timeout;
                }

                return Transact(handle, caps, reportId, CmdGet, sub, out reply);
            }
            finally
            {
                HidNative.Close(handle);
            }
        }

        private static RogBatteryReading Asleep(string path, Model model, string note)
            => Asleep(path, model.Pid, model.Name, model.Wireless, note);

        private static RogBatteryReading Asleep(string path, ushort pid, string name, bool wireless, string note)
            => new()
            {
                Id = path,
                Name = name,
                ProductId = pid,
                IsWireless = wireless,
                Percent = null,
                Note = note
            };

        /// <summary>OMNI 配对设备默认按无线处理的重载。</summary>
        private static RogBatteryReading Asleep(string path, ushort pid, string name, string note)
            => Asleep(path, pid, name, wireless: true, note);

        // ---------- 通道匹配 ----------

        private static bool ChannelMatches(Channel channel, string path, HidNative.HidpCaps caps)
        {
            switch (channel.Kind)
            {
                case ChannelKind.Interface:
                    return HidNative.InterfaceNumberOf(path) == channel.Number;

                case ChannelKind.VendorPage:
                    return caps.UsagePage == 0xFF00;

                case ChannelKind.Collection:
                    return HidNative.InterfaceNumberOf(path) == channel.Number
                        && string.Equals(HidNative.CollectionNameOf(path), channel.Collection,
                            StringComparison.OrdinalIgnoreCase);

                default:
                    return false;
            }
        }

        private static bool IsCollection(string path, int interfaceNumber, string collection)
            => HidNative.InterfaceNumberOf(path) == interfaceNumber
                && string.Equals(HidNative.CollectionNameOf(path), collection,
                    StringComparison.OrdinalIgnoreCase);
    }
}