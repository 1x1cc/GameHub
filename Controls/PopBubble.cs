#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace GameHub.Controls
{
    /// <summary>
    /// 主题一致的轻量气泡。两种用途：
    ///   1) 「已应用 / 已保存」提示：浮在按钮上方，2 秒后自动消失（第 7 条）；
    ///   2) 「?」说明气泡：点问号弹出，点其他地方才消失（第 5 条）。
    ///
    /// 颜色全部走主题键（Surface / Stroke / Fg / Dim / Accent），用 SetResourceReference
    /// 绑定，所以在气泡存活期间切换昼夜或主题色也会立刻跟着变。
    /// </summary>
    public static class PopBubble
    {
        private static Popup? _current;
        private static DispatcherTimer? _timer;

        /// <summary>短提示（默认 2 秒自动消失）。</summary>
        public static void Show(FrameworkElement anchor, string text, double seconds = 2.0)
            => ShowCore(anchor, text, seconds, help: false);

        /// <summary>说明气泡：不自动消失，点别处关闭。</summary>
        public static void ShowHelp(FrameworkElement anchor, string text)
            => ShowCore(anchor, text, 0, help: true);

        public static void Close()
        {
            try { _timer?.Stop(); } catch { }
            _timer = null;
            try { if (_current != null) _current.IsOpen = false; } catch { }
            _current = null;
        }

        private static void ShowCore(FrameworkElement anchor, string text, double seconds, bool help)
        {
            if (anchor == null || string.IsNullOrWhiteSpace(text))
                return;

            Close();

            var body = new TextBlock
            {
                Text = text,
                FontSize = 12,
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = help ? 380 : 280
            };
            body.SetResourceReference(TextBlock.ForegroundProperty, "FgBrush");

            // 20/16/4 双层圆角：外层 20、内层 16、中间 4px 填充（与卡片、通知气泡同一套规范）
            var inner = new Border
            {
                CornerRadius = new CornerRadius(16),
                Padding = new Thickness(12, 9, 12, 9),
                Child = body
            };
            inner.SetResourceReference(Border.BackgroundProperty, "BgBrush");

            var border = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(20),
                Padding = new Thickness(4),
                Child = inner,
                Effect = new DropShadowEffect { BlurRadius = 18, ShadowDepth = 2, Opacity = 0.40, Color = Colors.Black },
                RenderTransform = new TranslateTransform(0, 6)
            };
            border.SetResourceReference(Border.BackgroundProperty, "SurfaceBrush");
            border.SetResourceReference(Border.BorderBrushProperty, help ? "AccentBrush" : "StrokeBrush");

            var popup = new Popup
            {
                PlacementTarget = anchor,
                Placement = PlacementMode.Top,
                VerticalOffset = -8,
                HorizontalOffset = 0,
                StaysOpen = false,                 // 点其他任何地方自动消失
                AllowsTransparency = true,
                PopupAnimation = PopupAnimation.Fade,
                Child = border
            };

            _current = popup;
            popup.Closed += (_, __) =>
            {
                if (ReferenceEquals(_current, popup))
                {
                    try { _timer?.Stop(); } catch { }
                    _timer = null;
                    _current = null;
                }
            };

            popup.IsOpen = true;

            // 出现动画：淡入 + 轻微上浮
            border.BeginAnimation(UIElement.OpacityProperty,
                new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(120)));
            if (border.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty,
                    new DoubleAnimation(6, 0, TimeSpan.FromMilliseconds(140)));

            if (seconds > 0)
            {
                _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(seconds) };
                _timer.Tick += (_, __) => Close();
                _timer.Start();
            }
        }

        /// <summary>在按钮上方浮出「已应用 / 已保存」，替代右下角通知与居中弹窗。</summary>
        public static void ShowApplied(FrameworkElement? anchor, string text = "已应用")
        {
            if (anchor == null)
                return;
            Show(anchor, text, 2.0);
        }
    }
}
