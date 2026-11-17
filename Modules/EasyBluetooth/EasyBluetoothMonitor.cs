using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Threading;
using GameHub.Utils;

namespace GameHub.Modules.EasyBluetooth
{
    /// <summary>
    /// EasyBluetooth 的后台轮询器。
    ///
    /// 职责：
    ///   · 按设置的间隔扫描无线设备电量（默认 30 秒）；
    ///   · 低电量时弹右下角通知（带重复提醒抑制）；
    ///   · 把最新结果广播给主界面卡片与悬浮窗（Updated 事件）。
    ///
    /// 为什么用 DispatcherTimer：
    ///   扫描本身是 async（不阻塞界面），而结果要更新界面。
    ///   定时器跑在 UI 线程上可以省掉跨线程调度，代码更简单，也不会出现
    ///   “窗口已关闭但后台线程还在刷 UI”的问题。
    ///
    /// 资源占用：轮询只做设备枚举 + 属性读取；GATT 读取仅对“已连接且会话已建立”
    /// 的设备发生（毫秒级）。30 秒与 30 分钟的 CPU 差异可以忽略，故默认取 30 秒。
    /// </summary>
    internal static class EasyBluetoothMonitor
    {
        private static DispatcherTimer? _timer;
        private static bool _scanning;
        private static bool _firstScanRequested;

        /// <summary>每台设备上次低电量提醒时间，用于重复提醒抑制。</summary>
        private static readonly Dictionary<string, DateTimeOffset> LastLowBatteryNotifyAt =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>最近一次扫描结果（未过滤，界面按各自设置过滤）。</summary>
        public static IReadOnlyList<WirelessBatteryDevice> Devices { get; private set; } =
            Array.Empty<WirelessBatteryDevice>();

        public static DateTimeOffset? LastScanAt { get; private set; }

        /// <summary>最近一次扫描的错误信息（没有错误时为空）。</summary>
        public static string LastError { get; private set; } = string.Empty;

        public static bool IsScanning => _scanning;

        public static bool IsSupported => WirelessBatteryService.IsSupported;

        /// <summary>扫描完成后触发（在 UI 线程）。</summary>
        public static event Action? Updated;

        // ---------- 启动 / 停止 ----------

