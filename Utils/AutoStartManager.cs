using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Text;

namespace GameHub.Utils
{
    /// <summary>
    /// 开机自启动管理。
    ///
    /// 全程序只维护 **一个** 自启动项：注册表值
    /// <c>HKCU\SOFTWARE\Microsoft\Windows\CurrentVersion\Run\GameHub</c>。
    ///   1) 工具箱右上角的总开关是唯一的自启动控制点；
    ///   2) 历史版本会为每个模块另写一项 "GameHub.{模块名}"（命令行带 --module），
    ///      但程序从未处理过 --module，多个项只会带来两个问题：
    ///        · 关掉其中一个开关，其它项仍在，开机照样自启（用户看到的现象）；
    ///        · 开机时多个项同时触发，第二个实例会弹"已经在运行中"对话框。
    ///      这些历史项在启动时由 <see cref="CleanupLegacyModuleEntries"/> 清除；
    ///   3) 注册的命令行固定带 <see cref="AutoStartArgument"/> 参数，程序据此判断
    ///      "本次是被开机自启动拉起来的"，从而只驻留托盘、不弹出主窗口。
    ///
    /// 造成"开关显示 on、开机却没反应"的三个坑，这里都堵住了：
    ///   · **路径失效**：注册表里记的是写开关那一刻的 exe 路径。换个目录、重新发布、
    ///     清理过 bin 之后，这个路径就不存在了，Windows 会静默跳过（不会报错），
    ///     而界面只看"注册表值是否存在"，所以照样显示 on。
    ///     → 每次启动都核对路径并自动重写，见 <see cref="SyncRegisteredPath"/>。
    ///   · **被任务管理器禁用**：在「启动应用」里关掉过 GameHub 后，Windows 会在
    ///     Explorer\StartupApproved\Run 里留下"已禁用"标记；只重写 Run 项无法恢复，
    ///     必须清掉这个标记。→ 见 <see cref="ClearDisabledApproval"/>。
    ///   · **写入没校验**：旧实现 SetValue 不抛异常就当作成功。
    ///     → 现在写入后立即回读比对，失败明确返回 false。
    /// </summary>
    public static class AutoStartManager
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        private const string ApprovalKeyPath =
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";

        /// <summary>工具箱自身的注册表值名。</summary>
        public const string HubKey = "GameHub";

        /// <summary>开机自启动时附加的命令行参数（程序自己识别，Windows 不解释它）。</summary>
        public const string AutoStartArgument = "--autostart";

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

