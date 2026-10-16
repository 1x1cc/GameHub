using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameHub.Utils;

namespace GameHub.Modules.SoundSwitch
{
    /// <summary>
    /// 设备列表项（带选中状态）
    /// </summary>
    public class DeviceListItem : INotifyPropertyChanged
    {
        public AudioDeviceInfo Device { get; set; }
        public string DisplayText => Device.DisplayText;
        
        private bool _isSelected;
        public bool IsSelected
        {
            get => _isSelected;
            set
            {
                if (_isSelected != value)
                {
                    _isSelected = value;
                    OnPropertyChanged(nameof(IsSelected));
                }
            }
        }

        public DeviceListItem(AudioDeviceInfo device)
        {
            Device = device;
        }

        public event PropertyChangedEventHandler PropertyChanged;
        protected void OnPropertyChanged(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// SoundSwitch 内置功能面板。
    /// 只做三件事：输出设备手动切换、输入设备手动切换、热键切换。
    /// </summary>
    public partial class SoundSwitchSettingsWindow : Window
    {
        private List<DeviceListItem> _playbackDevices = new();
        private List<DeviceListItem> _recordingDevices = new();
        private ModuleSettings _settings = SettingsStore.Load();

        public SoundSwitchSettingsWindow()
        {
            InitializeComponent();
            LoadDevices();
            LoadHotkeySettings();
        }

        // ---------- 设备列表 ----------

        private void LoadDevices()
        {
            var playbackRaw = AudioDeviceService.GetDevices(AudioFlow.Playback);
            var recordingRaw = AudioDeviceService.GetDevices(AudioFlow.Recording);

            _playbackDevices = playbackRaw.Select(d => new DeviceListItem(d)).ToList();
            _recordingDevices = recordingRaw.Select(d => new DeviceListItem(d)).ToList();

            FillList(PlaybackList, _playbackDevices);
            FillList(RecordingList, _recordingDevices);
        }

        private static void FillList(ListBox list, List<DeviceListItem> devices)
        {
            list.ItemsSource = devices;

            // 全部设备默认勾选（热键切换范围）
            foreach (var device in devices)
                device.IsSelected = true;
        }

        private void RefreshDevices_Click(object sender, RoutedEventArgs e) => LoadDevices();

        private void PlaybackDevice_Click(object sender, RoutedEventArgs e)
        {
            if (PlaybackList.SelectedItem is DeviceListItem item)
                ApplyDefaultByItem(PlaybackList, _playbackDevices, item, "输出");
        }

        private void RecordingDevice_Click(object sender, RoutedEventArgs e)
        {
            if (RecordingList.SelectedItem is DeviceListItem item)
                ApplyDefaultByItem(RecordingList, _recordingDevices, item, "输入");
        }

        private void ApplyDefault(ListBox list, List<DeviceListItem> devices, string kind)
        {
            if (list.SelectedItem is not DeviceListItem item)
            {
                MessageBox.Show($"请先在列表中选择一个{kind}设备。", "SoundSwitch",
                    MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            ApplyDefaultByItem(list, devices, item, kind);
        }

        private void ApplyDefaultByItem(ListBox list, List<DeviceListItem> devices, DeviceListItem item, string kind)
        {
            if (item.Device.IsDefault)
                return;

            bool ok = AudioDeviceService.SetDefaultDevice(item.Device.Id);
            if (!ok)
            {
                MessageBox.Show($"切换{kind}设备失败，请稍后重试。", "SoundSwitch",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }

            LoadDevices();
            NotifyMainWindow();
        }

        // ---------- 热键设置 ----------

        private void LoadHotkeySettings()
        {
            _settings = SettingsStore.Load();
            PlaybackHotkeyEnabled.IsChecked = _settings.PlaybackHotkeyEnabled;
            RecordingHotkeyEnabled.IsChecked = _settings.RecordingHotkeyEnabled;
            PlaybackHotkeyBox.Text = _settings.PlaybackHotkey;
            RecordingHotkeyBox.Text = _settings.RecordingHotkey;

            // 加载热键目标设备（多选）
            LoadHotkeyTargets(PlaybackList, _settings.HotkeyTargetPlaybackDeviceIds);
            LoadHotkeyTargets(RecordingList, _settings.HotkeyTargetRecordingDeviceIds);
        }

        private void LoadHotkeyTargets(ListBox list, List<string> targetIds)
        {
            var devices = list == PlaybackList ? _playbackDevices : _recordingDevices;
            foreach (var device in devices)
            {
                device.IsSelected = targetIds.Contains(device.Device.Id);
            }
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

        // ---------- 保存 / 关闭 ----------

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            _settings.PlaybackHotkeyEnabled = PlaybackHotkeyEnabled.IsChecked == true;
            _settings.RecordingHotkeyEnabled = RecordingHotkeyEnabled.IsChecked == true;
            _settings.PlaybackHotkey = string.IsNullOrWhiteSpace(PlaybackHotkeyBox.Text)
                ? "Ctrl+Alt+F11" : PlaybackHotkeyBox.Text.Trim();
            _settings.RecordingHotkey = string.IsNullOrWhiteSpace(RecordingHotkeyBox.Text)
                ? "Ctrl+Alt+F7" : RecordingHotkeyBox.Text.Trim();

            // 保存热键目标设备（多选）
            _settings.HotkeyTargetPlaybackDeviceIds = _playbackDevices.Where(d => d.IsSelected).Select(d => d.Device.Id).ToList();
            _settings.HotkeyTargetRecordingDeviceIds = _recordingDevices.Where(d => d.IsSelected).Select(d => d.Device.Id).ToList();

            SettingsStore.Save(_settings);
            NotifyMainWindow(reloadHotkeys: true);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void NotifyMainWindow(bool reloadHotkeys = false)
        {
            if (Application.Current?.MainWindow is Views.MainWindow main)
            {
                if (reloadHotkeys)
                    main.ReloadHotkeys();
                main.NotifyPanelClosed();
            }
        }
    }
}
