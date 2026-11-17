using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Enumeration;
using Windows.Media.Audio;

namespace GameHub.Utils
{
    public sealed class BluetoothAudioDeviceInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;

        /// <summary>远端蓝牙地址，例如 5C:AD:BA:7F:7E:BA（用于区分同名设备/多条配对记录）。</summary>
        public string Address { get; set; } = string.Empty;

        public bool IsConnected { get; set; }

        /// <summary>同一台设备在系统里存在多条配对记录（名称相同）。</summary>
        public bool HasDuplicateRecord { get; set; }

        /// <summary>上一次连接尝试失败（该配对记录可能已失效）。</summary>
        public bool LastConnectFailed { get; set; }

        public string DisplayText
        {
            get
            {
                var text = new StringBuilder(string.IsNullOrWhiteSpace(Name) ? "(未命名设备)" : Name);
                if (HasDuplicateRecord && Address.Length > 0)
                    text.Append("  [").Append(Address).Append(']');
                if (IsConnected)
                    text.Append("   ✔ 已连接");
                else if (LastConnectFailed)
                    text.Append("   ⚠ 上次连接失败");
                return text.ToString();
            }
        }
    }

    /// <summary>
    /// 蓝牙音频接收（A2DP Sink）连接服务。
    /// 基于 Windows 10 2004+ 的 AudioPlaybackConnection WinRT API。
    ///
    /// 这里有两处系统行为必须遵守，否则表现为“连不上手机/程序崩溃”：
    ///
    /// 1) 调用顺序必须是 Start() → OpenAsync()。
    ///    Start() 的语义是 “Configures the system to allow connections from the remote audio source”，
    ///    OpenAsync() 才是真正发起连接。若先 OpenAsync()，系统会直接返回
    ///    AudioPlaybackConnectionOpenResultStatus.DeniedBySystem，手机收不到任何连接请求。
    ///    （本机实测：先 Open 后 Start → DeniedBySystem；先 Start 后 Open → Success + 链路 Connected）
    ///
    /// 2) 连接对象在本机上是“不可释放”的。
    ///    本机（Windows 10 19045 + Intel 蓝牙）实测：只要调用 Dispose()，或让它被 GC 终结
    ///    （WinRT.IObjectReference.Finalize → Marshal.Release），就会抛无法捕获的
    ///    System.AccessViolationException 直接杀掉进程；已打开(Opened)的对象 100% 崩，
    ///    未打开的对象在多对象共存时同样会崩，控制台(MTA) 与 WPF(STA) 宿主都一样。
    ///    因此本服务的策略是：
    ///      · 绝不调用 Dispose()；
    ///      · 每个创建出来的连接对象都 SuppressFinalize（外层对象 + WinRT.IObjectReference）；
    ///      · 所有对象长期保留强引用，绝不被 GC 回收。
    ///    代价是无法在程序内真正释放连接（“断开”只解除跟踪），换来的是进程绝不因此崩溃。
    /// </summary>
    public static class BluetoothAudioService
    {
        private sealed class ConnectionHandle
        {
            public AudioPlaybackConnection Connection = null!;
        }

        // 连接对象缓存：同时承担“保持强引用”的作用，避免被 GC 终结（见类注释第 2 点）
        private static readonly Dictionary<string, ConnectionHandle> Handles =
            new(StringComparer.OrdinalIgnoreCase);

        // 创建过的所有连接对象，永久保留引用：本机上释放任何 AudioPlaybackConnection 都可能崩溃，
        // 连终结器都不能跑，所以既不 Dispose 也不允许被回收。
        private static readonly List<AudioPlaybackConnection> Retained = new();

        // 我们认为处于“已连接/已武装”状态的端点
        private static readonly HashSet<string> ActiveIds = new(StringComparer.OrdinalIgnoreCase);

        // 上一次连接失败的配对记录
        private static readonly HashSet<string> FailedIds = new(StringComparer.OrdinalIgnoreCase);

        private static readonly SemaphoreSlim Gate = new(1, 1);

        private static List<BluetoothAudioDeviceInfo> _lastScan = new();

        /// <summary>等待远端（手机）响应的最长时间。</summary>
        public static readonly TimeSpan OpenTimeout = TimeSpan.FromSeconds(30);

        /// <summary>是否为 Windows 10 2004+（A2DP Sink 可用）。</summary>
        public static bool IsSupported =>
            Environment.OSVersion.Version.Build >= 19041;

        // ---------- 枚举 ----------

        public static async Task<List<BluetoothAudioDeviceInfo>> GetDevicesAsync()
        {
            var result = new List<BluetoothAudioDeviceInfo>();
            if (!IsSupported)
                return result;

            try
            {
                string selector = AudioPlaybackConnection.GetDeviceSelector();
                var devices = await DeviceInformation.FindAllAsync(selector);

                var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var liveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                foreach (var info in devices)
                {
                    string address = ExtractAddress(info.Id);

                    // 同一蓝牙地址只保留一条记录
                    if (address.Length > 0 && !seenAddresses.Add(address))
                        continue;

                    var connection = GetOrCreateHandle(info.Id);
                    bool connected = false;
                    if (connection != null)
                    {
                        try
                        {
                            connected = connection.State == AudioPlaybackConnectionState.Opened;
                        }
                        catch
                        {
                            connected = false;
                        }
                    }

                    if (connected)
                        ActiveIds.Add(info.Id);

                    liveIds.Add(info.Id);

                    result.Add(new BluetoothAudioDeviceInfo
                    {
                        Id = info.Id,
                        Name = string.IsNullOrWhiteSpace(info.Name) ? "(未命名设备)" : info.Name,
                        Address = address,
                        IsConnected = connected,
                        LastConnectFailed = FailedIds.Contains(info.Id)
                    });
                }

                // 同名 ⇒ 同一台设备有多条配对记录，用地址区分
                foreach (var group in result.GroupBy(d => d.Name, StringComparer.OrdinalIgnoreCase))
                {
                    if (group.Count() > 1)
                        foreach (var item in group)
                            item.HasDuplicateRecord = true;
                }

                PruneHandles(liveIds);
            }
            catch
            {
                // 蓝牙不可用或无权限时返回空列表
            }

            _lastScan = result;
            return result;
        }

        private static void PruneHandles(HashSet<string> liveIds)
        {
            // 端点已消失（例如用户解除了配对）：只解除跟踪，绝不释放对象
            // （释放会 AccessViolation 崩溃，对象仍被 Retained 持有，终结器也已被抑制）
            foreach (var key in Handles.Keys.Where(k => !liveIds.Contains(k)).ToList())
            {
                Handles.Remove(key);
                ActiveIds.Remove(key);
                FailedIds.Remove(key);
            }
        }

        // ---------- 连接 ----------

        /// <summary>
        /// 连接指定设备。同一台设备若有多条配对记录（其中一条可能已失效），会自动依次尝试。
        /// </summary>
        public static async Task<(bool ok, string message)> ConnectAsync(string deviceId)
        {
            if (!IsSupported)
                return (false, "当前系统版本不支持（需要 Windows 10 2004 或更高版本）");

            if (string.IsNullOrWhiteSpace(deviceId))
                return (false, "设备无效");

            await Gate.WaitAsync();
            try
            {
                var target = _lastScan.FirstOrDefault(d => d.Id == deviceId);
                var candidates = new List<string> { deviceId };

                // 同名（同一台设备的其他配对记录）作为备选，避免用户点到已失效的那条
                if (target != null)
                {
                    foreach (var other in _lastScan)
                    {
                        if (other.Id != deviceId &&
                            string.Equals(other.Name, target.Name, StringComparison.OrdinalIgnoreCase))
                            candidates.Add(other.Id);
                    }
                }

                var failures = new List<string>();
                for (int i = 0; i < candidates.Count; i++)
                {
                    var (ok, message) = await TryOpenAsync(candidates[i]);
                    if (ok)
                    {
                        FailedIds.Remove(candidates[i]);
                        var suffix = i == 0 ? string.Empty : "（已自动改用另一条同名配对记录）";
                        return (true, message + suffix);
                    }

                    FailedIds.Add(candidates[i]);
                    failures.Add(message);
                }

                return (false, string.Join("；", failures.Distinct()));
            }
            finally
            {
                Gate.Release();
            }
        }

        private static async Task<(bool ok, string message)> TryOpenAsync(string deviceId)
        {
            try
            {
                var connection = GetOrCreateHandle(deviceId);
                if (connection == null)
                    return (false, "无法创建连接（设备可能不支持音频接收）");

                if (connection.State == AudioPlaybackConnectionState.Opened)
                {
                    // 已连接：只需保持接收端处于允许接入的状态
                    try { connection.Start(); } catch { }
                    ActiveIds.Add(deviceId);
                    return (true, "该设备已处于连接状态");
                }

                // 关键修复：必须先武装接收端，再发起连接（详见类注释第 1 点）
                try
                {
                    connection.Start();
                }
                catch (Exception ex)
                {
                    return (false, $"启动接收端失败：{ex.Message}");
                }

                var openTask = connection.OpenAsync().AsTask();
                var finished = await Task.WhenAny(openTask, Task.Delay(OpenTimeout));

                if (finished != openTask)
                {
                    // 观察异常，避免产生未观察的 Task 异常
                    _ = openTask.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);

                    // 接收端已武装：手机此后可自行连入
                    if (connection.State == AudioPlaybackConnectionState.Opened)
                    {
                        ActiveIds.Add(deviceId);
                        return (true, "连接成功（手机已连入）");
                    }

                    return (false,
                        $"连接超时（{(int)OpenTimeout.TotalSeconds} 秒）：手机未响应。" +
                        "电脑已进入“可接收”状态，请确认手机在附近并已开启蓝牙，" +
                        "也可在手机的蓝牙设置里主动连接本电脑");
                }

                var openResult = await openTask;
                if (openResult.Status == AudioPlaybackConnectionOpenResultStatus.Success)
                {
                    ActiveIds.Add(deviceId);
                    return (true, "连接成功");
                }

                return (false, DescribeFailure(openResult.Status, openResult.ExtendedError));
            }
            catch (Exception ex)
            {
                return (false, $"连接异常：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string DescribeFailure(AudioPlaybackConnectionOpenResultStatus status, Exception? extendedError)
        {
            string detail = string.IsNullOrWhiteSpace(extendedError?.Message)
                ? string.Empty
                : $"（{extendedError!.Message}）";

            switch (status)
            {
                case AudioPlaybackConnectionOpenResultStatus.RequestTimedOut:
                    return "连接超时：设备未响应，请确认手机在附近并已开启蓝牙" + detail;

                case AudioPlaybackConnectionOpenResultStatus.DeniedBySystem:
                    return "系统拒绝了连接请求：该配对记录可能已失效，接收端也可能未就绪。" +
                           "建议在系统蓝牙设置中删除该设备后重新配对" + detail;

                case AudioPlaybackConnectionOpenResultStatus.UnknownFailure:
                    return "连接失败：设备无响应，该配对记录可能已失效" +
                           "（可在系统蓝牙设置中删除旧记录后重新配对）" + detail;

                default:
                    return $"连接失败：{status}" + detail;
            }
        }

        // ---------- 暂停/恢复音频传输（静音蓝牙接收会话） ----------

        /// <summary>蓝牙音频暂停/恢复的结果。</summary>
        public sealed class BluetoothAudioToggleResult
        {
            /// <summary>本次操作是否真的作用到了音频会话。</summary>
            public bool Success { get; init; }

            /// <summary>操作之后的暂停状态：true=已暂停（手机声音不从电脑输出）。</summary>
            public bool Paused { get; init; }

            /// <summary>给用户看的中文说明。</summary>
            public string Message { get; init; } = string.Empty;
        }

        /// <summary>
        /// 当前是否处于“蓝牙音频已暂停”状态。
        /// 直接从音频会话的真实静音状态推导，不依赖内存标志，因此手机重连、
        /// 手动在音量合成器里改动静音都不会让状态失真。
        /// </summary>
        public static bool IsBluetoothAudioPaused
        {
            get
            {
                try
                {
                    var (total, muted) = AudioSessionMute.GetState();
                    return total > 0 && muted == total;
                }
                catch
                {
                    return false;
                }
            }
        }

        /// <summary>
        /// 切换蓝牙音频传输（暂停/恢复）。
        ///
        /// 实现方式：静音/取消静音“蓝牙音频接收”对应的 WASAPI 会话
        /// （显示名形如“线路 (2- &lt;手机名&gt; A2DP SNK)”）。
        /// 这样手机声音不再从电脑输出，而：
        ///   · 电脑上其它程序的声音不受影响；
        ///   · 手机与电脑的蓝牙连接保持不变，恢复是瞬时的；
        ///   · 不需要（本机也无法安全地）释放 AudioPlaybackConnection。
        ///
        /// 判定规则：只要还有一个蓝牙音频会话未静音，本次就执行“暂停”；
        /// 全部已静音则执行“恢复”。这样无论上次是谁静音的，热键都能切回来。
        /// </summary>
        public static BluetoothAudioToggleResult ToggleAudioPlayback()
        {
            try
            {
                var (total, muted) = AudioSessionMute.GetState();

                if (total == 0)
                {
                    return new BluetoothAudioToggleResult
                    {
                        Success = false,
                        Paused = false,
                        Message = "未找到蓝牙音频流会话：请确认手机已连接电脑，" +
                                  "必要时在手机上播放一下音频再按热键"
                    };
                }

                bool pause = muted < total;
                int changed = AudioSessionMute.SetMute(pause);

                if (changed == 0)
                {
                    return new BluetoothAudioToggleResult
                    {
                        Success = false,
                        Paused = !pause,
                        Message = "操作失败：无法修改蓝牙音频会话的静音状态"
                    };
                }

                return new BluetoothAudioToggleResult
                {
                    Success = true,
                    Paused = pause,
                    Message = pause
                        ? $"已暂停：手机声音不再从电脑输出（{changed} 个蓝牙音频流已静音）"
                        : $"已恢复：手机声音从电脑输出（{changed} 个蓝牙音频流已取消静音）"
                };
            }
            catch (Exception ex)
            {
                return new BluetoothAudioToggleResult
                {
                    Success = false,
                    Paused = false,
                    Message = $"操作异常：{ex.GetType().Name}: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// 程序退出时调用：取消静音，避免下次启动/手机重连后莫名没有声音。
        /// </summary>
        public static void RestoreAudioOnExit()
        {
            try
            {
                var (total, muted) = AudioSessionMute.GetState();
                if (total > 0 && muted > 0)
                    AudioSessionMute.SetMute(false);
            }
            catch
            {
                // 退出路径不抛异常
            }
        }

        // ---------- 断开 ----------

        /// <summary>断开指定设备。</summary>
        public static Task<(bool ok, string message)> DisconnectAsync(string deviceId)
        {
            if (!Handles.TryGetValue(deviceId, out var handle))
            {
                ActiveIds.Remove(deviceId);
                return Task.FromResult<(bool, string)>((true, "设备未在连接中"));
            }

            bool opened;
            try
            {
                opened = handle.Connection.State == AudioPlaybackConnectionState.Opened;
            }
            catch
            {
                opened = true; // 保守处理
            }

            ActiveIds.Remove(deviceId);

            if (opened)
            {
                // 不能释放：Dispose/终结器都会 AccessViolation 崩溃（见类注释第 2 点）
                return Task.FromResult<(bool, string)>((true,
                    "已停止跟踪该连接。注意：本机系统缺陷导致程序内无法真正关闭已建立的蓝牙接收连接，" +
                    "如需彻底断开，请在手机上断开该设备，或关闭手机蓝牙"));
            }

            return Task.FromResult<(bool, string)>((true, "该设备当前未处于连接状态，无需断开"));
        }

        /// <summary>
        /// 程序退出时调用：解除所有跟踪。
        /// 不释放任何连接对象（释放会 AccessViolation 崩溃），连接对象由进程退出时统一回收。
        /// </summary>
        public static void DisconnectAll()
        {
            try
            {
                Handles.Clear();
                ActiveIds.Clear();
                // Retained 故意保留，让对象活到进程结束，避免终结器触发崩溃
            }
            catch
            {
                // 忽略
            }
        }

        // ---------- 工具 ----------

        /// <summary>
        /// 为端点创建连接对象，并永久阻止其被 GC 终结（终结会触发系统崩溃，见类注释第 2 点）。
        /// </summary>
        private static AudioPlaybackConnection? GetOrCreateHandle(string deviceId)
        {
            if (Handles.TryGetValue(deviceId, out var existing))
                return existing.Connection;

            var connection = AudioPlaybackConnection.TryCreateFromId(deviceId);
            if (connection == null)
                return null;

            SuppressFinalizer(connection);
            Retained.Add(connection);
            Handles[deviceId] = new ConnectionHandle { Connection = connection };
            return connection;
        }

        private static void SuppressFinalizer(AudioPlaybackConnection connection)
        {
            try
            {
                if (connection is WinRT.IWinRTObject winrtObject)
                    GC.SuppressFinalize(winrtObject.NativeObject);
                GC.SuppressFinalize(connection);
            }
            catch
            {
                // 抑制终结失败不致命，忽略
            }
        }

        private static readonly Regex AddressPattern =
            new(@"&0&([0-9A-Fa-f]{12})_", RegexOptions.Compiled);

        /// <summary>从端点 ID 中解析远端蓝牙地址，形如 5C:AD:BA:7F:7E:BA。</summary>
        private static string ExtractAddress(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
                return string.Empty;

            var match = AddressPattern.Match(deviceId);
            if (!match.Success)
                return string.Empty;

            string hex = match.Groups[1].Value.ToUpperInvariant();
            var builder = new StringBuilder(17);
            for (int i = 0; i < hex.Length; i += 2)
            {
                if (builder.Length > 0) builder.Append(':');
                builder.Append(hex, i, 2);
            }
            return builder.ToString();
        }
    }
}
