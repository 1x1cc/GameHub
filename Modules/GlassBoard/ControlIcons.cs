using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 控制中心小球的图标画笔（对应原版 ControlIcons）。
    ///
    /// 做法照搬原版：先在一块 24×24 的画布上按给定数据画路径，
    /// 再整体塞进 Viewbox 缩放到目标字号——所以同一份几何数据，
    /// 26 的球和 42 的球看起来是同一枚图标，不会粗细不一。
    /// switches 样式额外点两个小圆点，正是原版那两枚拨杆的“头”。
    /// </summary>
    public static class ControlIcons
    {
        public static Viewbox Create(string kind, Brush ink, double size)
        {
            var canvas = new Canvas
            {
                Width = 24.0,
                Height = 24.0,
                IsHitTestVisible = false
            };

            string source = ControlIconRules.Style(kind) switch
            {
                // 三种小球样式（原版数据，逐字节一致）
                "tiles" => "M5,3 H9 Q11,3 11,5 V9 Q11,11 9,11 H5 Q3,11 3,9 V5 Q3,3 5,3 Z M17,3 H19 Q21,3 21,5 V7 Q21,9 19,9 H17 Q15,9 15,7 V5 Q15,3 17,3 Z M5,15 H7 Q9,15 9,17 V19 Q9,21 7,21 H5 Q3,21 3,19 V17 Q3,15 5,15 Z M15,13 H19 Q21,13 21,15 V19 Q21,21 19,21 H15 Q13,21 13,19 V15 Q13,13 15,13 Z",
                "spark" => "M12,2 C12,8 8,12 2,12 C8,12 12,16 12,22 C12,16 16,12 22,12 C16,12 12,8 12,2 Z",

                // 面板里还会用到的几个图标（原版 ControlIcons 里的同一张表）
                "wifi" => "M2,8 Q12,0 22,8 M5,12 Q12,6 19,12 M8,16 Q12,12 16,16 M11.7,20 L12.3,20",
                "bluetooth" => "M7,6 L18,17 L12,22 L12,2 L18,7 L7,18",
                "power" => "M12,2 V11 M6,5 A9,9 0 1 0 18,5",
                "close" => "M6,6 L18,18 M18,6 L6,18",
                "chevron" => "M6,9 L12,15 L18,9",
                "check" => "M5,12 L10,17 L20,7",
                "refresh" => "M20,8 A9,9 0 1 0 20,17 M20,3 V9 H14",
                "plus" => "M12,4 V20 M4,12 H20",
                "settings" => "M19.39,8.94 L22.34,10.18 L22.34,13.82 L19.39,15.06 L19.39,15.06 L20.60,18.02 L18.02,20.60 L15.06,19.39 L15.06,19.39 L13.82,22.34 L10.18,22.34 L8.94,19.39 L8.94,19.39 L5.98,20.60 L3.40,18.02 L4.61,15.06 L4.61,15.06 L1.66,13.82 L1.66,10.18 L4.61,8.94 L4.61,8.94 L3.40,5.98 L5.98,3.40 L8.94,4.61 L8.94,4.61 L10.18,1.66 L13.82,1.66 L15.06,4.61 L15.06,4.61 L18.02,3.40 L20.60,5.98 L19.39,8.94 Z M16,12 A4,4 0 1 1 8,12 A4,4 0 1 1 16,12",

                // switches：两枚圆角拨杆
                _ => "M7,2 H17 A4,4 0 0 1 17,10 H7 A4,4 0 0 1 7,2 Z M7,14 H17 A4,4 0 0 1 17,22 H7 A4,4 0 0 1 7,14 Z"
            };

            canvas.Children.Add(new Path
            {
                Data = Geometry.Parse(source),
                Stroke = ink,
                StrokeThickness = 1.65,
                StrokeStartLineCap = PenLineCap.Round,
                StrokeEndLineCap = PenLineCap.Round,
                StrokeLineJoin = PenLineJoin.Round
            });

            // switches 样式的两个拨杆头（原版就是在 (8,6) 与 (16,18) 各点一个 3.5 的实心圆）
            if (ControlIconRules.Style(kind) == "switches")
            {
                foreach (var point in new[] { new Point(8.0, 6.0), new Point(16.0, 18.0) })
                {
                    var dot = new Ellipse
                    {
                        Width = 3.5,
                        Height = 3.5,
                        Fill = ink
                    };

                    Canvas.SetLeft(dot, point.X - 1.75);
                    Canvas.SetTop(dot, point.Y - 1.75);
                    canvas.Children.Add(dot);
                }
            }

            return new Viewbox
            {
                Child = canvas,
                Width = size,
                Height = size,
                Stretch = Stretch.Uniform,
                IsHitTestVisible = false
            };
        }
    }
}
