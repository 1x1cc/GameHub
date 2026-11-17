using System;
using System.Threading.Tasks;
using System.Windows;
using GameHub.Interfaces;
using GameHub.Utils;

namespace GameHub.Modules.Dronesee
{
    /// <summary>
    /// Dronesee 模块（游戏视频录制与即时重放）。
    ///
    /// 功能：
    ///   1) 屏幕录制（支持硬件编码：Nvidia NVENC / Intel QSV / AMD AMF）
    ///   2) 即时重放（Ring Buffer 内存缓存，最大支持 30 分钟）
    ///   3) 热键绑定（录制 / 即时重放）
    ///   4) 可选麦克风录制、分辨率、帧率、码率、关键帧间隔、音频参数
    ///   5) 内存占用监控与提示
    /// </summary>
    public sealed class DroneseeModule : IModule
    {
        /// <summary>设置窗口只允许开一个，避免重复点击卡片弹出多个窗口。</summary>
        private static DroneseeSettingsWindow? _openWindow;

        /// <summary>
        /// 全程序唯一的录制引擎实例。
        ///
        /// 旧实现有两个问题，都会导致「按热键没反应 / 没有开始录制通知」：
        ///   1) 卡片状态用 _recordingEngine，热键却另建了一个 _droneseeEngine，
        ///      两个引擎状态互不相通，热键开始录制后卡片仍显示「就绪」；
        ///   2) 热键新建的引擎没有挂 OnRecordingComplete / OnRecordingStarted，
        ///      所以录制开始/结束都不会弹任何提示。
        /// 现在统一为下面这一个实例，所有入口都走 GetOrCreateEngine()。
        /// </summary>
        private static DroneseeRecordingEngine? _recordingEngine;
        private static readonly object _engineLock = new();

        public string Name => "Dronesee";

        public string Description => "游戏视频录制与即时重放（硬件编码 · 热键控制 · 内存缓存）";

        public string IconPath => "pack://application:,,,/Assets/Icons/dronesee.png";

        public bool IsAutoStartEnabled => AutoStartManager.IsEnabled();

        public Task<string> GetStatusTextAsync() => Task.Run(ComputeStatusText);

        /// <summary>卡片状态文字的同步实现（不访问 UI 对象，可从后台线程调用）。</summary>
        private static string ComputeStatusText()
        {
            try
            {
                var settings = SettingsStore.Load().Dronesee;
                var engine = GetOrCreateEngine();
                if (engine == null) return "未初始化";

                if (engine.IsRecording)
                {
                    var elapsed = engine.RecordingElapsed;
                    var memUsage = engine.MemoryUsageMB;
                    // 已取消内存上限检测：不再判断“占用过高”，只显示实际用量。
                    return $"录制中 {FormatTime(elapsed)} | 内存 {memUsage:F0}MB";
                }

                if (engine.IsReplayBufferRunning)
                    return $"即时重放缓冲中 {engine.ReplayElapsed:F0} 秒 | 热键保存片段";

                // 开关都关着时把状态写清楚，避免又出现「按了热键没反应」的误会
                if (!settings.RecordingEnabled && !settings.InstantReplayEnabled)
                    return "就绪（录制 / 即时重放均未开启）";
                if (!settings.InstantReplayEnabled)
                    return "就绪（即时重放未开启）";
                if (!settings.RecordingEnabled)
                    return "就绪（录制未开启）";

                return "就绪";
            }
            catch
            {
                return "状态读取失败";
            }
        }

        public void OpenPanel() => OpenSettingsWindow();

