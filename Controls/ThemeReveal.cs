#nullable enable
using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;

namespace GameHub.Controls
{
    /// <summary>
    /// 以某个控件为圆心的换肤过渡（第 6 条）：
    ///   1) 先把「换肤前」的窗口画面截成位图；
    ///   2) 执行换肤（主题立刻生效）；
    ///   3) 把旧画面贴回窗口最上层，裁成「除了一个越来越大的圆以外的部分」，
    ///      圆从按钮中心向外扩散 → 视觉上就是新配色以按钮为圆心扩散开；
    ///   4) 动画结束移除贴图。
    /// 任何一步出错都直接跳过过渡（换肤本身不受影响）。
    /// </summary>
    public static class ThemeReveal
    {
        public static void PlayWith(FrameworkElement? root, FrameworkElement? anchor, Action? applyTheme)
        {
            if (root == null || anchor == null)
            {
                applyTheme?.Invoke();
                return;
            }

            Panel? host = null;
            Grid? layer = null;

            // 1) 换肤前快照
            try
            {
                root.UpdateLayout();
                double w = root.ActualWidth;
                double h = root.ActualHeight;

                if (w >= 1 && h >= 1 && root is Panel panel)
                {
                    double dpi = 96;
                    var snapshot = new RenderTargetBitmap(
                        (int)Math.Ceiling(w), (int)Math.Ceiling(h), dpi, dpi, PixelFormats.Pbgra32);
                    snapshot.Render(root);

                    layer = new Grid { IsHitTestVisible = false, SnapsToDevicePixels = true };
                    layer.Children.Add(new Image { Source = snapshot, Stretch = Stretch.None });
                    host = panel;
                }
            }
            catch
            {
                host = null;
                layer = null;
            }

            // 2) 换肤
            applyTheme?.Invoke();

            if (host == null || layer == null)
                return;

            // 3) 贴旧画面 + 圆形扩散
            try
            {
                Point center;
                try
                {
                    center = anchor.TransformToAncestor(root)
                        .Transform(new Point(anchor.ActualWidth / 2, anchor.ActualHeight / 2));
                }
                catch
                {
                    center = new Point(root.ActualWidth / 2, root.ActualHeight / 2);
                }

                double maxRadius = Math.Max(60, MaxCornerDistance(center, root.ActualWidth, root.ActualHeight));
                var hole = new EllipseGeometry(center, 0, 0);
                layer.Clip = new CombinedGeometry(
                    GeometryCombineMode.Exclude,
                    new RectangleGeometry(new Rect(0, 0, root.ActualWidth, root.ActualHeight)),
                    hole);

                host.Children.Add(layer);

                var duration = TimeSpan.FromMilliseconds(420);
                var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

                var radiusX = new DoubleAnimation(0, maxRadius, duration) { EasingFunction = ease };
                var radiusY = new DoubleAnimation(0, maxRadius, duration) { EasingFunction = ease };
                radiusX.Completed += (_, __) =>
                {
                    try { host.Children.Remove(layer); } catch { }
                };

                hole.BeginAnimation(EllipseGeometry.RadiusXProperty, radiusX);
                hole.BeginAnimation(EllipseGeometry.RadiusYProperty, radiusY);
            }
            catch
            {
                try { host.Children.Remove(layer); } catch { }
            }
        }

        private static double MaxCornerDistance(Point center, double width, double height)
        {
            double best = 0;
            var corners = new[]
            {
                new Point(0, 0),
                new Point(width, 0),
                new Point(0, height),
                new Point(width, height)
            };

            foreach (var corner in corners)
            {
                double dx = corner.X - center.X;
                double dy = corner.Y - center.Y;
                best = Math.Max(best, Math.Sqrt(dx * dx + dy * dy));
            }

            return best;
        }
    }
}
