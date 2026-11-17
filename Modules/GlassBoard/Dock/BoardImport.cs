using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard.Dock
{
    /// <summary>
    /// 本地化名称：向 Windows shell 要“快捷方式显示名”。
    ///
    /// 这一步解决的是“控制面板为什么不显示中文”：
    /// 开始菜单里那些系统快捷方式的文件名本身就是英文（Memory Diagnostics Tool.lnk），
    /// 但 shell 知道它对应的本地化名称。SHGetFileInfo + SHGFI_DISPLAYNAME
    /// 会把快捷方式解析到目标资源，返回系统语言下的名字（中文系统就是中文）。
    /// </summary>
    public static class LocalizedNames
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct SHFILEINFO
        {
            public IntPtr hIcon;
            public int iIcon;
            public uint dwAttributes;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
            public string szDisplayName;

            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 80)]
            public string szTypeName;
        }

        private const uint SHGFI_DISPLAYNAME = 0x00000020;

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(
            string pszPath,
            uint dwFileAttributes,
            ref SHFILEINFO psfi,
            uint cbFileInfo,
            uint uFlags);

        /// <summary>拿本地化显示名；拿不到返回空字符串（调用方自行回退到文件名）。</summary>
        public static string GetDisplayName(string? path)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
                    return string.Empty;

                var info = new SHFILEINFO();

                IntPtr result = SHGetFileInfo(
                    path,
                    0,
                    ref info,
                    (uint)Marshal.SizeOf(typeof(SHFILEINFO)),
                    SHGFI_DISPLAYNAME);

                if (result == IntPtr.Zero || string.IsNullOrWhiteSpace(info.szDisplayName))
                    return string.Empty;

                return info.szDisplayName.Trim();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 导入：读取本地化名称失败 {path}：{ex.GetType().Name}: {ex.Message}");
                return string.Empty;
            }
        }
        /// <summary>
        /// 常见 Windows 系统工具的中英对照。
        /// 有些机器上 shell 只回英文文件名（本地化资源没被解析出来），
        /// 这一层兜底保证这些工具显示中文——原版也带同类对照表。
        /// </summary>
        private static readonly Dictionary<string, string> ChineseNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["Administrative Tools"] = "管理工具",
            ["Character Map"] = "字符映射表",
            ["Command Prompt"] = "命令提示符",
            ["Component Services"] = "组件服务",
            ["Computer Management"] = "计算机管理",
            ["Control Panel"] = "控制面板",
            ["Device Manager"] = "设备管理器",
            ["Disk Cleanup"] = "磁盘清理",
            ["Event Viewer"] = "事件查看器",
            ["File Explorer"] = "文件资源管理器",
            ["Memory Diagnostics Tool"] = "内存诊断工具",
            ["Performance Monitor"] = "性能监视器",
            ["Recovery Drive"] = "恢复驱动器",
            ["Registry Editor"] = "注册表编辑器",
            ["Remote Desktop Connection"] = "远程桌面连接",
            ["Resource Monitor"] = "资源监视器",
            ["services"] = "服务",
            ["Services"] = "服务",
            ["Steps Recorder"] = "步骤记录器",
            ["System Configuration"] = "系统配置",
            ["System Information"] = "系统信息",
            ["Task Manager"] = "任务管理器",
            ["Task Scheduler"] = "任务计划程序",
            ["Windows Firewall"] = "Windows 防火墙",
            ["Windows Memory Diagnostic"] = "Windows 内存诊断",
            ["Windows PowerShell"] = "Windows PowerShell",
            ["Windows Security"] = "Windows 安全中心",
            ["Windows Update"] = "Windows 更新",

            // 辅助功能 / 附件：这些在中文系统里本就有中文名，但对不上就地翻译
            ["Narrator"] = "讲述人",
            ["On-Screen Keyboard"] = "屏幕键盘",
            ["Magnifier"] = "放大镜",
            ["Ease of Access"] = "轻松使用",
            ["Speech Recognition"] = "语音识别",
            ["Windows Speech Recognition"] = "Windows 语音识别",
            ["Snipping Tool"] = "截图工具",
            ["Snip & Sketch"] = "截图工具",
            ["Sticky Notes"] = "便笺",
            ["Paint"] = "画图",
            ["Notepad"] = "记事本",
            ["Calculator"] = "计算器",
            ["WordPad"] = "写字板",
            ["Sound Recorder"] = "录音机",
            ["Voice Recorder"] = "录音机",
            ["Windows Terminal"] = "Windows 终端",
            ["Windows Media Player"] = "Windows Media Player",
            ["Windows Fax and Scan"] = "Windows 传真和扫描",
            ["Optional Features"] = "启用或关闭 Windows 功能",
            ["Disk Management"] = "磁盘管理",
            ["Optimize Drives"] = "优化驱动器",
            ["Quick Assist"] = "快速助手",
            ["Get Help"] = "获取帮助",
            ["Tips"] = "使用技巧",
            ["DirectX Diagnostic Tool"] = "DirectX 诊断工具",
            ["Color Management"] = "颜色管理",
            ["Windows Accessories"] = "Windows 附件",
            ["Windows Administrative Tools"] = "Windows 管理工具",
            ["Windows Ease of Access"] = "Windows 轻松使用",
            ["Windows System"] = "Windows 系统",
            ["Windows Tools"] = "Windows 工具",
            ["Windows PowerShell ISE (x86)"] = "Windows PowerShell ISE (x86)",
            ["Windows PowerShell (x86)"] = "Windows PowerShell (x86)",

            // —— 追加：按本机实测名单补掉无障碍 / 附件 / 管理类里长期漏译的项 ——
            ["iSCSI Initiator"] = "iSCSI 发起程序",
            ["LiveCaptions"] = "实时字幕",
            ["VoiceAccess"] = "语音访问",
            ["Print Management"] = "打印管理",
            ["ODBC Data Sources (32-bit)"] = "ODBC 数据源(32 位)",
            ["ODBC Data Sources (64-bit)"] = "ODBC 数据源(64 位)",
            ["RecoveryDrive"] = "恢复驱动器",
            ["Security Configuration Management"] = "安全配置管理",
            ["Windows Defender Firewall with Advanced Security"] = "高级安全 Windows Defender 防火墙"
        };

        /// <summary>
        /// 按「启动目标的可执行文件名」对照中文。
        /// 显示名随系统语言变，exe 名不会——所以这一层比名字匹配可靠得多，
        /// 讲述人(narrator) / 屏幕键盘(osk) / 放大镜(magnify) 这类都能稳定命中。
        /// </summary>
        private static readonly Dictionary<string, string> ExecutableNames = new(StringComparer.OrdinalIgnoreCase)
        {
            ["narrator"] = "讲述人",
            ["osk"] = "屏幕键盘",
            ["magnify"] = "放大镜",
            ["utilman"] = "轻松使用",
            ["sethc"] = "轻松使用（粘滞键）",
            ["speechuxwizard"] = "Windows 语音识别",
            ["speechuxtrainer"] = "语音训练",
            ["snippingtool"] = "截图工具",
            ["snipandsketch"] = "截图工具",
            ["screenclippinghost"] = "截图工具",
            ["mspaint"] = "画图",
            ["notepad"] = "记事本",
            ["calc"] = "计算器",
            ["write"] = "写字板",
            ["charmap"] = "字符映射表",
            ["cleanmgr"] = "磁盘清理",
            ["dfrgui"] = "优化驱动器",
            ["diskmgmt"] = "磁盘管理",
            ["wmplayer"] = "Windows Media Player",
            ["soundrecorder"] = "录音机",
            ["wt"] = "Windows 终端",
            ["powershell"] = "Windows PowerShell",
            ["pwsh"] = "PowerShell",
            ["cmd"] = "命令提示符",
            ["regedit"] = "注册表编辑器",
            ["taskmgr"] = "任务管理器",
            ["control"] = "控制面板",
            ["explorer"] = "文件资源管理器",
            ["devmgmt"] = "设备管理器",
            ["eventvwr"] = "事件查看器",
            ["compmgmt"] = "计算机管理",
            ["services"] = "服务",
            ["perfmon"] = "性能监视器",
            ["resmon"] = "资源监视器",
            ["mstsc"] = "远程桌面连接",
            ["optionalfeatures"] = "启用或关闭 Windows 功能",
            ["msinfo32"] = "系统信息",
            ["msconfig"] = "系统配置",
            ["recdisc"] = "恢复驱动器",
            ["psr"] = "步骤记录器",
            ["quickassist"] = "快速助手",
            ["msra"] = "快速助手",
            ["fontsettings"] = "字体设置",
            ["colorcpl"] = "颜色管理",
            ["wfs"] = "Windows 传真和扫描",
            ["dxdiag"] = "DirectX 诊断工具",
            ["fsquirt"] = "蓝牙文件传送",
            ["stikynot"] = "便笺"
        };

        /// <summary>命中对照表就换成中文，否则原样返回。</summary>
        public static string ToChinese(string? name) => ToChinese(name, null);

        /// <summary>
        /// 先按显示名对照，再按启动目标的 exe 名对照；都命中不了原样返回。
        /// exe 名那一层只对“Windows 目录下的目标 / 纯英文名”生效，
        /// 免得某个第三方软件恰好叫 services.exe 就被改写成“服务”。
        /// </summary>
        public static string ToChinese(string? name, string? launchTarget)
        {
            if (!string.IsNullOrWhiteSpace(name))
            {
                string key = name.Trim();

                if (ChineseNames.TryGetValue(key, out var byName) && !string.IsNullOrWhiteSpace(byName))
                    return byName;

                // 已经带中文就别再动（免得二次导入把中文又翻回去）
                bool hasChinese = key.Any(c => c >= 0x4E00 && c <= 0x9FFF);

                string stem = StemOf(launchTarget);
                bool englishName = !hasChinese;

                if (!string.IsNullOrWhiteSpace(stem)
                    && (IsWindowsPath(launchTarget) || englishName)
                    && ExecutableNames.TryGetValue(stem, out var byExe)
                    && !string.IsNullOrWhiteSpace(byExe))
                {
                    return byExe;
                }

                return key;
            }

            string onlyStem = StemOf(launchTarget);
            if (!string.IsNullOrWhiteSpace(onlyStem)
                && IsWindowsPath(launchTarget)
                && ExecutableNames.TryGetValue(onlyStem, out var fallback)
                && !string.IsNullOrWhiteSpace(fallback))
            {
                return fallback;
            }

            return name ?? string.Empty;
        }

        /// <summary>取可执行文件名（不带扩展名），用于对照。</summary>
        private static string StemOf(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return string.Empty;

            try
            {
                return Path.GetFileNameWithoutExtension(target.Trim()) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>目标是否在 Windows 目录下（自带组件的强特征）。</summary>
        public static bool IsWindowsPath(string? target)
        {
            if (string.IsNullOrWhiteSpace(target))
                return false;

            try
            {
                string root = Environment.GetFolderPath(Environment.SpecialFolder.Windows);

                if (string.IsNullOrWhiteSpace(root))
                    return false;

                return target.Trim().StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

    }

    /// <summary>
    /// 可打开的文档/媒体类型（对应原版 DocumentRules）。
    /// 这些属于“文件”，不该作为开始菜单入口导入。
    /// </summary>
    public static class BoardDocumentRules
    {
        private static readonly HashSet<string> Extensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".doc", ".docx", ".docm", ".dot", ".dotx", ".wps", ".wpt", ".xls", ".xlsx", ".xlsm",
            ".xlsb", ".xlt", ".xltx", ".et", ".ett", ".csv", ".ppt", ".pptx", ".pptm", ".dps",
            ".dpt", ".pdf", ".txt", ".md", ".rtf", ".odt", ".ods", ".odp", ".jpg", ".jpeg",
            ".png", ".webp", ".svg", ".mp4", ".mp3", ".zip", ".rar", ".7z"
        };

        public static bool Supported(string? path)
        {
            try
            {
                return Extensions.Contains(Path.GetExtension(path ?? string.Empty));
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// 开始菜单入口的判定规则（对应原版 StartupEntryRules + EntryRules.Importable）。
    /// 目的：把“卸载程序、更新器、崩溃报告、README、许可、帮助”这类噪音挡在外面。
    /// </summary>
    public static class BoardStartupRules
    {
        private static readonly string[] AuxiliaryNames =
        {
            "update", "updater", "autoupdate", "setup", "install", "installer", "crashreporter",
            "crashreportclient", "crashpadhandler", "cefsubprocess", "qtwebengineprocess",
            "unitycrashhandler64", "unitycrashhandler32", "werfault", "vc_redist", "vcredist",
            "dxsetup", "dxwebsetup", "dxdiag", "readme", "license", "help", "说明", "帮助",
            "repair", "修复", "reportbug", "feedback"
        };

        public static string Leaf(string? path)
        {
            path = (path ?? string.Empty).Replace('\\', '/');
            int slash = path.LastIndexOf('/');
            return slash < 0 ? path : path[(slash + 1)..];
        }

        public static string Stem(string? path)
        {
            string leaf = Leaf(path);
            int dot = leaf.LastIndexOf('.');
            return dot <= 0 ? leaf : leaf[..dot];
        }

        /// <summary>卸载/更新/说明类噪音。</summary>
        public static bool Auxiliary(string? path)
        {
            string name = BoardAutoGroup.Canonical(Stem(path));

            if (name.Length == 0)
                return false;

            if (name.StartsWith("unins", StringComparison.Ordinal)
                || name.StartsWith("uninstall", StringComparison.Ordinal)
                || name.Contains("卸载", StringComparison.Ordinal)
                || name.EndsWith("updater", StringComparison.Ordinal)
                || name.Contains("crashreport", StringComparison.Ordinal)
                || name.Contains("crashpad", StringComparison.Ordinal))
            {
                return true;
            }

            foreach (var keyword in AuxiliaryNames)
            {
                string canonical = BoardAutoGroup.Canonical(keyword);
                if (canonical.Length == 0)
                    continue;

                if (name == canonical
                    || name.StartsWith(canonical + "64", StringComparison.Ordinal)
                    || name.StartsWith(canonical + "32", StringComparison.Ordinal))
                {
                    return true;
                }
            }

            return false;
        }

        public static bool GameUri(string? value)
        {
            value = (value ?? string.Empty).Trim().ToLowerInvariant();

            if (value.StartsWith("steam://rungameid/", StringComparison.Ordinal)
                || value.StartsWith("steam://run/", StringComparison.Ordinal)
                || value.StartsWith("com.epicgames.launcher://apps/", StringComparison.Ordinal)
                || value.StartsWith("uplay://launch/", StringComparison.Ordinal)
                || value.StartsWith("origin://launchgame/", StringComparison.Ordinal))
            {
                return true;
            }

            return value.StartsWith("battlenet://", StringComparison.Ordinal);
        }

        public static string ReadUrl(string path)
        {
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists || info.Length > 32768)
                    return string.Empty;

                foreach (var line in File.ReadLines(path))
                {
                    string text = line.Trim();
                    if (text.StartsWith("URL=", StringComparison.OrdinalIgnoreCase))
                        return text[4..].Trim();
                }
            }
            catch
            {
                // 读不到就当没有
            }

            return string.Empty;
        }

        /// <summary>原版 EntryRules.Importable：白名单扩展名，或属于可打开文档。</summary>
        public static bool Importable(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return false;

            string ext = Path.GetExtension(path).ToLowerInvariant();

            return ext switch
            {
                ".lnk" or ".appref-ms" or ".exe" or ".url" or ".cmd" or ".bat" => true,
                _ => BoardDocumentRules.Supported(path)
            };
        }

        /// <summary>原版 StartupEntryRules.Candidate：能不能作为“开始菜单入口”收进面板。</summary>
        public static bool Candidate(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)
                || !Importable(path)
                || BoardDocumentRules.Supported(path)
                || Auxiliary(path))
            {
                return false;
            }

            // .url 是网页快捷方式：只收游戏平台的（否则会混进一堆网页）
            if (Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase))
                return GameUri(ReadUrl(path));

            return true;
        }
    }

    /// <summary>
    /// 首次运行的自动导入（对应原版 Controller 里的首次导入流程）：
    /// 扫描「当前用户开始菜单 + 所有用户开始菜单」（可选带上桌面），
    /// 递归收集 → 取本地化名称 → 去重 → 过滤噪音 → 按关键词自动分组。
    /// </summary>
    public static class BoardImporter
    {
        private const int FileLimit = 2500;
        private const int DirectoryLimit = 10000;

        public static int Import(DockModel model, bool includeDesktop = true)
        {
            var roots = new List<string>();

            try
            {
                AddIfAny(roots, Environment.GetFolderPath(Environment.SpecialFolder.Programs));
                AddIfAny(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonPrograms));

                if (includeDesktop)
                {
                    AddIfAny(roots, Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory));
                    AddIfAny(roots, Environment.GetFolderPath(Environment.SpecialFolder.CommonDesktopDirectory));
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 导入：读取系统目录失败：{ex.GetType().Name}: {ex.Message}");
            }

            var found = Scan(roots);
            int added = 0;

            // 去重键：同一个软件会同时出现在「当前用户」和「所有用户」开始菜单里，
            // 两个 .lnk 路径不同但指向同一个 exe，所以优先用解析后的启动目标做键。
            var keys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            string KeyOf(DockEntry e)
            {
                string? target = e.LaunchTarget;

                if (!string.IsNullOrWhiteSpace(target)
                    && Path.GetExtension(target).Equals(".exe", StringComparison.OrdinalIgnoreCase))
                {
                    return target!;
                }

                return e.Path;
            }

            foreach (var app in model.Apps)
                keys.Add(KeyOf(app));

            // 对照表升级后，已有入口也要重新对照一次——不必清空配置就能生效
            int relabeled = Relabel(model);
            if (relabeled > 0)
                AppLog.Write($"GlassBoard 导入：重新对照 {relabeled} 个入口的名称/分组");

            foreach (var path in found)
            {
                try
                {
                    if (!BoardStartupRules.Candidate(path))
                        continue;

                    string normalized = EntryPathRules.Normalize(path);
                    if (string.IsNullOrWhiteSpace(normalized))
                        continue;

                    var entry = new DockEntry
                    {
                        Path = normalized,
                        Name = EntryPathRules.Name(normalized)
                    };

                    try
                    {
                        LaunchInfo.Enrich(entry);
                    }
                    catch
                    {
                        // 快捷方式解析失败不影响收录
                    }

                    // 优先用 shell 给的本地化名称（中文系统上就是中文），拿不到才用文件名
                    string localized = LocalizedNames.GetDisplayName(normalized);
                    if (!string.IsNullOrWhiteSpace(localized))
                        entry.Name = localized;

                    // shell 只回英文名时用对照表兜成中文
                    entry.Name = LocalizedNames.ToChinese(entry.Name, entry.LaunchTarget);

                    if (string.IsNullOrWhiteSpace(entry.Name))
                        entry.Name = BoardStartupRules.Stem(normalized);

                    if (!keys.Add(KeyOf(entry)))
                        continue;

                    // 按关键词表自动归类（创作/办公/游戏/开发/音乐…）
                    entry.Group = BoardAutoGroup.Guess(entry);

                    // 关键词没命中，但名字本身是系统工具 → 强制归到“系统工具”
                    if (string.IsNullOrWhiteSpace(entry.Group)
                        && BoardAutoGroup.IsSystemTool(BoardAutoGroup.Canonical(entry.Name)))
                    {
                        entry.Group = "系统工具";
                    }

                    model.Apps.Add(entry);
                    added++;
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard 导入：跳过 {path}：{ex.GetType().Name}: {ex.Message}");
                }
            }

            if (added > 0)
                model.Normalize();

            model.Imported = true;

            AppLog.Write($"GlassBoard：开始菜单导入完成，新增 {added} 个入口（共扫描 {found.Count} 个文件）");
            return added;
        }

        /// <summary>
        /// 对已有入口重新应用中文对照与「系统工具」归类。
        /// 升级对照表后不用删 dock.json 就能让旧入口跟着变。
        /// </summary>
        public static int Relabel(DockModel model)
        {
            int changed = 0;

            foreach (var entry in model.Apps)
            {
                // 人工“移出系统工具”过的入口不再自动改回去，否则用户的操作每次启动都被抹掉
                if (entry.ManualNonSystem)
                    continue;

                try
                {
                    string before = entry.Name;
                    string after = LocalizedNames.ToChinese(entry.Name, entry.LaunchTarget);

                    if (!string.IsNullOrWhiteSpace(after) && !string.Equals(before, after, StringComparison.Ordinal))
                    {
                        entry.Name = after;
                        changed++;
                    }

                    if (string.IsNullOrWhiteSpace(entry.Group)
                        && (BoardAutoGroup.IsSystemTool(BoardAutoGroup.Canonical(entry.Name))
                            || BoardAutoGroup.IsWindowsBuiltIn(entry)))
                    {
                        entry.Group = "系统工具";
                        changed++;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard 导入：重新对照失败 {entry.Path}：{ex.GetType().Name}: {ex.Message}");
                }
            }

            return changed;
        }

        private static void AddIfAny(List<string> roots, string? path)
        {
            if (!string.IsNullOrWhiteSpace(path) && Directory.Exists(path))
                roots.Add(path);
        }

        private static List<string> Scan(List<string> roots)
        {
            var result = new List<string>();
            var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var stack = new Stack<string>(roots);

            while (stack.Count > 0)
            {
                if (visited.Count > DirectoryLimit || result.Count >= FileLimit)
                    break;

                string dir = stack.Pop();
                if (!visited.Add(dir))
                    continue;

                try
                {
                    foreach (var file in Directory.EnumerateFiles(dir))
                    {
                        if (result.Count >= FileLimit)
                            break;

                        if (BoardStartupRules.Importable(file))
                            result.Add(file);
                    }

                    foreach (var sub in Directory.EnumerateDirectories(dir))
                    {
                        try
                        {
                            // 跳过 junction / 符号链接，避免在开始菜单里绕圈
                            if ((File.GetAttributes(sub) & FileAttributes.ReparsePoint) == 0)
                                stack.Push(sub);
                        }
                        catch
                        {
                            // 没权限的子目录直接跳过
                        }
                    }
                }
                catch
                {
                    // 整个目录读不了就跳过
                }
            }

            return result
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(p => Path.GetFileNameWithoutExtension(p), StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
    }
}