        /// <summary>
        /// 确保轮询器已启动。可重复调用；不在 UI 线程时会自动切到 UI 线程。
        /// </summary>
        public static void EnsureStarted()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(EnsureStarted));
                return;
            }

            var settings = SettingsStore.Load().EasyBluetooth;
            settings.Normalize();

            if (_timer == null)
            {
                _timer = new DispatcherTimer(DispatcherPriority.Background, dispatcher);
                _timer.Tick += OnTimerTick;
                AppLog.Write($"EasyBluetooth：轮询器已创建，间隔 {settings.RefreshIntervalSeconds} 秒，" +
                             $"自动刷新={(settings.AutoRefreshEnabled ? "开" : "关")}，低电量通知={(settings.LowBatteryNotifyEnabled ? "开" : "关")}");
            }

            ApplyInterval(settings);

            // 首次启动立刻扫一次，否则卡片会长时间显示“正在检测…”
            if (!_firstScanRequested)
            {
                _firstScanRequested = true;
                _ = RefreshAsync(manual: false);
            }

            HookApplicationExit();
        }

        private static bool _exitHooked;

        /// <summary>程序退出时停掉轮询并释放 GATT 会话。</summary>
        private static void HookApplicationExit()
        {
            if (_exitHooked || Application.Current == null)
                return;

            _exitHooked = true;
            Application.Current.Exit += (_, _) => Stop();
        }

        /// <summary>设置变更后调用：重新读取间隔与开关并重启定时器。</summary>
        public static void ApplySettings()
        {
            var settings = SettingsStore.Load().EasyBluetooth;
            settings.Normalize();
            ApplyInterval(settings);
        }

        private static void ApplyInterval(EasyBluetoothSettings settings)
        {
            if (_timer == null)
                return;

            _timer.Interval = TimeSpan.FromSeconds(EasyBluetoothSettings.NormalizeInterval(settings.RefreshIntervalSeconds));

            if (settings.AutoRefreshEnabled)
                _timer.Start();
            else
                _timer.Stop();
        }

        /// <summary>程序退出时调用。</summary>
        public static void Stop()
        {
            var dispatcher = Application.Current?.Dispatcher;

            void StopCore()
            {
                if (_timer != null)
                {
                    _timer.Stop();
                    _timer.Tick -= OnTimerTick;
                    _timer = null;
                }
            }

            if (dispatcher != null && !dispatcher.CheckAccess())
                dispatcher.BeginInvoke(new Action(StopCore));
            else
                StopCore();

            WirelessBatteryService.Shutdown();
        }

        private static async void OnTimerTick(object? sender, EventArgs e)
        {
            await RefreshAsync(manual: false);
        }

        /// <summary>主界面卡片首次取状态时调用，保证首次扫描已完成。</summary>
        public static async Task EnsureFirstScanAsync()
        {
            EnsureStarted();

            for (int i = 0; i < 20 && LastScanAt == null && _scanning; i++)
                await Task.Delay(100).ConfigureAwait(false);
        }

        // ---------- 扫描 ----------

        public static async Task RefreshAsync(bool manual)
        {
            if (_scanning)
                return;

            if (!WirelessBatteryService.IsSupported)
            {
                LastError = "当前系统版本不支持（需要 Windows 10 2004 或更高版本）";
                Updated?.Invoke();
                return;
            }

            _scanning = true;
            try
            {
                var devices = await WirelessBatteryService.ScanAsync(allowGattRead: true).ConfigureAwait(true);

                Devices = devices;
                LastScanAt = DateTimeOffset.Now;
                LastError = string.Empty;

                if (manual)
                    AppLog.Write($"EasyBluetooth：手动刷新完成，{devices.Count} 台设备");

                NotifyLowBattery(devices);
            }
            catch (Exception ex)
            {
                LastError = $"{ex.GetType().Name}: {ex.Message}";
                AppLog.Write("EasyBluetooth：刷新电量失败", ex);
            }
            finally
            {
                _scanning = false;
                Updated?.Invoke();
            }
        }

        // ---------- 低电量通知 ----------

        private static void NotifyLowBattery(IReadOnlyList<WirelessBatteryDevice> devices)
        {
            var settings = SettingsStore.Load().EasyBluetooth;
            settings.Normalize();

            if (!settings.LowBatteryNotifyEnabled)
                return;

            var now = DateTimeOffset.Now;
            var repeat = TimeSpan.FromMinutes(EasyBluetoothSettings.NormalizeRepeat(settings.LowBatteryRepeatMinutes));

            foreach (var device in devices)
            {
                if (!device.BatteryPercent.HasValue)
                    continue;

                // 未连接的设备不提醒：旧电量值不代表“正在用的设备快没电了”
                if (!device.IsConnected)
                    continue;

                if (device.BatteryPercent.Value > settings.LowBatteryThresholdPercent)
                    continue;

                // 已提醒过的设备，只有“电量又降了”或“超过重复间隔”才再提醒
                if (LastLowBatteryNotifyAt.TryGetValue(device.Id, out var last) && now - last < repeat)
                    continue;

                LastLowBatteryNotifyAt[device.Id] = now;

                string name = string.IsNullOrWhiteSpace(device.Name) ? "(未命名设备)" : device.Name;
                AppLog.Write($"EasyBluetooth：低电量提醒 {name} {device.BatteryPercent.Value}%（阈值 {settings.LowBatteryThresholdPercent}%）");

                NotificationManager.ShowInfo(
                    "无线设备电量偏低",
                    $"{name} 电量 {device.BatteryPercent.Value}%（低于 {settings.LowBatteryThresholdPercent}%），请及时充电");
            }
        }

        // ---------- 状态文本 ----------

        /// <summary>主界面卡片副标题。</summary>
        public static string BuildStatusText()
        {
            if (!WirelessBatteryService.IsSupported)
                return "需要 Windows 10 2004 或更高版本";

            if (!string.IsNullOrEmpty(LastError))
                return "读取失败，详见 报错/日志";

            if (Devices.Count == 0)
                return LastScanAt == null ? "正在检测…" : "未发现无线设备";

            var connected = Devices.Where(d => d.IsConnected).ToList();
            var withBattery = Devices.Where(d => d.BatteryPercent.HasValue).ToList();

            if (withBattery.Count > 0)
            {
                var lowest = withBattery.OrderBy(d => d.BatteryPercent!.Value).First();
                string lowestName = string.IsNullOrWhiteSpace(lowest.Name) ? "(未命名设备)" : lowest.Name;

                if (lowestName.Length > 14)
                    lowestName = lowestName[..14] + "…";

                return $"已连接 {connected.Count} 台 · 最低 {lowestName} {lowest.BatteryPercent!.Value}%";
            }

            return $"已连接 {connected.Count} 台 · 电量未知";
        }
    }
}
