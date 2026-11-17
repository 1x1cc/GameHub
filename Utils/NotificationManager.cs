using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameHub.Utils
{
    /// <summary>
    /// 轻量通知弹窗管理器（屏幕右下角）。
    ///
    /// 只在右键看到一条简短提示，默认 1 秒后自动消失。
    ///
    /// 关键约束（旧实现踩过的坑）：
    ///   1. 整个生命周期**只创建一个 Window**，隐藏一律用 Hide()。
    ///      WPF 的 Window 一旦 Close() 就永久销毁，之后 Show() 会抛异常，通知再也弹不出来。
    ///   2. 显示前**不要**用 IsVisible 做前置判断。窗口首次使用时 IsVisible 必然为 false，
    ///      先判断再 Show() 会导致永远不显示。
    /// </summary>
    public static class NotificationManager
    {
        /// <summary>通知停留时长（毫秒）。</summary>
        private const int DisplayDurationMs = 1000;

        /// <summary>每多一行多加的停留时间（毫秒）。</summary>
        private const int DisplayDurationPerExtraLineMs = 350;

        /// <summary>停留时长上限（毫秒）。</summary>
        private const int MaxDisplayDurationMs = 2600;

        /// <summary>多条通知在这个时间窗内到达时，合并进同一个气泡（毫秒）。</summary>
        private const int MergeWindowMs = 2500;

        /// <summary>宽度固定，文字在此宽度内自动换行。</summary>
        private const double WindowWidth = 320;

        /// <summary>高度自适应上限，防止超长设备名把弹窗拉得过高。</summary>
        private const double MaxWindowHeight = 240;

        private const double MarginRight = 24;
        private const double MarginBottom = 24;

        private static Window? _window;
        private static TextBlock? _text;
        private static DispatcherTimer? _hideTimer;

        /// <summary>当前气泡里已经合并的通知（同一时间只显示一个气泡）。</summary>
        private static readonly System.Collections.Generic.List<string> _lines =
            new System.Collections.Generic.List<string>();

        /// <summary>上一次刷新气泡的时间，用于判断新通知是否还能合并进同一气泡。</summary>
        private static DateTime _lastShowUtc = DateTime.MinValue;

        /// <summary>创建通知窗口。必须在 UI 线程调用；重复调用无副作用。</summary>
        public static void Initialize()
        {
            if (_window != null)
                return;

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            // 保证在 UI 线程创建
            if (!dispatcher.CheckAccess())
            {
                dispatcher.Invoke(new Action(Initialize));
                return;
            }

            _text = new TextBlock
            {
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                VerticalAlignment = VerticalAlignment.Center
            };
            _text.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");

            // 20/16/4 双层圆角：外层 20、内层 16、中间 4px 填充（与六张卡片同一套规范）
            var inner = new Border
            {
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(13, 9, 13, 9),
                Child = _text
            };
            inner.SetResourceReference(Border.BackgroundProperty, "BgBrush");

            var outer = new Border
            {
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(4),
                BorderThickness = new Thickness(1),
                Child = inner,
                Effect = new System.Windows.Media.Effects.DropShadowEffect
                {
                    BlurRadius = 22,
                    ShadowDepth = 2,
                    Opacity = 0.38,
                    Color = Colors.Black
                }
            };
            outer.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            outer.SetResourceReference(Border.BorderBrushProperty, "StrokeBrush");

            _window = new Window
            {
                WindowStyle = WindowStyle.None,
                Background = Brushes.Transparent,
                AllowsTransparency = true,
                ShowInTaskbar = false,
                ShowActivated = false,   // 不抢焦点
                Topmost = true,

                // 宽度固定 + 高度自适应：长设备名会换行而不是被截断
                Width = WindowWidth,
                SizeToContent = SizeToContent.Height,
                MaxHeight = MaxWindowHeight,

                WindowStartupLocation = WindowStartupLocation.Manual,
                Opacity = 1.0,
                Content = outer
            };

            _hideTimer = new DispatcherTimer(DispatcherPriority.Normal, dispatcher)
            {
                Interval = TimeSpan.FromMilliseconds(DisplayDurationMs)
            };
            _hideTimer.Tick += OnHideTimerTick;
        }

        private static void OnHideTimerTick(object? sender, EventArgs e)
        {
            _hideTimer?.Stop();

            // 气泡收掉后清空合并列表，下一条通知从新气泡开始
            _lines.Clear();
            HideNotification();
        }

        /// <summary>显示设备切换通知（可从任意线程调用）。</summary>
        public static void ShowDeviceNotification(string kind, string deviceName)
        {
            ShowNotification($"已切换{kind}设备：{deviceName}");
        }

        /// <summary>显示普通通知（可从任意线程调用）。</summary>
        public static void ShowInfo(string title, string message)
        {
            ShowNotification($"{title}\n{message}");
        }

        private static void ShowNotification(string message)
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
            {
                Log("ShowNotification: Application.Current/Dispatcher 为 null，放弃");
                return;
            }

            // 保证在 UI 线程显示
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => ShowNotification(message)));
                return;
            }

            Initialize();

            if (_window == null || _text == null)
            {
                Log($"通知未显示：窗口未就绪 _window={(_window == null ? "null" : "ok")} _text={(_text == null ? "null" : "ok")}");
                return;
            }

            // 多条通知（例如开机时各模块同时上报）合并进同一个气泡：
            // 气泡还在显示、且距上次刷新不超过 MergeWindowMs 时追加一行，而不是另弹一个气泡。
            bool merge = _window.IsVisible
                && (DateTime.UtcNow - _lastShowUtc).TotalMilliseconds <= MergeWindowMs;

            if (!merge)
                _lines.Clear();

            if (!_lines.Contains(message))
                _lines.Add(message);

            _lastShowUtc = DateTime.UtcNow;
            _text.Text = BuildText();

            // 行数多了多留一会儿，但不超过上限
            if (_hideTimer != null)
            {
                _hideTimer.Interval = TimeSpan.FromMilliseconds(
                    Math.Min(MaxDisplayDurationMs,
                        DisplayDurationMs + DisplayDurationPerExtraLineMs * Math.Max(0, _lines.Count - 1)));
            }

            // 连续触发时重新计时，避免上一条的定时器把新通知提前收掉
            _hideTimer?.Stop();

            try
            {
                // Show() 可重复调用：首次创建显示，隐藏后再次调用即重新显示
                _window.Show();

                // 高度是自适应的，必须在 Show() 完成布局后才能拿到实际尺寸来定位
                PositionWindow();

                _window.Topmost = true;   // 确保压在其它窗口（含全屏）之上
                _hideTimer?.Start();
            }
            catch (Exception ex)
            {
                Log($"通知显示失败 {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>把合并后的多条通知拼成一个气泡里的多行文本（第二行起加项目符号）。</summary>
        private static string BuildText()
        {
            if (_lines.Count == 0)
                return string.Empty;

            if (_lines.Count == 1)
                return _lines[0];

            var sb = new System.Text.StringBuilder();
            for (int i = 0; i < _lines.Count; i++)
            {
                if (i > 0)
                    sb.Append('\n').Append("· ");

                sb.Append(_lines[i]);
            }

            return sb.ToString();
        }

        /// <summary>跟踪日志（临时诊断用）。</summary>
        private static void Log(string message)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gamehub.log"),
                    $"[{DateTime.Now:HH:mm:ss}] {message}\n");
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>把通知窗口摆到所在显示器的右下角。</summary>
        private static void PositionWindow()
        {
            if (_window == null)
                return;

            // 不能直接用 SystemParameters.WorkArea：本机（1440x900 物理屏 + 200% 缩放 +
            // PerMonitor DPI 感知）实测它返回的是物理像素(1440x860)，而窗口坐标是 DIP，
            // 两者差一倍，Left/Top 被算成两倍 → 窗口跑到屏幕外，通知永远看不见。
            // 这里改为：取窗口所在显示器的物理工作区，再按窗口实际 DPI 换算回 DIP。
            var dpi = VisualTreeHelper.GetDpi(_window);
            double scaleX = dpi.DpiScaleX > 0 ? dpi.DpiScaleX : 1.0;
            double scaleY = dpi.DpiScaleY > 0 ? dpi.DpiScaleY : 1.0;

            var work = GetMonitorWorkArea(_window);

            // 高度自适应，ActualHeight 在布局完成后才有效
            double w = _window.ActualWidth > 0 ? _window.ActualWidth : WindowWidth;
            double h = _window.ActualHeight > 0 ? _window.ActualHeight : 56;

            _window.Left = work.Right / scaleX - w - MarginRight;
            _window.Top = work.Bottom / scaleY - h - MarginBottom;
        }

        // ---------- 显示器工作区（物理像素） ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct MONITORINFO
        {
            public int cbSize;
            public RECT rcMonitor;
            public RECT rcWork;
            public uint dwFlags;
        }

        [DllImport("user32.dll")]
        private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFO lpmi);

        /// <summary>返回窗口所在显示器的物理像素工作区（已排除任务栏）。</summary>
        private static RECT GetMonitorWorkArea(Window window)
        {
            const uint MONITOR_DEFAULTTONEAREST = 2;

            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd != IntPtr.Zero)
                {
                    IntPtr monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
                    if (monitor != IntPtr.Zero)
                    {
                        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
                        if (GetMonitorInfoW(monitor, ref info))
                            return info.rcWork;
                    }
                }
            }
            catch
            {
                // 回退到下面的 SystemParameters
            }

            // 回退：本机上 SystemParameters.WorkArea 就是物理像素
            var area = SystemParameters.WorkArea;
            return new RECT
            {
                Left = (int)area.Left,
                Top = (int)area.Top,
                Right = (int)area.Right,
                Bottom = (int)area.Bottom
            };
        }

        private static void HideNotification()
        {
            if (_window == null)
                return;

            var dispatcher = _window.Dispatcher;
            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(HideNotification));
                return;
            }

            try
            {
                // 用 Hide() 而不是 Close()：Close() 会永久销毁窗口
                _window.Hide();
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>退出时调用：真正销毁窗口。</summary>
        public static void Cleanup()
        {
            if (_hideTimer != null)
            {
                _hideTimer.Stop();
                _hideTimer.Tick -= OnHideTimerTick;
                _hideTimer = null;
            }

            if (_window != null)
            {
                try
                {
                    _window.Hide();
                    _window.Close();
                }
                catch
                {
                    // 忽略
                }
            }

            _window = null;
            _text = null;
        }
    }
}
