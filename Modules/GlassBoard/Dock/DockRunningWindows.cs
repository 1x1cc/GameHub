using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard.Dock
{
    /// <summary>一个正在运行的窗口（对应原版 RunningWindow）。</summary>
    public sealed class RunningWindow
    {
        public IntPtr Handle { get; set; }

        public uint ProcessId { get; set; }

        public string? Path { get; set; }

        public string Title { get; set; } = string.Empty;
    }

    /// <summary>入口与运行中窗口的匹配规则（对应原版 WindowActivationRules）。</summary>
    public static class WindowActivationRules
    {
        public static string? NormalizePath(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return null;

            string text = path.Trim().Trim('"').Replace('/', '\\');
            return text.StartsWith("\\\\?\\", StringComparison.Ordinal) ? text[4..] : text;
        }

        /// <summary>这个入口可能对应的 exe 路径（Dock 用它判断“是否正在运行”）。</summary>
        public static string[] Executables(DockEntry entry)
        {
            if (entry == null || entry.IsFolder)
                return Array.Empty<string>();

            string? target = NormalizePath(entry.WindowTarget);
            if (target != null && Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                return new[] { target };

            return new[] { entry.LaunchTarget, entry.Path }
                .Select(NormalizePath)
                .Where(p => p != null && Path.GetExtension(p).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                .Select(p => p!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }

        /// <summary>
        /// 同程序有多个窗口时选哪个：优先切到当前窗口的下一个（轮流切换），
        /// 否则用上次切过的那个（原版 Select）。
        /// </summary>
        public static long Select(IEnumerable<long> windows, long foreground, long previous, bool ownForeground)
        {
            var list = windows.Where(h => h != 0).Distinct().OrderBy(h => h).ToList();
            if (list.Count == 0)
                return 0L;

            int current = list.IndexOf(foreground);
            if (current >= 0)
                return list[(current + 1) % list.Count];

            int last = list.IndexOf(previous);
            if (last >= 0)
                return list[ownForeground ? (last + 1) % list.Count : last];

            return list[0];
        }
    }

    /// <summary>
    /// 运行中窗口的枚举与激活（对应原版 RunningWindows）：
    /// 只收“有标题、普通层、非最小化隐藏、非本进程、非桌面/任务栏”的可见窗口，
    /// 这样 Dock 里显示的就是用户真正在用的程序。
    /// </summary>
    public static class DockRunningWindows
    {
        private delegate bool EnumCallback(IntPtr window, IntPtr value);

        private struct FlashInfo
        {
            public uint Size;
            public IntPtr Window;
            public uint Flags;
            public uint Count;
            public uint Timeout;
        }

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumCallback callback, IntPtr value);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsWindow(IntPtr window);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr window);

        [DllImport("user32.dll")]
        private static extern IntPtr GetWindow(IntPtr window, uint command);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong(IntPtr window, int index);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr window, out uint id);

        [DllImport("user32.dll")]
        private static extern bool ShowWindowAsync(IntPtr window, int command);

        [DllImport("user32.dll")]
        private static extern IntPtr GetForegroundWindow();

        [DllImport("user32.dll")]
        private static extern IntPtr GetLastActivePopup(IntPtr window);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowText(IntPtr window, StringBuilder text, int length);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetClassName(IntPtr window, StringBuilder text, int length);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessageTimeout(IntPtr window, uint message, IntPtr w, IntPtr l, uint flags, uint timeout, out IntPtr result);

        [DllImport("user32.dll")]
        private static extern bool FlashWindowEx(ref FlashInfo info);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr window);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern IntPtr OpenProcess(uint access, bool inherit, uint id);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool QueryFullProcessImageName(IntPtr process, uint flags, StringBuilder path, ref int length);

        [DllImport("kernel32.dll")]
        private static extern bool CloseHandle(IntPtr handle);

        [DllImport("dwmapi.dll")]
        private static extern int DwmGetWindowAttribute(IntPtr window, int attribute, out int value, int size);

        private static readonly List<RunningWindow> Windows = new();
        private static readonly Dictionary<string, long> LastActivated = new(StringComparer.OrdinalIgnoreCase);

        private static readonly string[] SkipClasses = { "Progman", "WorkerW", "Shell_TrayWnd", "Shell_SecondaryTrayWnd" };

        public static string? ProcessPath(uint id)
        {
            IntPtr process = OpenProcess(4096u, false, id);
            if (process == IntPtr.Zero)
                return null;

            try
            {
                int length = 512;
                var buffer = new StringBuilder(length);

                if (!QueryFullProcessImageName(process, 0u, buffer, ref length))
                {
                    if (Marshal.GetLastWin32Error() != 122)
                        return null;

                    length = 32768;
                    buffer = new StringBuilder(length);
                    if (!QueryFullProcessImageName(process, 0u, buffer, ref length))
                        return null;
                }

                return WindowActivationRules.NormalizePath(buffer.ToString());
            }
            finally
            {
                CloseHandle(process);
            }
        }

        public static void Scan()
        {
            Windows.Clear();

            var byPid = new Dictionary<uint, string?>();
            int own = Process.GetCurrentProcess().Id;

            EnumWindows((handle, _) =>
            {
                if (!IsWindowVisible(handle))
                    return true;

                int style = GetWindowLong(handle, -20);
                bool toolWindow = ((uint)style & 0x80u) != 0;
                IntPtr owner = GetWindow(handle, 4u);
                bool ownedVisible = (style & 0x40000) == 0 && owner != IntPtr.Zero && IsWindowVisible(owner);

                if (toolWindow || ownedVisible)
                    return true;

                try
                {
                    // 14 = DWMWA_CLOAKED：被“虚拟桌面/挂起 UWP”藏起来的窗口不算运行中
                    if (DwmGetWindowAttribute(handle, 14, out int cloaked, 4) == 0 && cloaked != 0)
                        return true;
                }
                catch
                {
                    // 老系统没有这个属性，忽略
                }

                GetWindowThreadProcessId(handle, out uint pid);
                if (pid == own)
                    return true;

                if (!byPid.TryGetValue(pid, out string? path))
                {
                    try { path = ProcessPath(pid); }
                    catch { path = null; }

                    byPid[pid] = path;
                }

                var title = new StringBuilder(1024);
                GetWindowText(handle, title, title.Capacity);
                if (string.IsNullOrWhiteSpace(title.ToString()))
                    return true;

                var cls = new StringBuilder(128);
                GetClassName(handle, cls, cls.Capacity);
                if (SkipClasses.Contains(cls.ToString()))
                    return true;

                Windows.Add(new RunningWindow
                {
                    Handle = handle,
                    ProcessId = pid,
                    Path = path,
                    Title = title.ToString()
                });

                return true;
            }, IntPtr.Zero);
        }

        public static List<RunningWindow> For(DockEntry entry)
        {
            if (entry == null || entry.IsFolder || Directory.Exists(entry.Path))
                return new List<RunningWindow>();

            string[] paths = WindowActivationRules.Executables(entry);

            return Windows
                .Where(w => paths.Contains(w.Path ?? string.Empty, StringComparer.OrdinalIgnoreCase))
                .OrderBy(w => w.Handle.ToInt64())
                .ToList();
        }

        public static List<RunningWindow> Snapshot() => Windows.ToList();

        public static bool IsRunning(DockEntry entry) => For(entry).Count > 0;

        /// <summary>把窗口切到前台；被系统拒绝时闪一下任务栏提示（原版同款兜底）。</summary>
        public static bool ActivateHandle(IntPtr handle)
        {
            if (!IsWindow(handle))
                return false;

            if (IsIconic(handle))
                ShowWindowAsync(handle, 9);

            IntPtr popup = GetLastActivePopup(handle);
            IntPtr target = popup != IntPtr.Zero && IsWindowVisible(popup) ? popup : handle;

            if (!SetForegroundWindow(target))
            {
                var info = default(FlashInfo);
                info.Size = (uint)Marshal.SizeOf(typeof(FlashInfo));
                info.Window = target;
                info.Flags = 3u;
                info.Count = 2u;
                FlashWindowEx(ref info);

                AppLog.Write($"GlassBoard Dock：切前台被系统拦下，已改为闪烁提示 handle={target}");
            }

            return true;
        }

        /// <summary>点击 Dock 图标：没运行就启动，运行中就切换窗口。</summary>
        public static bool Activate(DockEntry entry)
        {
            Scan();

            var candidates = For(entry);
            if (candidates.Count == 0)
                return false;

            IntPtr foreground = GetForegroundWindow();
            GetWindowThreadProcessId(foreground, out uint pid);

            LastActivated.TryGetValue(entry.Path, out long previous);
            long selected = WindowActivationRules.Select(
                candidates.Select(w => w.Handle.ToInt64()),
                foreground.ToInt64(),
                previous,
                pid == (uint)Process.GetCurrentProcess().Id);

            if (!ActivateHandle(new IntPtr(selected)))
                return false;

            LastActivated[entry.Path] = selected;
            return true;
        }

        /// <summary>窗口图标：优先问窗口要大图标，失败就退回 exe 自带图标。</summary>
        public static ImageSource? Icon(RunningWindow window)
        {
            try
            {
                SendMessageTimeout(window.Handle, 127u, new IntPtr(1), IntPtr.Zero, 2u, 80u, out IntPtr icon);
                if (icon == IntPtr.Zero)
                    SendMessageTimeout(window.Handle, 127u, new IntPtr(2), IntPtr.Zero, 2u, 80u, out icon);

                if (icon != IntPtr.Zero)
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(icon, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                    source.Freeze();
                    return source;
                }

                if (!string.IsNullOrEmpty(window.Path) && File.Exists(window.Path))
                {
                    using var fileIcon = System.Drawing.Icon.ExtractAssociatedIcon(window.Path);
                    if (fileIcon != null)
                    {
                        var source = Imaging.CreateBitmapSourceFromHIcon(fileIcon.Handle, Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());
                        source.Freeze();
                        return source;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：读取窗口图标失败：{ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }
    }

    /// <summary>
    /// 解析入口的启动目标（对应原版 LaunchInfoService 里 Dock 需要的部分）：
    /// 快捷方式要解析出真正的 exe，否则无法判断“是否正在运行”。
    /// </summary>
    public static class LaunchInfo
    {
        public static void Enrich(DockEntry entry)
        {
            string path = entry.Path ?? string.Empty;

            if (Directory.Exists(path))
            {
                entry.IsFolder = true;
                entry.LaunchTarget = path;
                entry.LaunchArguments = string.Empty;
                return;
            }

            string target = path;
            string arguments = string.Empty;

            if (Path.GetExtension(path).Equals(".lnk", StringComparison.OrdinalIgnoreCase))
            {
                object? shell = null;
                object? shortcut = null;

                try
                {
                    Type? type = Type.GetTypeFromProgID("WScript.Shell");
                    if (type != null)
                    {
                        shell = Activator.CreateInstance(type);
                        shortcut = type.InvokeMember("CreateShortcut", System.Reflection.BindingFlags.InvokeMethod, null, shell, new object[] { path });
                        if (shortcut != null)
                        {
                            target = (string?)shortcut.GetType().InvokeMember("TargetPath", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) ?? path;
                            arguments = (string?)shortcut.GetType().InvokeMember("Arguments", System.Reflection.BindingFlags.GetProperty, null, shortcut, null) ?? string.Empty;
                        }
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard Dock：解析快捷方式失败 {path}：{ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    if (shortcut != null && Marshal.IsComObject(shortcut))
                        Marshal.ReleaseComObject(shortcut);

                    if (shell != null && Marshal.IsComObject(shell))
                        Marshal.ReleaseComObject(shell);
                }
            }

            entry.LaunchTarget = target;
            entry.LaunchArguments = arguments;
            entry.IsFolder = entry.IsFolder || Directory.Exists(target);

            try
            {
                if (Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase) && File.Exists(target))
                {
                    var info = FileVersionInfo.GetVersionInfo(target);
                    entry.ProductInfo = $"{info.ProductName} {info.FileDescription}".Trim();
                }
            }
            catch
            {
                // 读版本信息失败不影响使用
            }
        }
    }
}