        /// <summary>
        /// 获取（必要时创建）全局唯一的录制引擎实例，并挂好所有事件。
        /// 可从任意线程调用；引擎本身不创建 UI 对象，因此无需切到 UI 线程。
        /// </summary>
        public static DroneseeRecordingEngine? GetOrCreateEngine()
        {
            if (_recordingEngine != null) return _recordingEngine;

            lock (_engineLock)
            {
                if (_recordingEngine != null) return _recordingEngine;

                try
                {
                    var engine = new DroneseeRecordingEngine();

                // ---------- 事件接线（确保全部挂上，一个都不能漏） ----------
                engine.OnStatusChanged += OnEngineStatusChanged;
                engine.OnRecordingComplete += OnRecordingComplete;
                engine.OnRecordingStarted += OnRecordingStarted;
                engine.OnReplayStarted += OnReplayStarted;
                engine.OnReplayClipSaved += OnReplayClipSaved;
                engine.OnAudioInfo += OnAudioInfo;
                engine.OnCaptureWarning += OnCaptureWarning;

                _recordingEngine = engine;
                AppLog.Write("Dronesee：录制引擎已初始化（事件已接线）");
                return engine;
                }
                catch (Exception ex)
                {
                    AppLog.Write("Dronesee：引擎初始化失败", ex);
                    return null;
                }
            }
        }

        /// <summary>兼容旧调用点：初始化录制引擎（懒初始化，首次访问时创建）。</summary>
        public static void InitializeEngine() => GetOrCreateEngine();

        /// <summary>释放引擎（程序退出时调用）。</summary>
        public static void DisposeEngine()
        {
            lock (_engineLock)
            {
                try { _recordingEngine?.Dispose(); } catch { }
                _recordingEngine = null;
            }
        }

        /// <summary>引擎状态变化回调（UI 线程更新卡片状态）。</summary>
        private static void OnEngineStatusChanged(string status)
        {
            if (Application.Current?.MainWindow is Views.MainWindow main)
            {
                main.NotifyPanelClosed();
            }
        }

        /// <summary>录制真正开始（FFmpeg 已拉起）→ 右下角提示。</summary>
        private static void OnRecordingStarted()
        {
            NotificationManager.ShowInfo("Dronesee", "开始录制");
        }

        /// <summary>即时重放缓冲开始缓存 → 右下角提示。</summary>
        private static void OnReplayStarted()
        {
            NotificationManager.ShowInfo("Dronesee", "即时重放缓冲已开启（按热键保存片段）");
        }

        /// <summary>一次回放录像已保存 → 提示文件位置。</summary>
        private static void OnReplayClipSaved(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;

            try { NotificationManager.ShowInfo("即时重放已保存", "已存入录像文件夹"); }
            catch { }
        }

