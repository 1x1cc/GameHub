using System.Windows;
using GameHub.Utils;

namespace GameHub.Views
{
    /// <summary>
    /// 软件设置窗口（主程序级设置）。
    ///
    /// 目前只有一项：关闭主窗口时的行为。写入 settings.json 后立刻通知主窗口重新读取，
    /// 否则主窗口内存里的旧设置会在退出时把新设置覆盖掉。
    /// </summary>
    public partial class HubSettingsWindow : Window
    {
        private ModuleSettings _settings = SettingsStore.Load();

        public HubSettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            _settings = SettingsStore.Load();
            SelectBehavior(CloseBehavior.Normalize(_settings.CloseAction));
        }

        private void SelectBehavior(string behavior)
        {
            AskRadio.IsChecked = behavior == CloseBehavior.Ask;
            ExitRadio.IsChecked = behavior == CloseBehavior.Exit;
            TrayRadio.IsChecked = behavior == CloseBehavior.Tray;
        }

        private string GetSelectedBehavior()
        {
            if (ExitRadio.IsChecked == true) return CloseBehavior.Exit;
            if (TrayRadio.IsChecked == true) return CloseBehavior.Tray;
            return CloseBehavior.Ask;
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            var behavior = GetSelectedBehavior();
            _settings.CloseAction = behavior;
            SettingsStore.Save(_settings);

            // 关键：让主窗口丢掉内存里的旧设置，否则它退出时会用旧值覆盖刚保存的关闭方式
            if (Application.Current?.MainWindow is MainWindow main)
                main.ReloadSettings();

            NotificationManager.ShowInfo("设置已保存", $"关闭窗口时：{CloseBehavior.ToDisplayName(behavior)}");
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
