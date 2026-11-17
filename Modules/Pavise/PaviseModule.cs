using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using GameHub.Interfaces;
using GameHub.Utils;

namespace GameHub.Modules.Pavise
{
    /// <summary>
    /// Pavise 模块（第 5 卡片）：受 GameHub 托管的后台压制 / 调度优化工具。
    ///
    /// 托管方式（2026-11-16 用户要求：GameHub 是老大，Pavise 只管电脑设置）：
    ///   * **随 GameHub 启停**：GameHub 启动时把 Pavise 拉起来（隐藏运行：无窗口、无托盘图标），
    ///     GameHub 退出时收掉它；Pavise 自己不再有托盘图标，桌面上只有 GameHub 一个入口。
    ///   * **开机自启只在 GameHub 里设置**：全机只保留 GameHub 的注册表启动项
    ///     （HKCU\...\Run\GameHub），开机拉起 GameHub，再由 GameHub 拉起 Pavise。
    ///     Pavise 面板里的「开机自启」开关读写的就是这一项（见 Pavise 源码 HubAutostart.cs），
    ///     不再使用它原来的计划任务，因此不会出现两个自启项。
    ///   * **面板由卡片唤出**：点卡片时 GameHub 带 --show-panel 再拉一次 Pavise，
    ///     由它自己的单实例机制把已运行实例的面板唤到前台（Global\Pavise_ShowPanel）；
    ///     面板右上角关闭 = 隐藏并保留实例，再点卡片立即唤回。
    ///   * **退出走优雅通道**：GameHub 退出时发 Global\Pavise_Exit 事件让 Pavise 自行收尾
    ///     （它会还原改过的系统状态：电源计划 / 服务暂停 / 线程亲和性等），
    ///     因此这里**不强制结束进程**——强杀会把改动留在系统上。
    ///
    /// 账号安全约束：Pavise 只做进程调度与后台压制，不修改任何游戏文件；
    /// GameHub 侧只负责启动 / 唤出 / 退出，不注入、不 hook、不改游戏文件。
    /// </summary>
    public sealed class PaviseModule : IModule
    {
        private const string ProcessName = "Pavise";

        private const string ExeName = "Pavise.exe";

        /// <summary>程序目录下的相对路径（编译输出与发布包都放在这里）。</summary>
        private const string RelativeFolder = @"Modules\Pavise";

        /// <summary>Pavise 暴露的「唤出面板」命名事件。</summary>
        private const string ShowPanelEventName = @"Global\Pavise_ShowPanel";

        /// <summary>Pavise 暴露的「请求优雅退出」命名事件。</summary>
        private const string ExitEventName = @"Global\Pavise_Exit";

        /// <summary>等待 Pavise 优雅退出的上限（它要还原系统状态，给足时间）。</summary>
        private const int ExitWaitMs = 20000;

        public string Name => "Pavise";

        public string Description => "后台压制与调度优化 · 由 GameHub 托管：压制后台占用、稳定关键线程，让游戏帧率更稳";

        /// <summary>卡片图标沿用原来 EVO 的图标（文件名为 pavise.png，尺寸留白与其它模块一致）。</summary>
        public string IconPath => "pack://application:,,,/Assets/Icons/pavise.png";

        public bool IsAutoStartEnabled => AutoStartManager.IsEnabled();

        public Task<string> GetStatusTextAsync()
        {
            return Task.Run(() =>
            {
                if (ResolveExecutablePath() == null)
                    return "未找到 Pavise.exe（重新编译或发布后即可用）";

                return IsRunning()
                    ? "运行中 · 由 GameHub 托管（无托盘图标）"
                    : "已停止 · GameHub 启动时会自动拉起";
            });
        }

        /// <summary>卡片「打开」：唤出 Pavise 面板。</summary>
        public void OpenPanel() => ShowPanel();

