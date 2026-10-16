using GameHub.Interfaces;
using GameHub.Utils;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace GameHub.Modules.SoundSwitch
{
    /// <summary>
    /// SoundSwitch 模块（内置实现，不依赖外部 SoundSwitch.exe）。
    ///
    /// 只保留三件事：
    ///   1) 输出设备（播放）手动切换
    ///   2) 输入设备（录音）手动切换
    ///   3) 热键循环切换
    ///
    /// 已按要求移除：未连接设备展示、故障排查、通知项。
    /// </summary>
    public sealed class SoundSwitchModule : IModule
    {
        public string Name => "SoundSwitch";

        public string Description => "输出/输入设备手动切换与热键切换";

        public string IconPath => "pack://application:,,,/Assets/Icons/soundswitch.png";

        public bool IsAutoStartEnabled => AutoStartManager.IsEnabled();

        /// <summary>热键切换输出设备时，在这些设备 ID 间循环切换。</summary>
        public List<string> HotkeyTargetPlaybackDeviceIds { get; set; } = new();

        /// <summary>热键切换输入设备时，在这些设备 ID 间循环切换。</summary>
        public List<string> HotkeyTargetRecordingDeviceIds { get; set; } = new();

        public Task<string> GetStatusTextAsync()
        {
            return Task.Run(() =>
            {
                var playback = AudioDeviceService.GetDevices(AudioFlow.Playback);
                var defaultDevice = playback.FirstOrDefault(d => d.IsDefault);

                if (defaultDevice == null)
                    return playback.Count == 0 ? "未检测到输出设备" : $"{playback.Count} 个输出设备";

                return $"默认输出：{Trim(defaultDevice.Name)}";
            });
        }

        public void OpenPanel()
        {
            var window = new SoundSwitchSettingsWindow
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            window.ShowDialog();
        }

        /// <summary>设备名过长时截断，避免卡片文字溢出。</summary>
        private static string Trim(string name, int max = 28)
        {
            if (string.IsNullOrEmpty(name)) return "未命名设备";
            return name.Length <= max ? name : name[..max] + "…";
        }
    }
}
