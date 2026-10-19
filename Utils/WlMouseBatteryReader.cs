using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;

namespace GameHub.Utils
{
    /// <summary>WLMouse（万灵）设备的一次电量读数。</summary>
    public sealed class WlMouseBatteryReading
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public ushort ProductId { get; init; }
        public bool IsWireless { get; init; }

        /// <summary>电量百分比；null = 读不到。</summary>
        public int? Percent { get; init; }

        public bool IsCharging { get; init; }

        public string Note { get; init; } = string.Empty;
    }

    /// <summary>
    /// WLMouse（万灵 / 花兽）鼠标电量读取（厂商私有 HID 协议）。
    ///
    /// 协议来源（两个独立开源实现，互相印证）：
    ///   · mee7ya/wlmouse-cli（Rust，2023）：Beast X 8K 家族走 feature report，
    ///     电量在回复 [8]（本文件缓冲下标，含 report id 占位）；
    ///   · Creationsss/wl-mouse（Rust，2026，持续维护）：全型号 feature report
    ///     协议 + 充电状态，还解出了 hid_index（回复整体左右偏 1 字节，因型号而异）。
    ///
    /// 本实现按两者公开结论合并（缓冲下标按「[0] 是 report id」计）：
    ///   · VID 0x36A7；feature report [0]=report id 0，
    ///     电量查询命令字节在 [3]=0x02 [4]=0x02 [6]=0x83；
    ///   · 回复：OK 标记 0xA1 —— hid_index=0 在 [2]、hid_index=1 在 [1]；
    ///     命令回显 0x83 固定在 [6]；休眠标记 0xA0 与 OK 同位；
    ///   · 电量与充电位在 [8-hid_index] / [9-hid-index]：两个字节里 >1 的那个是
    ///     百分比、另一个是充电标志（wl-mouse 的顺序启发式）；
    ///   · 老款 Beast X（非 8K：接收器 0xA887 / 有线 0xA888）不走 feature report，
    ///     而是输出报告 [0x04,0,0,0x1A] + 输入报告回复（wlmouse-cli 逆向）。
    ///
    /// 型号表来自 wl-mouse（2026-10）：覆盖花兽（Beast）家族 2024–2026 全系
    /// （花兽标准版Pro ≈ Beast X Pro，花兽Max ≈ Beast Max，花兽Mini ≈ Beast Mini 等）。
    /// 未收录的新型号走同一套 feature 协议试探，只在回复完全校验通过时列出。
    /// </summary>
    public static class WlMouseBatteryReader
    {
        private const ushort VendorId = 0x36A7;

        private const byte ResponseOk = 0xA1;
        private const byte ResponseSleeping = 0xA0;
        private const byte CmdBattery = 0x83;

        private sealed class Model
        {
            public ushort Pid;
            public string Name = string.Empty;
            public bool Wireless;

            /// <summary>接收器/有线同款归并用。</summary>
            public string Family = string.Empty;

            /// <summary>false = 老款 Beast X 的中断端点协议。</summary>
            public bool FeatureChannel = true;
        }

        private static Model M(ushort pid, string name, bool wireless, string family, bool feature = true)
            => new() { Pid = pid, Name = name, Wireless = wireless, Family = family, FeatureChannel = feature };

        private static readonly Model[] Models =
        {
            // 花兽（Beast）家族
            M(0xA882, "WLMouse Beast X 花兽 (1K 接收器)", true, "beastx"),
            M(0xA883, "WLMouse Beast X 花兽 (8K 接收器)", true, "beastx"),
            M(0xA884, "WLMouse Beast X 花兽 (有线)", false, "beastx"),
            M(0xA870, "WLMouse Beast X Pro 花兽Pro (接收器)", true, "beastxpro"),
            M(0xA871, "WLMouse Beast X Pro 花兽Pro (有线)", false, "beastxpro"),
            M(0xA880, "WLMouse Beast Max 花兽Max (接收器)", true, "beastmax"),
            M(0xA881, "WLMouse Beast Max 花兽Max (有线)", false, "beastmax"),
            M(0xA885, "WLMouse Beast Mini 花兽Mini (接收器)", true, "beastmini"),
            M(0xA886, "WLMouse Beast Mini 花兽Mini (有线)", false, "beastmini"),
            M(0xA868, "WLMouse Beast Mini Pro 花兽MiniPro (接收器)", true, "beastminipro"),
            M(0xA869, "WLMouse Beast Mini Pro 花兽MiniPro (有线)", false, "beastminipro"),

            // 其他在售型号
            M(0xA872, "WLMouse Strider (接收器)", true, "strider"),
            M(0xA873, "WLMouse Strider (有线)", false, "strider"),
            M(0xA874, "WLMouse Ying (接收器)", true, "ying"),
            M(0xA875, "WLMouse Ying (有线)", false, "ying"),
            M(0xA878, "WLMouse Sword X (接收器)", true, "swordx"),
            M(0xA879, "WLMouse Sword X (有线)", false, "swordx"),
            M(0xA866, "WLMouse Beast Miao (接收器)", true, "miao"),
            M(0xA867, "WLMouse Beast Miao (有线)", false, "miao"),
            M(0xA863, "WLMouse Huan (接收器)", true, "huan"),
            M(0xA864, "WLMouse Huan (有线)", false, "huan"),

            // 老款 Beast X（非 8K）：中断端点协议
            M(0xA887, "WLMouse Beast X 老款 (接收器)", true, "beastx-classic", feature: false),
            M(0xA888, "WLMouse Beast X 老款 (有线)", false, "beastx-classic", feature: false),
        };

        private const int ResultCacheMs = 2500;

        private static readonly object Gate = new();
        private static List<WlMouseBatteryReading>? _cached;
        private static DateTime _cachedAtUtc = DateTime.MinValue;

        public static List<WlMouseBatteryReading> ReadAll()
        {
            lock (Gate)
            {
                if (_cached != null &&
                    (DateTime.UtcNow - _cachedAtUtc).TotalMilliseconds < ResultCacheMs)
                {
                    return new List<WlMouseBatteryReading>(_cached);
                }

                var result = QueryAll();
                _cached = result;
                _cachedAtUtc = DateTime.UtcNow;
                return new List<WlMouseBatteryReading>(result);
            }
        }

        private static List<WlMouseBatteryReading> QueryAll()
        {
            var byPid = new Dictionary<ushort, WlMouseBatteryReading>();

            foreach (string path in HidNative.EnumerateInterfacePaths())
            {
                if (path.IndexOf("vid_36a7", StringComparison.OrdinalIgnoreCase) < 0)
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

                    if (!HidNative.TryGetCaps(handle, out var caps))
                        continue;

                    var model = FindModel(attrs.ProductID);

                    WlMouseBatteryReading? reading;
                    if (model == null)
                    {
                        // 未收录型号：只在协议校验完全通过时才列出，避免把无关接口当成设备
                        reading = QueryFeatureUnknown(handle, path, attrs.ProductID, caps);
                    }
                    else
                    {
                        if (byPid.TryGetValue(model.Pid, out var existing) && existing.Percent != null)
                            continue;

                        reading = model.FeatureChannel
                            ? QueryFeature(handle, path, model, caps)
                            : QueryReport(handle, path, model, caps);
                    }

                    if (reading == null)
                        continue;

                    if (!byPid.TryGetValue(reading.ProductId, out var old) ||
                        (old.Percent == null && reading.Percent != null))
                    {
                        byPid[reading.ProductId] = reading;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("EasyBluetooth/WLMouse：读取设备失败", ex);
                }
                finally
                {
                    HidNative.Close(handle);
                }
            }

            // 「接收器 + 有线」同时在线（插线充电）时只保留一行：优先有读数的，其次无线
            var byFamily = new Dictionary<string, WlMouseBatteryReading>();
            foreach (var reading in byPid.Values)
            {
                string? family = FindModel(reading.ProductId)?.Family;
                string key = family ?? $"pid:{reading.ProductId:X4}";

                if (!byFamily.TryGetValue(key, out var old) || FamilyScore(reading) > FamilyScore(old))
                    byFamily[key] = reading;
            }

            return byFamily.Values.ToList();
        }

        private static int FamilyScore(WlMouseBatteryReading r)
            => (r.Percent != null ? 2 : 0) + (r.IsWireless ? 1 : 0);

        private static Model? FindModel(ushort productId)
        {
            foreach (var m in Models)
            {
                if (m.Pid == productId)
                    return m;
            }
            return null;
        }

        private enum FeatureResult { Ok, Sleeping, NotThisChannel }

        /// <summary>
        /// 新版 feature report 协议（wl-mouse，2026；命令字节兼容 wlmouse-cli 老实现）。
        /// </summary>
        private static FeatureResult TryQueryFeature(
            IntPtr handle, HidNative.HidpCaps caps, out int percent, out bool charging)
        {
            percent = 0;
            charging = false;

            int len = caps.FeatureReportByteLength;
            if (len < 16)
                return FeatureResult.NotThisChannel;   // 不是控制通道

            var request = new byte[len];
            request[0] = 0x00;   // report id 0
            request[3] = 0x02;
            request[4] = 0x02;
            request[5] = 0x00;
            request[6] = CmdBattery;

            if (!HidNative.SetFeature(handle, request))
                return FeatureResult.NotThisChannel;

            Thread.Sleep(50);   // 参考实现 30–100ms：给设备一点准备时间

            var reply = new byte[len];
            reply[0] = 0x00;
            if (!HidNative.GetFeature(handle, reply))
                return FeatureResult.Sleeping;   // 命令已受理但没有回复：设备大概率休眠

            // hid_index=0：OK 标记在 [2]；hid_index=1：在 [1]；命令回显固定在 [6]
            int hidIndex;
            if (reply[2] == ResponseOk && reply[6] == CmdBattery)
                hidIndex = 0;
            else if (reply[1] == ResponseOk && reply[6] == CmdBattery)
                hidIndex = 1;
            else if (reply[1] == ResponseSleeping || reply[2] == ResponseSleeping)
                return FeatureResult.Sleeping;
            else
                return FeatureResult.NotThisChannel;

            // 电量与充电位在 [8-hid] / [9-hid]；>1 的那个是百分比（wl-mouse 顺序启发式）
            int a = reply[8 - hidIndex];
            int b = reply[9 - hidIndex];
            if (a > 1)
            {
                percent = a;
                charging = b != 0;
            }
            else
            {
                percent = b;
                charging = a != 0;
            }

            if (percent > 100)
                return FeatureResult.NotThisChannel;

            return FeatureResult.Ok;
        }

        private static WlMouseBatteryReading? QueryFeature(
            IntPtr handle, string path, Model model, HidNative.HidpCaps caps)
        {
            switch (TryQueryFeature(handle, caps, out int percent, out bool charging))
            {
                case FeatureResult.Ok:
                    AppLog.Write($"EasyBluetooth/WLMouse：{model.Name} 电量 {percent}%（充电={charging}）");
                    return new WlMouseBatteryReading
                    {
                        Id = path,
                        Name = model.Name,
                        ProductId = model.Pid,
                        IsWireless = model.Wireless,
                        Percent = percent,
                        IsCharging = charging
                    };

                case FeatureResult.Sleeping:
                    return Asleep(path, model);

                default:
                    return null;   // 这个接口不认协议，换下一个
            }
        }

        /// <summary>未收录型号：只有协议校验完全通过才列出。</summary>
        private static WlMouseBatteryReading? QueryFeatureUnknown(
            IntPtr handle, string path, ushort pid, HidNative.HidpCaps caps)
        {
            if (TryQueryFeature(handle, caps, out int percent, out bool charging) != FeatureResult.Ok)
                return null;

            AppLog.Write($"EasyBluetooth/WLMouse：未收录型号 PID 0x{pid:X4} 电量 {percent}%（充电={charging}）");
            return new WlMouseBatteryReading
            {
                Id = path,
                Name = $"WLMouse 未收录型号 (PID 0x{pid:X4})",
                ProductId = pid,
                IsWireless = true,
                Percent = percent,
                IsCharging = charging,
                Note = "未收录型号：协议校验通过，读数应可用"
            };
        }

        /// <summary>老款 Beast X（非 8K）：输出报告 + 输入报告回复。该协议无充电状态，如实置 false。</summary>
        private static WlMouseBatteryReading? QueryReport(
            IntPtr handle, string path, Model model, HidNative.HidpCaps caps)
        {
            if (caps.OutputReportByteLength < 8 || caps.InputReportByteLength < 16)
                return null;

            var packet = new byte[caps.OutputReportByteLength];
            packet[0] = 0x04;   // report id 4
            packet[3] = 0x1A;

            if (!HidNative.WriteReport(handle, packet, 500))
                return Asleep(path, model);

            var buffer = new byte[caps.InputReportByteLength];
            int n = HidNative.ReadReport(handle, buffer, 500);
            if (n < 9)
                return Asleep(path, model);

            int percent = buffer[8];
            if (percent > 100)
                return Asleep(path, model);

            AppLog.Write($"EasyBluetooth/WLMouse：{model.Name} 电量 {percent}%");
            return new WlMouseBatteryReading
            {
                Id = path,
                Name = model.Name,
                ProductId = model.Pid,
                IsWireless = model.Wireless,
                Percent = percent
            };
        }

        private static WlMouseBatteryReading Asleep(string path, Model model)
            => new()
            {
                Id = path,
                Name = model.Name,
                ProductId = model.Pid,
                IsWireless = model.Wireless,
                Percent = null,
                Note = "设备未应答（可能休眠，动一下鼠标）"
            };
    }
}