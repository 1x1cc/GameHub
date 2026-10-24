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

        /// <summary>
        /// 这一行是「接收器状态」而不是一台设备（例如：OMNI 接收器在线，
        /// 但配对列表为空 → 键盘没连上）。UI 里用不同分组/图标展示。
        /// </summary>
        public bool IsReceiverStatus { get; init; }
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
    ///   · OMNI 多设备接收器（PID 0x1ACE）：转发通道 mi_02/col02（report id 0x02）接键盘、
    ///     mi_02/col03（report id 0x03）接鼠标——接收器把 [0x12, 0x01/0x07] 转给设备并代答电量。
    ///     【设备在不在线，只看这两条通道有没有应答】。控制集合 mi_02/col01 上另有接收器
    ///     自答命令（0xA0 子命令 0x00 曾被当成“配对列表”），但 2026-10-20 实机确认：
    ///     键盘经接收器正常打字、电量也读得到时，它照样回全 0。所以它只能当型号命名提示，
    ///     绝不能当在线判据（早期版本就是拿它判在线，才会出现“能打字却显示未连接”）。
    ///
    /// 未收录型号（如 2025 年末的新品）出现在 OMNI 配对列表里时，会按
    /// 「键盘（夜魔家族布局优先）→ 鼠标」的顺序用只读查询试探并推断布局，读数带
    /// 「推断」标注，回复原始字节写入日志，方便日后精确收录。
    ///
    /// 实机验证记录（本机 ROG OMNI RECEIVER + ROG AZOTH X，2026-10）：
    ///   · 接收器自答命令（不转发给设备）：mi_02/col01 rid 0x01 cmd 0xA0 sub 0x00；
    ///     cmd 0xA1 sub 0x00 取接收器序列号（ASCII T5MPKRD01SGN），sub 0x01 取状态（08 00 07）。
    ///   · 键盘 2.4G 经接收器（2026-10-20 实测，设备正在打字时）：发 02 12 01 到 col02 →
    ///     回 02 12 01 00 00 00 64 02 01 00 14 64 EB 10 …，即 [6]=0x64=100%、[9]=0 未充电；
    ///     同一时刻 col03（鼠标）回 FF AA（没有鼠标），而 cmd 0xA0 sub 0x00 回全 0
    ///     ——所以「配对列表」不能用来判断设备在不在线。
    ///   · col02（0xFF00，配 report id 0x02）与 col03（0xFF01，rid 0x03）会转发命令；
    ///     设备不在线时接收器代答 [1]=0xFF [2]=0xAA（设备未应答），此时任何子命令都读不到电量。
    ///   · 键盘有线直连（VID_0B05/PID_1C24）时走 mi_01（usage 0xFF00，65 字节报告，
    ///     无 report id）：发 00 12 01 → 回 00 12 01 00 00 00 63 02 01 01 0A 63 …，
    ///     即 [6]=99%、[9]=1 充电中（Azoth 布局）。
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

        private static Channel Mi(int n, byte reportId)
            => new() { Kind = ChannelKind.Interface, Number = n, ReportId = reportId };

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

        // 补齐（参考 rog-2.4g-plugin）：直连鼠标走 mi_00 · rid 0x03；
        // 无线键盘自有接收器走 mi_02&col02 · rid 0x02。
        private static readonly Channel MouseMi0Rid3 = Mi(0, 0x03);
        private static readonly Channel Keyboard24G = Col(2, "col02", 0x02);

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
                // 实机验证（ROG OMNI RECEIVER VID_0B05/PID_1ACE，序列号 T5MPKRD01SGN）：
                // 配对列表回 0x1C25 = ROG AZOTH X（无线）；插线后 VID_0B05/PID_1C24 = ROG AZOTH X（有线）。
                // 有线通道 mi_01（usage 0xFF00）回包：
                //   00 12 01 00 00 00 63 02 01 01 0A 63 … → [6]=0x63=99%、[9]=1（充电中）
                // 与 Azoth 布局一致（[6] 百分比、[9] 充电位）。
                M(0x1C25, "ROG Azoth X (OMNI)", true, false, true, KeyboardOmni, 6),
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

                // —— 补齐：参考实现 rog-2.4g-plugin 全量鼠标表（mi_00 · rid 0x03）——
                M(0x17E0, "ROG Strix Impact", true, true, false, MouseMi0Rid3, 5),
                M(0x1807, "ROG Strix Evolve", true, true, false, MouseMi0Rid3, 5),
                M(0x1822, "ROG Strix Impact II", true, true, false, MouseMi0Rid3, 5),
                M(0x1837, "ROG Strix Impact II Electro Punk", true, true, false, MouseMi0Rid3, 5),
                M(0x183D, "ROG Pugio", true, true, false, MouseMi0Rid3, 5),
                M(0x1845, "ROG Gladius II", true, true, false, MouseMi0Rid3, 5),
                M(0x1846, "ROG Pugio", true, true, false, MouseMi0Rid3, 5),
                M(0x1847, "ROG Strix Impact II", true, true, false, MouseMi0Rid3, 5),
                M(0x184A, "ROG Strix Impact III", true, true, false, MouseMi0Rid3, 5),
                M(0x1854, "ROG TUF M4 Wireless", true, true, false, MouseMi0Rid3, 5),
                M(0x185B, "ROG Strix Carry", true, true, false, MouseMi0Rid3, 5),
                M(0x1866, "ROG Gladius II Origin Pink", true, true, false, MouseMi0Rid3, 5),
                M(0x1869, "ROG Gladius II Origin", true, true, false, MouseMi0Rid3, 5),
                M(0x1877, "ROG Gladius II", true, true, false, MouseMi0Rid3, 5),
                M(0x1898, "ROG Spatha", true, true, false, MouseMi0Rid3, 5),
                M(0x18C6, "ROG Strix", true, true, false, MouseMi0Rid3, 5),
                M(0x18CD, "ROG Gladius II", true, true, false, MouseMi0Rid3, 5),
                M(0x18E1, "ROG Strix Impact II (Wireless)", true, true, false, MouseMi0Rid3, 5),
                M(0x1910, "ROG Spatha X", true, true, false, MouseMi0Rid3, 5),
                M(0x1958, "ROG Chakram", true, true, false, MouseMi0Rid3, 5),
                M(0x195C, "ROG Keris", true, true, false, MouseMi0Rid3, 5),
                M(0x197B, "ROG Gladius III", true, true, false, MouseMi0Rid3, 5),
                M(0x19B6, "ROG Harpe Ace AimLab Edition", true, true, false, MouseMi0Rid3, 5),
                M(0x19F4, "ROG TUF M3", true, true, false, MouseMi0Rid3, 5),
                M(0x1A00, "ROG Gladius III", true, true, false, MouseMi0Rid3, 5),
                M(0x1A01, "ROG Gladius III", true, true, false, MouseMi0Rid3, 5),
                M(0x1A02, "ROG Gladius III", true, true, false, MouseMi0Rid3, 5),
                M(0x1A03, "ROG Spatha", true, true, false, MouseMi0Rid3, 5),
                M(0x1A30, "ROG Strix", true, true, false, MouseMi0Rid3, 5),
                M(0x1A88, "ROG Azoth", true, true, false, MouseMi0Rid3, 5),
                M(0x1A8D, "ROG TUF M3 Gen 2", true, true, false, MouseMi0Rid3, 5),
                M(0x1A9B, "ROG Spatha X", true, true, false, MouseMi0Rid3, 5),
                M(0x1ABE, "ROG TUF M5 Wireless", true, true, false, MouseMi0Rid3, 5),
                M(0x1AF3, "ROG TUF M4", true, true, false, MouseMi0Rid3, 5),
                M(0x1AF5, "ROG TUF M4", true, true, false, MouseMi0Rid3, 5),
                M(0x1B22, "ROG Harpe Ace Extreme", true, true, false, MouseMi0Rid3, 5),
                M(0x1B23, "ROG Harpe Ace Mini", true, true, false, MouseMi0Rid3, 5),
                M(0x1B2C, "ROG Strix Impact III Wireless", true, true, false, MouseMi0Rid3, 5),
                M(0x1B4C, "ROG Keris Wireless AimPoint", true, true, false, MouseMi0Rid3, 5),
                M(0x1B6E, "ROG Gladius III AimPoint (Wireless)", true, true, false, MouseMi0Rid3, 5),
                M(0x1C5A, "天选mini初音未来版", true, true, false, MouseMi0Rid3, 5),
                M(0x8502, "ROG Strix", true, true, false, MouseMi0Rid3, 5),
                M(0x8854, "ROG Strix", true, true, false, MouseMi0Rid3, 5),

                // —— 补齐：参考实现 rog-2.4g-plugin 全量无线键盘表（mi_02&col02 · rid 0x02）——
                M(0x1835, "ROG 键盘 (PID 0x1835)", true, false, false, Keyboard24G, 6),
                M(0x1840, "ROG 键盘 (PID 0x1840)", true, false, false, Keyboard24G, 6),
                M(0x1841, "ROG 键盘 (PID 0x1841)", true, false, false, Keyboard24G, 6),
                M(0x1842, "ROG 键盘 (PID 0x1842)", true, false, false, Keyboard24G, 6),
                M(0x1843, "ROG 键盘 (PID 0x1843)", true, false, false, Keyboard24G, 6),
                M(0x184D, "ROG 键盘 (PID 0x184D)", true, false, false, Keyboard24G, 6),
                M(0x1864, "ROG 键盘 (PID 0x1864)", true, false, false, Keyboard24G, 6),
                M(0x1865, "ROG 键盘 (PID 0x1865)", true, false, false, Keyboard24G, 6),
                M(0x1866, "ROG 键盘 (PID 0x1866)", true, false, false, Keyboard24G, 6),
                M(0x1875, "ROG 键盘 (PID 0x1875)", true, false, false, Keyboard24G, 6),
                M(0x1899, "ROG 键盘 (PID 0x1899)", true, false, false, Keyboard24G, 6),
                M(0x18A6, "ROG 键盘 (PID 0x18A6)", true, false, false, Keyboard24G, 6),
                M(0x18A7, "ROG 键盘 (PID 0x18A7)", true, false, false, Keyboard24G, 6),
                M(0x18A8, "ROG 键盘 (PID 0x18A8)", true, false, false, Keyboard24G, 6),
                M(0x18AA, "ROG 键盘 (PID 0x18AA)", true, false, false, Keyboard24G, 6),
                M(0x18AF, "ROG 键盘 (PID 0x18AF)", true, false, false, Keyboard24G, 6),
                M(0x18CF, "ROG 键盘 (PID 0x18CF)", true, false, false, Keyboard24G, 6),
                M(0x18F8, "ROG 键盘 (PID 0x18F8)", true, false, false, Keyboard24G, 6),
                M(0x1904, "ROG 键盘 (PID 0x1904)", true, false, false, Keyboard24G, 6),
                M(0x1905, "ROG 键盘 (PID 0x1905)", true, false, false, Keyboard24G, 6),
                M(0x190C, "ROG 键盘 (PID 0x190C)", true, false, false, Keyboard24G, 6),
                M(0x1911, "ROG 键盘 (PID 0x1911)", true, false, false, Keyboard24G, 6),
                M(0x1912, "ROG 键盘 (PID 0x1912)", true, false, false, Keyboard24G, 6),
                M(0x1913, "ROG 键盘 (PID 0x1913)", true, false, false, Keyboard24G, 6),
                M(0x1914, "ROG 键盘 (PID 0x1914)", true, false, false, Keyboard24G, 6),
                M(0x1945, "ROG 键盘 (PID 0x1945)", true, false, false, Keyboard24G, 6),
                M(0x194B, "ROG 键盘 (PID 0x194B)", true, false, false, Keyboard24G, 6),
                M(0x1951, "ROG 键盘 (PID 0x1951)", true, false, false, Keyboard24G, 6),
                M(0x1954, "ROG 键盘 (PID 0x1954)", true, false, false, Keyboard24G, 6),
                M(0x19F6, "ROG 键盘 (PID 0x19F6)", true, false, false, Keyboard24G, 6),
                M(0x19F8, "ROG 键盘 (PID 0x19F8)", true, false, false, Keyboard24G, 6),
                M(0x19FC, "ROG 键盘 (PID 0x19FC)", true, false, false, Keyboard24G, 6),
                M(0x19FE, "ROG 键盘 (PID 0x19FE)", true, false, false, Keyboard24G, 6),
                M(0x1A05, "ROG 键盘 (PID 0x1A05)", true, false, false, Keyboard24G, 6),
                M(0x1A2D, "ROG 键盘 (PID 0x1A2D)", true, false, false, Keyboard24G, 6),
                M(0x1A2E, "ROG 键盘 (PID 0x1A2E)", true, false, false, Keyboard24G, 6),
                M(0x1A2F, "ROG 键盘 (PID 0x1A2F)", true, false, false, Keyboard24G, 6),
                M(0x1A30, "ROG 键盘 (PID 0x1A30)", true, false, false, Keyboard24G, 6),
                M(0x1A41, "ROG 键盘 (PID 0x1A41)", true, false, false, Keyboard24G, 6),
                M(0x1A42, "ROG 键盘 (PID 0x1A42)", true, false, false, Keyboard24G, 6),
                M(0x1A43, "ROG 键盘 (PID 0x1A43)", true, false, false, Keyboard24G, 6),
                M(0x1A44, "ROG 键盘 (PID 0x1A44)", true, false, false, Keyboard24G, 6),
                M(0x1A86, "ROG 键盘 (PID 0x1A86)", true, false, false, Keyboard24G, 6),
                M(0x1A87, "ROG 键盘 (PID 0x1A87)", true, false, false, Keyboard24G, 6),
                M(0x1A88, "ROG 键盘 (PID 0x1A88)", true, false, false, Keyboard24G, 6),
                M(0x1A89, "ROG 键盘 (PID 0x1A89)", true, false, false, Keyboard24G, 6),
                M(0x1A9D, "ROG 键盘 (PID 0x1A9D)", true, false, false, Keyboard24G, 6),
                M(0x1A9E, "ROG 键盘 (PID 0x1A9E)", true, false, false, Keyboard24G, 6),
                M(0x1A9F, "ROG 键盘 (PID 0x1A9F)", true, false, false, Keyboard24G, 6),
                M(0x1AA0, "ROG 键盘 (PID 0x1AA0)", true, false, false, Keyboard24G, 6),
                M(0x1AA6, "ROG 键盘 (PID 0x1AA6)", true, false, false, Keyboard24G, 6),
                M(0x1AA7, "ROG 键盘 (PID 0x1AA7)", true, false, false, Keyboard24G, 6),
                M(0x1AA8, "ROG 键盘 (PID 0x1AA8)", true, false, false, Keyboard24G, 6),
                M(0x1AB3, "ROG 键盘 (PID 0x1AB3)", true, false, false, Keyboard24G, 6),
                M(0x1AB5, "ROG 键盘 (PID 0x1AB5)", true, false, false, Keyboard24G, 6),
                M(0x1B12, "ROG 键盘 (PID 0x1B12)", true, false, false, Keyboard24G, 6),
                M(0x1B30, "ROG 键盘 (PID 0x1B30)", true, false, false, Keyboard24G, 6),
                M(0x1B71, "ROG 键盘 (PID 0x1B71)", true, false, false, Keyboard24G, 6),
                M(0x1B72, "ROG 键盘 (PID 0x1B72)", true, false, false, Keyboard24G, 6),
                M(0x1B73, "ROG 键盘 (PID 0x1B73)", true, false, false, Keyboard24G, 6),
                M(0x1B74, "ROG 键盘 (PID 0x1B74)", true, false, false, Keyboard24G, 6),
                M(0x1B81, "ROG 键盘 (PID 0x1B81)", true, false, false, Keyboard24G, 6),
                M(0x1B82, "ROG 键盘 (PID 0x1B82)", true, false, false, Keyboard24G, 6),
                M(0x1B83, "ROG 键盘 (PID 0x1B83)", true, false, false, Keyboard24G, 6),
                M(0x1B84, "ROG 键盘 (PID 0x1B84)", true, false, false, Keyboard24G, 6),
                M(0x1C5E, "ROG 键盘 (PID 0x1C5E)", true, false, false, Keyboard24G, 6),
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

                // 配对列表只用来给读数配型号名（认得的 PID → 正式型号名），
                // 【2026-10-20 实机踩坑】键盘经接收器正常打字、电量也读得到时，
                // 这条命令照样可能回全 0（当时用户看到的就是“能打字却显示未连接”）。
                // 所以：配对列表为空 ≠ 设备没连上，它绝不能当在线判据；
                // 在不在线只看 col02 / col03 转发通道有没有应答。
                List<ushort>? paired = ReadOmniPairedList(col01);
                var registered = paired ?? new List<ushort>();

                AppLog.Write(registered.Count > 0
                    ? $"EasyBluetooth/ROG：OMNI 接收器 {instance} 配对列表：" +
                      string.Join(", ", registered.Select(p => $"0x{p:X4}"))
                    : $"EasyBluetooth/ROG：OMNI 接收器 {instance} 配对列表为空或读取失败" +
                      "（该结果不作为在线判据，继续直读转发通道）");

                var rows = new List<RogBatteryReading>();
                var probed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var unlisted = new List<ushort>();

                // ① 配对列表里认得型号的：用正式型号名去问它自己那条通道
                foreach (ushort pid in registered)
                {
                    var model = Models.FirstOrDefault(m => m.IsOmni && m.Pid == pid);
                    if (model == null)
                    {
                        unlisted.Add(pid);
                        continue;
                    }

                    string? channelPath = model.IsMouse ? col03 : col02;
                    if (channelPath == null || !probed.Add(channelPath))
                        continue;

                    var reading = QueryOnPath(channelPath, model);
                    if (reading != null)
                        rows.Add(reading);
                }

                // ② 不看配对列表，直接问两条转发通道：设备在线 → 接收器代答电量；
                //    不在线 → 回 FF AA。这才是判断“在不在”的唯一实锤。
                if (col02 != null && probed.Add(col02))
                {
                    var reading = ReadOmniChannel(col02, 0x02, isMouse: false, unlisted.FirstOrDefault());
                    if (reading != null)
                        rows.Add(reading);
                }

                if (col03 != null && probed.Add(col03))
                {
                    var reading = ReadOmniChannel(col03, 0x03, isMouse: true, unlisted.FirstOrDefault());
                    if (reading != null)
                        rows.Add(reading);
                }

                // ③ 两条通道都没应答：登记过的按“未应答”逐台提示，否则给接收器状态行
                if (rows.Count == 0)
                {
                    if (registered.Count > 0)
                    {
                        foreach (ushort pid in registered)
                            rows.Add(Asleep(col01, pid, $"ROG 设备 (OMNI, PID 0x{pid:X4})",
                                "接收器登记了这台设备，但通道没有应答：设备可能已关机、休眠或未连接"));
                    }
                    else
                    {
                        rows.Add(ReceiverIdleStatus(col01));
                    }
                }

                foreach (var reading in rows)
                    result[$"omni:{instance}:{reading.Id}"] = reading;
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
        /// 直读 OMNI 接收器的转发通道（键盘 mi_02/col02 report id 0x02，
        /// 鼠标 mi_02/col03 report id 0x03）。
        ///
        /// 接收器会把命令转给当前连接的设备：设备在线 → 代答真实电量；
        /// 设备不在线 → 回 [1]=0xFF [2]=0xAA（无应答）→ 返回 null，
        /// 由上层决定是报“设备未应答”还是只写一条接收器状态行。
        ///
        /// unlistedPid：配对列表里登记过、但本表没收录的 PID（仅用于命名）；0 = 连 PID 也没报到。
        /// </summary>
        private static RogBatteryReading? ReadOmniChannel(
            string path, byte reportId, bool isMouse, ushort unlistedPid)
        {
            var kind = TransactOnPath(path, reportId,
                isMouse ? SubMouseBattery : SubKeyboardBattery, out byte[]? reply);

            if (kind != ReplyKind.Reply || reply == null)
                return null;   // 无应答（FF AA）/ 超时：这条通道上没有设备

            string hex = BitConverter.ToString(reply, 0, Math.Min(20, reply.Length));
            AppLog.Write($"EasyBluetooth/ROG：OMNI {(isMouse ? "鼠标" : "键盘")}通道" +
                         $"（rid 0x{reportId:X2}）回复 {hex}");

            string name = unlistedPid != 0
                ? $"ROG {(isMouse ? "鼠标" : "键盘")} (OMNI, PID 0x{unlistedPid:X4})"
                : $"ROG {(isMouse ? "鼠标" : "键盘")}（OMNI 接收器 · 2.4G）";

            if (isMouse)
            {
                int percent = reply[5];
                bool charging = reply[10] != 0;

                if (percent > 100)
                    return UnknownShape(path, name, unlistedPid);

                if (percent == 0 && !charging)
                    return Asleep(path, unlistedPid, name, "电量 0 且未充电，视为休眠/关机");

                AppLog.Write($"EasyBluetooth/ROG：{name} 电量 {percent}%（充电={charging}）");
                return new RogBatteryReading
                {
                    Id = path,
                    Name = name,
                    ProductId = unlistedPid,
                    IsWireless = true,
                    Percent = percent,
                    IsCharging = charging,
                    Note = unlistedPid != 0
                        ? "未收录型号：按鼠标布局识别，读数仅供参考"
                        : "接收器未报型号：按鼠标布局识别（仅供参考）"
                };
            }

            // 键盘回复有两种布局：
            //   · [6] 是 0–10 档位表、[11] 才是百分比 → 夜魔家族（Falchion 系）；
            //   · [6] 直接就是百分比 → Azoth 等。
            int percentValue;
            int gauge = -1;
            string layout;

            if (reply[6] <= 10 && reply[11] > reply[6] && reply[11] <= 100)
            {
                percentValue = reply[11];
                gauge = reply[6];
                layout = "夜魔家族";
            }
            else if (reply[6] <= 100)
            {
                percentValue = reply[6];
                layout = "标准";
            }
            else
            {
                return UnknownShape(path, name, unlistedPid);
            }

            bool isCharging = reply[9] == 1;

            if (percentValue == 0 && !isCharging)
                return Asleep(path, unlistedPid, name, "电量 0 且未充电，视为休眠/关机");

            AppLog.Write($"EasyBluetooth/ROG：{name} 电量 {percentValue}%" +
                         $"（充电={isCharging}，{layout}布局）");

            return new RogBatteryReading
            {
                Id = path,
                Name = name,
                ProductId = unlistedPid,
                IsWireless = true,
                Percent = percentValue,
                IsCharging = isCharging,
                Gauge = gauge,
                Note = unlistedPid != 0
                    ? "未收录型号：按回复布局识别，读数仅供参考"
                    : $"接收器未报型号：按{layout}布局识别（仅供参考）"
            };
        }

        private static RogBatteryReading UnknownShape(string path, string name, ushort pid)
            => new()
            {
                Id = path,
                Name = name,
                ProductId = pid,
                IsWireless = true,
                Percent = null,
                Note = "回复形状不符合已知布局，原始字节已写入日志"
            };

        /// <summary>
        /// 接收器在线、但两条转发通道全都没应答（设备关机 / 没连上 / 没配对）。
        /// 措辞注意：只能写“设备未应答”，不能写“无已连接设备”——本机实测
        /// 设备连着的时候这条命令照样可能一台设备都不报。
        /// </summary>
        private static RogBatteryReading ReceiverIdleStatus(string col01)
            => new()
            {
                Id = $"omni-status:{col01}",
                Name = "ROG OMNI RECEIVER（接收器在线，设备未应答）",
                ProductId = OmniReceiverPid,
                IsWireless = true,
                Percent = null,
                IsReceiverStatus = true,
                Note = "接收器读到了，但键盘/鼠标通道都没有应答：设备可能没开机、没连上或未配对。" +
                       "确认设备已开机并在 2.4G 档；有线连接时插 USB 线可直接读电量"
            };

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
                return Asleep(path, model, "接收器已配对但设备未应答：键盘可能休眠/未开机或档位不在 2.4G，按一下键盘再刷新即可");

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