        /// <summary>
        /// GameHub 启动时调用（幂等）：把 Pavise 拉起来隐藏运行。
        /// 不带 --show-panel，所以它只驻留后台，不会自己弹窗。
        /// </summary>
        public static void StartWithHub()
        {
            try
            {
                string? exe = ResolveExecutablePath();
                if (exe == null)
                {
                    AppLog.Write("Pavise：未找到 Pavise.exe，跳过随 GameHub 启动");
                    return;
                }

                if (IsRunning())
                {
                    AppLog.Write("Pavise：已在运行，无需重复启动（单实例）");
                    return;
                }

                Launch(exe, "--hub");
                AppLog.Write("Pavise：已随 GameHub 启动（隐藏运行，无托盘图标）");
            }
            catch (Exception ex)
            {
                AppLog.Write($"Pavise：随 GameHub 启动失败 {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>唤出面板：未运行就先拉起并直接显示；已运行就发事件唤到前台。</summary>
        public static void ShowPanel()
        {
            try
            {
                string? exe = ResolveExecutablePath();
                if (exe == null)
                {
                    AppLog.Write("Pavise：未找到 Pavise.exe（Modules\\Pavise 下应为编译产物）");
                    MessageBox.Show(
                        "找不到 Pavise.exe。\n\n请重新编译 GameHub：编译时会把 Modules\\Pavise\\Pavise.exe 放到程序旁边。",
                        "Pavise", MessageBoxButton.OK, MessageBoxImage.Warning);
                    return;
                }

                if (!IsRunning())
                {
                    Launch(exe, "--hub --show-panel");
                    AppLog.Write("Pavise：已启动并显示面板");
                    return;
                }

                if (TrySignal(ShowPanelEventName))
                {
                    AppLog.Write("Pavise：已请求把面板唤到前台");
                    return;
                }

                // 事件打不开（旧版 Pavise / 权限异常）时退回「再启动一次」，
                // 由 Pavise 自己的单实例逻辑把面板唤出来。
                Launch(exe, "--hub --show-panel");
                AppLog.Write("Pavise：事件不可用，已通过再次启动请求唤出面板");
            }
            catch (Exception ex)
            {
                AppLog.Write($"Pavise：唤出面板失败 {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// GameHub 退出时调用：请求 Pavise 优雅退出并等待。
        /// 只发信号 + 等待，不强制结束：Pavise 收尾时要还原它对系统的改动
        /// （电源计划 / 服务暂停 / 亲和性等），强杀会把这些改动留在系统上。
        /// </summary>
        public static void StopWithHub()
        {
            try
            {
                if (!IsRunning())
                    return;

                if (!TrySignal(ExitEventName))
                {
                    AppLog.Write("Pavise：无法发出退出信号（事件不可用），Pavise 将继续在后台运行");
                    return;
                }

                var deadline = DateTime.UtcNow.AddMilliseconds(ExitWaitMs);
                while (DateTime.UtcNow < deadline && IsRunning())
                    Thread.Sleep(200);

                AppLog.Write(IsRunning()
                    ? "Pavise：等待优雅退出超时，未强制结束（避免系统设置停在改动状态）"
                    : "Pavise：已随 GameHub 退出");
            }
            catch (Exception ex)
            {
                AppLog.Write($"Pavise：退出处理异常 {ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Launch(string exe, string arguments)
        {
            var psi = new ProcessStartInfo
            {
                FileName = exe,
                Arguments = arguments,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
                UseShellExecute = true
            };

            Process.Start(psi);
        }

        /// <summary>给已运行的 Pavise 发命名事件（唤出面板 / 请求退出）。</summary>
        private static bool TrySignal(string eventName)
        {
            try
            {
                if (EventWaitHandle.TryOpenExisting(eventName, out var handle))
                {
                    using (handle)
                        handle.Set();
                    return true;
                }
            }
            catch
            {
                // 事件不存在或打不开：由调用方决定怎么退回
            }

            return false;
        }

        /// <summary>Pavise 是否已在运行（只读进程名，不做任何注入）。</summary>
        private static bool IsRunning()
        {
            try
            {
                var procs = Process.GetProcessesByName(ProcessName);
                try { return procs.Length > 0; }
                finally { foreach (var p in procs) p.Dispose(); }
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 定位 Pavise.exe：优先 exe 旁的 Modules\Pavise\；其次开发时的仓库布局
        /// （从程序目录逐级往上找 Modules\Pavise\）。
        /// </summary>
        private static string? ResolveExecutablePath()
        {
            try
            {
                string beside = Path.Combine(AppContext.BaseDirectory, RelativeFolder, ExeName);
                if (File.Exists(beside))
                    return beside;

                var dir = new DirectoryInfo(AppContext.BaseDirectory);
                for (int i = 0; i < 8 && dir?.Parent != null; i++)
                {
                    dir = dir.Parent;
                    string candidate = Path.Combine(dir.FullName, RelativeFolder, ExeName);
                    if (File.Exists(candidate))
                        return candidate;
                }
            }
            catch
            {
                // 定位失败按“找不到”处理
            }

            return null;
        }
    }
}
