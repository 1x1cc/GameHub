using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard.Dock
{
    /// <summary>
    /// 图标读取（对应原版 Native.Icon）。
    ///
    /// 【取图策略 —— 本机实测结论，别再走回头路】
    ///
    /// 1) 只用 IShellItemImageFactory(ICONONLY, 256) 是不够的。实测它对一部分目标会返回
    ///    “空心边框 + 正中小块”的坏图：外圈 2px 有约 50% 是实心暗色（看着就是一圈黑边），
    ///    真正的图标只有正中约 12px，整张图的不透明像素只有 10~11%。
    ///    中招的例子：放大镜(magnify.exe)、微信输入法·隔空传送(wetype_update.exe)、
    ///    以及 Steam 那批 .url。同一个路径**改成按 48 请求却是正常的图标** ——
    ///    也就是说坏的是“按 256 请求”这条通道，不是图标本身。
    ///
    /// 2) 所以改成多通道取图、按“图像质量”择优：
    ///      · 直读图标文件（.ico / .png / .bmp：入口自带 IconPath 或 .url 的 IconFile）
    ///      · IShellItemImageFactory（256 与 48）
    ///      · PrivateExtractIcons（从 exe/dll 里按尺寸直接抽图标；实测能拿到正牌 256×256）
    ///      · SHGetFileInfo LARGEICON
    ///      · ExtractIconEx
    ///    每个候选量四个指标：是否近似正方形、不透明像素占比、
    ///    **去掉“同色边带 + 透明留白”后的实心内容占比**、以及**最外圈 2px 的暗边占比**。
    ///    判坏图只看“外圈很暗 **且** 整张几乎不透明”这一条：Windows 那批线描图标
    ///    （语音访问 / 磁盘清理 / 恢复驱动器…）本来就大量透明，拿“不透明占比”当门槛
    ///    会把它们全判成坏图，表现就是“这些工具没有图标”（上一版就踩了这个坑）。
    ///    另外：**任何情况下都不返回 null** —— 判据全不命中也会退回最可用的一张。
    ///
    /// 3) 候选来源也不只看入口文件本身，还包括：启动目标（.lnk 指向的 exe）、自定义图标、
    ///    .url 的 IconFile，以及 Steam 游戏本体 exe（IconFile 丢失时用游戏目录里的可执行
    ///    文件把图标找回来 —— 本机“黑纱掠袭”就是这样，它的 games\*.ico 已经没了）。
    ///
    /// 4) 最后再裁掉一圈透明留白，避免“图标小小一个、四周一大圈空白/暗边”。
    ///
    /// 【不要再用 SHGetImageList(SHIL_JUMBO)】本机实测：拿 SHGetFileInfo 的 iIcon 去索引
    /// jumbo 列表会索引错位，六个完全不同的程序会取到同一张错图。
    /// </summary>
    public static class DockIcons
    {
        // ---------------- 老接口：SHGetFileInfo ----------------

        private const uint SHGFI_ICON = 0x00000100;
        private const uint SHGFI_LARGEICON = 0x00000000;
        private const uint SHGFI_USEFILEATTRIBUTES = 0x00000010;

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

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SHGetFileInfo(string pszPath, uint dwFileAttributes, out SHFILEINFO psfi, uint cbFileInfo, uint uFlags);

        [DllImport("user32.dll")]
        private static extern bool DestroyIcon(IntPtr hIcon);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr hObject);

        // ---------------- IShellItemImageFactory ----------------

        [StructLayout(LayoutKind.Sequential)]
        private struct NativeSize
        {
            public int Width;
            public int Height;
        }

        private const int SIIGBF_BIGGERSIZEOK = 0x1;
        private const int SIIGBF_ICONONLY = 0x4;

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"),
         InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig]
            int GetImage(NativeSize size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
        private static extern void SHCreateItemFromParsingName(
            string pszPath,
            IntPtr pbc,
            in Guid riid,
            [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory factory);

        private static readonly Guid FactoryIid = new("bcc18b79-ba16-442f-80c4-8a59c30c463b");

        // ---------------- PrivateExtractIcons / ExtractIconEx ----------------

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern uint PrivateExtractIcons(
            string szFileName, int nIconIndex, int cxIcon, int cyIcon,
            IntPtr[] phicon, uint[]? piconid, uint nIcons, uint flags);

        [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
        private static extern uint ExtractIconEx(
            string lpszFile, int nIconIndex, IntPtr[]? phiconLarge, IntPtr[]? phiconSmall, uint nIcons);

        // ---------------- 缓存 ----------------

        private static readonly Dictionary<string, ImageSource?> Cache = new(StringComparer.OrdinalIgnoreCase);

        public static void Clear() => Cache.Clear();

        public static ImageSource? For(DockEntry? entry)
        {
            if (entry == null)
                return null;

            string key = CacheKey(entry);
            if (key.Length == 0)
                return null;

            if (Cache.TryGetValue(key, out var cached))
                return cached;

            ImageSource? result = Extract(entry);
            Cache[key] = result;
            return result;
        }

        /// <summary>缓存键要带上“来源”，否则同一个 .url 换了图标文件不会刷新。</summary>
        private static string CacheKey(DockEntry entry)
            => string.Join("|", new[] { entry.Path, entry.IconPath, entry.LaunchTarget, entry.Name }
                .Where(s => !string.IsNullOrWhiteSpace(s)));

        /// <summary>入口里用来取图标的路径（保留旧语义：优先快捷方式/文件本身）。</summary>
        public static string? Resolve(DockEntry? entry)
        {
            if (entry == null)
                return null;

            if (!string.IsNullOrWhiteSpace(entry.Path) && File.Exists(entry.Path))
                return entry.Path;

            if (!string.IsNullOrWhiteSpace(entry.IconPath) && File.Exists(entry.IconPath))
                return entry.IconPath;

            if (!string.IsNullOrWhiteSpace(entry.LaunchTarget) && File.Exists(entry.LaunchTarget))
                return entry.LaunchTarget;

            if (!string.IsNullOrWhiteSpace(entry.Path) && Directory.Exists(entry.Path))
                return entry.Path;

            return null;
        }

        // ---------------- 主流程 ----------------

        private static ImageSource? Extract(DockEntry entry)
        {
            var sources = Sources(entry);
            if (sources.Count == 0)
                return null;

            Candidate? best = null;
            Candidate? fallback = null;
            var state = new ExtractionState();

            foreach (var (label, image) in Candidates(sources, state))
            {
                var candidate = Candidate.Measure(label, image);

                if (candidate == null)
                    continue;

                // 兜底：就算判定“不合格”也留一张尺寸/占比最好的。
                // 绝不能因为判据没命中就把图标变成“完全没有”——那比稍微难看得多。
                if (fallback == null || candidate.BetterThan(fallback))
                    fallback = candidate;

                if (!candidate.Acceptable)
                    continue;

                if (best == null || candidate.BetterThan(best))
                    best = candidate;

                // 已经足够好就不再往下试（绝大多数图标第一条通道就命中，速度不受影响）
                if (best.Excellent)
                    break;
            }

            var chosen = best ?? fallback;

            if (chosen == null)
            {
                AppLog.Write($"GlassBoard 图标：{Path.GetFileName(entry.Path)} 所有取图通道都没拿到图（{sources.Count} 个来源）");
                return null;
            }

            if (best == null)
            {
                AppLog.Write($"GlassBoard 图标：{Path.GetFileName(entry.Path)} 没有完全合格的候选，"
                    + $"退回最可用的一张（{chosen.Channel}，{chosen.Size}px）");
            }
            else if (state.ShellBad && chosen.Channel != "shell256")
            {
                AppLog.Write($"GlassBoard 图标：{Path.GetFileName(entry.Path)} shell 的 256 通道返回坏图，"
                    + $"改用 {chosen.Channel}（{chosen.Size}px，实心 {chosen.Fill:P0}）");
            }

            return CropTransparent(chosen.Image);
        }

        /// <summary>
        /// 迭代器不能带 ref/out 参数，所以用一个可变状态对象把“256 通道是否给了坏图”带出去。
        /// </summary>
        private sealed class ExtractionState
        {
            public bool ShellBad;
        }

        /// <summary>按优先级产出候选（惰性：够好就在调用方提前 break）。</summary>
        private static IEnumerable<(string Label, ImageSource? Image)> Candidates(List<string> sources, ExtractionState state)
        {
            // 一、直读图标文件：质量最好也最便宜
            foreach (string src in sources.Where(IsImageFile))
                yield return ($"直读{Path.GetExtension(src)}", DecodeImageFile(src));

            // 二、shell 的 256 通道（正常程序在这里就命中了）
            var primary = sources[0];
            var shell256 = ShellItem(primary, 256);
            var probe = Candidate.Measure("shell256", shell256);
            if (probe == null || !probe.Acceptable)
                state.ShellBad = true;

            yield return ("shell256", shell256);

            foreach (string src in sources.Skip(1))
                yield return ("shell256", ShellItem(src, 256));

            // 三、直接从 exe/dll 资源里按尺寸抽图标（256 通道坏掉时的主力）
            foreach (string src in sources)
            {
                yield return ("Private256", PrivateIcon(src, 256));
                yield return ("Private128", PrivateIcon(src, 128));
            }

            // 四、老接口：SHGetFileInfo LARGEICON（32×32）
            foreach (string src in sources)
                yield return ("SHGetFileInfo", ShellLargeIcon(src));

            // 五、shell 的 48 通道（实测同一个目标按 48 请求往往就是正常图标）
            foreach (string src in sources)
                yield return ("shell48", ShellItem(src, 48));

            // 六、最后兜底：ExtractIconEx
            foreach (string src in sources)
                yield return ("ExtractIconEx", ExtractIcon(src, true));
        }

        /// <summary>候选来源：自定义图标 / .url 的 IconFile / 启动目标 / 入口本身 / Steam 游戏本体。</summary>
        private static List<string> Sources(DockEntry entry)
        {
            var list = new List<string>();

            void Add(string? path)
            {
                if (string.IsNullOrWhiteSpace(path))
                    return;

                string full;
                try { full = Path.GetFullPath(path); }
                catch { return; }

                if (!File.Exists(full))
                    return;

                if (list.Any(x => string.Equals(x, full, StringComparison.OrdinalIgnoreCase)))
                    return;

                list.Add(full);
            }

            Add(entry.IconPath);
            Add(UrlIconFile(entry.Path));
            Add(entry.LaunchTarget);
            Add(entry.Path);
            Add(SteamInstalledExe(entry));

            return list;
        }

        // ---------------- 各通道实现 ----------------

        private static ImageSource? ShellItem(string path, int size)
        {
            try
            {
                SHCreateItemFromParsingName(path, IntPtr.Zero, FactoryIid, out var factory);
                if (factory == null)
                    return null;

                try
                {
                    IntPtr hBitmap = IntPtr.Zero;

                    try
                    {
                        int hr = factory.GetImage(
                            new NativeSize { Width = size, Height = size },
                            SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK,
                            out hBitmap);

                        if (hr != 0 || hBitmap == IntPtr.Zero)
                            return null;

                        var source = Imaging.CreateBitmapSourceFromHBitmap(
                            hBitmap, IntPtr.Zero, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                        source.Freeze();
                        return source;
                    }
                    finally
                    {
                        if (hBitmap != IntPtr.Zero)
                            DeleteObject(hBitmap);

                        Marshal.ReleaseComObject(factory);
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard 图标：ShellItem 取图失败 {path}：{ex.GetType().Name}: {ex.Message}");
                    return null;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 图标：ShellItem 取图失败 {path}：{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static ImageSource? PrivateIcon(string path, int size)
        {
            try
            {
                string ext = Path.GetExtension(path).ToLowerInvariant();
                if (ext != ".exe" && ext != ".dll" && ext != ".ocx" && ext != ".cpl" && ext != ".scr")
                    return null;

                var handles = new IntPtr[1];
                uint count = PrivateExtractIcons(path, 0, size, size, handles, null, 1, 0);

                if (count == 0 || handles[0] == IntPtr.Zero)
                    return null;

                try
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(
                        handles[0], System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                    source.Freeze();
                    return source;
                }
                finally
                {
                    DestroyIcon(handles[0]);
                }
            }
            catch
            {
                return null;
            }
        }

        private static ImageSource? ShellLargeIcon(string path)
        {
            IntPtr hIcon = IntPtr.Zero;

            try
            {
                var info = new SHFILEINFO();
                uint flags = SHGFI_ICON | SHGFI_LARGEICON;

                if (!File.Exists(path) && !Directory.Exists(path))
                    flags |= SHGFI_USEFILEATTRIBUTES;

                IntPtr result = SHGetFileInfo(path, 0, out info, (uint)Marshal.SizeOf(typeof(SHFILEINFO)), flags);

                if (result == IntPtr.Zero || info.hIcon == IntPtr.Zero)
                    return null;

                hIcon = info.hIcon;

                var source = Imaging.CreateBitmapSourceFromHIcon(
                    hIcon, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                source.Freeze();
                return source;
            }
            catch
            {
                return null;
            }
            finally
            {
                if (hIcon != IntPtr.Zero)
                    DestroyIcon(hIcon);
            }
        }

        private static ImageSource? ExtractIcon(string path, bool large)
        {
            try
            {
                var big = new IntPtr[1];
                var small = new IntPtr[1];

                uint count = ExtractIconEx(path, 0, big, small, 1);
                if (count == 0)
                    return null;

                IntPtr chosen = large ? big[0] : small[0];
                IntPtr other = large ? small[0] : big[0];

                if (other != IntPtr.Zero)
                    DestroyIcon(other);

                if (chosen == IntPtr.Zero)
                    return null;

                try
                {
                    var source = Imaging.CreateBitmapSourceFromHIcon(
                        chosen, System.Windows.Int32Rect.Empty, BitmapSizeOptions.FromEmptyOptions());

                    source.Freeze();
                    return source;
                }
                finally
                {
                    DestroyIcon(chosen);
                }
            }
            catch
            {
                return null;
            }
        }

        /// <summary>把图标 / 图片文件按图片解码，取最大的一帧（比绕 shell 稳）。</summary>
        private static ImageSource? DecodeImageFile(string path)
        {
            try
            {
                using var stream = File.OpenRead(path);
                var decoder = BitmapDecoder.Create(stream, BitmapCreateOptions.PreservePixelFormat, BitmapCacheOption.OnLoad);

                BitmapFrame? best = null;
                foreach (var frame in decoder.Frames)
                {
                    if (best == null || (long)frame.PixelWidth * frame.PixelHeight > (long)best.PixelWidth * best.PixelHeight)
                        best = frame;
                }

                if (best == null)
                    return null;

                best.Freeze();
                return best;
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 图标：解码图片失败 {path}：{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static bool IsImageFile(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return ext == ".ico" || ext == ".png" || ext == ".bmp" || ext == ".jpg" || ext == ".jpeg" || ext == ".gif";
        }

        // ---------------- 来源解析：.url 的 IconFile / Steam 游戏本体 ----------------

        private static string? UrlIconFile(string? path)
        {
            if (string.IsNullOrWhiteSpace(path)
                || !Path.GetExtension(path).Equals(".url", StringComparison.OrdinalIgnoreCase)
                || !File.Exists(path))
            {
                return null;
            }

            try
            {
                var info = new FileInfo(path);
                if (info.Length > 32768)
                    return null;

                foreach (string line in File.ReadLines(path))
                {
                    string text = line.Trim();

                    if (text.StartsWith("IconFile=", StringComparison.OrdinalIgnoreCase))
                    {
                        string value = text[9..].Trim();

                        // IconFile 可能带 ",索引"，去掉
                        int comma = value.LastIndexOf(',');
                        if (comma > 0 && int.TryParse(value[(comma + 1)..], out _))
                            value = value[..comma];

                        return value;
                    }
                }
            }
            catch
            {
                // 读不到就当没有
            }

            return null;
        }

        /// <summary>
        /// IconFile 丢失时的兜底：从 .url 的 steam://rungameid/&lt;id&gt; 找到 Steam 安装目录，
        /// 读 appmanifest 的 installdir，再从游戏目录里挑一个可执行文件当图标来源。
        /// （本机“黑纱掠袭 / Raiders of Blackveil”就是 games\*.ico 没了、只能这么找回来。）
        /// </summary>
        private static string? SteamInstalledExe(DockEntry entry)
        {
            try
            {
                string? url = entry.Path;

                if (string.IsNullOrWhiteSpace(url)
                    || !Path.GetExtension(url).Equals(".url", StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(url))
                {
                    return null;
                }

                // 图标文件还在就别费这个劲
                string? iconFile = UrlIconFile(url);
                if (!string.IsNullOrWhiteSpace(iconFile) && File.Exists(iconFile))
                    return null;

                string text = File.ReadAllText(url);
                var match = Regex.Match(text, @"steam://run(?:gameid)?/(\d+)", RegexOptions.IgnoreCase);
                if (!match.Success)
                    return null;

                string appId = match.Groups[1].Value;

                foreach (string steam in SteamRoots())
                {
                    string manifest = Path.Combine(steam, "steamapps", "appmanifest_" + appId + ".acf");
                    if (!File.Exists(manifest))
                        continue;

                    var dir = Regex.Match(File.ReadAllText(manifest), "\"installdir\"\\s+\"([^\"]+)\"");
                    if (!dir.Success)
                        continue;

                    string game = Path.Combine(steam, "steamapps", "common", dir.Groups[1].Value);
                    string? exe = PickGameExe(game);

                    if (!string.IsNullOrWhiteSpace(exe))
                        return exe;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 图标：Steam 本体查找失败：{ex.GetType().Name}: {ex.Message}");
            }

            return null;
        }

        /// <summary>
        /// Steam 安装根目录。注意：迭代器里不能出现“带 catch 的 try 中 yield”（CS1626），
        /// 所以这里先把结果收进列表再返回。
        /// </summary>
        private static IEnumerable<string> SteamRoots()
        {
            var roots = new List<string>();

            string? current = ReadRegistryString(
                Microsoft.Win32.Registry.CurrentUser, @"Software\Valve\Steam", "SteamPath");

            if (!string.IsNullOrWhiteSpace(current))
                roots.Add(current.Replace('/', '\\'));

            string? machine = ReadRegistryString(
                Microsoft.Win32.Registry.LocalMachine, @"SOFTWARE\WOW6432Node\Valve\Steam", "InstallPath");

            if (!string.IsNullOrWhiteSpace(machine))
                roots.Add(machine.Replace('/', '\\'));

            return roots;
        }

        private static string? ReadRegistryString(Microsoft.Win32.RegistryKey root, string subKey, string valueName)
        {
            try
            {
                using var key = root.OpenSubKey(subKey);
                return key?.GetValue(valueName) as string;
            }
            catch
            {
                return null;
            }
        }

        private static readonly string[] ExeNoise =
        {
            "crashhandler", "crashreport", "unins", "uninstall", "vcredist", "dxsetup",
            "dxwebsetup", "redist", "helper", "setup", "install"
        };

        private static string? PickGameExe(string gameDir)
        {
            try
            {
                if (!Directory.Exists(gameDir))
                    return null;

                var exes = Directory.EnumerateFiles(gameDir, "*.exe", SearchOption.TopDirectoryOnly)
                    .Select(p =>
                    {
                        try { return (Path: p, Length: new FileInfo(p).Length); }
                        catch { return (Path: p, Length: 0L); }
                    })
                    .Where(x => !ExeNoise.Any(n => Path.GetFileName(x.Path).Contains(n, StringComparison.OrdinalIgnoreCase)))
                    .OrderByDescending(x => x.Length)
                    .ToList();

                return exes.Count > 0 ? exes[0].Path : null;
            }
            catch
            {
                return null;
            }
        }

        // ---------------- 质量判据 ----------------

        private sealed class Candidate
        {
            public ImageSource Image { get; private init; } = null!;

            public string Channel { get; private init; } = string.Empty;

            /// <summary>像素边长（取宽高较大者）。</summary>
            public int Size { get; private init; }

            /// <summary>不透明像素占比。</summary>
            public double Opaque { get; private init; }

            /// <summary>去掉“同色边带 + 透明留白”之后的实心内容，占整张画布的比例。</summary>
            public double Fill { get; private init; }

            public bool Square { get; private init; }

            /// <summary>最外圈 2px 里“实心且很暗”的像素占比。坏图那一圈黑边就是它。</summary>
            public double DarkRing { get; private init; }

            /// <summary>
            /// “能用”的判据。
            ///
            /// 这里**故意不用“不透明占比”当门槛**：Windows 那批线描图标本来就是大量透明的
            /// 细线条 —— 实测语音访问 256×256 实心占比 100% 但不透明像素只有 44.7%，
            /// 磁盘清理 34.4%，恢复驱动器 31.4%。拿占比当门槛会把它们全判成坏图，
            /// 表现就是“这些系统工具没有图标”（上一版就是这样翻车的）。
            ///
            /// 真正要挡的是“空心黑框 + 正中小块”那种坏图，它的特征是
            /// **外圈很暗 且 整张图几乎不透明**，两条同时成立才判坏。
            /// </summary>
            public bool Acceptable => Square && !(DarkRing >= 0.15 && Opaque <= 0.60);

            /// <summary>足够好：直接停止继续试其它通道。</summary>
            public bool Excellent => Acceptable && Size >= 128 && Opaque >= 0.90 && Fill >= 0.90;

            public bool BetterThan(Candidate other)
                => Size != other.Size ? Size > other.Size : Fill > other.Fill;

            public static Candidate? Measure(string channel, ImageSource? source)
            {
                if (source is not BitmapSource bitmap)
                    return null;

                BitmapSource pixels = bitmap.Format == PixelFormats.Bgra32
                    ? bitmap
                    : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0.0);

                int width = pixels.PixelWidth;
                int height = pixels.PixelHeight;

                if (width <= 0 || height <= 0)
                    return null;

                int stride = width * 4;
                var buffer = new byte[stride * height];
                pixels.CopyPixels(buffer, stride, 0);

                int opaque = 0;

                for (int y = 0; y < height; y++)
                {
                    int row = y * stride;

                    for (int x = 0; x < width; x++)
                    {
                        if (buffer[row + x * 4 + 3] > 8)
                            opaque++;
                    }
                }

                double aspect = (double)Math.Max(width, height) / Math.Min(width, height);
                var box = ContentBox(buffer, width, height, stride);
                double fill = (double)box.W * box.H / (width * (double)height);

                var candidate = new Candidate
                {
                    Image = source,
                    Channel = channel,
                    Size = Math.Max(width, height),
                    Opaque = (double)opaque / (width * height),
                    Fill = fill,
                    Square = aspect <= 1.15,
                    DarkRing = DarkRingRatio(buffer, width, height, stride)
                };

                // 注意：不合格也要返回，由调用方用 Acceptable 判定；
                // 不能在这里返回 null —— 调用方会直接拿它读属性，那就是空引用。
                return candidate;
            }
        }

        /// <summary>
        /// 最外圈 2px 里“实心且很暗”的像素占比 —— 坏图那圈黑边就是它。
        /// 实测：坏图约 0.50；正常图标（含深色图标）通常 ≤ 0.10。
        /// </summary>
        private static double DarkRingRatio(byte[] px, int width, int height, int stride)
        {
            int band = Math.Max(1, Math.Min(width, height) / 128 + 1);
            int ring = 0;
            int dark = 0;

            for (int y = 0; y < height; y++)
            {
                bool edgeRow = y < band || y >= height - band;

                for (int x = 0; x < width; x++)
                {
                    if (!edgeRow && x >= band && x < width - band)
                        continue;

                    ring++;

                    int i = y * stride + x * 4;

                    if (px[i + 3] <= 8)
                        continue;

                    int luma = (px[i + 2] * 299 + px[i + 1] * 587 + px[i] * 114) / 1000;

                    if (luma < 100)
                        dark++;
                }
            }

            return ring == 0 ? 0.0 : (double)dark / ring;
        }

        /// <summary>
        /// 实心内容框：先剥掉四周连续的同色边带（伪边框，最多 10% 边长），
        /// 再裁掉透明留白。返回剩下的实心区域。
        /// </summary>
        private static (int X, int Y, int W, int H) ContentBox(byte[] px, int width, int height, int stride)
        {
            int top = 0, bottom = height - 1, left = 0, right = width - 1;
            int maxBand = Math.Max(1, Math.Min(width, height) / 10);

            for (int k = 0; k < maxBand && top < bottom && UniformBand(px, width, height, stride, top, true); k++) top++;
            for (int k = 0; k < maxBand && bottom > top && UniformBand(px, width, height, stride, bottom, true); k++) bottom--;
            for (int k = 0; k < maxBand && left < right && UniformBand(px, width, height, stride, left, false); k++) left++;
            for (int k = 0; k < maxBand && right > left && UniformBand(px, width, height, stride, right, false); k++) right--;

            while (top < bottom && RowBlank(px, stride, top, left, right)) top++;
            while (bottom > top && RowBlank(px, stride, bottom, left, right)) bottom--;
            while (left < right && ColBlank(px, stride, left, top, bottom)) left++;
            while (right > left && ColBlank(px, stride, right, top, bottom)) right--;

            return (left, top, right - left + 1, bottom - top + 1);
        }

        private static bool RowBlank(byte[] px, int stride, int y, int x0, int x1)
        {
            int hit = 0;

            for (int x = x0; x <= x1; x++)
            {
                if (px[y * stride + x * 4 + 3] <= 8)
                    hit++;
            }

            return hit >= (x1 - x0 + 1) * 0.98;
        }

        private static bool ColBlank(byte[] px, int stride, int x, int y0, int y1)
        {
            int hit = 0;

            for (int y = y0; y <= y1; y++)
            {
                if (px[y * stride + x * 4 + 3] <= 8)
                    hit++;
            }

            return hit >= (y1 - y0 + 1) * 0.98;
        }

        /// <summary>这一行/列是不是“同色条带”（伪边框）：≥90% 像素落在同一颜色附近。</summary>
        private static bool UniformBand(byte[] px, int width, int height, int stride, int index, bool horizontal)
        {
            int count = horizontal ? width : height;
            int first = horizontal ? index * stride : index * 4;

            byte b0 = px[first], g0 = px[first + 1], r0 = px[first + 2], a0 = px[first + 3];

            if (a0 <= 8)
                return false;   // 透明条带交给透明留白那一步

            int same = 0;

            for (int k = 0; k < count; k++)
            {
                int i = horizontal ? index * stride + k * 4 : k * stride + index * 4;

                if (px[i + 3] <= 8)
                {
                    same++;
                    continue;
                }

                if (Math.Abs(px[i] - b0) <= 24 && Math.Abs(px[i + 1] - g0) <= 24 && Math.Abs(px[i + 2] - r0) <= 24)
                    same++;
            }

            return same >= count * 0.9;
        }

        /// <summary>裁掉一圈透明留白（只在“确实裁掉一大圈”时才动手，避免把贴边图标裁坏）。</summary>
        private static ImageSource CropTransparent(ImageSource source)
        {
            try
            {
                if (source is not BitmapSource bitmap)
                    return source;

                BitmapSource pixels = bitmap.Format == PixelFormats.Bgra32
                    ? bitmap
                    : new FormatConvertedBitmap(bitmap, PixelFormats.Bgra32, null, 0.0);

                int width = pixels.PixelWidth;
                int height = pixels.PixelHeight;

                if (width < 8 || height < 8)
                    return source;

                int stride = width * 4;
                var buffer = new byte[stride * height];
                pixels.CopyPixels(buffer, stride, 0);

                var box = ContentBox(buffer, width, height, stride);

                if (box.W >= width && box.H >= height)
                    return source;

                if (box.W < width * 0.5 || box.H < height * 0.5)
                    return source;

                if ((width - box.W) < width * 0.08 && (height - box.H) < height * 0.08)
                    return source;

                if (pixels.CanFreeze)
                    pixels.Freeze();

                var cropped = new CroppedBitmap(pixels, new System.Windows.Int32Rect(box.X, box.Y, box.W, box.H));
                cropped.Freeze();
                return cropped;
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 图标：裁剪留白失败：{ex.GetType().Name}: {ex.Message}");
                return source;
            }
        }
    }
}
