using GameHub.Utils;
using System;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 桌面时钟组件（对应原版 GlassBoard 的“时钟”桌面组件）。
    ///
    /// 行为对齐原版：
    ///   1) 无边框 + 深色玻璃底板（#181A20，与 GlassMaterials 的深色底一致）；
    ///   2) 按住即可拖动，松手记录位置，下次启动回到原位；
    ///   3) 把窗口 owner 设成 shell 窗口，让时钟贴在壁纸之上、普通窗口之下，
    ///      不出现在 Alt+Tab（原版 DesktopPersistence 的做法）；
    ///   4) 每秒自检一次窗口可见性，被系统藏掉就重新显示
    ///      （对应原版 RefreshOrder 里的 IsWindowVisible 兜底）。
    /// </summary>
    public partial class GlassBoardClockWindow : Window
    {
        private const int GWLP_HWNDPARENT = -8;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLongClock32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongClock64(IntPtr hWnd, int nIndex);

        private readonly DispatcherTimer _tick;
        private readonly DispatcherTimer _maintain;
        private readonly DispatcherTimer _saveDelay;

        /// <summary>锁定移动：拖动直接忽略。</summary>
        private bool _locked;

        /// <summary>鼠标穿透：点击穿过窗口。</summary>
        private bool _clickThrough;

        public GlassBoardClockWindow()
        {
            InitializeComponent();

            _tick = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _tick.Tick += (_, _) => UpdateText();

            // 原版是挂 WinEvent 钩子实时纠正层级；这里用轻量的定时自检代替，
            // 效果一样但不用写钩子，也不会在极端情况下漏解钩子。
            _maintain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _maintain.Tick += (_, _) => Maintain();

            // 拖动过程中会疯狂触发位置变化，延迟一点再落盘，避免每秒写几百次文件。
            _saveDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _saveDelay.Tick += (_, _) =>
            {
                _saveDelay.Stop();
                GlassBoardModule.SaveClockPlacement(Left, Top);
            };

            PlaceFromSettings();
            ReloadStyle();

            SourceInitialized += (_, _) => OwnToDesktop();
            Loaded += (_, _) =>
            {
                UpdateText();
                ApplyFloatingOptions(GlassBoardModule.Settings.ClockFloating);
                _tick.Start();
                _maintain.Start();
            };
            Closed += (_, _) =>
            {
                _tick.Stop();
                _maintain.Stop();
                _saveDelay.Stop();
            };
        }

        /// <summary>按配置摆放窗口；没有记录过就放在主屏右侧偏上。</summary>
        private void PlaceFromSettings()
        {
            var settings = GlassBoardModule.Settings;

            if (settings.ClockPlaced)
            {
                Left = settings.ClockLeft;
                Top = settings.ClockTop;
                return;
            }

            var area = SystemParameters.WorkArea;
            Left = Math.Max(area.Left + 24, area.Right - Width - 48);
            Top = area.Top + 120;
        }

        /// <summary>按配置重画外观（面板或右键菜单改样式后调用）。</summary>
        public void ReloadStyle()
        {
            var settings = GlassBoardModule.Settings;

            TimeText.FontSize = Math.Max(20.0, Math.Min(120.0, settings.ClockSize));
            TimeText.Foreground = ToBrush(settings.TextColor);

            DateText.Visibility = settings.ShowDate ? Visibility.Visible : Visibility.Collapsed;
            DateText.Foreground = ToBrush(settings.TextColor);

            // 统一外观：底色 / 圆角 / 描边 / 投影都取自 GlassBoardTheme 那一套，改一处全变
            GlassBoardTheme.ApplyGlassCard(Shell);

            // 无框：去掉底板、描边与投影，只留文字
            if (settings.Frameless)
            {
                Shell.Background = Brushes.Transparent;
                Shell.BorderBrush = Brushes.Transparent;
                Shell.Effect = null;
            }

            var shadow = settings.TextShadow && settings.Frameless
                ? new DropShadowEffect { BlurRadius = 10, ShadowDepth = 1, Opacity = 0.75, Color = Colors.Black }
                : null;

            TimeText.Effect = shadow;
            DateText.Effect = shadow;

            MenuFrameless.IsChecked = settings.Frameless;
            MenuDate.IsChecked = settings.ShowDate;
            MenuHour24.IsChecked = settings.Hour24;

            UpdateText();
        }

        private static Brush ToBrush(string value)
        {
            try
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return new SolidColorBrush((Color)ColorConverter.ConvertFromString(value));
            }
            catch
            {
                // 颜色写坏了就用默认白色，不影响使用
            }

            return Brushes.White;
        }

        private void UpdateText()
        {
            var now = DateTime.Now;
            var settings = GlassBoardModule.Settings;

            TimeText.Text = settings.Hour24 ? now.ToString("HH:mm") : now.ToString("h:mm");
            DateText.Text = now.ToString("yyyy年M月d日 dddd", CultureInfo.CurrentCulture);
        }

        // ---------- 拖拽 ----------

        private void Shell_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            if (_locked)
                return;

            try
            {
                // DragMove 会一直阻塞到松开鼠标，返回后位置就是最终位置
                DragMove();
                _saveDelay.Stop();
                _saveDelay.Start();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：拖动时钟失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------- 右键菜单 ----------

        private void MenuFrameless_Click(object sender, RoutedEventArgs e)
        {
            GlassBoardModule.Settings.Frameless = !GlassBoardModule.Settings.Frameless;
            GlassBoardModule.RefreshClockStyle();
        }

        private void MenuDate_Click(object sender, RoutedEventArgs e)
        {
            GlassBoardModule.Settings.ShowDate = !GlassBoardModule.Settings.ShowDate;
            GlassBoardModule.RefreshClockStyle();
        }

        private void MenuHour24_Click(object sender, RoutedEventArgs e)
        {
            GlassBoardModule.Settings.Hour24 = !GlassBoardModule.Settings.Hour24;
            GlassBoardModule.RefreshClockStyle();
        }

        private void MenuHide_Click(object sender, RoutedEventArgs e)
            => GlassBoardModule.HideClock();

        /// <summary>
        /// 套用悬浮窗选项（锁定移动 / 始终置顶 / 鼠标穿透 / 不透明度）。
        ///
        /// 时钟没有继承组件基类 GlassBoardWidgetWindow（它是 XAML 窗口自己实现的一套），
        /// 所以这里保留一份等价实现；以后若把时钟改成继承基类，这段可以整段删掉。
        /// </summary>
        public void ApplyFloatingOptions(FloatingOptions? options)
        {
            var floating = options ?? new FloatingOptions();
            floating.Normalize();

            _locked = floating.Locked;
            _clickThrough = floating.ClickThrough;

            Opacity = floating.Opacity;
            Topmost = floating.Topmost;

            ApplyWindowStyles();
        }

        /// <summary>鼠标穿透 = WS_EX_TRANSPARENT。读-改-写，别把 WS_EX_LAYERED 冲掉。</summary>
        private void ApplyWindowStyles()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                    return;

                int exStyle = IntPtr.Size == 8
                    ? (int)GetWindowLongClock64(handle, GWL_EXSTYLE).ToInt64()
                    : GetWindowLongClock32(handle, GWL_EXSTYLE);

                exStyle |= WS_EX_TOOLWINDOW;

                if (_clickThrough)
                    exStyle |= WS_EX_TRANSPARENT;
                else
                    exStyle &= ~WS_EX_TRANSPARENT;

                if (IntPtr.Size == 8)
                    SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(exStyle));
                else
                    SetWindowLong32(handle, GWL_EXSTYLE, exStyle);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：设置时钟窗口样式失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------- 层级维护 ----------

        private void OwnToDesktop()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                    return;

                var shell = GetShellWindow();
                if (shell == IntPtr.Zero)
                    return;

                if (IntPtr.Size == 8)
                    SetWindowLongPtr(handle, GWLP_HWNDPARENT, shell);
                else
                    SetWindowLong32(handle, GWLP_HWNDPARENT, shell.ToInt32());
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：设置时钟层级失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 自检：万一被系统或其它程序藏起来，就重新显示且不抢焦点
        /// （原版 RefreshOrder 同样有这个判断）。
        /// </summary>
        private void Maintain()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero || !IsVisible)
                    return;

                // 每 1.5 秒对一次悬浮窗选项（设置改完最多 1.5 秒生效）
                ApplyFloatingOptions(GlassBoardModule.Settings.ClockFloating);

                if (!IsWindowVisible(handle))
                    ShowWindow(handle, SW_SHOWNOACTIVATE);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：时钟自检失败：{ex.GetType().Name}: {ex.Message}");
                _maintain.Stop();
            }
        }
    }
}
