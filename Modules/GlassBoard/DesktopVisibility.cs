using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 桌面可见性控制：隐藏 / 显示 Windows 任务栏与桌面图标。
    ///
    /// 机制移植自 GlassBoard v0.9.17 反编译源码（DesktopVisibility.cs），行为保持一致：
    ///   1) 任务栏：Shell_TrayWnd，以及多显示器上的 Shell_SecondaryTrayWnd；
    ///   2) 桌面图标：SHELLDLL_DefView 下的 SysListView32；
    ///   3) 隐藏期间用 750ms 定时器反复压住，否则会被 Windows 自己刷新回来。
    ///
    /// 兜底设计（重要）：隐藏之前先把每个窗口句柄的“原始可见状态”写入
    /// shell-recovery.json；GameHub 下次启动、用户点“恢复”、或程序正常退出时，
    /// 都会按原始状态还原，避免出现“任务栏和桌面图标藏了回不来”。
    /// </summary>
    public static class DesktopVisibility
    {
        private delegate bool EnumCallback(IntPtr h, IntPtr data);

        private sealed class ShellState
        {
            public long Handle { get; set; }
            public string ClassName { get; set; } = string.Empty;
            public bool Visible { get; set; }
        }

        private static readonly Dictionary<long, ShellState> Original = new();
        private static readonly object Gate = new();

        private static bool? _taskbarHidden;
        private static bool? _iconsHidden;
        private static Timer? _maintenance;

        /// <summary>上一次执行「隐藏」的时刻：用来区分「我们自己刚藏的」与「用户手动显示的」。</summary>
        private static DateTime _hiddenSinceUtc = DateTime.MinValue;

        // 用 SW_SHOWNA(8) 而不是 SW_SHOW(5)：只显示窗口，不抢前台焦点。
        private const int SW_HIDE = 0;
        private const int SW_SHOWNA = 8;
        private const int GWL_STYLE = -16;
        private const int WS_VISIBLE = 0x10000000;

        private static string RecoveryFile => Path.Combine(GlassBoardModule.DataFolder, "shell-recovery.json");

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? cls, string? title);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindowEx(IntPtr parent, IntPtr after, string? cls, string? title);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr h);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr h);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindow(IntPtr h, int cmd);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool ShowWindowAsync(IntPtr h, int cmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr h, int index);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr h, StringBuilder text, int count);

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumCallback callback, IntPtr data);

        [DllImport("user32.dll")]
        private static extern bool EnumChildWindows(IntPtr parent, EnumCallback callback, IntPtr data);

        private static string Class(IntPtr h)
        {
            var sb = new StringBuilder(256);
            GetClassName(h, sb, 256);
            return sb.ToString();
        }

        /// <summary>当前所有任务栏窗口（主屏 + 副屏）。</summary>
        private static List<IntPtr> Taskbars()
        {
            var result = new List<IntPtr>();

            IntPtr main = FindWindow("Shell_TrayWnd", null);
            if (main != IntPtr.Zero)
                result.Add(main);

            EnumWindows((h, _) =>
            {
                string cls = Class(h);
                if ((cls == "Shell_TrayWnd" || cls == "Shell_SecondaryTrayWnd") && !result.Contains(h))
                    result.Add(h);
                return true;
            }, IntPtr.Zero);

            return result;
        }

        /// <summary>
        /// 当前所有桌面图标列表窗口 —— **只取 SysListView32，绝不包含 SHELLDLL_DefView**。
        ///
        /// 【为什么不能连 SHELLDLL_DefView 一起隐藏】
        /// DefView 是桌面视图的容器，**桌面空白处的右键菜单就是它提供的**。
        /// 这里原来把 DefView 和它的子窗口「两个都开关一遍」（注释还写着“更稳”），
        /// 结果右键菜单一起消失 —— 用户实测「开着 GameHub 右键桌面无效、关掉就好」。
        /// 只隐藏 SysListView32 同样能把图标藏掉，而 DefView 保留 → 右键菜单不受影响。
        /// 另外 IsHidden() 判断图标是否隐藏时本来就只查 SysListView32，
        /// 改完隐藏与查询口径一致，不再出现“藏了 DefView、查询却看不到”的不对称。
        /// </summary>
        private static List<IntPtr> DesktopLists()
        {
            var result = new HashSet<IntPtr>();

            void AddListsUnder(IntPtr view)
            {
                EnumChildWindows(view, (child, _) =>
                {
                    if (Class(child) == "SysListView32")
                        result.Add(child);
                    return true;
                }, IntPtr.Zero);
            }

            EnumWindows((h, _) =>
            {
                // DefView 可能挂在 Progman 下，也可能挂到某个 WorkerW 下（Win10/11 都会变），两种都找。
                IntPtr defView = FindWindowEx(h, IntPtr.Zero, "SHELLDLL_DefView", null);
                if (defView != IntPtr.Zero)
                    AddListsUnder(defView);

                string cls = Class(h);
                if (cls == "Progman" || cls == "WorkerW")
                {
                    EnumChildWindows(h, (child, _) =>
                    {
                        if (Class(child) == "SHELLDLL_DefView")
                            AddListsUnder(child);
                        return true;
                    }, IntPtr.Zero);
                }
                return true;
            }, IntPtr.Zero);

            return result.ToList();
        }

        private static bool MatchesSaved(ShellState state)
        {
            var h = new IntPtr(state.Handle);
            if (!IsWindow(h) || Class(h) != state.ClassName)
                return false;

            if (state.ClassName is "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
                return Taskbars().Contains(h);

            if (state.ClassName is "SysListView32" or "SHELLDLL_DefView")
                return DesktopLists().Contains(h);

            return false;
        }

        /// <summary>第一次隐藏之前，记录每个窗口的原始可见状态。</summary>
        private static void Remember(IEnumerable<IntPtr> handles)
        {
            bool changed = false;

            foreach (IntPtr h in handles)
            {
                if (Original.ContainsKey(h.ToInt64()))
                    continue;

                Original[h.ToInt64()] = new ShellState
                {
                    Handle = h.ToInt64(),
                    ClassName = Class(h),
                    Visible = (GetWindowLong(h, GWL_STYLE) & WS_VISIBLE) != 0
                };
                changed = true;
            }

            if (!changed)
                return;

            try
            {
                Directory.CreateDirectory(GlassBoardModule.DataFolder);
                string tmp = RecoveryFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(Original.Values.ToList()));
                File.Move(tmp, RecoveryFile, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：写入桌面恢复记录失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>查询当前是否处于隐藏状态（找不到对应窗口时返回 null）。</summary>
        public static bool? IsHidden(bool taskbar)
        {
            List<IntPtr> windows = taskbar ? Taskbars() : DesktopLists();
            if (windows.Count == 0)
                return null;

            if (!taskbar)
            {
                List<IntPtr> lists = windows.Where(h => Class(h) == "SysListView32").ToList();
                if (lists.Count > 0)
                    windows = lists;
            }

            return !windows.Any(IsWindowVisible);
        }

        public static bool DesiredHidden(bool taskbar)
        {
            return (taskbar ? _taskbarHidden : _iconsHidden) ?? IsHidden(taskbar).GetValueOrDefault();
        }

        /// <summary>隐藏 / 显示任务栏或桌面图标。返回给用户看的说明文字。</summary>
        public static string SetHidden(bool taskbar, bool hide)
        {
            List<IntPtr> windows = taskbar ? Taskbars() : DesktopLists();
            string what = taskbar ? "任务栏" : "桌面图标";

            if (windows.Count == 0)
                return $"未找到{what}，请确认 Windows 资源管理器（explorer.exe）正在运行。";

            Remember(windows);

            int failed = 0;
            foreach (IntPtr h in windows)
            {
                // 隐藏或恢复都走异步版本：不阻塞 UI 线程
                if (!ShowWindowAsync(h, hide ? SW_HIDE : SW_SHOWNA))
                {
                    failed++;
                    AppLog.Write($"GlassBoard：无法切换 {Class(h)}：{new Win32Exception(Marshal.GetLastWin32Error()).Message}");
                }
            }

            if (taskbar)
                _taskbarHidden = hide;
            else
                _iconsHidden = hide;

            if (hide)
                _hiddenSinceUtc = DateTime.UtcNow;

            // 记下“用户选择的显隐”，下次自启按它重放（以前只存在内存里，重启就忘）
            PersistDesired();

            EnsureMaintenance();

            return failed <= 0
                ? $"已{(hide ? "隐藏" : "显示")}{what}"
                : $"{what}部分窗口切换失败，详情见日志。";
        }

        // ---------------- “用户选择的显隐”记忆（修复：以前重启即忘） ----------------

        private sealed class DesiredState
        {
            public bool? Taskbar { get; set; }
            public bool? Icons { get; set; }
        }

        private static string DesiredFile => Path.Combine(GlassBoardModule.DataFolder, "shell-desired.json");

        /// <summary>把当前“用户选择的显隐”落盘（与 shell-recovery.json 分开：那个存的是“原始状态”）。</summary>
        private static void PersistDesired()
        {
            try
            {
                Directory.CreateDirectory(GlassBoardModule.DataFolder);
                string tmp = DesiredFile + ".tmp";
                File.WriteAllText(tmp, JsonSerializer.Serialize(new DesiredState
                {
                    Taskbar = _taskbarHidden,
                    Icons = _iconsHidden
                }));
                File.Move(tmp, DesiredFile, overwrite: true);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：写入任务栏/图标显隐记忆失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 启动时重放“上一次用户选择的显隐”。必须在 Recover()（还原原始状态）之后调用：
        /// 先还原成安全状态，再按用户的选择重新隐藏/显示，才是“记住上次的选择”。
        /// </summary>
        public static void ReplayDesired()
        {
            try
            {
                if (!File.Exists(DesiredFile))
                    return;

                DesiredState? saved = JsonSerializer.Deserialize<DesiredState>(File.ReadAllText(DesiredFile));
                if (saved == null)
                    return;

                if (saved.Taskbar is bool taskbar)
                    AppLog.Write("GlassBoard：按上次记忆恢复任务栏 → " + SetHidden(taskbar: true, hide: taskbar));

                if (saved.Icons is bool icons)
                    AppLog.Write("GlassBoard：按上次记忆恢复桌面图标 → " + SetHidden(taskbar: false, hide: icons));
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：恢复任务栏/图标显隐记忆失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        public static string ToggleTaskbar() => SetHidden(taskbar: true, hide: !DesiredHidden(taskbar: true));

        public static string ToggleIcons() => SetHidden(taskbar: false, hide: !DesiredHidden(taskbar: false));

        /// <summary>
        /// 隐藏期间反复压住：Windows 在切换桌面、分辨率变化、
        /// explorer 重建窗口时会把任务栏/图标重新显示出来。
        /// </summary>
        private static void Maintain()
        {
            try
            {
                foreach (bool taskbar in new[] { true, false })
                {
                    if ((taskbar ? _taskbarHidden : _iconsHidden) != true)
                        continue;

                    List<IntPtr> windows = taskbar ? Taskbars() : DesktopLists();
                    if (windows.Count == 0)
                        continue;

                    // 判定“这是用户自己显示的”：必须是**我们藏过、且当时是隐藏状态**的那个句柄又可见了。
                    // 只判断“有窗口可见”不够 —— explorer 重建桌面图标窗口时会给出**新句柄**，
                    // 新窗口默认就是可见的，之前会被误判成“用户的选择”，于是巡逻彻底放弃压制
                    //（2026-11-15 用户实测：打开面板后图标冒出来，之后再也不自动隐藏）。
                    // 新句柄一律**不算**用户的选择，继续压制。
                    bool userShowed = windows.Any(h =>
                    {
                        if (!IsWindowVisible(h))
                            return false;

                        return Original.TryGetValue(h.ToInt64(), out var savedNow) && savedNow != null && !savedNow.Visible;
                    });

                    if ((DateTime.UtcNow - _hiddenSinceUtc).TotalSeconds > 2.0 && userShowed)
                    {
                        AppLog.Write("GlassBoard：检测到" + (taskbar ? "任务栏" : "桌面图标")
                            + "已被用户自己显示 → 记住这次选择，不再自动隐藏");

                        if (taskbar)
                            _taskbarHidden = false;
                        else
                            _iconsHidden = false;

                        PersistDesired();
                        continue;
                    }

                    Remember(windows);
                    foreach (IntPtr h in windows)
                    {
                        if (IsWindowVisible(h))
                            ShowWindowAsync(h, SW_HIDE);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：维持隐藏状态失败：{ex.GetType().Name}: {ex.Message}");
                StopMaintenance();
            }
        }

        private static void EnsureMaintenance()
        {
            lock (Gate)
            {
                bool need = _taskbarHidden == true || _iconsHidden == true;

                if (need && _maintenance == null)
                    _maintenance = new Timer(_ => Maintain(), null, 750, 750);
                else if (!need)
                    StopMaintenanceLocked();
            }
        }

        private static void StopMaintenance()
        {
            lock (Gate)
                StopMaintenanceLocked();
        }

        private static void StopMaintenanceLocked()
        {
            _maintenance?.Dispose();
            _maintenance = null;
        }

        /// <summary>
        /// 启动时兜底：上次隐藏后程序异常退出（没来得及还原），
        /// 这里按 shell-recovery.json 记录的原始状态把窗口还原回去。
        /// </summary>
        public static void Recover()
        {
            try
            {
                if (!File.Exists(RecoveryFile))
                    return;

                var saved = JsonSerializer.Deserialize<List<ShellState>>(File.ReadAllText(RecoveryFile));
                if (saved != null)
                {
                    foreach (ShellState state in saved)
                    {
                        if (MatchesSaved(state))
                            ShowWindow(new IntPtr(state.Handle), state.Visible ? SW_SHOWNA : SW_HIDE);
                    }
                }

                File.Delete(RecoveryFile);
                AppLog.Write("GlassBoard：已按上次记录还原任务栏 / 桌面图标状态");
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：启动还原失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>按本次会话记录的原始状态还原，并停止维持定时器。</summary>
        public static void RestoreOriginal()
        {
            StopMaintenance();

            // 【关键】先把“用户选择的显隐”落盘，再清空内存状态。
            // 这次还原只是“退出时把窗口还回原样”，不代表用户改了选择；
            // 少了这一步，下次自启读到的就是“没有记忆”，任务栏又会显示出来。
            PersistDesired();

            _taskbarHidden = null;
            _iconsHidden = null;

            foreach (ShellState state in Original.Values)
            {
                if (MatchesSaved(state))
                    ShowWindow(new IntPtr(state.Handle), state.Visible ? SW_SHOWNA : SW_HIDE);
            }

            Original.Clear();

            try
            {
                if (File.Exists(RecoveryFile))
                    File.Delete(RecoveryFile);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：删除恢复记录失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 无条件全部显示（兜底用，不管原来是什么状态）。
        ///
        /// rememberChoice=true（默认，用户自己在界面里点“一键恢复”）：显示也算一次明确的选择，落盘记住。
        /// rememberChoice=false（**崩溃/退出兜底专用**，2026-11-15 用户实测的坑）：
        /// 只把窗口显示回来让用户能操作，**绝不改“用户想隐藏”的记忆** ——
        /// 否则下次启动读到的就是“显示”，再也不会自动隐藏（用户实测：“打开面板图标又回来了、还得手动藏”）。
        /// </summary>
        public static void ShowAll(bool rememberChoice = true)
        {
            StopMaintenance();

            if (rememberChoice)
            {
                _taskbarHidden = false;
                _iconsHidden = false;

                // “全部显示”是一次明确的选择，记下来，下次自启保持一致
                PersistDesired();
            }

            foreach (IntPtr h in Taskbars().Concat(DesktopLists()))
                ShowWindow(h, SW_SHOWNA);

            if (!rememberChoice)
                return;

            Original.Clear();

            try
            {
                if (File.Exists(RecoveryFile))
                    File.Delete(RecoveryFile);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：删除恢复记录失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
