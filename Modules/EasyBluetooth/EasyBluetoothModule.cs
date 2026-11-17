using System;
using System.Threading.Tasks;
using System.Windows;
using GameHub.Interfaces;
using GameHub.Utils;

namespace GameHub.Modules.EasyBluetooth
{
    /// <summary>
    /// EasyBluetooth 模块（内置实现）。
    ///
    /// 功能：显示本机无线设备（蓝牙 / 2.4G 无线外设）及其电量，
    /// 支持定时刷新、低电量通知与常驻悬浮窗。
    ///
    /// 数据来源见 WirelessBatteryService（GATT 电池服务 / Windows 电源接口 / PnP 设备属性），
    /// 全部为只读的系统 API，不安装驱动、不注入进程、不挂钩键鼠，可放心在游戏后台运行。
    /// </summary>
    public sealed class EasyBluetoothModule : IModule
    {
        /// <summary>模块设置窗口只允许开一个，避免重复点击卡片弹出多个窗口。</summary>
        private static EasyBluetoothSettingsWindow? _openWindow;

        private static bool _initialized;

        public string Name => "EasyBluetooth";

        public string Description => "查看无线设备电量（蓝牙 / 2.4G），低电量提醒与常驻悬浮窗";

        public string IconPath => "pack://application:,,,/Assets/Icons/easybluetooth.png";

        public bool IsAutoStartEnabled => AutoStartManager.IsEnabled();

        public async Task<string> GetStatusTextAsync()
        {
            if (!WirelessBatteryService.IsSupported)
                return "需要 Windows 10 2004 或更高版本";

            InitializeOnce();

            await EasyBluetoothMonitor.EnsureFirstScanAsync();
            return EasyBluetoothMonitor.BuildStatusText();
        }

        public void OpenPanel() => OpenSettingsWindow();

        /// <summary>模块首次被访问时启动轮询器并按设置恢复悬浮窗。</summary>
        private static void InitializeOnce()
        {
            if (_initialized)
                return;

            _initialized = true;

            // 放到 UI 线程的空闲时机，避免在主窗口构造/加载过程中创建窗口
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            dispatcher.BeginInvoke(new Action(() =>
            {
                try
                {
                    EasyBluetoothMonitor.EnsureStarted();
                    EasyBluetoothFloatingController.Apply();
                }
                catch (Exception ex)
                {
                    AppLog.Write("EasyBluetooth：模块初始化失败", ex);
                }
            }), System.Windows.Threading.DispatcherPriority.ApplicationIdle);
        }

        /// <summary>打开模块设置面板（卡片点击 / 悬浮窗右键菜单都会走这里）。</summary>
        public static void OpenSettingsWindow()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(OpenSettingsWindow));
                return;
            }

            try
            {
                if (_openWindow != null)
                {
                    _openWindow.Activate();
                    return;
                }

                _openWindow = new EasyBluetoothSettingsWindow();
                _openWindow.Closed += (_, _) => _openWindow = null;

                // 主窗口可见时让它当 Owner：窗口居中更好看，也不会跑到主窗口后面
                if (Application.Current?.MainWindow is Window main && main.IsVisible && !ReferenceEquals(main, _openWindow))
                    _openWindow.Owner = main;

                _openWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                _openWindow = null;
                AppLog.Write("EasyBluetooth：打开设置窗口失败", ex);

                MessageBox.Show("打开 EasyBluetooth 设置窗口失败：" + ex.Message, "EasyBluetooth",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        /// <summary>
        /// 通知主窗口重新读取设置。
        /// 主窗口启动时会把 settings.json 读进内存并在退出时写回，
        /// 不同步的话，新保存的悬浮窗开关会被主窗口里的旧值覆盖。
        /// </summary>
        internal static void NotifySettingsChanged()
        {
            if (Application.Current?.MainWindow is Views.MainWindow main)
            {
                main.ReloadSettings();
                main.NotifyPanelClosed();
            }
        }
    }
}
