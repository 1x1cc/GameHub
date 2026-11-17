using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace GameHub.Controls
{
    /// <summary>
    /// 圆形滑块开关（卡片总开关）。
    ///
    /// 交互：
    ///   1) 单击 —— 直接切换开 / 关；
    ///   2) 按住左右拖动 —— 松手时按拖到的那一侧决定开 / 关，即“左右滑”。
    ///
    /// 外观由模板里的 PART_Track（轨道）与 PART_Thumb（圆形滑块）决定，
    /// 样式定义在 MainWindow.xaml 里，不改动全局主题。
    /// </summary>
    public class ToggleSlider : ToggleButton
    {
        /// <summary>拖动超过这个距离才算“滑动”，否则按单击处理。</summary>
        private const double DragThreshold = 4.0;

        /// <summary>滑块左右各留出的内边距（与模板里的 Margin 保持一致）。</summary>
        private const double ThumbMargin = 3.0;

        /// <summary>果冻动效时长（毫秒）。</summary>
        private const double JellyDurationMs = 350.0;

        /// <summary>滑动途中横向拉长的额外宽度（胶囊形）。</summary>
        private const double JellyStretch = 7.0;

        private Border? _track;
        private Border? _thumb;
        private TranslateTransform? _shift;

        /// <summary>滑块的静止宽度（拉长动画期间 Width 会被改写，不能拿它算行程）。</summary>
        private double _thumbBaseWidth = 20.0;

        /// <summary>自有的轨道画刷：颜色动效需要一个可动画的 SolidColorBrush。</summary>
        private SolidColorBrush? _trackBrush;

        private bool _dragging;
        private double _startX;
        private double _startOffset;

        /// <summary>开启状态下的轨道颜色（默认沿用 GameHub 的绿色）。</summary>
        public static readonly DependencyProperty OnBrushProperty =
            DependencyProperty.Register(nameof(OnBrush), typeof(Brush), typeof(ToggleSlider),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0x50, 0xC8, 0x78))));

        /// <summary>关闭状态下的轨道颜色（默认灰色）。</summary>
        public static readonly DependencyProperty OffBrushProperty =
            DependencyProperty.Register(nameof(OffBrush), typeof(Brush), typeof(ToggleSlider),
                new PropertyMetadata(new SolidColorBrush(Color.FromRgb(0xC9, 0xCF, 0xD6))));

        public Brush OnBrush
        {
            get => (Brush)GetValue(OnBrushProperty);
            set => SetValue(OnBrushProperty, value);
        }

        public Brush OffBrush
        {
            get => (Brush)GetValue(OffBrushProperty);
            set => SetValue(OffBrushProperty, value);
        }

        public ToggleSlider()
        {
            // 卡片整体是可点的：开关自己处理焦点与鼠标，避免抢走卡片的键盘导航
            Focusable = false;
            Cursor = Cursors.Hand;
            VerticalAlignment = VerticalAlignment.Center;
        }

        public override void OnApplyTemplate()
        {
            base.OnApplyTemplate();

            _track = GetTemplateChild("PART_Track") as Border;
            _thumb = GetTemplateChild("PART_Thumb") as Border;

            if (_thumb != null)
            {
                // 用代码挂 TranslateTransform：模板里不必再声明一次，
                // 拖动与动画都只操作这一个对象，状态不会打架。
                _shift = new TranslateTransform();
                _thumb.RenderTransform = _shift;

                if (!double.IsNaN(_thumb.Width) && _thumb.Width > 0)
                    _thumbBaseWidth = _thumb.Width;
            }

            UpdateVisual(animate: false);
        }

        protected override void OnChecked(RoutedEventArgs e)
        {
            base.OnChecked(e);
            UpdateVisual(animate: true);
        }

        protected override void OnUnchecked(RoutedEventArgs e)
        {
            base.OnUnchecked(e);
            UpdateVisual(animate: true);
        }

        /// <summary>滑块可移动的最大距离（轨道宽度 - 滑块宽度 - 左右留白）。
        /// 窗口还没渲染完（ActualWidth=NaN）或模板还没加载完（_thumb==null）时，
        /// 返回 0 而不是让动画拿到 NaN 值导致崩溃（原版 GlassBoard 的 ToggleSlider 同样有这坑）。
        /// </summary>
        private double MaxOffset
        {
            get
            {
                if (double.IsNaN(ActualWidth))
                    return 0.0;

                return Math.Max(0.0, ActualWidth - _thumbBaseWidth - ThumbMargin * 2.0);
            }
        }

        /// <summary>把轨道颜色与滑块位置刷成当前状态（果冻：先拉长成胶囊、到终点回弹恢复圆形）。
        /// 如果 MaxOffset 是 NaN（窗口还没渲染完），直接跳到目标位置，不启动动画。
        /// </summary>
        private void UpdateVisual(bool animate)
        {
            if (_shift == null || _thumb == null)
                return;

            bool on = IsChecked == true;
            double target = on ? MaxOffset : 0.0;

            if (!animate || double.IsNaN(target))
            {
                _shift.BeginAnimation(TranslateTransform.XProperty, null);
                _shift.X = target;

                _thumb.BeginAnimation(FrameworkElement.WidthProperty, null);
                _thumb.Width = _thumbBaseWidth;

                ApplyTrackBrush(on, animate: false);
                return;
            }

            // 1) 圆点横向拉长成胶囊（到 45% 行程最宽），随后回弹恢复圆形
            var jelly = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(JellyDurationMs)
            };
            jelly.KeyFrames.Add(new EasingDoubleKeyFrame(_thumbBaseWidth + JellyStretch, KeyTime.FromPercent(0.45))
            {
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            });
            jelly.KeyFrames.Add(new EasingDoubleKeyFrame(_thumbBaseWidth, KeyTime.FromPercent(1.0))
            {
                EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.9 }
            });
            _thumb.BeginAnimation(FrameworkElement.WidthProperty, jelly);

            // 2) 滑向另一端（带回弹的弹簧曲线）
            _shift.BeginAnimation(TranslateTransform.XProperty,
                new DoubleAnimation(target, TimeSpan.FromMilliseconds(JellyDurationMs))
                {
                    EasingFunction = new BackEase { EasingMode = EasingMode.EaseOut, Amplitude = 0.45 }
                });

            // 3) 底色由灰渐变到主题色
            ApplyTrackBrush(on, animate: true);
        }

        /// <summary>轨道底色：关=灰、开=主题色，两态之间做颜色渐变（跟主题走）。</summary>
        private void ApplyTrackBrush(bool on, bool animate)
        {
            if (_track == null)
                return;

            var target = on ? AccentColor() : OffColor();

            if (_trackBrush == null)
            {
                _trackBrush = new SolidColorBrush(target);
                _track.Background = _trackBrush;
                return;
            }

            if (!animate)
            {
                _trackBrush.BeginAnimation(SolidColorBrush.ColorProperty, null);
                _trackBrush.Color = target;
                return;
            }

            _trackBrush.BeginAnimation(SolidColorBrush.ColorProperty,
                new ColorAnimation(target, TimeSpan.FromMilliseconds(JellyDurationMs))
                {
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                });
        }

        /// <summary>开启态颜色：优先用主题绑定的 OnBrush（随主题色变化），否则回退强调色资源。</summary>
        private Color AccentColor()
        {
            if (OnBrush is SolidColorBrush own)
                return own.Color;

            if (TryFindResource("AccentBrush") is SolidColorBrush accent)
                return accent.Color;

            return Color.FromRgb(0x34, 0xD0, 0x94);
        }

        /// <summary>关闭态颜色（灰色轨道）。</summary>
        private Color OffColor()
            => OffBrush is SolidColorBrush off ? off.Color : Color.FromRgb(0xC9, 0xCF, 0xD6);

        protected override void OnRenderSizeChanged(SizeChangedInfo info)
        {
            base.OnRenderSizeChanged(info);

            // 卡片宽度变化（窗口缩放）后重新摆正滑块；拖动中不要打断手感
            if (!_dragging)
                UpdateVisual(animate: false);
        }

        protected override void OnPreviewMouseLeftButtonDown(MouseButtonEventArgs e)
        {
            // 卡片整块都能点开面板，这里必须吃掉事件，
            // 否则拖滑块会连带把功能面板打开。
            e.Handled = true;

            _dragging = false;
            _startX = e.GetPosition(this).X;
            _startOffset = _shift?.X ?? 0.0;

            CaptureMouse();
            base.OnPreviewMouseLeftButtonDown(e);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            if (IsMouseCaptured && e.LeftButton == MouseButtonState.Pressed && _shift != null)
            {
                double delta = e.GetPosition(this).X - _startX;

                if (!_dragging && Math.Abs(delta) >= DragThreshold)
                    _dragging = true;

                if (_dragging)
                {
                    // 跟手移动（不加动画，否则会有拖拽延迟感）
                    _shift.BeginAnimation(TranslateTransform.XProperty, null);
                    _shift.X = Math.Max(0.0, Math.Min(MaxOffset, _startOffset + delta));
                }
            }

            base.OnMouseMove(e);
        }

        protected override void OnPreviewMouseLeftButtonUp(MouseButtonEventArgs e)
        {
            e.Handled = true;

            if (IsMouseCaptured)
                ReleaseMouseCapture();

            if (_dragging)
            {
                // 滑动：按松手时滑块偏在哪一侧决定开关
                bool on = (_shift?.X ?? 0.0) > MaxOffset / 2.0;

                if (on != (IsChecked == true))
                    IsChecked = on;
                else
                    UpdateVisual(animate: true);   // 状态没变也要弹回正确位置
            }
            else
            {
                // 单击：直接切换
                IsChecked = IsChecked != true;
            }

            _dragging = false;
            base.OnPreviewMouseLeftButtonUp(e);
        }

        protected override void OnLostMouseCapture(MouseEventArgs e)
        {
            _dragging = false;
            base.OnLostMouseCapture(e);
        }
    }
}
