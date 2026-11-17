using System.Windows;
using GameHub.Utils;

namespace GameHub.Views
{
    /// <summary>
    /// 关闭主窗口时的选择对话框：完全退出软件 / 最小化到托盘，
    /// 并可勾选"记住我的选择，下次不再询问"。
    ///
    /// 调用方式（MainWindow 里）：
    ///     var dialog = new CloseConfirmWindow { Owner = this };
    ///     bool? ok = dialog.ShowDialog();
    ///     ok == true → dialog.SelectedAction 是 CloseBehavior.Exit 或 CloseBehavior.Tray；
    ///     ok != true → 用户取消（或直接关掉了对话框），调用方什么都不做。
    /// </summary>
    public partial class CloseConfirmWindow : Window
    {
        /// <summary>用户选择的行为（取消时为 null）。</summary>
        public string? SelectedAction { get; private set; }

        /// <summary>用户是否勾选了"记住我的选择"。</summary>
        public bool Remember => RememberCheck.IsChecked == true;

        public CloseConfirmWindow()
        {
            InitializeComponent();

            // 默认焦点落在"最小化到托盘"上：这是最不具破坏性的选择
            Loaded += (_, _) => TrayChoice.Focus();
        }

        private void ExitChoice_Click(object sender, RoutedEventArgs e) => Finish(CloseBehavior.Exit);

        private void TrayChoice_Click(object sender, RoutedEventArgs e) => Finish(CloseBehavior.Tray);

        private void Finish(string action)
        {
            SelectedAction = action;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            // IsCancel="True" 已经能让 Esc 生效，这里再显式给一个结果，语义更清楚
            SelectedAction = null;
            DialogResult = false;
        }
    }
}