        /// <summary>从注册表读回当前登记的命令行（未登记时返回 null）。</summary>
        public static string? RegisteredCommand()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                var value = key?.GetValue(HubKey)?.ToString();
                return string.IsNullOrWhiteSpace(value) ? null : value;
            }
            catch
            {
                return null;
            }
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

        /// <summary>是否已启用自启动（只看注册表值是否存在）。</summary>
        public static bool IsEnabled() => RegisteredCommand() != null;

        /// <summary>
        /// 已登记的命令行是否还能用：路径必须是当前这个 exe，且带上自启动参数。
        /// 返回 true 表示需要重写注册表项。
        /// </summary>
        public static bool NeedsRepair()
        {
            var command = RegisteredCommand();
            if (command == null)
                return false;   // 没开启，谈不上修复

            var registered = ParseExecutablePath(command);
            if (string.IsNullOrEmpty(registered) || !File.Exists(registered))
                return true;

            if (!string.Equals(registered, ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return true;

            return !command.Contains(AutoStartArgument, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 自启动是否处于"真的能用"的状态：已登记 + 路径有效 + 没被系统禁用。
        /// 界面上的开关颜色用它决定，避免出现"显示 on 其实不生效"。
        /// </summary>
        public static bool IsReady()
        {
            if (NeedsRepair() || RegisteredCommand() == null)
                return false;

            return !IsDisabledApproval();
        }

        /// <summary>系统（任务管理器/策略）是否把这个启动项标成了"已禁用"。</summary>
        public static bool IsDisabledApproval()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(ApprovalKeyPath, false);
                if (key?.GetValue(HubKey) is byte[] blob && blob.Length > 0)
                    return (blob[0] & 0x01) != 0;   // 0x02 = 启用，0x03 = 禁用
            }
            catch
            {
                // 读不到就当作没被禁用
            }

            return false;
        }

        /// <summary>清除"已被系统禁用"的标记（等价于在任务管理器里重新启用）。</summary>
        private static void ClearDisabledApproval()
        {
            try
            {
                if (!IsDisabledApproval())
                    return;

                using var key = Registry.CurrentUser.OpenSubKey(ApprovalKeyPath, true);
                key?.DeleteValue(HubKey, false);
                AppLog.Write("开机自启动：已清除系统启动项里的「已禁用」标记");
            }
            catch (Exception ex)
            {
                AppLog.Write($"开机自启动：清除「已禁用」标记失败 {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>清除历史遗留的按模块自启动项（GameHub.*），只保留统一的 GameHub 项。</summary>
        public static void CleanupLegacyModuleEntries()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                if (key == null)
                    return;

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
            catch (Exception ex)
            {
                AppLog.Write($"开机自启动：清理历史项失败 {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>启用自启动。写入后回读校验，不一致即视为失败。</summary>
        public static bool Enable()
        {
            var exe = ExecutablePath;
            if (string.IsNullOrEmpty(exe) || !File.Exists(exe))
            {
                AppLog.Write($"开机自启动：开启失败，当前程序路径不可用（{exe}）");
                return false;
            }

            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
                if (key == null)
                {
                    AppLog.Write("开机自启动：开启失败，无法打开注册表 Run 项");
                    return false;
                }

                var command = HubCommand();
                key.SetValue(HubKey, command, RegistryValueKind.String);

                var readBack = key.GetValue(HubKey)?.ToString();
                if (!string.Equals(readBack, command, StringComparison.Ordinal))
                {
                    AppLog.Write($"开机自启动：开启失败，写入后回读不一致（写入={command}，回读={readBack}）");
                    return false;
                }

                // 关键：清掉"曾被系统禁用"的标记，否则注册表项再正确，开机也不会执行
                ClearDisabledApproval();

                AppLog.Write($"开机自启动：已开启（{command}）");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write($"开机自启动：开启异常 {ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }

        /// <summary>禁用自启动。</summary>
        public static bool Disable()
        {
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true))
                    key?.DeleteValue(HubKey, false);

                if (IsEnabled())
                {
                    AppLog.Write("开机自启动：关闭失败，注册表项仍然存在");
                    return false;
                }

                AppLog.Write("开机自启动：已关闭");
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write($"开机自启动：关闭异常 {ex.GetType().Name}: {ex.Message}");
                return false;
            }
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
        /// 启动时调用：已开启自启动就把注册路径校准成当前这个 exe。
        ///
        /// 用户可能把程序换目录、重新发布到别处，或者干脆从另一个副本启动；
        /// 不做这一步，注册表里会一直留着旧路径，开机时 Windows 静默跳过，
        /// 表现就是"开关是 on，开机却没反应"。
        /// </summary>
        public static void SyncRegisteredPath()
        {
            var command = RegisteredCommand();
            if (command == null)
            {
                AppLog.Write("开机自启动：当前为 off（未注册）");
                return;
            }

            if (NeedsRepair())
            {
                bool ok = Enable();
                AppLog.Write(ok
                    ? $"开机自启动：注册路径已自动修复（旧={command}，新={HubCommand()}）"
                    : $"开机自启动：注册路径修复失败（旧={command}）");
                return;
            }

            ClearDisabledApproval();
            AppLog.Write($"开机自启动：on，注册正常（{command}）");
        }

        /// <summary>给界面/日志用的一行式状态摘要。</summary>
        public static string GetStatusSummary()
        {
            if (RegisteredCommand() == null)
                return "开机自启动：off";

            if (IsDisabledApproval())
                return "开机自启动：on，但已被系统/任务管理器禁用";

            if (NeedsRepair())
                return "开机自启动：on，但注册的路径已失效";

            return "开机自启动：on，状态正常";
        }

        /// <summary>给界面提示/日志用的多行详细状态。</summary>
        public static string GetStatusReport()
        {
            var command = RegisteredCommand();
            var sb = new StringBuilder();

            if (command == null)
            {
                sb.AppendLine("注册状态：未注册（off）");
                sb.Append($"当前程序：{ExecutablePath}");
                return sb.ToString();
            }

            var registeredExe = ParseExecutablePath(command);
            sb.AppendLine($"注册状态：已注册（on）");
            sb.AppendLine($"注册命令：{command}");
            sb.AppendLine($"注册路径是否存在：{(File.Exists(registeredExe) ? "是" : "否（开机时 Windows 会静默跳过）")}");
            sb.AppendLine($"与当前程序一致：{(string.Equals(registeredExe, ExecutablePath, StringComparison.OrdinalIgnoreCase) ? "是" : "否")}");
            sb.AppendLine($"系统启动项状态：{(IsDisabledApproval() ? "已被禁用（任务管理器/组策略）" : "正常")}");
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
    }
}
