using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using GameHub.Utils;

namespace GameHub.Modules.EasyBluetooth
{
    /// <summary>悬浮窗里的一行设备（供数据绑定）。</summary>
    public sealed class FloatingDeviceItem
    {
        public string Name { get; init; } = string.Empty;

        /// <summary>电量；未知按 0 处理（条会显示为空）。</summary>
        public int Percent { get; init; }

        public string PercentText { get; init; } = "—";

        public Brush BarBrush { get; init; } = Brushes.Gray;
    }

    /// <summary>
    /// EasyBluetooth 悬浮窗：常驻显示已检索到的无线设备电量。
    ///
    /// 相关设置（见「设置 · 悬浮窗」）：
    ///   · 锁定移动：锁定后鼠标不能拖动窗口（避免游戏里误拖）；
    ///   · 置顶 / 鼠标穿透：穿透时不拦截鼠标，不挡游戏操作；
    ///   · 不透明度；只显示已连接设备；
    ///   · 位置记忆：拖动结束后把坐标写回设置。
    ///
    /// 窗口本身不发起扫描，只订阅 EasyBluetoothMonitor.Updated，
    /// 因此不会额外增加轮询开销。
    /// </summary>
    public partial class EasyBluetoothFloatingWindow : Window
    {
        private const int GwlExStyle = -20;
        private const int WsExTransparent = 0x00000020;
        private const int WsExToolWindow = 0x00000080;

        private bool _locked;
        private bool _clickThrough;

        /// <summary>正在按设置摆放窗口时不要回写位置，否则会把自动摆放的结果当成用户拖动。</summary>
        private bool _applyingPosition;

        public EasyBluetoothFloatingWindow()
        {
            InitializeComponent();

            SourceInitialized += (_, _) => ApplyWindowStyles();
            Loaded += OnLoaded;
            LocationChanged += OnLocationChanged;
            Closed += OnClosed;

            EasyBluetoothMonitor.Updated += OnMonitorUpdated;
        }

        /// <summary>窗口是否正被用户拖动以外的原因改变位置（供控制器判断）。</summary>
        public bool IsApplyingPosition => _applyingPosition;

        // ---------- 生命周期 ----------

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            var settings = SettingsStore.Load().EasyBluetooth;
            settings.Normalize();

            _locked = settings.FloatingLocked;
            _clickThrough = settings.FloatingClickThrough;

            Opacity = settings.FloatingOpacity;
            Topmost = settings.FloatingTopmost;

            UpdateLockVisual();
            ApplyWindowStyles();
            RestorePosition(settings);
            RefreshFromMonitor();
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            EasyBluetoothMonitor.Updated -= OnMonitorUpdated;
        }

        private void OnMonitorUpdated() => RefreshFromMonitor();

        // ---------- 内容 ----------

        /// <summary>按最新扫描结果刷新列表（必须在 UI 线程调用）。</summary>
        public void RefreshFromMonitor()
        {
            if (!Dispatcher.CheckAccess())
            {
                Dispatcher.BeginInvoke(new Action(RefreshFromMonitor));
                return;
            }

            var settings = SettingsStore.Load().EasyBluetooth;
            settings.Normalize();

            if (!EasyBluetoothMonitor.IsSupported)
            {
                DeviceList.ItemsSource = Array.Empty<FloatingDeviceItem>();
                EmptyHint.Text = "需要 Windows 10 2004 或更高版本";
                EmptyHint.Visibility = Visibility.Visible;
                FooterText.Text = string.Empty;
                return;
            }

            var devices = EasyBluetoothMonitor.Devices.AsEnumerable();

            if (settings.FloatingOnlyConnected)
                devices = devices.Where(d => d.IsConnected);

            var items = devices
                .Select(d => new FloatingDeviceItem
                {
                    Name = string.IsNullOrWhiteSpace(d.Name) ? "(未命名设备)" : d.Name,
                    Percent = d.BatteryPercent ?? 0,
                    PercentText = d.BatteryPercent.HasValue ? $"{d.BatteryPercent.Value}%" : "—",
                    BarBrush = ResolveBarBrush(d.BatteryPercent)
                })
                .ToList();

            DeviceList.ItemsSource = items;

            EmptyHint.Text = settings.FloatingOnlyConnected ? "没有已连接的设备" : "未发现设备";
            EmptyHint.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

            FooterText.Text = EasyBluetoothMonitor.LastScanAt is DateTimeOffset at
                ? $"更新于 {at:HH:mm:ss} · 共 {items.Count} 台"
                : "正在获取设备…";
        }

