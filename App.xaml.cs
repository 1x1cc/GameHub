using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;

namespace GameHub
{
    public partial class App : Application
    {
        // 单实例互斥体
        private static Mutex? _singleInstanceMutex;

        // 第二个实例启动时用它"叫醒"已有实例，让它在托盘里把主窗口显示出来
        private const string ActivateEventName = "GameHub-Activate-Event";
        private static EventWaitHandle? _activateEvent;
        private static Thread? _activateThread;
        private static volatile bool _activateListening;

        [DllImport("user32.dll")]
        private static extern bool SetProcessDpiAwareness(int context);

        private const int PROCESS_DPI_AWARENESS = 1;
        private const int PROCESS_PER_MONITOR_DPI_AWARE = 2;
        private const int PROCESS_PER_MONITOR_DPI_AWARENESS_V2 = 3;

        protected override void OnStartup(StartupEventArgs e)
        {
            // 全局异常处理器
            DispatcherUnhandledException += (s, ex) =>
            {
                var log = $"[{DateTime.Now:HH:mm:ss}] UI 线程异常：{ex.Exception.GetType().Name}: {ex.Exception.Message}\n{ex.Exception.StackTrace}";
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gamehub.log"), log + "\n");
                ex.Handled = true;
            };

            AppDomain.CurrentDomain.UnhandledException += (s, ex) =>
            {
                var exception = ex.ExceptionObject as Exception;
                var log = $"[{DateTime.Now:HH:mm:ss}] 未处理异常：{exception?.GetType().Name}: {exception?.Message}\n{exception?.StackTrace}";
                System.IO.File.AppendAllText(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gamehub.log"), log + "\n");
            };

            base.OnStartup(e);

            // 创建单实例互斥体
            _singleInstanceMutex = new Mutex(true, "GameHub-SingleInstance-Mutex", out bool isNewInstance);
            
            // 检查是否已运行实例
            if (!isNewInstance)
            {
                // 已有实例可能在托盘里（窗口已隐藏）：请求它把主窗口显示出来，
                // 否则用户会看到"已经在运行中"却找不到窗口。
                if (!SignalExistingInstance())
                {
                    MessageBox.Show("GameHub 已经在运行中，无法启动第二个实例。",
                        "GameHub", MessageBoxButton.OK, MessageBoxImage.Information);
                }
                Shutdown();
                return;
            }

            // 本实例是主实例：启动监听，以便后续启动的实例能把窗口叫回来
            StartActivateListener();

            // 清理历史遗留的按模块自启动项：全程序只保留一个 "GameHub" 项，
            // 否则“关掉其中一个开关”并不能阻止开机启动（其它项仍会拉起 GameHub）。
            Utils.AutoStartManager.CleanupLegacyModuleEntries();

            // 设置 DPI 感知（代码级，配合 manifest 的 PerMonitorV2）
            SetDpiAwareness();

            // 初始化应用
            InitializeApp();
            DiagnoseResources();
        }

        /// <summary>请求已在运行的实例把主窗口显示出来（成功返回 true）。</summary>
        private static bool SignalExistingInstance()
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(ActivateEventName, out var handle))
                {
                    using (handle)
                        handle.Set();
                    return true;
                }
            }
            catch
            {
                // 打开失败：回落到传统的"已经在运行中"提示
            }

