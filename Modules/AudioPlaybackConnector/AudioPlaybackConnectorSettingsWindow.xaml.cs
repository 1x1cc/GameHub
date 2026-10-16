using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameHub.Utils;

namespace GameHub.Modules.AudioPlaybackConnector
{
    /// <summary>
    /// AudioPlaybackConnector 内置功能面板（蓝牙 A2DP Sink 连接管理）。
    /// </summary>
    public partial class AudioPlaybackConnectorSettingsWindow : Window
    {
        private List<BluetoothAudioDeviceInfo> _devices = new();
        private ModuleSettings _settings = SettingsStore.Load();

        public AudioPlaybackConnectorSettingsWindow()
        {
            InitializeComponent();
            Loaded += async (_, _) => 
            {
                await RefreshAsync();
                LoadHotkeySettings();
            };
        }

        private void LoadHotkeySettings()
        {
            _settings = SettingsStore.Load();
            BluetoothAudioPauseHotkeyEnabled.IsChecked = _settings.BluetoothAudioPauseHotkeyEnabled;
            BluetoothAudioPauseHotkeyBox.Text = _settings.BluetoothAudioPauseHotkey;
        }

        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box)
                return;

            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // 只按下修饰键时不算一个完整组合
            if (key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin
                or Key.None)
            {
                return;
            }

            var parts = new List<string>();
            var modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(key.ToString());

            box.Text = string.Join("+", parts);
        }

        private async Task RefreshAsync()
        {
            if (!BluetoothAudioService.IsSupported)
            {
                StatusText.Text = "当前系统不支持：需要 Windows 10 2004（内部版本 19041）或更高版本。";
                DeviceList.IsEnabled = false;
                return;
            }

            StatusText.Text = "正在扫描已配对的蓝牙音频设备…";

            _devices = await BluetoothAudioService.GetDevicesAsync();

            DeviceList.Items.Clear();
            foreach (var device in _devices)
                DeviceList.Items.Add(device.DisplayText);

            StatusText.Text = BuildSummary();
        }

        private string BuildSummary()
        {
            if (_devices.Count == 0)
                return "未发现已配对的蓝牙音频设备。请先在系统蓝牙设置中配对设备。";

            var connectedNames = _devices
                .Where(d => d.IsConnected)
                .Select(d => d.Name)
                .Distinct()
                .ToList();

            string text = connectedNames.Count > 0
                ? $"已连接：{string.Join("、", connectedNames)}（共 {_devices.Count} 条已配对记录）。"
                : $"发现 {_devices.Count} 条已配对记录，当前均未连接。选中设备后点「连接」，或双击该项。";

            if (_devices.Any(d => d.HasDuplicateRecord))
                text += " 注意：同一台设备存在多条配对记录（列表用 [MAC] 区分）；连接失败时会自动尝试另一条。";

            // 暂停状态从音频会话的真实静音状态推导
            if (BluetoothAudioService.IsBluetoothAudioPaused)
                text += " 当前：蓝牙音频已暂停（手机声音不从电脑输出，按热键可恢复）。";

            return text;
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e) => await RefreshAsync();

        private async void Connect_Click(object sender, RoutedEventArgs e) => await ConnectSelectedAsync();

        private async void DeviceList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
            => await ConnectSelectedAsync();

        private async Task ConnectSelectedAsync()
        {
            int index = DeviceList.SelectedIndex;
            if (index < 0 || index >= _devices.Count)
            {
                MessageBox.Show("请先选择一个蓝牙音频设备。", "蓝牙音频接收",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var device = _devices[index];
            StatusText.Text = $"正在连接 {device.Name}（最多等待 {(int)BluetoothAudioService.OpenTimeout.TotalSeconds} 秒）…";

            var (_, message) = await BluetoothAudioService.ConnectAsync(device.Id);

            // 先刷新，再把本次结果放在最前面显示（刷新会重建摘要文本）
            await RefreshAsync();
            StatusText.Text = $"{device.Name}：{message}\n\n{StatusText.Text}";

            NotifyMainWindow();
        }

        private async void Disconnect_Click(object sender, RoutedEventArgs e)
        {
            int index = DeviceList.SelectedIndex;
            if (index < 0 || index >= _devices.Count)
            {
                MessageBox.Show("请先选择一个蓝牙音频设备。", "蓝牙音频接收",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            var device = _devices[index];
            var (_, message) = await BluetoothAudioService.DisconnectAsync(device.Id);

            await RefreshAsync();
            StatusText.Text = $"{device.Name}：{message}\n\n{StatusText.Text}";

            NotifyMainWindow();
        }

        private void OpenBluetoothSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开系统蓝牙设置：{ex.Message}", "蓝牙音频接收",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            // 保存热键设置
            _settings.BluetoothAudioPauseHotkeyEnabled = BluetoothAudioPauseHotkeyEnabled.IsChecked == true;
            _settings.BluetoothAudioPauseHotkey = string.IsNullOrWhiteSpace(BluetoothAudioPauseHotkeyBox.Text)
                ? "Ctrl+Alt+F12" : BluetoothAudioPauseHotkeyBox.Text.Trim();
            SettingsStore.Save(_settings);

            // 立即生效：让主窗口按最新设置重新注册热键（否则要重启程序才生效）
            ApplyHotkeySettings();

            Close();
        }

        /// <summary>让主窗口按最新设置重新注册热键。</summary>
        private void ApplyHotkeySettings()
        {
            try
            {
                if (Application.Current?.MainWindow is Views.MainWindow main)
                    main.ReloadHotkeys();
            }
            catch (Exception ex)
            {
                // 不能静默：注册中途失败会导致热键全部失效
                try
                {
                    System.IO.File.AppendAllText(
                        System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gamehub.log"),
                        $"[{DateTime.Now:HH:mm:ss}] 重新注册热键失败：{ex.GetType().Name}: {ex.Message}\n");
                }
                catch { }
            }
        }

        private void NotifyMainWindow()
        {
            if (Application.Current?.MainWindow is Views.MainWindow main)
                main.NotifyPanelClosed();
        }
    }
}
