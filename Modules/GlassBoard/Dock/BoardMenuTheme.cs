using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard.Dock
{
    /// <summary>
    /// 右键气泡的玻璃样式。
    ///
    /// 上一版只改颜色、不换模板，结果**没生效**：WPF 自带的 ContextMenu / MenuItem 模板
    /// 是用 StaticResource 直接取系统色的，StaticResource 在模板解析时就固化了，
    /// 挂在菜单实例上的资源覆盖不到它——于是底色还是白的，而 Foreground 生效了，
    /// 变成“白底白字”，完全看不清（用户反馈）。
    ///
    /// 所以这一版改成自建模板，颜色全部用自己的画刷，不再依赖任何系统色键：
    ///   - ContextMenu：一个圆角 Border + 竖排项宿主；
    ///   - MenuItem：Border(底) + Grid(勾选列 / 标题 / 子菜单箭头) + Popup(子菜单)。
    /// 子菜单（PART_Popup）和勾选标记都在模板里补全，不会像“只换模板不补结构”那样丢失。
    /// </summary>
    public static class BoardMenuTheme
    {
        public static void Apply(ContextMenu? menu, double opacityPercent)
        {
            if (menu == null)
                return;

            try
            {
                // 透明度跟着面板滑块走，但留一个下限：气泡是直接浮在桌面上的，
                // 太透会让白字看不清（上一轮的问题就是看不清）。
                double overlay = DockRules.GlassOpacity(opacityPercent) / 100.0;
                double k = Math.Max(0.78, overlay);

                var panel = Freeze(Color.FromArgb(Alpha(248 * k), 26, 32, 46));
                var edge = Freeze(Color.FromArgb(Alpha(120 * k), 255, 255, 255));
                var hover = Freeze(Color.FromArgb(Alpha(120 * k), 130, 180, 255));
                var line = Freeze(Color.FromArgb(Alpha(80 * k), 255, 255, 255));

                menu.Background = panel;
                menu.Foreground = Brushes.White;
                menu.BorderBrush = edge;
                menu.BorderThickness = new Thickness(1);
                menu.Padding = new Thickness(0);

                menu.Template = MenuTemplate(panel, edge);
                menu.Resources[typeof(MenuItem)] = ItemStyle(panel, edge, hover);
                menu.Resources[typeof(Separator)] = SeparatorStyle(line);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 菜单：套用玻璃样式失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>ContextMenu 外壳：圆角玻璃底 + 竖排项宿主。</summary>
        private static ControlTemplate MenuTemplate(Brush panel, Brush edge)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.BackgroundProperty, panel);
            border.SetValue(Border.BorderBrushProperty, edge);
            border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            border.SetValue(Border.PaddingProperty, new Thickness(4));
            border.SetValue(UIElement.SnapsToDevicePixelsProperty, true);

            var host = new FrameworkElementFactory(typeof(StackPanel));
            host.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
            host.SetValue(StackPanel.IsItemsHostProperty, true);

            // 必须先 AppendChild 再赋给 VisualTree：模板一旦“密封”就不能再改结构。
            border.AppendChild(host);

            return new ControlTemplate(typeof(ContextMenu)) { VisualTree = border };
        }

        /// <summary>菜单项：勾选列 + 标题 + 子菜单箭头 + 子菜单 Popup，全部自绘。</summary>
        private static Style ItemStyle(Brush panel, Brush edge, Brush hover)
        {
            var root = new FrameworkElementFactory(typeof(Border));
            root.Name = "Bd";
            root.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            root.SetValue(Border.CornerRadiusProperty, new CornerRadius(7));
            root.SetValue(Border.PaddingProperty, new Thickness(10, 6, 12, 6));
            root.SetValue(Border.MarginProperty, new Thickness(1, 0, 1, 0));

            var grid = new FrameworkElementFactory(typeof(Grid));

            var checks = new FrameworkElementFactory(typeof(ColumnDefinition));
            checks.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);

            var headers = new FrameworkElementFactory(typeof(ColumnDefinition));
            headers.SetValue(ColumnDefinition.WidthProperty, new GridLength(1, GridUnitType.Star));

            var arrows = new FrameworkElementFactory(typeof(ColumnDefinition));
            arrows.SetValue(ColumnDefinition.WidthProperty, GridLength.Auto);

            grid.AppendChild(checks);
            grid.AppendChild(headers);
            grid.AppendChild(arrows);

            // 勾选标记（IsChecked 才显示）
            var check = new FrameworkElementFactory(typeof(TextBlock));
            check.Name = "Check";
            check.SetValue(TextBlock.TextProperty, "✓");
            check.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            check.SetValue(TextBlock.FontSizeProperty, 12.0);
            check.SetValue(TextBlock.MarginProperty, new Thickness(0, 0, 8, 0));
            check.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            check.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            check.SetValue(Grid.ColumnProperty, 0);
            grid.AppendChild(check);

            // 标题
            var header = new FrameworkElementFactory(typeof(ContentPresenter));
            header.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            header.SetValue(ContentPresenter.RecognizesAccessKeyProperty, true);
            header.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            header.SetValue(Grid.ColumnProperty, 1);
            grid.AppendChild(header);

            // 子菜单箭头（Role == SubmenuHeader 才显示）
            var arrow = new FrameworkElementFactory(typeof(TextBlock));
            arrow.Name = "Arrow";
            arrow.SetValue(TextBlock.TextProperty, "›");
            arrow.SetValue(TextBlock.ForegroundProperty, Brushes.White);
            arrow.SetValue(TextBlock.FontSizeProperty, 14.0);
            arrow.SetValue(TextBlock.MarginProperty, new Thickness(12, 0, 0, 0));
            arrow.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            arrow.SetValue(UIElement.VisibilityProperty, Visibility.Collapsed);
            arrow.SetValue(Grid.ColumnProperty, 2);
            grid.AppendChild(arrow);

            // 子菜单 Popup：名字必须是 PART_Popup，MenuItem 靠它开关子菜单
            var popup = new FrameworkElementFactory(typeof(Popup));
            popup.Name = "PART_Popup";
            popup.SetValue(Popup.PlacementProperty, PlacementMode.Right);
            popup.SetValue(Popup.AllowsTransparencyProperty, true);
            popup.SetValue(Popup.PopupAnimationProperty, PopupAnimation.Fade);
            popup.SetValue(Popup.FocusableProperty, false);
            popup.SetValue(Popup.IsOpenProperty, new TemplateBindingExtension(MenuItem.IsSubmenuOpenProperty));

            var popupBorder = new FrameworkElementFactory(typeof(Border));
            popupBorder.SetValue(Border.BackgroundProperty, panel);
            popupBorder.SetValue(Border.BorderBrushProperty, edge);
            popupBorder.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            popupBorder.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            popupBorder.SetValue(Border.PaddingProperty, new Thickness(4));

            var popupHost = new FrameworkElementFactory(typeof(StackPanel));
            popupHost.SetValue(StackPanel.OrientationProperty, Orientation.Vertical);
            popupHost.SetValue(StackPanel.IsItemsHostProperty, true);

            popupBorder.AppendChild(popupHost);
            popup.AppendChild(popupBorder);
            popup.SetValue(Grid.ColumnProperty, 2);
            grid.AppendChild(popup);

            root.AppendChild(grid);

            var template = new ControlTemplate(typeof(MenuItem)) { VisualTree = root };

            // 悬停高亮：样式里的模板触发器直接改命名元素，不依赖系统高亮色
            var highlighted = new Trigger { Property = MenuItem.IsHighlightedProperty, Value = true };
            highlighted.Setters.Add(new Setter(Border.BackgroundProperty, hover, "Bd"));
            template.Triggers.Add(highlighted);

            var isChecked = new Trigger { Property = MenuItem.IsCheckedProperty, Value = true };
            isChecked.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Check"));
            template.Triggers.Add(isChecked);

            var submenu = new Trigger { Property = MenuItem.RoleProperty, Value = MenuItemRole.SubmenuHeader };
            submenu.Setters.Add(new Setter(UIElement.VisibilityProperty, Visibility.Visible, "Arrow"));
            template.Triggers.Add(submenu);

            var style = new Style(typeof(MenuItem));
            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Brushes.White));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
            style.Setters.Add(new Setter(Control.CursorProperty, Cursors.Hand));
            style.Setters.Add(new Setter(Control.HorizontalContentAlignmentProperty, HorizontalAlignment.Stretch));

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(Control.ForegroundProperty, Freeze(Color.FromArgb(120, 255, 255, 255))));
            style.Triggers.Add(disabled);

            return style;
        }

        private static Style SeparatorStyle(Brush line)
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.HeightProperty, 1.0);
            border.SetValue(Border.MarginProperty, new Thickness(8, 5, 8, 5));
            border.SetValue(Border.BackgroundProperty, line);

            var style = new Style(typeof(Separator));
            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(Separator)) { VisualTree = border }));
            return style;
        }

        private static byte Alpha(double value)
            => (byte)Math.Max(0, Math.Min(255, Math.Round(value)));

        private static SolidColorBrush Freeze(Color color)
        {
            var brush = new SolidColorBrush(color);
            brush.Freeze();
            return brush;
        }
    }
}