            return false;
        }

        /// <summary>后台监听激活信号，收到后把主窗口从托盘里叫回来。</summary>
        private static void StartActivateListener()
        {
            try
            {
                _activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            }
            catch
            {
                // 创建失败不影响正常使用，只失去"二次启动唤回窗口"的能力
                _activateEvent = null;
                return;
            }

            _activateListening = true;
            // 后台线程：绝不能阻止进程退出
            _activateThread = new Thread(ActivateListenLoop)
            {
                IsBackground = true,
                Name = "GameHub-ActivateListener"
            };
            _activateThread.Start();
        }

        private static void ActivateListenLoop()
        {
            while (_activateListening)
            {
                var evt = _activateEvent;
                if (evt == null)
                    return;

                try
                {
                    if (!evt.WaitOne())
                        return;
                }
                catch
                {
                    // 事件已释放（程序正在退出）
                    return;
                }

                try
                {
                    if (Current?.Dispatcher is not { } dispatcher)
                        return;

                    dispatcher.BeginInvoke(new Action(() =>
                    {
                        if (Current?.MainWindow is Views.MainWindow main)
                            main.ShowMainWindow();
                    }));
                }
                catch
                {
                    // 退出过程中调度器可能已关闭，忽略即可
                }
            }
        }

        /// <summary>设置进程 DPI 感知级别。</summary>
        private static void SetDpiAwareness()
        {
            try
            {
                // 尝试使用 V2（PerMonitorV2），需要 Windows 10 1803+
                if (IsWindowsVersionOrGreater(10, 0, 17763))
                {
                    SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARENESS_V2);
                }
                else if (IsWindowsVersionOrGreater(10, 0, 14393))
                {
                    // Windows 10 1607+ 支持 PerMonitor
                    SetProcessDpiAwareness(PROCESS_PER_MONITOR_DPI_AWARE);
                }
                else
                {
                    // Windows 8.1+
                    SetProcessDpiAwareness(PROCESS_DPI_AWARENESS);
                }
            }
            catch
            {
                // DPI 感知设置失败不影响正常使用
            }
        }

        private static bool IsWindowsVersionOrGreater(int major, int minor, int build)
        {
            var version = Environment.OSVersion.Version;
            return version.Major >= major &&
                   (version.Major > major || version.Minor >= minor || version.Build >= build);
        }

        /// <summary>临时诊断：确认图标资源的 URI 解析方式。</summary>
        private static void DiagnoseResources()
        {
            try
            {
                var sb = new StringBuilder();
                string[] candidates =
                {
                    "Assets/Icons/gamehub.png",
                    "/Assets/Icons/gamehub.png",
                    "pack://application:,,,/Assets/Icons/gamehub.png"
                };

                foreach (var c in candidates)
                {
                    try
                    {
                        var uri = c.StartsWith("pack:", StringComparison.OrdinalIgnoreCase)
                            ? new Uri(c)
                            : new Uri(c, UriKind.Relative);

                        var streamInfo = Application.GetResourceStream(uri);
                        sb.AppendLine(streamInfo != null
                            ? $"{c} => OK ({streamInfo.Stream.Length} bytes)"
                            : $"{c} => NULL");
                    }
                    catch (Exception ex)
                    {
                        sb.AppendLine($"{c} => EX {ex.GetType().Name}: {ex.Message}");
                    }
                }

                // 音频设备枚举（验证 CoreAudio 互操作是否可用）
                sb.AppendLine();
                sb.AppendLine("--- audio endpoints ---");
                foreach (var flow in new[] { Utils.AudioFlow.Playback, Utils.AudioFlow.Recording })
                {
                    var devices = Utils.AudioDeviceService.GetDevices(flow);
                    sb.AppendLine($"[{flow}] count={devices.Count}");
                    foreach (var d in devices)
                        sb.AppendLine($"    {(d.IsDefault ? "*" : " ")} {d.Name}");
                }

                // 非破坏性验证：把"当前默认设备"再设为默认，确认切换 API 通路正常
                var current = Utils.AudioDeviceService.GetDevices(Utils.AudioFlow.Playback)
                    .Find(d => d.IsDefault);
                if (current != null)
                {
                    bool ok = Utils.AudioDeviceService.SetDefaultDevice(current.Id);
                    sb.AppendLine($"SetDefaultEndpoint(self-test on default device) => {ok}");
                }

                File.WriteAllText(Path.Combine(Path.GetTempPath(), "gamehub-res.log"), sb.ToString());
            }
            catch
            {
                // 诊断失败不影响正常使用
            }
        }

        private void InitializeApp()
        {
            // 设置全局异常处理
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            // 检查是否已运行实例
            // CheckSingleInstance();
        }

        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            MessageBox.Show($"发生错误：{e.Exception.Message}", "错误", 
                MessageBoxButton.OK, MessageBoxImage.Error);
            e.Handled = true;
        }

        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            var exception = e.ExceptionObject as Exception;
            MessageBox.Show($"发生严重错误：{exception?.Message}", "错误", 
                MessageBoxButton.OK, MessageBoxImage.Error);
        }

        protected override void OnExit(ExitEventArgs e)
        {
            // 先停掉激活监听，再释放互斥体，避免退出过程中访问已销毁的对象
            try
            {
                _activateListening = false;
                _activateEvent?.Set();       // 立即唤醒等待中的线程，不必等超时
                _activateThread?.Join(500);
                _activateEvent?.Dispose();
            }
            catch
            {
                // 忽略
            }
            finally
            {
                _activateEvent = null;
                _activateThread = null;
            }

            base.OnExit(e);

            // 释放互斥体。注意：非首个实例并不持有互斥体，ReleaseMutex 会抛异常，
            // 不能让这个异常干扰退出流程。
            try
            {
                _singleInstanceMutex?.ReleaseMutex();
            }
            catch
            {
                // 未持有互斥体，忽略
            }
            finally
            {
                _singleInstanceMutex?.Dispose();
                _singleInstanceMutex = null;
            }
        }
    }
}
