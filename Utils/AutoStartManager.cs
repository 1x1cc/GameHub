using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;
using System.Xml;

namespace GameHub.Utils
{
    /// <summary>
    /// 开机自启动管理。
    ///
    /// 【为什么从注册表 Run 项改成计划任务】
    /// GameHub 的 app.manifest 声明了 requireAdministrator（整体提权运行）。
    /// Windows 出于安全原因会 **静默跳过** 需要提权的程序在
    /// HKCU\...\Run（以及「启动」文件夹）里的自启动项——注册表里写得再对、
    /// 任务管理器里也显示「已启用」，开机时就是不会执行，界面却仍显示 on。
    /// 这正是「开关是 on、开机却没反应」的根本原因。
    ///
    /// 而计划任务可以用 RunLevel=Highest 在登录时以提权方式启动（不弹 UAC），
    /// 与 Pavise 自己的 PaviseAuto 计划任务是同一套已验证可用的机制。
    /// 因此这里改为维护 **一个** 计划任务：
    ///   schtasks /Create /F /SC ONLOGON /RL HIGHEST /TN GameHubAuto
    ///            /TR "&lt;GameHub.exe&gt; --autostart"
    /// 不再写 HKCU\...\Run\GameHub；旧的历史注册表项在启动 / 开启时清理掉。
    ///
    /// 沿用旧实现要堵的三个坑，只是对象从注册表换成计划任务：
    ///   · 路径失效：任务里记的是建任务那一刻的 exe 路径，换目录 / 重新发布后失效，
    ///     启动时用 <see cref="SyncRegisteredPath"/> 自动重建；
    ///   · 被禁用：在任务计划程序里「禁用」该任务后，&lt;Settings&gt;&lt;Enabled&gt;false&lt;/Enabled&gt;
    ///     会被写进任务；Enable 总是 /F 强制重建，可恢复；
    ///   · 写入没校验：创建后立即 /Query 回读比对。
    /// </summary>
    public static class AutoStartManager
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        private const string ApprovalKeyPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        /// <summary>自启动计划任务名（与 Pavise 的 PaviseAuto 并列，互不冲突）。</summary>
        public const string TaskName = "GameHubAuto";

        /// <summary>历史版本使用的注册表值名（仅用于清理 / 迁移，不再用于注册自启动）。</summary>
        public const string HubKey = "GameHub";

        /// <summary>开机自启动时附加的命令行参数（程序自己识别，Windows 不解释它）。</summary>
        public const string AutoStartArgument = "--autostart";

        private static readonly object Gate = new();

        private static TaskState? _cachedState;
        private static DateTime _cachedAt;
        private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(2);

