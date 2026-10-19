using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using GameHub.Utils;

namespace GameHub.Modules.EasyBluetooth
{
    /// <summary>
    /// EasyBluetooth 模块面板：设备电量列表 + 刷新/通知/悬浮窗设置。
    /// </summary>
    public partial class EasyBluetoothSettingsWindow : Window
    {
        /// <summary>加载设置到界面时不要触发控件的变更事件。</summary>
        private bool _loading;

        public EasyBluetoothSettingsWindow()
        {
            InitializeComponent();

            Loaded += OnLoaded;
            Closed += OnClosed;

            EasyBluetoothMonitor.Updated += OnMonitorUpdated;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
            await EasyBluetoothMonitor.RefreshAsync(manual: true);
            RefreshDeviceList();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            EasyBluetoothMonitor.Updated -= OnMonitorUpdated;
        }

        private void OnMonitorUpdated() => RefreshDeviceList();

        // ---------- 设置读写 ----------

        private void LoadSettings()
        {
            _loading = true;
            try
            {
                var settings = SettingsStore.Load().EasyBluetooth;
                settings.Normalize();

                AutoRefreshEnabled.IsChecked = settings.AutoRefreshEnabled;
                SelectByTag(RefreshIntervalBox, settings.RefreshIntervalSeconds);

                LowBatteryNotifyEnabled.IsChecked = settings.LowBatteryNotifyEnabled;
                LowBatteryThresholdBox.Text = settings.LowBatteryThresholdPercent.ToString();
                SelectByTag(LowBatteryRepeatBox, settings.LowBatteryRepeatMinutes);

                ListOnlyConnected.IsChecked = settings.ListOnlyConnected;

                FloatingEnabled.IsChecked = settings.FloatingEnabled;
                FloatingLocked.IsChecked = settings.FloatingLocked;
                FloatingTopmost.IsChecked = settings.FloatingTopmost;
                FloatingClickThrough.IsChecked = settings.FloatingClickThrough;
                FloatingOnlyConnected.IsChecked = settings.FloatingOnlyConnected;
                FloatingOpacity.Value = settings.FloatingOpacity;
                UpdateOpacityText(settings.FloatingOpacity);
            }
            catch (Exception ex)
            {
                AppLog.Write("EasyBluetooth：加载设置失败", ex);
            }
            finally
            {
                _loading = false;
            }
        }

        /// <summary>把界面上的值写回设置并保存。</summary>
        private bool SaveSettings(bool showError)
        {
            try
            {
                var settings = SettingsStore.Load().EasyBluetooth;

                settings.AutoRefreshEnabled = AutoRefreshEnabled.IsChecked == true;
                settings.RefreshIntervalSeconds = EasyBluetoothSettings.NormalizeInterval(GetTag(RefreshIntervalBox, 30));

                settings.LowBatteryNotifyEnabled = LowBatteryNotifyEnabled.IsChecked == true;
                settings.LowBatteryThresholdPercent =
                    EasyBluetoothSettings.NormalizeThreshold(ParseInt(LowBatteryThresholdBox.Text, 20));
                settings.LowBatteryRepeatMinutes =
                    EasyBluetoothSettings.NormalizeRepeat(GetTag(LowBatteryRepeatBox, 30));

                settings.ListOnlyConnected = ListOnlyConnected.IsChecked == true;

                settings.FloatingEnabled = FloatingEnabled.IsChecked == true;
                settings.FloatingLocked = FloatingLocked.IsChecked == true;
                settings.FloatingTopmost = FloatingTopmost.IsChecked == true;
                settings.FloatingClickThrough = FloatingClickThrough.IsChecked == true;
                settings.FloatingOnlyConnected = FloatingOnlyConnected.IsChecked == true;
                settings.FloatingOpacity = EasyBluetoothSettings.NormalizeOpacity(FloatingOpacity.Value);

                settings.Normalize();
                SettingsStore.Save(settings);

                // 让新设置立刻生效：刷新间隔、悬浮窗显示状态
                EasyBluetoothMonitor.ApplySettings();
                EasyBluetoothFloatingController.Apply();
                EasyBluetoothModule.NotifySettingsChanged();

                // 界面回填被收敛后的值，避免显示与保存不一致
                LowBatteryThresholdBox.Text = settings.LowBatteryThresholdPercent.ToString();
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write("EasyBluetooth：保存设置失败", ex);

                if (showError)
                {
                    MessageBox.Show("保存设置失败：" + ex.Message, "EasyBluetooth",
                        MessageBoxButton.OK, MessageBoxImage.Warning);
                }

                return false;
            }
        }

        // ---------- 设备列表 ----------

