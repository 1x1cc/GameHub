using System;

namespace GameHub.Utils.Battery
{
    /// <summary>
    /// 设备大类，对应 EasyBluetooth 统一接口里的 <c>deviceType</c>。
    /// 只用于图标/分类，不参与取数逻辑。
    /// </summary>
    public enum BatteryDeviceKind
    {
        General,
        Headphones,
        Mouse,
        Keyboard,
        Speaker,
        Gamepad
    }

    /// <summary>
    /// 统一连接状态，对应统一接口里的 <c>status</c>。
    /// 注意它和 <see cref="BatteryDevice.Battery"/> 是**两件独立的事**：
    /// 设备 online 但读不到电量是正常的（battery = null）。
    /// </summary>
    public enum BatteryDeviceStatus
    {
        Unknown,
        Online,
        Offline
    }

    /// <summary>
    /// 统一外设电量模型。
    ///
    /// 设计参考 EasyBluetooth 的「统一标准数据接口」，这里落地的是它已经
    /// 踩出来的几条语义（这些才是关键，字段本身只是载体）：
    ///
    ///   1) <c>battery = null</c> 表示「当前没有有效电量值」，**不是 0**；
    ///      显示端应渲染成 <c>--</c>。
    ///   2) <c>IsSleeping = true</c> 不意味着 battery 必须为 null ——
    ///      如果之前读到过有效值，休眠期间继续返回那个值，
    ///      且 <see cref="BatteryLastUpdatedUtc"/> **不更新**（保持原读数时间）。
    ///   3) <see cref="IsBatteryUnsupported"/> 用来区分「这设备压根不支持读电量」
    ///      和「支持但这次没读到」，前者用户不该指望它变好。
    ///   4) <see cref="Status"/> 与电量解耦，避免「读不到就显示离线」的误判。
    /// </summary>
    public sealed class BatteryDevice
    {
        /// <summary>混淆后的稳定标识（12 位十六进制）。不暴露 MAC / 设备路径。</summary>
        public string Id { get; set; } = string.Empty;

        /// <summary>系统 / 协议给出的原始设备名。</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>用户重命名（未重命名为 null）。</summary>
        public string? RenamedName { get; set; }

        /// <summary>设备大类。</summary>
        public BatteryDeviceKind Kind { get; set; } = BatteryDeviceKind.General;

        /// <summary>数据来源（classic / ble / razer / logitech / rog / atk ...）。</summary>
        public string Source { get; set; } = "unknown";

        /// <summary>统一连接状态。</summary>
        public BatteryDeviceStatus Status { get; set; } = BatteryDeviceStatus.Unknown;

        /// <summary>原始连接状态文本，供排查用。</summary>
        public string ConnectionStatus { get; set; } = string.Empty;

        /// <summary>主电量 0–100；未知为 null。</summary>
        public int? Battery { get; set; }

        /// <summary>是否正在充电。</summary>
        public bool IsCharging { get; set; }

        /// <summary>是否处于休眠/待机。</summary>
        public bool IsSleeping { get; set; }

        /// <summary>该设备是否根本不支持读取电量。</summary>
        public bool IsBatteryUnsupported { get; set; }

        /// <summary>用户是否显式标记为「在托盘显示」。</summary>
        public bool IsShownInTray { get; set; }

        /// <summary>
        /// 电量的最后更新时间。注意：休眠期间沿用旧值时刻，**不会**被刷成当前时间，
        /// 这样界面上的「更新于」才是有意义的（否则会显示成永远刚刚更新过）。
        /// </summary>
        public DateTimeOffset? BatteryLastUpdatedUtc { get; set; }

        // ---------- AirPods 三路电量（非 AirPods 恒为 null） ----------

        public int? AirPodsLeftBattery { get; set; }
        public int? AirPodsRightBattery { get; set; }
        public int? AirPodsCaseBattery { get; set; }

        // ---------- 展示辅助 ----------

        /// <summary>界面应优先显示用户改过的名字。</summary>
        public string DisplayName => string.IsNullOrWhiteSpace(RenamedName) ? Name : RenamedName!;

        /// <summary>电量文本；未知显示 <c>--</c>（不要显示 0%）。</summary>
        public string BatteryText => Battery.HasValue ? $"{Battery.Value}%" : "--";

        /// <summary>是否为「已知支持电量但这次没读到」。</summary>
        public bool IsBatteryUnknown => !Battery.HasValue && !IsBatteryUnsupported;

        // ---------- 枚举 → 线上字符串（与统一接口取值一致） ----------

        public string KindToWire() => Kind switch
        {
            BatteryDeviceKind.Headphones => "headphones",
            BatteryDeviceKind.Mouse => "mouse",
            BatteryDeviceKind.Keyboard => "keyboard",
            BatteryDeviceKind.Speaker => "speaker",
            BatteryDeviceKind.Gamepad => "gamepad",
            _ => "general"
        };

        public string StatusToWire() => Status switch
        {
            BatteryDeviceStatus.Online => "online",
            BatteryDeviceStatus.Offline => "offline",
            _ => "unknown"
        };

        /// <summary>把 "general"/"mouse"... 解析回枚举，未知值回退 general（前向兼容）。</summary>
        public static BatteryDeviceKind KindFromWire(string? value) => (value ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "headphones" => BatteryDeviceKind.Headphones,
            "mouse" => BatteryDeviceKind.Mouse,
            "keyboard" => BatteryDeviceKind.Keyboard,
            "speaker" => BatteryDeviceKind.Speaker,
            "gamepad" => BatteryDeviceKind.Gamepad,
            _ => BatteryDeviceKind.General
        };

        /// <summary>
        /// 按原始设备名自动判类（对应统一接口文档里
        /// "auto-detected from the original device name"）。
        /// 判不出来就归 general —— 这只是图标分类，判错不影响功能。
        /// </summary>
        public static BatteryDeviceKind DetectKind(string? name)
        {
            var n = (name ?? string.Empty).ToLowerInvariant();
            if (n.Length == 0)
                return BatteryDeviceKind.General;

            if (Contains(n, "手柄", "gamepad", "controller", "xbox", "dualsense", "dualshock", "joy-con", "joycon", "octopus", "八爪鱼", "g7 ", "switch pro", "致动", "zd-u"))
                return BatteryDeviceKind.Gamepad;

            if (Contains(n, "鼠标", "mouse", "deathadder", "viper", "basilisk", "orochi", "g pro", "gpw", "naga", "harpe", "gladius",
                "keris", "chakram", "spatha", "pugio", "strider", "sword x", "beast", "花兽", "万灵", "miao", "huan", "ying"))
                return BatteryDeviceKind.Mouse;

            if (Contains(n, "键盘", "keyboard", "k330", "q1", "ganss", "高斯", "keychron", "vgn", "磁轴", "k99", "k75", "夜魔", "falchion", "azoth", "claymore"))
                return BatteryDeviceKind.Keyboard;

            if (Contains(n, "耳机", "headphone", "headset", "airpods", "pods", "buds", "arctis", "earbud", "寒冰", "漫步者", "maxwell"))
                return BatteryDeviceKind.Headphones;

            if (Contains(n, "音箱", "speaker", "soundbar", "回音壁"))
                return BatteryDeviceKind.Speaker;

            return BatteryDeviceKind.General;
        }

        private static bool Contains(string haystack, params string[] needles)
        {
            foreach (var needle in needles)
            {
                if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
                    return true;
            }
            return false;
        }
    }
}
