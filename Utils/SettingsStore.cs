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
    }
}