        /// <summary>当前可执行文件完整路径。</summary>
        public static string ExecutablePath
        {
            get
            {
                try
                {
                    var path = Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(path))
                        return path;
                }
                catch
                {
                    // 回退到程序集位置
                }

                return Assembly.GetEntryAssembly()?.Location ?? string.Empty;
            }
        }

        /// <summary>工具箱自身的命令行（带自启动标记参数）。</summary>
        public static string HubCommand() => $"\"{ExecutablePath}\" {AutoStartArgument}";

        /// <summary>判断本次进程是不是被开机自启动拉起来的。</summary>
        public static bool IsAutoStartInvocation(string[]? args)
        {
            if (args == null)
                return false;

            foreach (var arg in args)
            {
                if (string.Equals(arg?.Trim(), AutoStartArgument, StringComparison.OrdinalIgnoreCase))
                    return true;
            }

            return false;
        }

        // ---------------- 计划任务读取 ----------------

        private sealed class TaskState
        {
            public bool Exists;
            public string? Command;   // 完整命令行："D:\...\GameHub.exe" --autostart
            public bool Disabled;
        }

        private static TaskState ReadState()
        {
            lock (Gate)
            {
                if (_cachedState != null && DateTime.UtcNow - _cachedAt < CacheTtl)
                    return _cachedState;
            }

            var state = QueryState();
            lock (Gate)
            {
                _cachedState = state;
                _cachedAt = DateTime.UtcNow;
            }
            return state;
        }

        private static void InvalidateCache()
        {
            lock (Gate)
            {
                _cachedState = null;
            }
        }

        private static TaskState QueryState()
        {
            var xml = QueryTaskXml();
            if (xml == null)
                return new TaskState { Exists = false };

            try
            {
                var doc = new XmlDocument();
                doc.XmlResolver = null;
                doc.LoadXml(xml.TrimStart('\ufeff'));

                var cmd = NodeText(doc, "Task/Actions/Exec/Command");
                var args = NodeText(doc, "Task/Actions/Exec/Arguments");
                var enabled = NodeText(doc, "Task/Settings/Enabled");

                string command;
                if (string.IsNullOrEmpty(cmd))
                {
                    command = string.Empty;
                }
                else
                {
                    command = args == null ? cmd : $"{cmd} {args}";
                }

                return new TaskState
                {
                    Exists = true,
                    Command = command,
                    Disabled = string.Equals(enabled, "false", StringComparison.OrdinalIgnoreCase),
                };
            }
            catch
            {
                // 任务存在但 XML 读不出来：仍当作已注册，但判定为需要修复
                return new TaskState { Exists = true, Command = string.Empty, Disabled = false };
            }
        }

        private static string? NodeText(XmlDocument doc, string localPath)
        {
            var node = doc.SelectSingleNode(LocalNamePath(localPath));
            return node?.InnerText?.Trim();
        }

        /// <summary>把 "Actions/Exec/Command" 这种路径转成忽略命名空间的 XPath。</summary>
        private static string LocalNamePath(string localPath)
        {
            var sb = new StringBuilder();
            foreach (var part in localPath.Split('/'))
            {
                if (sb.Length > 0)
                    sb.Append('/');
                sb.Append("*[local-name()='").Append(part).Append("']");
            }
            return sb.ToString();
        }

        private static string? QueryTaskXml()
        {
            int code = RunSchtasks($"/Query /TN {TaskName} /XML", out var stdout, out _);
            if (code != 0)
                return null;
            return string.IsNullOrWhiteSpace(stdout) ? null : stdout;
        }

        private static bool TaskExists() => ReadState().Exists;

        // ---------------- 对外状态 ----------------

        /// <summary>从计划任务读回当前登记的命令行（未登记时返回 null）。</summary>
        public static string? RegisteredCommand()
        {
            var state = ReadState();
            if (!state.Exists || string.IsNullOrWhiteSpace(state.Command))
                return null;
            return state.Command;
        }

        /// <summary>
        /// 从命令行里取出可执行文件路径。兼容两种写法：
        ///   "C:\a b\GameHub.exe" --autostart  → C:\a b\GameHub.exe
        ///   C:\GameHub.exe                    → C:\GameHub.exe
        /// </summary>
        public static string ParseExecutablePath(string? command)
        {
            if (string.IsNullOrWhiteSpace(command))
                return string.Empty;

            var text = command.Trim();
            if (text.StartsWith("\"", StringComparison.Ordinal))
            {
                int end = text.IndexOf('"', 1);
                return end > 1 ? text.Substring(1, end - 1) : string.Empty;
            }

            int space = text.IndexOf(' ');
            return space > 0 ? text.Substring(0, space) : text;
        }

        /// <summary>是否已启用自启动（只看计划任务是否存在）。</summary>
        public static bool IsEnabled() => TaskExists();

        /// <summary>
        /// 已登记的命令行是否还能用：路径必须是当前这个 exe，且带上自启动参数。
        /// 返回 true 表示需要重建计划任务。
        /// </summary>
        public static bool NeedsRepair()
        {
            var state = ReadState();
            if (!state.Exists)
                return false;   // 没开启，谈不上修复

            if (string.IsNullOrWhiteSpace(state.Command))
                return true;

            var registered = ParseExecutablePath(state.Command);
            if (string.IsNullOrEmpty(registered) || !File.Exists(registered))
                return true;

            if (!string.Equals(registered, ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return true;

            return !state.Command.Contains(AutoStartArgument, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 自启动是否处于"真的能用"的状态：任务存在 + 路径有效 + 带参数 + 未被禁用。
        /// 界面上的开关颜色用它决定，避免出现"显示 on 其实不生效"。
        /// </summary>
        public static bool IsReady()
        {
            if (!IsEnabled() || NeedsRepair())
                return false;

            return !IsDisabledApproval();
        }

        /// <summary>计划任务是否被（任务计划程序 / 用户）标成了"已禁用"。</summary>
        public static bool IsDisabledApproval()
        {
            var state = ReadState();
            return state.Exists && state.Disabled;
        }

        // ---------------- 开关 ----------------

        /// <summary>启用自启动：创建计划任务，创建后回读校验。</summary>
        public static bool Enable()
        {
            var exe = ExecutablePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                AppLog.Write($"开机自启动：开启失败，当前程序路径不可用（{exe}）");
                return false;
            }

            if (IsVolatileExecutable())
            {
                // 明确告知：从 bin/obj 构建产物开启自启动，目录被清理后自启动会失效
                AppLog.Write($"开机自启动：当前为开发构建（{exe}），开启后若清理构建目录自启动会失效，建议从发布目录开启");
            }

            // 先清掉旧的注册表启动项，避免计划任务与 Run 项同时存在导致双重启动
            RemoveLegacyRunEntry();

            var command = BuildStartupCommandLine(exe, AutoStartArgument);
            var schtasksArgs = $"/Create /F /SC ONLOGON /RL HIGHEST /TN {TaskName} /TR {QuoteCommandArgument(command)}";
            int code = RunSchtasks(schtasksArgs, out _, out var error);
            if (code != 0)
            {
                AppLog.Write($"开机自启动：开启失败，schtasks 返回 {code}（{Condense(error)}）");
                InvalidateCache();
                return false;
            }

            // 回读校验
            InvalidateCache();
            if (!IsEnabled())
            {
                AppLog.Write("开机自启动：开启失败，创建后查询不到计划任务");
                return false;
            }
            if (NeedsRepair())
            {
                AppLog.Write($"开机自启动：开启失败，创建后回读不一致（任务={RegisteredCommand()}）");
                return false;
            }

            AppLog.Write($"开机自启动：已开启（计划任务 {TaskName} → {command}）");
            return true;
        }

        /// <summary>禁用自启动：删除计划任务，并清掉遗留注册表项。</summary>
        public static bool Disable()
        {
            RemoveLegacyRunEntry();

            int code = RunSchtasks($"/Delete /F /TN {TaskName}", out _, out var error);
            InvalidateCache();

            if (code != 0 && TaskExists())
            {
                AppLog.Write($"开机自启动：关闭失败，schtasks 返回 {code}（{Condense(error)}）");
                return false;
            }

            AppLog.Write("开机自启动：已关闭");
            return true;
        }

        /// <summary>切换自启动状态，返回切换后的状态。</summary>
        public static bool Toggle()
        {
            if (IsEnabled())
                Disable();
            else
                Enable();

            return IsEnabled();
        }

        /// <summary>
        /// 启动时调用：已开启自启动就把计划任务校准成当前这个 exe。
        /// 换目录 / 重新发布 / 从其它副本启动后，任务里会留着旧路径，开机时静默失败，
        /// 表现就是"开关是 on，开机却没反应"。
        /// </summary>
        public static void SyncRegisteredPath()
        {
            // 开发时用 dotnet run / 直接从 bin\obj 启动，绝不能把自启动任务改写到
            // 这些会被清理的临时路径上（否则又会回到「开关是 on、开机却没反应」）。
            // 此时保持任务原样：若任务已指向某个有效 exe，就别动它。
            if (IsVolatileExecutable())
            {
                if (TaskExists())
                    AppLog.Write($"开机自启动：当前为开发构建（{ExecutablePath}），跳过任务校准（保留={RegisteredCommand()}）");
                else
                    AppLog.Write("开机自启动：off（未注册）");
                return;
            }

            bool legacyOn = LegacyRunEntryExists();

            if (TaskExists())
            {
                // 任务在，就把可能残留的注册表项清掉，杜绝双重启动
                RemoveLegacyRunEntry();

                if (NeedsRepair())
                {
                    bool ok = Enable();
                    AppLog.Write(ok
                        ? $"开机自启动：计划任务已自动修复（→ {HubCommand()}）"
                        : "开机自启动：计划任务修复失败");
                }
                else
                {
                    AppLog.Write($"开机自启动：on，计划任务正常（{RegisteredCommand()}）");
                }
                return;
            }

            if (legacyOn)
            {
                // 旧版本用注册表 Run 项开的自启动：迁移到计划任务
                bool ok = Enable();
                AppLog.Write(ok
                    ? "开机自启动：已从注册表启动项迁移到计划任务"
                    : "开机自启动：从注册表迁移到计划任务失败");
                return;
            }

            AppLog.Write("开机自启动：当前为 off（未注册）");
        }

        /// <summary>清除历史遗留的按模块自启动项（GameHub.*），只保留统一的计划任务。</summary>
        public static void CleanupLegacyModuleEntries()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (key != null)
                {
                    // GetValueNames 返回的是快照数组，边遍历边删除是安全的
                    foreach (var name in key.GetValueNames())
                    {
                        if (name.StartsWith("GameHub.", StringComparison.OrdinalIgnoreCase))
                        {
                            key.DeleteValue(name, false);
                            AppLog.Write($"开机自启动：已清除历史遗留项 {name}");
                        }
                    }
                }

                // 顺带把历史项的"已禁用"标记也清掉
                using var approval = Registry.CurrentUser.OpenSubKey(ApprovalKeyPath, true);
                if (approval != null)
                {
                    foreach (var name in approval.GetValueNames())
                    {
                        if (name.StartsWith("GameHub.", StringComparison.OrdinalIgnoreCase))
                            approval.DeleteValue(name, false);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"开机自启动：清理历史项失败 {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>给界面/日志用的一行式状态摘要。</summary>
        public static string GetStatusSummary()
        {
            if (!IsEnabled())
                return "开机自启动：off";

            if (IsDisabledApproval())
                return "开机自启动：on，但计划任务已被禁用";

            if (NeedsRepair())
                return "开机自启动：on，但计划任务指向的路径已失效";

            return "开机自启动：on，状态正常";
        }

        /// <summary>给界面提示/日志用的多行详细状态。</summary>
        public static string GetStatusReport()
        {
            var state = ReadState();
            var command = RegisteredCommand();
            var sb = new StringBuilder();

            if (!state.Exists || command == null)
            {
                sb.AppendLine("注册状态：未注册（off）");
                sb.Append($"当前程序：{ExecutablePath}");
                return sb.ToString();
            }

            var registeredExe = ParseExecutablePath(command);
            sb.AppendLine($"注册状态：已注册（计划任务 {TaskName}）");
            sb.AppendLine($"注册命令：{command}");
            sb.AppendLine($"注册路径是否存在：{(File.Exists(registeredExe) ? "是" : "否（开机时 Windows 会静默跳过）")}");
            sb.AppendLine($"与当前程序一致：{(string.Equals(registeredExe, ExecutablePath, StringComparison.OrdinalIgnoreCase) ? "是" : "否")}");
            sb.AppendLine($"计划任务状态：{(state.Disabled ? "已被禁用（任务计划程序）" : "正常")}");
            sb.AppendLine($"启动参数 {AutoStartArgument}：{(command.Contains(AutoStartArgument, StringComparison.OrdinalIgnoreCase) ? "有" : "无（下次开启时自动补上）")}");
            sb.Append($"当前程序：{ExecutablePath}");
            return sb.ToString();
        }

        /// <summary>在文件资源管理器中定位当前程序。</summary>
        public static void RevealInExplorer()
        {
            try
            {
                var dir = Path.GetDirectoryName(ExecutablePath);
                if (!string.IsNullOrEmpty(dir))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{dir}\"") { UseShellExecute = true });
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>当前 exe 是否位于 bin/obj 这类会被构建清理的临时目录。</summary>
        private static bool IsVolatileExecutable()
        {
            var exe = ExecutablePath;
            if (string.IsNullOrEmpty(exe))
                return true;

            foreach (var part in exe.Replace('/', '\\').Split('\\'))
            {
                if (string.Equals(part, "bin", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(part, "obj", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }

            return false;
        }

        // ---------------- 注册表兼容（仅清理/迁移） ----------------

        private static bool LegacyRunEntryExists()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                return key?.GetValue(HubKey) != null;
            }
            catch
            {
                return false;
            }
        }

        private static void RemoveLegacyRunEntry()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                    key?.DeleteValue(HubKey, false);
            }
            catch
            {
                // 忽略
            }

            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(ApprovalKeyPath, true))
                    key?.DeleteValue(HubKey, false);
            }
            catch
            {
                // 忽略
            }
        }

        // ---------------- schtasks 调用 ----------------

        private static int RunSchtasks(string arguments, out string stdout, out string stderr)
        {
            stdout = string.Empty;
            stderr = string.Empty;

            try
            {
                var psi = new ProcessStartInfo(
                    Path.Combine(Environment.SystemDirectory, "schtasks.exe"), arguments)
                {
                    CreateNoWindow = true,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return -1;

                var outBuf = new StringBuilder();
                var errBuf = new StringBuilder();

                process.OutputDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (outBuf)
                            outBuf.AppendLine(e.Data);
                    }
                };
                process.ErrorDataReceived += (_, e) =>
                {
                    if (e.Data != null)
                    {
                        lock (errBuf)
                            errBuf.AppendLine(e.Data);
                    }
                };

                process.BeginOutputReadLine();
                process.BeginErrorReadLine();

                if (!process.WaitForExit(15000))
                {
                    try { process.Kill(); }
                    catch { /* 忽略 */ }
                    stdout = outBuf.ToString();
                    stderr = errBuf.ToString();
                    return -1;
                }

                process.WaitForExit();

                stdout = outBuf.ToString();
                stderr = errBuf.ToString();
                return process.ExitCode;
            }
            catch
            {
                return -1;
            }
        }

        private static string Condense(string? raw)
        {
            if (string.IsNullOrEmpty(raw))
                return "(无输出)";

            var sb = new StringBuilder();
            foreach (var line in raw.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var t = line.Trim();
                if (t.Length == 0)
                    continue;
                if (sb.Length > 0)
                    sb.Append(" | ");
                sb.Append(t);
            }
            return sb.Length == 0 ? "(无输出)" : sb.ToString();
        }

        private static string BuildStartupCommandLine(string exe, string args)
            => QuoteCommandArgument(exe) + " " + args;

        private static string QuoteCommandArgument(string value)
        {
            var sb = new StringBuilder("\"");
            int backslashes = 0;

            foreach (char c in value ?? string.Empty)
            {
                switch (c)
                {
                    case '\\':
                        backslashes++;
                        break;
                    case '"':
                        sb.Append('\\', backslashes * 2 + 1).Append(c);
                        backslashes = 0;
                        break;
                    default:
                        sb.Append('\\', backslashes).Append(c);
                        backslashes = 0;
                        break;
                }
            }

            sb.Append('\\', backslashes * 2).Append('"');
            return sb.ToString();
        }
    }
}
