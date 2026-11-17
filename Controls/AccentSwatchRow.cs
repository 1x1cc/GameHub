#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using GameHub.Utils;

namespace GameHub.Controls
{
    /// <summary>
    /// 主题色「菱形色块」选择器（参照 Pavise 的 ColorSwatch）：
    ///   · 每块是自绘菱形，**不再套 Button**——默认按钮主题在悬停时会画一块浅色底并带内边距，
    ///     那正是"鼠标放上去冒出白底且不对称"的原因；
    ///   · 悬停只在整个方形区域内轻微泛白（覆盖层用主题前景色，深浅主题都合适，左右上下对称）；
    ///   · 选中只换描边颜色、线宽恒为 1，所以不会出现选中后偏移 1px 的不对称。
    /// </summary>
    public static class AccentSwatchRow
    {
        private const double SwatchSize = 22;
        private const double HoverOpacity = 0.14;

        /// <summary>生成一整排菱形色块（横排），picked 回调传出被点的 #FFRRGGBB。</summary>
        public static FrameworkElement Create(string? currentHex, Action<string>? picked)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center
            };

            foreach (var preset in ThemeManager.AccentPresets)
                row.Children.Add(BuildSwatch(preset.Name, preset.Accent, SameColor(preset.Accent, currentHex), picked));

            return row;
        }

        private static FrameworkElement BuildSwatch(string name, string accentHex, bool selected, Action<string>? picked)
        {
            Color color = Parse(accentHex);

            var diamond = new Path
            {
                Data = Geometry.Parse("M 7,0 L 14,7 L 7,14 L 0,7 Z"),
                Fill = new SolidColorBrush(color),
                StrokeThickness = 1,
                Stretch = Stretch.None,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                SnapsToDevicePixels = true,
                IsHitTestVisible = false
            };
            diamond.SetResourceReference(Shape.StrokeProperty, selected ? "FgBrush" : "StrokeHiBrush");

            // 悬停泛白层：用主题前景色 + 低透明度，深色主题下就是"轻微泛白"，浅色主题下是轻微加深
            var hover = new Border
            {
                CornerRadius = new CornerRadius(6),
                Opacity = 0,
                IsHitTestVisible = false
            };
            hover.SetResourceReference(Border.BackgroundProperty, "FgBrush");

            var stack = new Grid();
            stack.Children.Add(diamond);
            stack.Children.Add(hover);

            var tile = new Border
            {
                Width = SwatchSize,
                Height = SwatchSize,
                Margin = new Thickness(0, 0, 8, 0),
                CornerRadius = new CornerRadius(6),
                BorderThickness = new Thickness(1),   // 恒为 1：选中只换颜色，避免布局位移
                Background = Brushes.Transparent,
                Cursor = Cursors.Hand,
                ToolTip = name,
                Child = stack,
                SnapsToDevicePixels = true
            };
            tile.SetResourceReference(Border.BorderBrushProperty, selected ? "FgBrush" : "StrokeBrush");

            tile.MouseEnter += (_, __) => hover.Opacity = HoverOpacity;
            tile.MouseLeave += (_, __) => hover.Opacity = 0;

            tile.MouseLeftButtonDown += (_, __) =>
            {
                try
                {
                    picked?.Invoke("#FF" + color.R.ToString("X2") + color.G.ToString("X2") + color.B.ToString("X2"));
                }
                catch
                {
                    // 忽略：选色失败不影响界面
                }
            };

            return tile;
        }

        private static bool SameColor(string a, string? b)
            => !string.IsNullOrWhiteSpace(b) && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

        private static Color Parse(string hex)
        {
            try { return (Color)ColorConverter.ConvertFromString(hex); }
            catch { return Color.FromRgb(0x34, 0xD0, 0x94); }
        }
    }
}