        private void RefreshDeviceList()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshDeviceList));
                return;
            }

            if (!EasyBluetoothMonitor.IsSupported)
            {
                DeviceList.ItemsSource = new List<string>();
                StatusText.Text = "当前系统不支持：需要 Windows 10 2004（内部版本 19041）或更高版本。";
                return;
            }

            bool onlyConnected = ListOnlyConnected.IsChecked == true;

            var devices = EasyBluetoothMonitor.Devices.AsEnumerable();
            if (onlyConnected)
                devices = devices.Where(d => d.IsConnected);

            var items = devices.Select(d => d.DisplayText).ToList();
            DeviceList.ItemsSource = items;

            StatusText.Text = BuildSummary(onlyConnected);
        }

        private string BuildSummary(bool onlyConnected)
        {
            if (!string.IsNullOrEmpty(EasyBluetoothMonitor.LastError))
                return $"上次刷新出错：{EasyBluetoothMonitor.LastError}（详见“报错/日志”）";

            var all = EasyBluetoothMonitor.Devices;
            if (all.Count == 0)
                return EasyBluetoothMonitor.LastScanAt == null
                    ? "正在检测无线设备…"
                    : "未发现无线设备。蓝牙设备需要先在系统设置里完成配对；2.4G 无线外设需要接收器已插入并支持电量上报。";

            int connectedCount = all.Count(d => d.IsConnected);
            int withBattery = all.Count(d => d.BatteryPercent.HasValue);

            string text = $"共 {all.Count} 台设备（蓝牙 {all.Count(d => d.IsBluetooth)} 台，其他无线 {all.Count(d => !d.IsBluetooth)} 台），" +
                          $"已连接 {connectedCount} 台，读到电量 {withBattery} 台";

            if (onlyConnected)
                text += $"，当前列表显示 {all.Count(d => d.IsConnected)} 台";

            if (EasyBluetoothMonitor.LastScanAt is DateTimeOffset at)
                text += $"；更新于 {at:HH:mm:ss}";

            if (withBattery < all.Count)
                text += "\n读不到电量的设备：要么不支持电量上报，要么当前未连接（经典蓝牙耳机/音箱需要连接后由系统上报）。";

            return text;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _ = RefreshManualAsync();
        }

        private async System.Threading.Tasks.Task RefreshManualAsync()
        {
            StatusText.Text = "正在刷新…";
            await EasyBluetoothMonitor.RefreshAsync(manual: true);
            RefreshDeviceList();
        }

        private void ListOnlyConnected_Changed(object sender, RoutedEventArgs e) => RefreshDeviceList();

        private void OpenBluetoothSettings_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                System.Diagnostics.Process.Start(
                    new System.Diagnostics.ProcessStartInfo("ms-settings:bluetooth") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                MessageBox.Show($"无法打开系统蓝牙设置：{ex.Message}", "EasyBluetooth",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        // ---------- 悬浮窗 ----------

        private void FloatingOpacity_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            UpdateOpacityText(e.NewValue);

            if (_loading)
                return;

            // 实时预览：只改窗口透明度，不写磁盘（保存时才落盘）
            EasyBluetoothFloatingController.PreviewOpacity(e.NewValue);
        }

        private void UpdateOpacityText(double value)
        {
            if (FloatingOpacityText != null)
                FloatingOpacityText.Text = $"{Math.Round(value * 100)}%";
        }

        private void ToggleFloating_Click(object sender, RoutedEventArgs e)
        {
            // 先把当前界面设置存下来，再按设置同步悬浮窗，避免“预览用的是旧设置”
            if (!SaveSettings(showError: true))
                return;

            EasyBluetoothFloatingController.Toggle();

            var settings = SettingsStore.Load().EasyBluetooth;
            FloatingEnabled.IsChecked = settings.FloatingEnabled;
        }

        private void ResetFloatingPosition_Click(object sender, RoutedEventArgs e)
        {
            EasyBluetoothFloatingController.ResetPosition();
        }

        // ---------- 保存 / 关闭 ----------

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            if (SaveSettings(showError: true))
                RefreshDeviceList();
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            SaveSettings(showError: false);
            Close();
        }

        // ---------- 工具 ----------

        private static int ParseInt(string text, int fallback)
            => int.TryParse(text?.Trim(), out int value) ? value : fallback;

        /// <summary>按 Tag 选中下拉项；没有匹配项时按当前值补一项，避免用户设置被静默改掉。</summary>
        private static void SelectByTag(ComboBox box, int value)
        {
            foreach (var item in box.Items.OfType<ComboBoxItem>())
            {
                if (item.Tag is string tag && int.TryParse(tag, out int seconds) && seconds == value)
                {
                    box.SelectedItem = item;
                    return;
                }
            }

            var custom = new ComboBoxItem { Content = $"{value}", Tag = value.ToString() };
            box.Items.Add(custom);
            box.SelectedItem = custom;
        }

        private static int GetTag(ComboBox box, int fallback)
        {
            if (box.SelectedItem is ComboBoxItem item && item.Tag is string tag && int.TryParse(tag, out int value))
                return value;

            return fallback;
        }
    }
}
