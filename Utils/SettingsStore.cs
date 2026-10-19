using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace GameHub.Utils
{
    /// <summary>
    /// 关闭主窗口时的行为（存成字符串，settings.json 里保持可读）。
    /// </summary>
    public static class CloseBehavior
    {
        /// <summary>每次关闭都弹出选择对话框（默认）。</summary>
        public const string Ask = "Ask";

        /// <summary>直接完全退出程序。</summary>
        public const string Exit = "Exit";

        /// <summary>直接最小化到系统托盘（后台继续运行，热键仍生效）。</summary>
        public const string Tray = "Tray";

        /// <summary>把任意（含历史/损坏）取值归一化为上面三个之一。</summary>
        public static string Normalize(string? value)
        {
            if (string.Equals(value, Exit, StringComparison.OrdinalIgnoreCase))
                return Exit;
            if (string.Equals(value, Tray, StringComparison.OrdinalIgnoreCase))
                return Tray;
            return Ask;
        }

        /// <summary>用于界面展示的中文名。</summary>
        public static string ToDisplayName(string? value) => Normalize(value) switch
        {
            Exit => "完全退出软件",
            Tray => "最小化到托盘",
            _ => "每次询问"
        };
    }

    public sealed class ModuleSettings
    {
        /// <summary>热键：循环切换输出设备</summary>
        public string PlaybackHotkey { get; set; } = "Ctrl+Alt+F11";

        /// <summary>热键：循环切换输入设备</summary>
        public string RecordingHotkey { get; set; } = "Ctrl+Alt+F7";

        /// <summary>热键：暂停/恢复蓝牙音频传输</summary>
        public string BluetoothAudioPauseHotkey { get; set; } = "Ctrl+Alt+F12";

        /// <summary>是否启用输出设备热键</summary>
        public bool PlaybackHotkeyEnabled { get; set; } = true;

        /// <summary>是否启用输入设备热键</summary>
        public bool RecordingHotkeyEnabled { get; set; } = true;

        /// <summary>是否启用蓝牙音频暂停热键</summary>
        public bool BluetoothAudioPauseHotkeyEnabled { get; set; } = true;

        /// <summary>热键切换输出设备时，在这些设备 ID 间循环切换。</summary>
        public List<string> HotkeyTargetPlaybackDeviceIds { get; set; } = new();

        /// <summary>热键切换输入设备时，在这些设备 ID 间循环切换。</summary>
        public List<string> HotkeyTargetRecordingDeviceIds { get; set; } = new();

        /// <summary>
        /// 关闭主窗口时的行为：每次询问(Ask) / 完全退出(Exit) / 最小化到托盘(Tray)。
        /// 用户在关闭对话框里勾选「记住我的选择」后写入，也可在「软件设置」中更改。
        /// </summary>
        public string CloseAction { get; set; } = CloseBehavior.Ask;

        /// <summary>
        /// 开机自启动拉起时：
        ///   true          = 只驻留系统托盘、不弹出主窗口，热键立即生效；
        ///   false（默认） = 和手动启动一样正常显示主窗口。
        /// 手动启动（双击 exe / 开始菜单）不受此项影响，始终显示主窗口。
        /// </summary>
        public bool AutoStartMinimizedToTray { get; set; } = false;

        /// <summary>
        /// EasyBluetooth 模块（无线设备电量）的设置。
        /// 独立成一个子对象，新增模块不影响已有模块的设置项。
        /// </summary>
        public EasyBluetoothSettings EasyBluetooth { get; set; } = new();
    }

    /// <summary>
    /// EasyBluetooth 模块设置：设备电量刷新、低电量通知、悬浮窗。
    /// 所有可调数值都经 Normalize 收敛，避免 settings.json 被手改成极端值后
    /// 导致高频轮询或悬浮窗跑到屏幕外。
    /// </summary>
    public sealed class EasyBluetoothSettings
    {
        /// <summary>是否定时自动刷新电量。</summary>
        public bool AutoRefreshEnabled { get; set; } = true;

        /// <summary>自动刷新间隔（秒）。默认 30 秒：一次刷新只是设备枚举 + 属性读取，开销极低。</summary>
        public int RefreshIntervalSeconds { get; set; } = 30;

        /// <summary>电量低于阈值时弹通知。</summary>
        public bool LowBatteryNotifyEnabled { get; set; } = true;

        /// <summary>低电量阈值（百分比）。</summary>
        public int LowBatteryThresholdPercent { get; set; } = 20;

        /// <summary>同一台设备低电量提醒的最小间隔（分钟），避免每轮都弹。</summary>
        public int LowBatteryRepeatMinutes { get; set; } = 30;

        /// <summary>设备列表只显示已连接的设备。</summary>
        public bool ListOnlyConnected { get; set; } = false;

        // ---------- 悬浮窗 ----------

        public bool FloatingEnabled { get; set; } = false;

        /// <summary>锁定后不能用鼠标拖动悬浮窗。</summary>
        public bool FloatingLocked { get; set; } = false;

        public bool FloatingTopmost { get; set; } = true;

        /// <summary>鼠标穿透：悬浮窗不拦截鼠标事件（打游戏时不挡操作）。</summary>
        public bool FloatingClickThrough { get; set; } = false;

        public double FloatingOpacity { get; set; } = 0.92;

        public bool FloatingOnlyConnected { get; set; } = true;

        /// <summary>悬浮窗位置；-1 表示还没保存过（首次显示时自动放到右上角）。</summary>
        public double FloatingLeft { get; set; } = -1;

        public double FloatingTop { get; set; } = -1;

        public static int NormalizeInterval(int seconds) => Math.Clamp(seconds, 10, 3600);

        public static int NormalizeThreshold(int percent) => Math.Clamp(percent, 1, 99);

        public static int NormalizeRepeat(int minutes) => Math.Clamp(minutes, 1, 1440);

        public static double NormalizeOpacity(double opacity)
            => opacity < 0.3 ? 0.3 : (opacity > 1.0 ? 1.0 : opacity);

        /// <summary>把从 JSON 读进来的值收敛到合法范围。</summary>
        public void Normalize()
        {
            RefreshIntervalSeconds = NormalizeInterval(RefreshIntervalSeconds);
            LowBatteryThresholdPercent = NormalizeThreshold(LowBatteryThresholdPercent);
            LowBatteryRepeatMinutes = NormalizeRepeat(LowBatteryRepeatMinutes);
            FloatingOpacity = NormalizeOpacity(FloatingOpacity);
        }
    }

    /// <summary>
    /// 简单的 JSON 设置存储：%APPDATA%\GameHub\settings.json
    /// </summary>
    public static class SettingsStore
    {
        private static readonly string Dir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub");

        private static readonly string FilePath = Path.Combine(Dir, "settings.json");

        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static ModuleSettings Load()
        {
            try
            {
                if (File.Exists(FilePath))
                {
                    var json = File.ReadAllText(FilePath);
                    var loaded = JsonSerializer.Deserialize<ModuleSettings>(json);
                    if (loaded != null)
                        return loaded;
                }
            }
            catch
            {
                // 读取失败时回落到默认值
            }

            return new ModuleSettings();
        }

        public static void Save(ModuleSettings settings)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                File.WriteAllText(FilePath, JsonSerializer.Serialize(settings, Options));
            }
            catch
            {
                // 忽略写入失败
            }
        }

        public static string SettingsFilePath => FilePath;

        /// <summary>读取 EasyBluetooth 模块设置（含归一化）。</summary>
        public static EasyBluetoothSettings LoadEasyBluetooth()
        {
            var settings = Load().EasyBluetooth;
            settings.Normalize();
            return settings;
        }

        /// <summary>
        /// 只更新 EasyBluetooth 模块设置，其余模块的设置保持不动。
        /// 模块若直接 Save(ModuleSettings) 会把内存里可能已过期的其它设置写回去，
        /// 因此这里统一走“读-改-写”，只覆盖自己的子对象。
        /// </summary>
        public static void Save(EasyBluetoothSettings settings)
        {
            var root = Load();
            root.EasyBluetooth = settings;
            Save(root);
        }
    }
}
