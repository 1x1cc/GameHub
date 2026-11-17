#nullable enable
using System.Windows;
using System.Windows.Controls;

namespace GameHub.Controls
{
    /// <summary>
    /// 第 5 条：把「小问号按钮 → 主题气泡说明」做成免代码接线。
    /// XAML 里只要写：
    ///     &lt;Button Content="?" Style="{StaticResource HelpButton}"
    ///             controls:HelpBubble.Text="这里是详细说明…"/&gt;
    /// 点一下就在按钮上方浮出气泡（颜色走主题），点其他任何地方自动消失。
    /// </summary>
    public static class HelpBubble
    {
        public static readonly DependencyProperty TextProperty = DependencyProperty.RegisterAttached(
            "Text",
            typeof(string),
            typeof(HelpBubble),
            new PropertyMetadata(null, OnTextChanged));

        public static void SetText(DependencyObject element, string value)
            => element.SetValue(TextProperty, value);

        public static string GetText(DependencyObject element)
            => (string)element.GetValue(TextProperty);

        private static void OnTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not Button button)
                return;

            button.Click -= OnClick;
            button.Click += OnClick;
        }

        private static void OnClick(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement anchor)
                return;

            if (anchor.GetValue(TextProperty) is string text && !string.IsNullOrWhiteSpace(text))
                PopBubble.ShowHelp(anchor, text);
        }
    }
}
