using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace GameHub.Modules.GlassBoard.Dock
{
    /// <summary>
    /// 自动分组（对应原版 AutoGroupRules）。
    ///
    /// 用途：新加入的入口没有人工指定分组时，按名称/文件名/产品信息猜一个分组，
    /// 让「创作 / 办公 / 游戏」这些分组栏一开始就不是空的。
    ///
    /// 规则来源两处，都与原版一致：
    ///   1) 内置关键词表（原版代码里写死的那 8 组）；
    ///   2) 可选外部词库 data\AppGroups.tsv（原版随包发布；放到本模块配置目录
    ///      data\AppGroups.tsv 即可启用，每行「分组名 \t word|exact \t 关键词|关键词」）。
    /// </summary>
    public static class BoardAutoGroup
    {
        private static readonly Dictionary<string, string> Catalog = new(StringComparer.OrdinalIgnoreCase);
        private static bool _loaded;

        /// <summary>内置关键词表（照抄原版代码里的 8 组，顺序也一致）。</summary>
        private static readonly string[][] BuiltIn =
        {
            new[] { "系统工具", "uninstall", "卸载", "winrar", "7-zip", "7zip", "todesk", "clash", "powershell", "任务管理器", "注册表", "控制面板", "memory diagnostics", "memory diagnostic", "device manager", "设备管理器", "event viewer", "事件查看器", "system info", "system information", "narrator", "讲述人", "on-screen keyboard", "屏幕键盘", "magnifier", "放大镜", "ease of access", "轻松使用", "speech recognition", "语音识别", "disk cleanup", "磁盘清理", "optimize drives", "优化驱动器", "optional features", "windows tools", "windows system", "windows accessories", "管理员工具" },
            new[] { "音乐", "qqmusic", "qq音乐", "网易云音乐", "cloudmusic", "酷狗", "酷我", "spotify", "foobar", "pro tools", "protools", "ableton", "audition", "fl studio", "reaper", "cubase", "asio4all" },
            new[] { "开发", "visual studio", "vscode", "code", "pycharm", "intellij", "git", "python", "unity", "unreal", "codex", "trae" },
            new[] { "创作", "photoshop", "after effects", "premiere", "illustrator", "lightroom", "indesign", "blender", "maya", "3ds max", "3dmax", "cinema 4d", "c4d", "marmoset", "substance", "davinci", "resolve", "capcut", "剪映", "canva", "houdini", "绘画" },
            new[] { "游戏", "steam", "epic games", "wegame", "ubisoft", "battle.net", "rockstar", "5e", "游戏", "对战平台", "战网" },
            new[] { "办公", "word", "excel", "powerpoint", "onenote", "outlook", "wps", "acrobat", "foxit", "notion", "obsidian", "飞书", "钉钉" },
            new[] { "浏览器", "chrome", "msedge", "edge", "firefox", "opera", "brave", "浏览器" },
            new[] { "社交", "qq", "微信", "wechat", "kook", "discord", "telegram", "tt语音" }
        };

        /// <summary>规格化：去掉非字母数字、兼容全角半角、转小写（原版 Canonical）。</summary>
        public static string Canonical(string? name)
            => new((name ?? string.Empty).Normalize(NormalizationForm.FormKC)
                .ToLowerInvariant()
                .Where(char.IsLetterOrDigit)
                .ToArray());

        /// <summary>把名字切成“词”，非字母数字当分隔（原版 Words）。</summary>
        private static string Words(string? name)
        {
            string text = new((name ?? string.Empty).Normalize(NormalizationForm.FormKC)
                .ToLowerInvariant()
                .Select(c => char.IsLetterOrDigit(c) ? c : ' ')
                .ToArray());

            return string.Join(" ", text.Split(' ', StringSplitOptions.RemoveEmptyEntries));
        }

        /// <summary>
        /// 判断是否是系统工具（用于强制归类）。
        /// </summary>
        public static bool IsSystemTool(string canonicalName)
        {
            if (string.IsNullOrEmpty(canonicalName))
                return false;

            // 常见的系统工具关键词
            var systemTools = new[]
            {
                "memory diagnostic", "device manager", "event viewer", "system information",
                "task manager", "registry editor", "remote desktop", "command prompt",
                "windows security", "control panel", "administrative tool",
                "component service", "computer management", "disk defragment",
                "system restore", "system file checker", "task scheduler",
                "event log", "performance monitor", "resource monitor",
                "disk management", "services", "network connection",
                "network and sharing", "default program", "user account",
                "power option", "display setting", "sound setting",
                "bluetooth", "printer", "storage", "about"
            };

            foreach (var tool in systemTools)
            {
                if (canonicalName.Contains(tool))
                    return true;
            }

            return false;
        }

        /// <summary>开始菜单里的“系统/辅助/附件”目录（中英文都算，作为自带的旁证）。</summary>
        private static readonly string[] BuiltInFolders =
        {
            "Administrative Tools", "管理工具",
            "Windows System", "Windows 系统",
            "Windows Tools", "Windows 工具",
            "Accessories", "附件",
            "Ease of Access", "轻松使用",
            "Accessibility", "辅助功能",
            "Speech", "语音"
        };

        /// <summary>
        /// 是不是 Windows 自带（讲述人 / 屏幕键盘 / 放大镜 / 记事本这类）。
        /// 判据取“不会随语言变”的两条：启动目标在 Windows 目录下，
        /// 或者快捷方式位于开始菜单的系统/辅助/附件目录里。
        /// </summary>
        public static bool IsWindowsBuiltIn(DockEntry? entry)
        {
            if (entry == null)
                return false;

            try
            {
                if (LocalizedNames.IsWindowsPath(entry.LaunchTarget))
                    return true;

                string path = (entry.Path ?? string.Empty).Replace('/', '\\');

                foreach (string marker in BuiltInFolders)
                {
                    if (path.Contains(marker, StringComparison.OrdinalIgnoreCase))
                        return true;
                }
            }
            catch
            {
                // 判定失败就当普通软件，宁可少归类不可乱归类
            }

            return false;
        }

        /// <summary>
        /// 词匹配（原版 Match）：英文词要求左右是边界，避免 code 命中 encode；
        /// 中文等非英文字母不做边界要求。
        /// </summary>
        private static bool Match(string text, string term)
        {
            if (string.IsNullOrEmpty(text) || string.IsNullOrEmpty(term))
                return false;

            int start = 0;

            while (start < text.Length)
            {
                int index = text.IndexOf(term, start, StringComparison.Ordinal);
                if (index < 0)
                    return false;

                int end = index + term.Length;
                bool leftOk = !IsAsciiLetter(term[0]) || index == 0 || !IsAsciiLetter(text[index - 1]);
                bool rightOk = !IsAsciiLetter(term[^1]) || end >= text.Length || !IsAsciiLetter(text[end]);

                if (leftOk && rightOk)
                    return true;

                start = index + 1;
            }

            return false;
        }

        private static bool IsAsciiLetter(char c) => c is >= 'a' and <= 'z';

        /// <summary>读取可选外部词库（原版 data\AppGroups.tsv）。</summary>
        private static void LoadCatalog()
        {
            _loaded = true;
            Catalog.Clear();

            try
            {
                string path = Path.Combine(GlassBoardModule.DataFolder, "data", "AppGroups.tsv");
                if (!File.Exists(path))
                    return;

                foreach (string line in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(line) || line.TrimStart().StartsWith('#'))
                        continue;

                    string[] parts = line.Split('\t');
                    if (parts.Length != 3)
                        continue;

                    string group = parts[0].Trim();
                    string mode = parts[1].Trim();

                    if (group.Length == 0 || group is BoardRules.All or BoardRules.None || (mode != "word" && mode != "exact"))
                        continue;

                    foreach (string name in parts[2].Split('|'))
                    {
                        string key = Canonical(name);
                        if (key.Length > 0 && !Catalog.ContainsKey(key))
                            Catalog[key] = group;
                    }
                }
            }
            catch (Exception ex)
            {
                GameHub.Utils.AppLog.Write($"GlassBoard 大面板：读取分组词库失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>猜一个分组；猜不到返回 null（原版行为）。</summary>
        public static string? Guess(DockEntry entry)
        {
            if (entry == null || entry.IsFolder)
                return null;

            if (!_loaded)
                LoadCatalog();

            string name = entry.Name ?? string.Empty;
            string fileStem = StemOf(entry.Path);
            string targetStem = StemOf(entry.LaunchTarget);

            // 1) 外部词库：完全匹配优先
            if (Catalog.Count > 0)
            {
                foreach (string candidate in new[] { Canonical(name), Canonical(fileStem), Canonical(targetStem) })
                {
                    if (candidate.Length > 0 && Catalog.TryGetValue(candidate, out string? group))
                        return group;
                }
            }

            // 2) 内置关键词：在名称/文件名/产品信息里找词
            string haystack = string.Join(" ", new[]
            {
                name.ToLowerInvariant(),
                fileStem.ToLowerInvariant(),
                targetStem.ToLowerInvariant(),
                (entry.ProductInfo ?? string.Empty).ToLowerInvariant()
            });

            string canonicalHaystack = Canonical(haystack);
            string wordsHaystack = Words(haystack);

            foreach (string[] row in BuiltIn)
            {
                for (int i = 1; i < row.Length; i++)
                {
                    string term = row[i];
                    if (Match(canonicalHaystack, Canonical(term)) || Match(wordsHaystack, Words(term)))
                        return row[0];
                }
            }

            // 3) Steam 库里的多半是游戏（原版行为）
            string launch = (entry.LaunchTarget ?? entry.Path ?? string.Empty).Replace('\\', '/').ToLowerInvariant();
            if (launch.Contains("/steamapps/common/"))
                return "游戏";

            return null;
        }

        private static string StemOf(string? path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return Path.GetFileNameWithoutExtension(path) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }
    }
}
