using Microsoft.Win32;
using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;

namespace GameHub.Utils
{
    /// <summary>
    /// 开机自启动管理。
    ///
    /// 全程序只维护 **一个** 自启动项：注册表值 "GameHub"。
    ///   1) 工具箱右上角的总开关是唯一的自启动控制点；
    ///   2) 历史版本会为每个模块另写一项 "GameHub.{模块名}"（命令行带 --module），
    ///      但程序从未处理过 --module，多个项只会带来两个问题：
    ///        · 关掉其中一个开关，其它项仍在，开机照样自启（用户看到的现象）；
    ///        · 开机时多个项同时触发，第二个实例会弹"已经在运行中"对话框。
    ///      这些历史项在启动时由 <see cref="CleanupLegacyModuleEntries"/> 清除。
    /// </summary>
    public static class AutoStartManager
    {
        private const string RunKeyPath = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run";

        /// <summary>工具箱自身的注册表值名。</summary>
        public const string HubKey = "GameHub";

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

        /// <summary>工具箱自身的命令行。</summary>
        public static string HubCommand() => $"\"{ExecutablePath}\"";

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
                        key.DeleteValue(name, false);
                }
            }
            catch
            {
                // 清理失败不影响正常使用
            }
        }

        /// <summary>是否已启用自启动。</summary>
        public static bool IsEnabled()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, false);
                var value = key?.GetValue(HubKey)?.ToString();
                return !string.IsNullOrEmpty(value);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>启用自启动。</summary>
        public static bool Enable()
        {
            try
            {
                using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, true);
                if (key == null) return false;
                key.SetValue(HubKey, HubCommand());
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>禁用自启动。</summary>
        public static bool Disable()
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath, true);
                key?.DeleteValue(HubKey, false);
                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>切换自启动状态，返回切换后的状态。</summary>
        public static bool Toggle()
        {
            if (IsEnabled())
            {
                Disable();
                return false;
            }

            Enable();
            return IsEnabled();
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
