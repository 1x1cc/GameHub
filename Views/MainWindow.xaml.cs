using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using GameHub.Modules.AudioPlaybackConnector;
using GameHub.Modules.SoundSwitch;
using GameHub.Utils;
using GameHub.ViewModels;

namespace GameHub.Views
{
    public partial class MainWindow : Window
    {
        private readonly List<ModuleCardViewModel> _moduleViewModels = new();
        private readonly HotkeyManager _hotkeyManager = new();
        private ModuleSettings _settings = SettingsStore.Load();
        private readonly SoundSwitchModule _soundSwitchModule = new();

        /// <summary>系统托盘图标（"最小化到托盘"后用它恢复窗口/退出程序）。</summary>
        private TrayIconManager? _tray;

        /// <summary>是否已确认真正退出：为 true 时 OnClosing 不再拦截。</summary>
        private bool _allowClose;

        /// <summary>“已最小化到托盘”的提示每次运行只弹一次，避免每次关闭都打扰。</summary>
        private bool _trayTipShown;

        public MainWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
            Closed += OnClosed;

            // 系统关机/注销时要直接退出，不能把窗口藏起来当"最小化到托盘"，
            // 否则会拦住系统的关机流程。
            if (Application.Current != null)
                Application.Current.SessionEnding += (_, _) => _allowClose = true;
        }