        /// <summary>
        /// 按设置里的「即时重放」滑块启动/停止后台缓冲。
        /// 设置保存后与程序启动时都要调一次，保证开关状态真正生效。
        /// </summary>
        public static void ApplyReplayBufferState()
        {
            try
            {
                var engine = GetOrCreateEngine();
                if (engine == null) return;

                bool want = SettingsStore.Load().Dronesee.InstantReplayEnabled;

                if (want && !engine.IsReplayBufferRunning)
                {
                    engine.StartReplayBuffer();
                    AppLog.Write("Dronesee：按设置开关启动即时重放缓冲");
                }
                else if (!want && engine.IsReplayBufferRunning)
                {
                    engine.StopReplayBuffer();
                    AppLog.Write("Dronesee：按设置开关停止即时重放缓冲");
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：应用即时重放开关失败", ex);
            }
        }

        /// <summary>
        /// 音频设备识别结果 → 右下角提示。
        /// 按需求：设备名自动识别；识别不到的那一路自动关闭，并明确告知用户。
        /// </summary>
        private static void OnAudioInfo(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            try { NotificationManager.ShowInfo("Dronesee 音频", message); }
            catch { }
        }

        /// <summary>
        /// 采集异常提示（FFmpeg 意外退出 / 缓冲长时间不刷新）。
        /// 旧实现只在状态栏写一行，用户看不到，只会发现“保存出来总是旧内容/黑屏”。
        /// </summary>
        private static void OnCaptureWarning(string message)
        {
            if (string.IsNullOrWhiteSpace(message)) return;

            try { NotificationManager.ShowInfo("Dronesee 采集异常", message); }
            catch { }
        }

        // 已移除「内存占用过高」弹窗（2026-11-12 用户要求）：
        // 取消上限检测后，任何情况下都不再弹窗报错；内存占用只在设置窗口的备注文字里说明。

        /// <summary>
        /// 每一局（随游戏自动录像）存档期间为 true：
        /// 那一路的录制完成只发右下角轻提示，不弹窗、不自动打开文件夹（2026-11-12 用户要求）。
        /// 手动录制（热键）不受影响，仍是原来的弹窗 + 打开文件夹。
        /// </summary>
        private static volatile bool _silentRecordingComplete;

        /// <summary>随游戏自动录像用：开录时置 true，归档收尾后置回 false。</summary>
        public static void SetSilentRecordingComplete(bool silent) => _silentRecordingComplete = silent;

        /// <summary>录制完成回调（UI 线程提示）。</summary>
        private static void OnRecordingComplete(string filePath)
        {
            if (Application.Current?.Dispatcher?.CheckAccess() == false)
            {
                Application.Current.Dispatcher.BeginInvoke(new Action(() => OnRecordingComplete(filePath)));
                return;
            }

            try
            {
                // 右下角轻提示（每局保存后只留这一条，不打断游戏）
                NotificationManager.ShowInfo("录制完成", "已存入录像文件夹");

                // 每局自动录像：到此为止 —— 不弹窗、不弹资源管理器
                if (_silentRecordingComplete) return;

                MessageBox.Show(
                    $"录制已完成！\n文件已保存：{filePath}\n\n" +
                    "点击「确定」打开文件所在文件夹。",
                    "Dronesee 录制完成",
                    MessageBoxButton.OK,
                    MessageBoxImage.Information);

                // 打开文件所在目录（仅手动录制）
                try
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = "explorer.exe",
                        Arguments = $"/select,\"{filePath}\"",
                        UseShellExecute = true
                    });
                }
                catch { }
            }
            catch { }
        }

        /// <summary>打开模块设置面板（卡片点击时调用）。</summary>
        public static void OpenSettingsWindow()
        {
            try
            {
                var dispatcher = Application.Current?.Dispatcher;
                if (dispatcher == null)
                {
                    AppLog.Write("Dronesee：Application.Current 或 Dispatcher 为 null");
                    return;
                }

                if (!dispatcher.CheckAccess())
                {
                    dispatcher.BeginInvoke(new Action(OpenSettingsWindow));
                    return;
                }

                if (_openWindow != null)
                {
                    _openWindow.Activate();
                    return;
                }

                _openWindow = new DroneseeSettingsWindow();
                _openWindow.Closed += (_, _) => _openWindow = null;

                var mainWindow = Application.Current?.MainWindow;
                if (mainWindow is Window main && main.IsVisible && !ReferenceEquals(main, _openWindow))
                {
                    _openWindow.Owner = main;
                }

                _openWindow.ShowDialog();
            }
            catch (Exception ex)
            {
                _openWindow = null;
                AppLog.Write("Dronesee：打开设置窗口失败", ex);

                MessageBox.Show(
                    $"打开 Dronesee 设置窗口失败：{ex.Message}\n\n" +
                    $"异常类型：{ex.GetType().Name}\n" +
                    $"堆栈：{ex.StackTrace}",
                    "Dronesee 错误",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            }
        }

        /// <summary>通知主窗口重新读取设置。</summary>
        internal static void NotifySettingsChanged()
        {
            if (Application.Current?.MainWindow is Views.MainWindow main)
            {
                main.ReloadSettings();
                main.ReloadHotkeys();
                main.NotifyPanelClosed();
            }
        }

        /// <summary>格式化时间（秒 → HH:MM:SS）。</summary>
        private static string FormatTime(double totalSeconds)
        {
            int h = (int)totalSeconds / 3600;
            int m = ((int)totalSeconds % 3600) / 60;
            int s = (int)totalSeconds % 60;
            if (h > 0) return $"{h:D2}:{m:D2}:{s:D2}";
            return $"{m:D2}:{s:D2}";
        }
    }
}
