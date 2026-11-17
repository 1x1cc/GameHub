using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// GlassBoard 自己的视觉基座——配色与控件配方照抄逆解出来的原版
    /// （对应原版 GlassWindow + Polish + GlassMaterials 三个文件）。
    ///
    /// 为什么要单独一份：GameHub 的卡片界面是浅蓝卡片风，而 GlassBoard 是深色玻璃风。
    /// 设置界面属于 GlassBoard 的一部分，用 GameHub 那套会显得像两个不同的软件。
    ///
    /// 原版取值（深色分支，逐一对应，别随意改）：
    ///   墨色 #F4F6FC · 次要 #9CA9BF · 卡片 #70263043 · 描边 #24D5E3FF
    ///   强调 #6A9BFA · 悬停 #405478 · 玻璃底 rgb(24,26,32) + 透明度换算
    /// 圆角：按钮 11 / 分区卡片 18 / 右键气泡 10。
    /// </summary>
    public static class GlassBoardTheme
    {
        private static readonly Dictionary<string, Brush> Cache = new(StringComparer.OrdinalIgnoreCase);

        private static Style? _button;
        private static Style? _danger;
        private static Style? _tabItem;
        private static Style? _tabControl;
        private static Style? _textBox;
        private static Style? _tip;

        // ---------------- 配色（原版 Polish / MenuTheme 同款） ----------------

        /// <summary>正文墨色。</summary>
        public static Brush Ink => Color("#F4F6FC");

        /// <summary>次要文字。</summary>
        public static Brush Muted => Color("#9CA9BF");

        /// <summary>卡片/按钮底（半透明深色）。</summary>
        public static Brush Card => Color("#70263043");

        /// <summary>细描边。</summary>
        public static Brush Line => Color("#24D5E3FF");

        /// <summary>强调色（原版 Polish.Accent）。</summary>
        public static Brush Accent => Color("#6A9BFA");

        /// <summary>悬停高亮（原版按钮悬停色）。</summary>
        public static Brush Hover => Color("#405478");

        /// <summary>危险操作（原版没有这一档，按同一套玻璃质感补的暗红）。</summary>
        public static Brush Danger => Color("#B54B45");

        public static Brush DangerHover => Color("#D2605A");

        /// <summary>按十六进制取画刷：带缓存并冻结（对应原版 Polish.Color）。</summary>
        public static Brush Color(string hex)
        {
            if (Cache.TryGetValue(hex, out var cached))
                return cached;

            var brush = new SolidColorBrush((System.Windows.Media.Color)ColorConverter.ConvertFromString(hex));
            brush.Freeze();
            Cache[hex] = brush;
            return brush;
        }

        /// <summary>
        /// 玻璃底面填充（对应原版 GlassMaterials.Fill 的深色分支、未自定义底色时）：
        /// 底色 rgb(24,26,32)，alpha = 透明度设置 × 255 / 100。
        /// </summary>
        public static Brush SurfaceFill(double opacityPercent)
        {
            double percent = Dock.DockRules.GlassOpacity(opacityPercent);
            byte alpha = (byte)Math.Max(0, Math.Min(255, Math.Round(percent * 255.0 / 100.0)));

            var brush = new SolidColorBrush(System.Windows.Media.Color.FromArgb(alpha, 24, 26, 32));
            brush.Freeze();
            return brush;
        }

        // ---------------- 悬浮窗统一外观 ----------------
        //
        // 四个桌面组件与第三模块悬浮窗共用这一套值。
        // XAML 里那几块 Border 是声明式的，写的是同样的字面量（并注明来源）；
        // C# 建的卡片一律走 ApplyGlassCard。

        /// <summary>悬浮窗统一圆角 18（原版 Polish.Section 同款）。</summary>
        public const double WidgetCorner = 18.0;

        /// <summary>悬浮窗统一底色不透明度（百分比）：70 ⇒ alpha B3 ⇒ #B3181A20。</summary>
        public const double WidgetOpacityPercent = 70.0;

        /// <summary>悬浮窗统一底色：70% 的 rgb(24,26,32)。</summary>
        public static Brush WidgetFill => SurfaceFill(WidgetOpacityPercent);

        /// <summary>悬浮窗统一投影（每处新建一份，避免共享可冻结对象出意外）。</summary>
        public static DropShadowEffect WidgetShadow() => new()
        {
            BlurRadius = 20,
            ShadowDepth = 3,
            Opacity = 0.35,
            Color = Colors.Black
        };

        /// <summary>
        /// 把一块 Border 套成统一的玻璃悬浮窗：底色 / 圆角 / 描边 / 投影。
        /// 改这里，所有走 C# 建的悬浮窗卡片一起变。
        /// </summary>
        public static void ApplyGlassCard(Border card)
        {
            card.CornerRadius = new CornerRadius(WidgetCorner);
            card.Background = WidgetFill;
            card.BorderBrush = Line;
            card.BorderThickness = new Thickness(1);
            card.Effect = WidgetShadow();
        }

        // ---------------- 窗口级样式 ----------------

        /// <summary>
        /// 把整套外观挂到窗口资源上：窗口内的按钮/标签页自动就是玻璃风，
        /// 不需要在每个控件上单独设样式（对应原版 Polish.Apply）。
        /// </summary>
        public static void Apply(Window? window)
        {
            if (window == null)
                return;

            window.UseLayoutRounding = true;
            window.SnapsToDevicePixels = true;
            window.FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            window.Foreground = Ink;
            window.FontSize = 13.0;

            window.Resources[typeof(Button)] = ButtonStyle();
            window.Resources[typeof(TabItem)] = TabItemStyle();
            window.Resources[typeof(TabControl)] = TabControlStyle();
            window.Resources[typeof(TextBox)] = TextBoxStyle();
            window.Resources[typeof(ToolTip)] = ToolTipStyle();
        }

        public static Style ButtonStyle() => _button ??= BuildButton(Card, Hover);

        public static Style DangerButtonStyle() => _danger ??= BuildButton(Danger, DangerHover);

        /// <summary>
        /// 按钮：圆角 11 玻璃底 + 1px 描边，悬停换成高亮色，按下 0.65、禁用 0.4
        /// （数值与原版 Polish.ButtonStyle 一致）。
        /// </summary>
        private static Style BuildButton(Brush fill, Brush hover)
        {
            var style = new Style(typeof(Button));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
            style.Setters.Add(new Setter(Control.BackgroundProperty, fill));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(16, 10, 16, 10)));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 38.0));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Medium));
            style.Setters.Add(new Setter(FrameworkElement.CursorProperty, Cursors.Hand));

            var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            chrome.SetValue(Border.BackgroundProperty, fill);
            chrome.SetValue(Border.BorderBrushProperty, Line);
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            // 关键：模板里的 Border 必须把 Padding 从样式接下来。
            // 不接的话样式上设的内边距会被整段忽略，按钮就只剩“文字高度”，
            // 上下贴边——实测按钮只有 19px 高、字本身就有 17px，所以看着很挤。
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            chrome.AppendChild(presenter);

            var template = new ControlTemplate(typeof(Button)) { VisualTree = chrome };

            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Border.BackgroundProperty, hover, "Chrome"));
            template.Triggers.Add(over);

            var pressed = new Trigger { Property = ButtonBase.IsPressedProperty, Value = true };
            pressed.Setters.Add(new Setter(UIElement.OpacityProperty, 0.65));
            template.Triggers.Add(pressed);

            var disabled = new Trigger { Property = UIElement.IsEnabledProperty, Value = false };
            disabled.Setters.Add(new Setter(UIElement.OpacityProperty, 0.4));
            template.Triggers.Add(disabled);

            var focused = new Trigger { Property = UIElement.IsKeyboardFocusedProperty, Value = true };
            focused.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "Chrome"));
            template.Triggers.Add(focused);

            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Seal();
            return style;
        }

        /// <summary>标签页：未选中用次要色，选中用墨色 + 高亮底（对应原版 TabItem 样式）。</summary>
        public static Style TabItemStyle() => _tabItem ??= BuildTabItem();

        private static Style BuildTabItem()
        {
            var style = new Style(typeof(TabItem));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Muted));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(18, 10, 18, 10)));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 38.0));
            style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 3, 8, 3)));
            style.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.Medium));

            var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(11));
            chrome.SetValue(Border.BackgroundProperty, Brushes.Transparent);
            // 同理：标签页的 Padding 不接下去，选中态那圈气泡就会和文字等高，
            // 视觉上像“气泡把字盖住了”（实测标签只有 17px 高）。
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(ContentPresenter.ContentSourceProperty, "Header");
            presenter.SetValue(FrameworkElement.HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(FrameworkElement.VerticalAlignmentProperty, VerticalAlignment.Center);
            chrome.AppendChild(presenter);

            var template = new ControlTemplate(typeof(TabItem)) { VisualTree = chrome };

            var selected = new Trigger { Property = TabItem.IsSelectedProperty, Value = true };
            selected.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
            selected.Setters.Add(new Setter(Border.BackgroundProperty, Color("#344A70"), "Chrome"));
            template.Triggers.Add(selected);

            var over = new Trigger { Property = UIElement.IsMouseOverProperty, Value = true };
            over.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
            template.Triggers.Add(over);

            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Seal();
            return style;
        }

        /// <summary>
        /// TabControl 外壳：系统默认模板会画一层浅色内容面板，压在玻璃底上很突兀
        /// （上一轮右键气泡白底就是同一类问题）。这里换成「上方标签条 + 下方内容」的全透明布局。
        /// </summary>
        public static Style TabControlStyle() => _tabControl ??= BuildTabControl();

        private static Style BuildTabControl()
        {
            var style = new Style(typeof(TabControl));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Brushes.Transparent));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));

            var grid = new FrameworkElementFactory(typeof(Grid));

            var row0 = new FrameworkElementFactory(typeof(RowDefinition));
            row0.SetValue(RowDefinition.HeightProperty, GridLength.Auto);
            var row1 = new FrameworkElementFactory(typeof(RowDefinition));
            row1.SetValue(RowDefinition.HeightProperty, new GridLength(1, GridUnitType.Star));
            grid.AppendChild(row0);
            grid.AppendChild(row1);

            var headers = new FrameworkElementFactory(typeof(TabPanel), "HeaderPanel");
            headers.SetValue(TabPanel.IsItemsHostProperty, true);
            headers.SetValue(Grid.RowProperty, 0);
            headers.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 0, 6));
            grid.AppendChild(headers);

            var content = new FrameworkElementFactory(typeof(ContentPresenter), "PART_SelectedContentHost");
            content.SetValue(ContentPresenter.ContentSourceProperty, "SelectedContent");
            content.SetValue(ContentPresenter.ContentTemplateProperty, new TemplateBindingExtension(TabControl.SelectedContentTemplateProperty));
            content.SetValue(Grid.RowProperty, 1);
            grid.AppendChild(content);

            style.Setters.Add(new Setter(Control.TemplateProperty, new ControlTemplate(typeof(TabControl)) { VisualTree = grid }));
            style.Seal();
            return style;
        }

        /// <summary>
        /// 悬停提示气泡：系统默认气泡上下只有 1~2px 内边距，字被夹得很难看。
        /// 这里给足内边距，并换成与玻璃一致的深色底。
        /// </summary>
        public static Style ToolTipStyle() => _tip ??= BuildToolTip();

        private static Style BuildToolTip()
        {
            var style = new Style(typeof(ToolTip));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Color("#F0181E2A")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 8, 12, 8)));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 12.5));
            style.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 420.0));

            var chrome = new FrameworkElementFactory(typeof(Border));
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
            chrome.SetValue(Border.BackgroundProperty, Color("#F0181E2A"));
            chrome.SetValue(Border.BorderBrushProperty, Line);
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));
            chrome.AppendChild(new FrameworkElementFactory(typeof(ContentPresenter)));

            style.Setters.Add(new Setter(Control.TemplateProperty,
                new ControlTemplate(typeof(ToolTip)) { VisualTree = chrome }));

            style.Seal();
            return style;
        }

        /// <summary>
        /// 输入框：深色玻璃底 + 圆角 10 + 1px 描边，聚焦时描边变强调色
        /// （取值与原版 Polish.Apply 里的 TextBox 样式一致）。
        /// </summary>
        public static Style TextBoxStyle() => _textBox ??= BuildTextBox();

        private static Style BuildTextBox()
        {
            var style = new Style(typeof(TextBox));
            style.Setters.Add(new Setter(Control.ForegroundProperty, Ink));
            style.Setters.Add(new Setter(Control.BackgroundProperty, Color("#90151E2D")));
            style.Setters.Add(new Setter(Control.BorderBrushProperty, Line));
            style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(1)));
            style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(12, 9, 12, 9)));
            style.Setters.Add(new Setter(Control.FontSizeProperty, 13.0));
            style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 38.0));
            style.Setters.Add(new Setter(TextBoxBase.CaretBrushProperty, Accent));
            style.Setters.Add(new Setter(TextBoxBase.SelectionBrushProperty, Accent));

            var chrome = new FrameworkElementFactory(typeof(Border), "Chrome");
            chrome.SetValue(Border.CornerRadiusProperty, new CornerRadius(10));
            chrome.SetValue(Border.BackgroundProperty, Color("#90151E2D"));
            chrome.SetValue(Border.BorderBrushProperty, Line);
            chrome.SetValue(Border.BorderThicknessProperty, new Thickness(1));
            // 和按钮同一个坑：Padding 必须从样式传进模板，否则文字上下贴边
            chrome.SetValue(Border.PaddingProperty, new TemplateBindingExtension(Control.PaddingProperty));

            var host = new FrameworkElementFactory(typeof(ScrollViewer), "PART_ContentHost");
            chrome.AppendChild(host);

            var template = new ControlTemplate(typeof(TextBox)) { VisualTree = chrome };

            var focus = new Trigger { Property = UIElement.IsKeyboardFocusWithinProperty, Value = true };
            focus.Setters.Add(new Setter(Border.BorderBrushProperty, Accent, "Chrome"));
            template.Triggers.Add(focus);

            style.Setters.Add(new Setter(Control.TemplateProperty, template));
            style.Seal();
            return style;
        }

        // ---------------- 分区卡片 ----------------

        /// <summary>
        /// 一节设置卡片：圆角 18 玻璃底 + 1px 描边 + 内边距 20，标题 18 半粗、说明 12 次要色
        /// （版式与原版 Polish.Section 一致）。返回的 Border 直接塞进页面即可。
        /// </summary>
        public static Border Section(string title, string description)
        {
            var stack = new StackPanel();

            stack.Children.Add(new TextBlock
            {
                Text = title,
                Foreground = Ink,
                FontSize = 15.0,
                FontWeight = FontWeights.SemiBold
            });

            // 描述只留一行（2026-11-15 用户要求：“没必要说得很详细”，甚至可删）：
            // 既精简，又让每张卡的首个控件从**同一高度**开始 —— 一行卡片里的开关 / 按钮才能上下对齐。
            // 描述为空也照样占位，否则对齐会被空描述破坏。
            stack.Children.Add(new TextBlock
            {
                Text = description ?? string.Empty,
                Foreground = Muted,
                TextWrapping = TextWrapping.NoWrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                FontSize = 11.5,
                Height = 18.0,
                Margin = new Thickness(0, 2, 0, 6)
            });

            return new Border
            {
                CornerRadius = new CornerRadius(18.0),
                Background = Card,
                BorderBrush = Line,
                BorderThickness = new Thickness(1),
                Padding = new Thickness(10.0),
                Child = stack,
                // 2026-11-15 用户要求：左右/上留白 2px（相邻两卡拼成 4px 列间距），
                // 下边距 6px —— 与下一行卡片的 2px 上边距合成 **8px 行间距**
                //（"把该行最高的那张卡下边框留白至 8px"，行内其余卡片拉伸同高）
                Margin = new Thickness(2, 2, 2, 6)
            };
        }
    }
}