        private async void OnLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                LoadModules();
                UpdateHubAutoStartButton();
                RegisterHotkeys();
                NotificationManager.Initialize();
                InitializeTrayIcon();
                await RefreshAllAsync();
                LogMessage("主窗口加载完成");
            }
            catch (Exception ex)
            {
                LogMessage($"主窗口加载异常：{ex.GetType().Name}: {ex.Message}");
                LogMessage($"堆栈：{ex.StackTrace}");
            }
        }

        // ---------- 模块 ----------

        private void LoadModules()
        {
            var modules = new List<Interfaces.IModule>
            {
                new SoundSwitchModule(),
                new AudioPlaybackConnectorModule()
            };

            _moduleViewModels.Clear();
            foreach (var module in modules)
            {
                _moduleViewModels.Add(new ModuleCardViewModel(module));
            }

            ModulesList.ItemsSource = _moduleViewModels;
        }

        private async System.Threading.Tasks.Task RefreshAllAsync()
        {
            foreach (var vm in _moduleViewModels)
                await vm.RefreshStatusAsync();
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAllAsync();
        }

        /// <summary>点击卡片非按钮区域 → 打开该模块的内置功能面板。</summary>
        private void ModuleCard_Click(object sender, MouseButtonEventArgs e)
        {
            if (sender is Border border && border.DataContext is ModuleCardViewModel vm)
                vm.OpenPanelCommand.Execute(null);
        }

        // ---------- 工具箱自身的自启动 ----------

        private void UpdateHubAutoStartButton()
        {
            bool enabled = AutoStartManager.IsEnabled();
            HubAutoStartButton.Content = enabled ? "开机自启动 on" : "开机自启动 off";
            // 颜色由 enabled 决定，不是 hover
            HubAutoStartButton.Background = enabled
                ? new SolidColorBrush(Color.FromRgb(0x50, 0xC8, 0x78))
                : new SolidColorBrush(Color.FromRgb(0x9E, 0x9E, 0x9E));
        }

        private async void HubAutoStart_Click(object sender, RoutedEventArgs e)
        {
            // 异步执行注册表操作，避免阻塞 UI 线程
            await Task.Run(() =>
            {
                AutoStartManager.Toggle();
            });
            UpdateHubAutoStartButton();
        }

        // ---------- 热键 ----------

        private void RegisterHotkeys()
        {
            if (!_hotkeyManager.Attach(this))
            {
                LogMessage("热键注册失败：窗口句柄未就绪");
                return;
            }

            _settings = SettingsStore.Load();
            LogMessage($"设置加载：蓝牙暂停热键={_settings.BluetoothAudioPauseHotkey}({_settings.BluetoothAudioPauseHotkeyEnabled})");
            RegisterHotkeysFromSettings();
            LogMessage($"热键注册完成：输出={_settings.PlaybackHotkey}({_settings.PlaybackHotkeyEnabled}), 输入={_settings.RecordingHotkey}({_settings.RecordingHotkeyEnabled}), 蓝牙={_settings.BluetoothAudioPauseHotkey}({_settings.BluetoothAudioPauseHotkeyEnabled})");
        }

        /// <summary>根据当前设置重新注册热键（设置窗口保存后调用）。</summary>
        public void ReloadHotkeys()
        {
            _hotkeyManager.UnregisterAll();
            _settings = SettingsStore.Load();
            RegisterHotkeysFromSettings();
        }

        private void RegisterHotkeysFromSettings()
        {
            // 注册失败（例如热键被其它程序占用）必须记入日志，
            // 旧实现用 out _ 丢弃了错误，导致按了没反应也无从查起。
            if (_settings.PlaybackHotkeyEnabled)
            {
                bool ok = _hotkeyManager.Register(_settings.PlaybackHotkey, WrapCallback(
                    () => SwitchDevice(AudioFlow.Playback, _settings.HotkeyTargetPlaybackDeviceIds)), out string playbackError);
                if (!ok)
                    LogMessage($"输出热键注册失败：{_settings.PlaybackHotkey}（{playbackError}）");
            }

            if (_settings.RecordingHotkeyEnabled)
            {
                bool ok = _hotkeyManager.Register(_settings.RecordingHotkey, WrapCallback(
                    () => SwitchDevice(AudioFlow.Recording, _settings.HotkeyTargetRecordingDeviceIds)), out string recordingError);
                if (!ok)
                    LogMessage($"输入热键注册失败：{_settings.RecordingHotkey}（{recordingError}）");
            }

            if (_settings.BluetoothAudioPauseHotkeyEnabled)
            {
                bool ok = _hotkeyManager.Register(_settings.BluetoothAudioPauseHotkey, WrapCallback(
                    ToggleBluetoothAudioPlayback), out string btError);
                if (!ok)
                    LogMessage($"蓝牙音频暂停热键注册失败：{_settings.BluetoothAudioPauseHotkey}（{btError}）");
            }
        }

        /// <summary>
        /// 切换设备：在勾选的设备间循环切换，如果没有勾选则循环所有设备。
        /// 使用 BeginInvoke 异步执行，避免后台线程阻塞 UI。
        /// 添加 UI 线程检查，避免窗口关闭后访问已销毁的 UI 线程。
        /// </summary>
        private void SwitchDevice(AudioFlow flow, List<string> targetIds)
        {
            // 检查 UI 线程是否仍然活跃
            if (Application.Current?.Dispatcher?.CheckAccess() == false)
            {
                // UI 线程不可用，直接返回
                LogMessage($"热键切换失败：UI 线程不可用");
                return;
            }

            // 异步提交到 UI 线程，不阻塞热键线程
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                // 再次检查 UI 线程是否仍然活跃
                if (Application.Current?.Dispatcher?.CheckAccess() == false)
                {
                    LogMessage($"热键切换失败：UI 线程已销毁");
                    return;
                }

                try
                {
                    var devices = AudioDeviceService.GetDevices(flow);
                    string? nextDeviceName = null;
                    string? failureReason = null;

                    // 如果有勾选的设备，在这些设备间切换
                    if (targetIds != null && targetIds.Count > 0)
                    {
                        // 按 targetIds 的固定顺序构建 filteredDevices（避免 IsDefault 变化导致顺序混乱）
                        var filteredDevices = targetIds
                            .Select(id => devices.FirstOrDefault(d => d.Id == id))
                            .Where(d => d != null)
                            .ToList();

                        // 勾选了但当前不在线的设备（拔掉/未插入）—— 记录下来，避免误以为勾选没生效
                        int offlineCount = targetIds.Count - filteredDevices.Count;
                        if (offlineCount > 0)
                        {
                            LogMessage($"热键切换：勾选 {targetIds.Count} 个设备，其中 {offlineCount} 个当前不在线，本次参与循环 {filteredDevices.Count} 个");
                        }

                        if (filteredDevices.Count > 1)
                        {
                            // 找到当前默认设备在 filteredDevices 中的索引
                            var currentDevice = devices.Find(d => d.IsDefault);
                            int currentIndex = filteredDevices.FindIndex(d => d.Id == currentDevice?.Id);
                            // 如果当前默认设备不在筛选列表中，从第一个开始
                            if (currentIndex < 0) currentIndex = 0;

                            // 循环到下一个设备
                            int nextIndex = (currentIndex + 1) % filteredDevices.Count;
                            var nextDevice = filteredDevices[nextIndex];

                            // 执行切换
                            if (AudioDeviceService.SetDefaultDevice(nextDevice.Id))
                                nextDeviceName = nextDevice.Name;
                            else
                                failureReason = $"设置默认设备失败：{nextDevice.Name}";
                        }
                        else if (filteredDevices.Count == 1)
                        {
                            // 只有一个勾选设备，切换回当前默认（如果不是它）
                            var currentDevice = devices.Find(d => d.IsDefault);
                            if (currentDevice != null && currentDevice.Id != filteredDevices[0].Id)
                            {
                                if (AudioDeviceService.SetDefaultDevice(filteredDevices[0].Id))
                                    nextDeviceName = filteredDevices[0].Name;
                                else
                                    failureReason = $"设置默认设备失败：{filteredDevices[0].Name}";
                            }
                        }
                    }

                    // 如果没有勾选设备，循环所有设备
                    if (nextDeviceName == null && failureReason == null)
                    {
                        var device = AudioDeviceService.SwitchToNext(flow);
                        if (device != null)
                            nextDeviceName = device.Name;
                        else
                            failureReason = devices.Count == 0 ? "未找到可用设备" : "切换默认设备失败";
                    }

                    // 显示通知（UI 线程）
                    if (nextDeviceName != null)
                    {
                        LogMessage($"热键切换成功：{flow} -> {nextDeviceName}");
                        NotificationManager.ShowDeviceNotification(
                            flow == AudioFlow.Playback ? "输出" : "输入", nextDeviceName);
                    }
                    else
                    {
                        LogMessage($"热键切换失败：{failureReason}");
                        NotificationManager.ShowDeviceNotification(
                            flow == AudioFlow.Playback ? "输出" : "输入", failureReason ?? "切换失败");
                    }
                }
                catch (Exception ex)
                {
                    LogMessage($"热键切换异常：{ex.GetType().Name}: {ex.Message}");
                    LogMessage($"堆栈：{ex.StackTrace}");
                    try
                    {
                        NotificationManager.ShowDeviceNotification(
                            flow == AudioFlow.Playback ? "输出" : "输入", "切换失败");
                    }
                    catch { }
                }
            });
        }

        /// <summary>包装回调，捕获所有异常防止热键触发时崩溃应用。</summary>
        private static Action WrapCallback(Action original)
        {
            return () =>
            {
                try
                {
                    original();
                }
                catch (Exception ex)
                {
                    var log = $"[{DateTime.Now:HH:mm:ss}] 热键异常：{ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
                    File.AppendAllText(Path.Combine(Path.GetTempPath(), "gamehub-hotkey.log"), log + "\n");
                }
            };
        }

        /// <summary>
        /// 切换蓝牙音频传输状态（暂停/恢复）。
        /// 按下后：暂停音频传输（手机声音不从电脑输出）
        /// 再按下：恢复音频传输（手机声音从电脑输出）
        /// 实际动作 = 静音/取消静音蓝牙音频接收对应的 WASAPI 会话。
        /// </summary>
        private void ToggleBluetoothAudioPlayback()
        {
            try
            {
                var result = BluetoothAudioService.ToggleAudioPlayback();

                string title = result.Success
                    ? (result.Paused ? "蓝牙音频已暂停" : "蓝牙音频已恢复")
                    : "蓝牙音频未切换";

                LogMessage($"蓝牙音频切换：{title} — {result.Message}");

                // 显示右下角提示（1 秒）：复用统一的通知组件，
                // 旧实现新建 WPF 窗口且不设位置，会出现在屏幕中间并截断长文案。
                NotificationManager.ShowInfo(title, result.Message);
            }
            catch (Exception ex)
            {
                LogMessage($"蓝牙音频切换异常：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>记录日志到文件。</summary>
        private void LogMessage(string message)
        {
            var log = $"[{DateTime.Now:HH:mm:ss}] {message}";
            File.AppendAllText(Path.Combine(Path.GetTempPath(), "gamehub.log"), log + "\n");
        }

        /// <summary>面板关闭后刷新卡片状态（默认设备可能已改变）。</summary>
        public async void NotifyPanelClosed()
        {
            await RefreshAllAsync();
        }

        // ---------- 关闭行为（完全退出 / 最小化到托盘） ----------

        /// <summary>创建托盘图标。失败只记日志：没有托盘图标时不允许“最小化到托盘”。</summary>
        private void InitializeTrayIcon()
        {
            try
            {
                _tray = new TrayIconManager(ShowMainWindow, OpenSettingsWindow, RequestExit);
                _tray.EnsureCreated();
                LogMessage(_tray.IsReady ? "托盘图标已就绪" : "托盘图标未就绪，关闭时只能选择完全退出");
            }
            catch (Exception ex)
            {
                LogMessage($"托盘图标初始化异常：{ex.GetType().Name}: {ex.Message}");
                _tray = null;
            }
        }

        /// <summary>
        /// 拦截窗口关闭：按设置决定“完全退出”还是“最小化到托盘”，
        /// 设置为“每次询问”时先弹选择对话框。
        /// </summary>
        protected override void OnClosing(CancelEventArgs e)
        {
            if (!_allowClose)
            {
                e.Cancel = true;   // 先拦住这次关闭，等用户/设置给出结果
                // 关键：WPF 在 OnClosing 期间不允许创建新对话框或对自身调用 Show()，
                // 所以必须延迟到下一个 UI 循环再处理，避免 InvalidOperationException。
                Dispatcher.BeginInvoke(new Action(HandleCloseRequest), DispatcherPriority.Background);
            }

            base.OnClosing(e);
        }

        private void HandleCloseRequest()
        {
            var action = CloseBehavior.Normalize(_settings.CloseAction);

            if (action == CloseBehavior.Ask)
            {
                try
                {
                    var dialog = new CloseConfirmWindow { Owner = this };
                    bool? confirmed = dialog.ShowDialog();

                    // 用户取消（或直接关掉对话框）：什么都不做，程序继续运行
                    if (confirmed != true || dialog.SelectedAction == null)
                    {
                        LogMessage("关闭已取消");
                        return;
                    }

                    action = dialog.SelectedAction;

                    if (dialog.Remember)
                    {
                        _settings.CloseAction = action;
                        SettingsStore.Save(_settings);
                        LogMessage($"已记住关闭方式：{CloseBehavior.ToDisplayName(action)}");
                    }
                }
                catch (Exception ex)
                {
                    // 无法弹对话框时，按默认行为退出
                    LogMessage($"创建对话框异常，直接退出：{ex.GetType().Name}: {ex.Message}");
                    RequestExit();
                    return;
                }
            }

            if (action == CloseBehavior.Tray)
                MinimizeToTray();
            else
                RequestExit();
        }

        /// <summary>隐藏主窗口，程序在后台继续运行（热键仍然生效）。</summary>
        private void MinimizeToTray()
        {
            if (_tray?.IsReady != true)
            {
                // 托盘图标不可用时隐藏窗口会把用户“关丢”程序，所以直接退出
                LogMessage("托盘图标不可用，改为完全退出");
                RequestExit();
                return;
            }

            Hide();
            LogMessage("已最小化到托盘，程序在后台继续运行");

            if (!_trayTipShown)
            {
                _trayTipShown = true;
                NotificationManager.ShowInfo("GameHub 仍在后台运行", "双击托盘图标可重新打开主界面");
            }
        }

        /// <summary>真正退出程序（无论当前是从标题栏、托盘菜单还是设置里触发）。</summary>
        public void RequestExit()
        {
            if (_allowClose)
                return;

            _allowClose = true;   // 之后 OnClosing 不再拦截
            LogMessage("正在退出 GameHub");

            // 已经隐藏（最小化到托盘）时也能关：Close() 对隐藏窗口同样会走 Closed → 清理流程
            Close();
        }

        /// <summary>把主窗口从托盘里叫回来（也供第二个实例的唤醒信号调用）。</summary>
        public void ShowMainWindow()
        {
            try
            {
                // 窗口不可见且处于最小化状态时，才尝试恢复
                // 如果窗口已关闭或正在关闭，Show() 会抛异常，由 catch 处理
                if (!IsVisible)
                {
                    Show();
                    if (WindowState == WindowState.Minimized)
                        WindowState = WindowState.Normal;

                    Activate();

                    // 仅 Activate() 在部分情况下不会把窗口提到最前，这里做个 Topmost 闪烁
                    Topmost = true;
                    Topmost = false;
                    Focus();
                }
            }
            catch (Exception ex)
            {
                LogMessage($"恢复主窗口失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>打开「软件设置」。从托盘菜单进入时主窗口可能是隐藏的，先把窗口恢复出来。</summary>
        private void OpenSettingsWindow()
        {
            if (!IsVisible)
                ShowMainWindow();

            var window = new HubSettingsWindow { Owner = this };
            window.ShowDialog();
        }

        private void Settings_Click(object sender, RoutedEventArgs e) => OpenSettingsWindow();

        /// <summary>重新读取 settings.json（设置窗口保存后调用，避免内存里的旧值覆盖新值）。</summary>
        public void ReloadSettings()
        {
            _settings = SettingsStore.Load();
            LogMessage($"设置已重新加载：关闭方式={CloseBehavior.ToDisplayName(_settings.CloseAction)}");
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            // 先注销热键，防止热键触发时访问已销毁的 UI 线程
            _hotkeyManager.Dispose();
            
            // 保存设置
            SettingsStore.Save(_settings);
            
            // 断开蓝牙连接
            BluetoothAudioService.DisconnectAll();

            // 退出时取消蓝牙音频静音（避免下次启动/重连后没有声音）
            BluetoothAudioService.RestoreAudioOnExit();
            
            // 清理通知管理器
            NotificationManager.Cleanup();

            // 移除托盘图标：不 Dispose 会在托盘区留下一个点不动的“幽灵图标”
            _tray?.Dispose();
            _tray = null;
            
            // 确保进程完全退出
            Application.Current.Shutdown();
        }
    }
}
