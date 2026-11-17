#nullable enable
using System;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;

namespace GameHub.Controls
{
    /// <summary>
    /// 第 3 条：卡片 → 详情页的共享元素转场（Shared Element Transition）。
    ///
    /// 点卡片时记下卡片的屏幕矩形；模块窗口首次显示时，把它的容器
    /// 从卡片的位置 / 尺寸**连续平滑放大**到窗口最终位置与尺寸（同时淡入），
    /// 观感上就是这张卡片长成了详情页，而不是凭空弹出一个窗口。
    ///
    /// 纯 WPF 动画实现：
    ///   · 内容根节点用 ScaleTransform 做等比放大（以中心为原点）；
    ///   · 窗口本身用 Left/Top/Width/Height 动画从卡片矩形过渡到最终矩形；
    ///   · 全程 try/catch —— 任何一步不满足条件就静默跳过，窗口按原样显示。
    /// 只有「刚点过卡片」（1.5 秒内）才会播放，其它窗口（托盘、对话框）不受影响。
    /// </summary>
    public static class SharedTransition
    {
        private const double DurationMs = 320.0;
        private const double FreshnessMs = 1500.0;

        /// <summary>转场起点：卡片矩形（设备像素）。</summary>
        private static Rect _sourceRect;

        private static DateTime _capturedUtc = DateTime.MinValue;
        private static bool _playing;
        private static bool _retried;

        /// <summary>记录转场起点（在卡片的点击处理里调用）。</summary>
        public static void Capture(FrameworkElement source)
        {
            try
            {
                if (source == null || source.ActualWidth < 1 || source.ActualHeight < 1)
                    return;

                var topLeft = source.PointToScreen(new Point(0, 0));
                var bottomRight = source.PointToScreen(new Point(source.ActualWidth, source.ActualHeight));

                _sourceRect = new Rect(topLeft, bottomRight);
                _capturedUtc = DateTime.UtcNow;
                _retried = false;
            }
            catch
            {
                _capturedUtc = DateTime.MinValue;
            }
        }

        /// <summary>窗口首次显示时的全局钩子（由 App 注册到 Window.Loaded）。</summary>
        public static void OnWindowLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is Window window)
                TryPlay(window);
        }

        /// <summary>有新鲜的起点就播放转场，否则什么都不做。</summary>
        public static void TryPlay(Window window)
        {
            if (_playing || window == null)
                return;

            if ((DateTime.UtcNow - _capturedUtc).TotalMilliseconds > FreshnessMs)
                return;

            if (window.Content is not FrameworkElement root || root.ActualWidth < 1 || root.ActualHeight < 1)
            {
                // 首帧尺寸可能还没出来：让出一次布局再试一次，之后不再重试
                if (_retried)
                    return;

                _retried = true;
                try
                {
                    window.Dispatcher.BeginInvoke(new Action(() => TryPlay(window)), DispatcherPriority.Loaded);
                }
                catch
                {
                }

                return;
            }

            _capturedUtc = DateTime.MinValue;   // 起点只消费一次
            _playing = true;

            try
            {
                double dpiX = 1.0, dpiY = 1.0;
                try
                {
                    var dpi = VisualTreeHelper.GetDpi(window);
                    dpiX = dpi.DpiScaleX <= 0 ? 1.0 : dpi.DpiScaleX;
                    dpiY = dpi.DpiScaleY <= 0 ? 1.0 : dpi.DpiScaleY;
                }
                catch
                {
                }

                var from = _sourceRect;
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
                var duration = TimeSpan.FromMilliseconds(DurationMs);

                // 1) 内容容器：从卡片尺寸等比放大到窗口尺寸（背景与卡片一起连续缩放）
                double targetW = Math.Max(1.0, window.ActualWidth * dpiX);
                double targetH = Math.Max(1.0, window.ActualHeight * dpiY);

                root.RenderTransformOrigin = new Point(0.5, 0.5);
                var scale = new ScaleTransform(
                    Math.Max(0.05, from.Width / targetW),
                    Math.Max(0.05, from.Height / targetH));
                root.RenderTransform = scale;

                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.0, duration) { EasingFunction = ease });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, duration) { EasingFunction = ease });

                // 2) 窗口整体：从卡片位置平滑移到最终位置
                double toLeft = window.Left;
                double toTop = window.Top;

                window.Opacity = 0;
                window.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(1.0, duration));

                bool canMove = !double.IsNaN(toLeft) && !double.IsNaN(toTop)
                               && !double.IsNaN(from.Left) && !double.IsNaN(from.Top);

                if (canMove)
                {
                    window.BeginAnimation(Window.LeftProperty,
                        new DoubleAnimation(from.Left / dpiX, toLeft, duration) { EasingFunction = ease });
                    window.BeginAnimation(Window.TopProperty,
                        new DoubleAnimation(from.Top / dpiY, toTop, duration) { EasingFunction = ease });
                }

                // 3) 窗口尺寸：只对固定尺寸窗口做（SizeToContent 的窗口不动尺寸，避免互相打架）
                if (window.SizeToContent == SizeToContent.Manual)
                {
                    double toWidth = window.Width;
                    double toHeight = window.Height;

                    if (!double.IsNaN(toWidth) && !double.IsNaN(toHeight) && toWidth > 1 && toHeight > 1)
                    {
                        window.BeginAnimation(FrameworkElement.WidthProperty,
                            new DoubleAnimation(from.Width / dpiX, toWidth, duration) { EasingFunction = ease });
                        window.BeginAnimation(FrameworkElement.HeightProperty,
                            new DoubleAnimation(from.Height / dpiY, toHeight, duration) { EasingFunction = ease });
                    }
                }

                // 动画结束：清掉所有动画，让窗口回到正常的布局行为（不留动画残留）
                var cleanup = new DispatcherTimer { Interval = duration + TimeSpan.FromMilliseconds(60) };
                cleanup.Tick += (_, __) =>
                {
                    cleanup.Stop();

                    try
                    {
                        scale.BeginAnimation(ScaleTransform.ScaleXProperty, null);
                        scale.BeginAnimation(ScaleTransform.ScaleYProperty, null);
                        root.RenderTransform = null;
                    }
                    catch
                    {
                    }

                    try
                    {
                        window.BeginAnimation(Window.LeftProperty, null);
                        window.BeginAnimation(Window.TopProperty, null);
                        window.BeginAnimation(FrameworkElement.WidthProperty, null);
                        window.BeginAnimation(FrameworkElement.HeightProperty, null);
                        window.Opacity = 1.0;
                    }
                    catch
                    {
                    }

                    _playing = false;
                };
                cleanup.Start();
            }
            catch
            {
                // 任何异常都不影响窗口正常显示
                try
                {
                    if (window.Content is FrameworkElement r)
                        r.RenderTransform = null;

                    window.Opacity = 1.0;
                }
                catch
                {
                }

                _playing = false;
            }
        }
    }
}
