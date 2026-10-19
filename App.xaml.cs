using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using GameHub.Utils;

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
            // 第一行就落日志：判断"开机自启动到底有没有把程序拉起来"，
            // 全靠这一行的时间和启动参数。（以前启动阶段完全不写日志，
            // 只能靠猜，这也是"疑似没生效"难以定位的原因。）
            var arguments = e.Args is { Length: > 0 } ? string.Join(" ", e.Args) : "(无)";
            bool startedByAutoStart = AutoStartManager.IsAutoStartInvocation(e.Args);
            AppLog.Write($"程序启动：自启动={startedByAutoStart}，参数={arguments}，exe={AutoStartManager.ExecutablePath}");

            // 全局异常处理器：写日志 + 自动导出崩溃报告（UI 线程可弹窗，后台线程只落盘）
            DispatcherUnhandledException += OnDispatcherUnhandledException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;

            base.OnStartup(e);

            // 创建单实例互斥体
            _singleInstanceMutex = new Mutex(true, "GameHub-SingleInstance-Mutex", out bool isNewInstance);
            
            // 检查是否已运行实例
            if (!isNewInstance)
            {
                // 已有实例可能在托盘里（窗口已隐藏）：请求它把主窗口显示出来，
                // 否则用户会看到"已经在运行中"却找不到窗口。
                bool signaled = SignalExistingInstance();

                // 开机自启动时，第二个实例很可能是"用户自己又启动了一次"或者
                // 注册表里残留了重复项；这种情况下不该在开机时弹一个对话框挡住用户。
                if (!signaled && !startedByAutoStart)
                {
                    MessageBox.Show("GameHub 已经在运行中，无法启动第二个实例。",
                        "GameHub", MessageBoxButton.OK, MessageBoxImage.Information);
                }

                AppLog.Write($"检测到已有实例，本次启动退出（已唤回旧窗口={signaled}）");
                Shutdown();
                return;
            }

            // 本实例是主实例：启动监听，以便后续启动的实例能把窗口叫回来
            StartActivateListener();

            // 清理历史遗留的按模块自启动项：全程序只保留一个 "GameHub" 项，
            // 否则“关掉其中一个开关”并不能阻止开机启动（其它项仍会拉起 GameHub）。
            AutoStartManager.CleanupLegacyModuleEntries();

            // 校准自启动注册路径：程序换过目录/重新发布过之后，注册表里可能还留着旧路径，
            // 开机时 Windows 会静默跳过（界面却依旧显示 on）。这里自动重写为当前路径。
            AutoStartManager.SyncRegisteredPath();

            // 设置 DPI 感知（代码级，配合 manifest 的 PerMonitorV2）
            SetDpiAwareness();

            // 初始化应用
            InitializeApp();
            DiagnoseResources();

            // 主窗口：开机自启动时默认只驻留托盘，不弹出界面（可在「软件设置」里改）
            CreateMainWindow(startedByAutoStart);
        }

        /// <summary>
        /// 创建主窗口。
        ///
        /// 开机自启动时（且设置允许）先把窗口的 Opacity 设为 0 再 Show：
        /// 这样 Loaded 里的初始化流程（托盘图标、热键、模块状态）照常跑，
        /// 但用户完全看不到窗口闪一下；初始化结束后主窗口会自己 Hide 到托盘。
        /// </summary>
        private void CreateMainWindow(bool startedByAutoStart)
        {
            bool startHidden = startedByAutoStart && SettingsStore.Load().AutoStartMinimizedToTray;

            var window = new Views.MainWindow
            {
                StartHiddenToTray = startHidden
            };

            MainWindow = window;

            if (startHidden)
            {
                window.Opacity = 0;
                window.ShowInTaskbar = false;
            }

            window.Show();

            AppLog.Write(startHidden
                ? "主窗口已创建：本次为开机自启动，初始化完成后最小化到托盘"
                : "主窗口已创建：正常显示");
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
            // 全局异常处理已在 OnStartup 中统一注册（见上方），这里不再重复注册，
            // 否则同一个异常会触发两次弹窗 / 两次写崩溃报告。
        }

        /// <summary>UI 线程崩溃：落盘 + 自动导出报告，再提示用户（并尝试让程序继续运行）。</summary>
        private void OnDispatcherUnhandledException(object sender, System.Windows.Threading.DispatcherUnhandledExceptionEventArgs e)
        {
            HandleCrash(e.Exception, "UI 线程");
            e.Handled = true;
        }

        /// <summary>后台线程崩溃：进程即将终止，无法安全弹窗，只尽力把报告写下来。</summary>
        private void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            try
            {
                HandleCrash(e.ExceptionObject as Exception, "后台线程");
            }
            catch
            {
                // 崩溃处理本身失败也不能影响退出
            }
        }

        /// <summary>统一崩溃处理：写日志 + 自动导出崩溃报告；仅在 UI 线程时弹窗提示。</summary>
        private void HandleCrash(Exception? exception, string scope)
        {
            AppLog.Write($"{scope}未处理异常：{exception?.GetType().FullName}: {exception?.Message}\n{exception?.StackTrace}");

            string? reportPath = null;
            try
            {
                reportPath = DiagnosticsReport.SaveCrashReport(exception);
            }
            catch (Exception reportEx)
            {
                AppLog.Write($"自动保存崩溃报告失败：{reportEx.GetType().Name}: {reportEx.Message}");
            }

            // 只有 UI 线程能安全弹窗；后台线程崩溃时进程要结束，弹了也可能看不到
            if (Dispatcher.CheckAccess())
            {
                try
                {
                    string msg = "程序发生错误，已自动保存错误报告：\n" + (reportPath ?? "(保存失败)") + "\n\n是否打开报告所在目录？";
                    var result = MessageBox.Show(msg, "GameHub 崩溃提示", MessageBoxButton.YesNo, MessageBoxImage.Error);
                    if (result == MessageBoxResult.Yes)
                        DiagnosticsReport.OpenReportDirectory(reportPath);
                }
                catch
                {
                    // 弹窗失败也不影响主流程
                }
            }
        }

        protected override void OnExit(ExitEventArgs e)
        {
            AppLog.Write($"程序退出：退出码={e.ApplicationExitCode}");

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
