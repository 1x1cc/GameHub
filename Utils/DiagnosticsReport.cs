using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace GameHub.Utils
{
    /// <summary>
    /// 一键生成"问题诊断报告"：系统信息 + 音频设备快照 + 热键设置 + 最近日志（+ 崩溃异常）。
    /// 用于排查"热键按了没反应 / 程序闪退"这类难以复现、只能拿现场数据来分析的问题。
    ///
    /// 报告内容不含任何用户身份/隐私信息，只有程序自身的运行状态，可直接发给开发者。
    /// </summary>
    public static class DiagnosticsReport
    {
        /// <summary>报告保存目录：%APPDATA%\GameHub\reports</summary>
        public static string ReportDirectory { get; } = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "GameHub", "reports");

        /// <summary>汇总生成诊断报告文本。crash 非空时会在报告里带上崩溃异常详情。</summary>
        public static string BuildReport(Exception? crash = null)
        {
            var sb = new StringBuilder();
            sb.AppendLine(crash == null ? "===== GameHub 诊断报告 =====" : "===== GameHub 崩溃报告 =====");
            sb.AppendLine($"生成时间：{DateTime.Now:yyyy-MM-dd HH:mm:ss}");
            sb.AppendLine($"系统：{RuntimeInformation.OSDescription} ({RuntimeInformation.OSArchitecture})");
            sb.AppendLine($"运行时：{RuntimeInformation.FrameworkDescription}");
            try { sb.AppendLine($"应用版本：{typeof(DiagnosticsReport).Assembly.GetName().Version}"); }
            catch { /* 忽略版本读取失败 */ }
            sb.AppendLine($"日志文件：{AppLog.FilePath}");
            sb.AppendLine();

            // 崩溃异常（如有）放在最前面，方便一眼看到出错原因
            if (crash != null)
            {
                sb.AppendLine("---- 崩溃异常 ----");
                AppendException(sb, crash);
                sb.AppendLine();
            }

            // 热键设置
            try
            {
                var s = SettingsStore.Load();
                sb.AppendLine("---- 热键设置 ----");
                sb.AppendLine($"输出热键：{s.PlaybackHotkey}  启用={s.PlaybackHotkeyEnabled}");
                sb.AppendLine($"输入热键：{s.RecordingHotkey}  启用={s.RecordingHotkeyEnabled}");
                sb.AppendLine($"蓝牙热键：{s.BluetoothAudioPauseHotkey}  启用={s.BluetoothAudioPauseHotkeyEnabled}");
                sb.AppendLine($"输出热键目标设备ID数：{s.HotkeyTargetPlaybackDeviceIds?.Count ?? 0}");
                sb.AppendLine($"输入热键目标设备ID数：{s.HotkeyTargetRecordingDeviceIds?.Count ?? 0}");
                sb.AppendLine($"关闭窗口行为：{s.CloseAction}  自启动最小化到托盘={s.AutoStartMinimizedToTray}");
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"读取设置失败：{ex.Message}");
                sb.AppendLine();
            }

            // 音频设备快照
            try
            {
                sb.AppendLine("---- 音频设备 ----");
                foreach (var flow in new[] { AudioFlow.Playback, AudioFlow.Recording })
                {
                    var devices = AudioDeviceService.GetDevices(flow);
                    sb.AppendLine($"[{flow}] 共 {devices.Count} 个：");
                    foreach (var d in devices)
                        sb.AppendLine($"    {(d.IsDefault ? "*默认 " : "      ")}{d.Name}    ID={d.Id}");
                }
                sb.AppendLine();
            }
            catch (Exception ex)
            {
                sb.AppendLine($"枚举设备失败：{ex.Message}");
                sb.AppendLine();
            }

            // 最近日志（取内存缓冲尾部，确保文件被占用/丢失时也能拿到现场）
            var recent = AppLog.RecentLines();
            sb.AppendLine($"---- 最近日志（内存 {recent.Count} 行）----");
            int skip = Math.Max(0, recent.Count - 400);
            foreach (var line in recent.Skip(skip))
                sb.AppendLine(line);

            return sb.ToString();
        }

        /// <summary>递归展开异常链（含 InnerException），入到报告。</summary>
        private static void AppendException(StringBuilder sb, Exception ex)
        {
            var current = ex;
            int depth = 0;
            while (current != null && depth < 10)
            {
                sb.AppendLine(depth == 0
                    ? $"{current.GetType().FullName}: {current.Message}"
                    : $"  └─ 内部异常({depth}) {current.GetType().FullName}: {current.Message}");
                sb.AppendLine(current.StackTrace ?? "(无堆栈)");
                current = current.InnerException;
                depth++;
            }
        }

        /// <summary>把报告写入文件并返回路径（手动触发，文件名带"报告"）。</summary>
        public static string SaveReport()
        {
            string path = WriteToFile($"GameHub-报告-{DateTime.Now:yyyyMMdd-HHmmss}.txt", BuildReport());
            AppLog.Write($"已导出诊断报告：{path}");
            return path;
        }

        /// <summary>崩溃时自动落盘一份带时间戳的报告（文件名带"崩溃"，与手动报告区分）。</summary>
        public static string SaveCrashReport(Exception? exception)
        {
            string path = WriteToFile($"GameHub-崩溃-{DateTime.Now:yyyyMMdd-HHmmss}.txt", BuildReport(exception));
            AppLog.Write($"已自动保存崩溃报告：{path}");
            return path;
        }

        /// <summary>
        /// 写入报告文件。优先写 %APPDATA%\GameHub\reports，失败则退回 %TEMP%，
        /// 无论如何都要让报告落盘（崩溃现场的数据最重要）。
        /// </summary>
        private static string WriteToFile(string fileName, string content)
        {
            string path1 = Path.Combine(ReportDirectory, fileName);
            try
            {
                Directory.CreateDirectory(ReportDirectory);
                File.WriteAllText(path1, content, new UTF8Encoding(true));
                return path1;
            }
            catch
            {
                // 掉到临时目录：报告不能丢
            }

            string path2 = Path.Combine(Path.GetTempPath(), fileName);
            try
            {
                File.WriteAllText(path2, content, new UTF8Encoding(true));
                return path2;
            }
            catch
            {
                // 连临时目录都写不了，只能退回原始日志文件本身
                return AppLog.FilePath;
            }
        }

        /// <summary>在资源管理器里选中指定文件（打开所在目录）。失败静默忽略。</summary>
        public static void OpenReportDirectory(string? reportPath)
        {
            if (string.IsNullOrEmpty(reportPath))
                return;

            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{reportPath}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                // 无法打开资源管理器也不影响主流程
            }
        }
    }
}