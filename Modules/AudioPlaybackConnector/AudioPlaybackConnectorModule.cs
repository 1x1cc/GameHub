using GameHub.Interfaces;
using GameHub.Utils;
using System.Linq;
using System.Threading.Tasks;

namespace GameHub.Modules.AudioPlaybackConnector
{
    /// <summary>
    /// AudioPlaybackConnector 模块（内置实现，不依赖外部 AudioPlaybackConnector.exe）。
    ///
    /// 基于 Windows 10 2004+ 自带的 AudioPlaybackConnection WinRT API，
    /// 提供蓝牙音频接收（A2DP Sink）的连接与断开。
    /// </summary>
    public sealed class AudioPlaybackConnectorModule : IModule
    {
        public string Name => "AudioPlaybackConnector";

        public string Description => "蓝牙音频接收 (A2DP Sink) 连接管理";

        public string IconPath => "pack://application:,,,/Assets/Icons/audiopc.png";

        public bool IsAutoStartEnabled => AutoStartManager.IsEnabled();

        public async Task<string> GetStatusTextAsync()
        {
            if (!BluetoothAudioService.IsSupported)
                return "需要 Windows 10 2004 或更高版本";

            var devices = await BluetoothAudioService.GetDevicesAsync();

            if (devices.Count == 0)
                return "未发现蓝牙音频设备";

            var connected = devices.Where(d => d.IsConnected).ToList();
            if (connected.Count > 0)
                return $"已连接：{Trim(connected[0].Name)}";

            return $"已配对 {devices.Count} 台 · 未连接";
        }

        public void OpenPanel()
        {
            var window = new AudioPlaybackConnectorSettingsWindow
            {
                Owner = System.Windows.Application.Current?.MainWindow
            };
            window.ShowDialog();
        }

        private static string Trim(string name, int max = 28)
        {
            if (string.IsNullOrEmpty(name)) return "未命名设备";
            return name.Length <= max ? name : name[..max] + "…";
        }
    }
}