        private static Brush ResolveBarBrush(int? percent)
        {
            if (!percent.HasValue)
                return new SolidColorBrush(Color.FromRgb(0x7E, 0x8C, 0xA3));

            return percent.Value switch
            {
                >= 60 => new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80)),
                >= 30 => new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24)),
                _ => new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71))
            };
        }

        // ---------- 位置 ----------

        private void RestorePosition(EasyBluetoothSettings settings)
        {
            _applyingPosition = true;
            try
            {
                if (settings.FloatingLeft >= 0 && settings.FloatingTop >= 0)
                {
                    Left = settings.FloatingLeft;
                    Top = settings.FloatingTop;

                    // 显示器拔掉/分辨率变化后旧坐标可能落在屏幕外，夹回可见区域
                    ClampIntoWorkArea();
                }
                else
                {
                    var work = SystemParameters.WorkArea;
                    Left = work.Right - Width - 24;
                    Top = work.Top + 24;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"EasyBluetooth：恢复悬浮窗位置失败：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _applyingPosition = false;
            }
        }

        private void ClampIntoWorkArea()
        {
            var work = SystemParameters.WorkArea;
            double width = ActualWidth > 0 ? ActualWidth : Width;
            double maxLeft = work.Right - 40;
            double maxTop = work.Bottom - 40;

            if (Left < work.Left - 20 || Left > maxLeft)
                Left = work.Right - width - 24;

            if (Top < work.Top - 20 || Top > maxTop)
                Top = work.Top + 24;
        }

        private void OnLocationChanged(object? sender, EventArgs e)
        {
            if (_applyingPosition || !IsLoaded)
                return;

            EasyBluetoothFloatingController.SavePosition(Left, Top);
        }

        // ---------- 交互 ----------

        private void Root_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (_locked)
                return;

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 拖动过程中鼠标按键状态异常时会抛这个异常，忽略即可
            }
        }

        private void Root_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            ShowContextMenu();
            e.Handled = true;
        }

        private void ShowContextMenu()
        {
            var menu = new ContextMenu();

            menu.Items.Add(CreateMenuItem("立即刷新", async () =>
                await EasyBluetoothMonitor.RefreshAsync(manual: true)));

            menu.Items.Add(CreateMenuItem(_locked ? "解锁（可拖动）" : "锁定（不可拖动）", ToggleLock));

            menu.Items.Add(CreateMenuItem("打开模块设置", () =>
                EasyBluetoothModule.OpenSettingsWindow()));

            menu.Items.Add(new Separator());

            menu.Items.Add(CreateMenuItem("关闭悬浮窗", () =>
                EasyBluetoothFloatingController.Hide(persistSetting: true)));

            menu.PlacementTarget = RootBorder;
            menu.IsOpen = true;
        }

        private static MenuItem CreateMenuItem(string header, Action action)
        {
            var item = new MenuItem { Header = header };
            item.Click += (_, _) => action();
            return item;
        }

        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _ = EasyBluetoothMonitor.RefreshAsync(manual: true);
        }

        private void Lock_Click(object sender, RoutedEventArgs e) => ToggleLock();

        private void ToggleLock()
        {
            _locked = !_locked;

            var settings = SettingsStore.Load().EasyBluetooth;
            settings.FloatingLocked = _locked;
            SettingsStore.Save(settings);

            UpdateLockVisual();
        }

        private void UpdateLockVisual()
        {
            LockButton.Content = _locked ? "🔒" : "🔓";
            LockHint.Visibility = _locked ? Visibility.Visible : Visibility.Collapsed;
            Cursor = _locked ? Cursors.Arrow : Cursors.SizeAll;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
            => EasyBluetoothFloatingController.Hide(persistSetting: true);

        // ---------- 窗口样式（工具窗口 / 鼠标穿透） ----------

        /// <summary>设置变更后由控制器调用。</summary>
        public void ApplySettings()
        {
            var settings = SettingsStore.Load().EasyBluetooth;
            settings.Normalize();

            _locked = settings.FloatingLocked;
            _clickThrough = settings.FloatingClickThrough;

            Opacity = settings.FloatingOpacity;
            Topmost = settings.FloatingTopmost;

            UpdateLockVisual();
            ApplyWindowStyles();

            // 位置也可能被设置窗口改过（例如「居中显示」）
            _applyingPosition = true;
            try
            {
                if (settings.FloatingLeft >= 0 && settings.FloatingTop >= 0)
                {
                    Left = settings.FloatingLeft;
                    Top = settings.FloatingTop;
                }
            }
            finally
            {
                _applyingPosition = false;
            }

            RefreshFromMonitor();
        }

        private void ApplyWindowStyles()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                    return;

                int exStyle = GetWindowLongAuto(handle, GwlExStyle);

                exStyle |= WsExToolWindow;   // 不出现在 Alt+Tab 列表里

                if (_clickThrough)
                    exStyle |= WsExTransparent;
                else
                    exStyle &= ~WsExTransparent;

                SetWindowLongAuto(handle, GwlExStyle, exStyle);
            }
            catch (Exception ex)
            {
                AppLog.Write($"EasyBluetooth：设置悬浮窗窗口样式失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // 32/64 位下 GetWindowLong/SetWindowLong 的导出名不同，这里按进程位数选择
        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        private static int GetWindowLongAuto(IntPtr hWnd, int nIndex)
            => IntPtr.Size == 8
                ? (int)GetWindowLongPtr64(hWnd, nIndex).ToInt64()
                : GetWindowLong32(hWnd, nIndex);

        private static void SetWindowLongAuto(IntPtr hWnd, int nIndex, int value)
        {
            if (IntPtr.Size == 8)
                SetWindowLongPtr64(hWnd, nIndex, new IntPtr(value));
            else
                SetWindowLong32(hWnd, nIndex, value);
        }
    }
}
