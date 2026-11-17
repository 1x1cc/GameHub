using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace GameHub.Modules.GlassBoard.Dock
{
    /// <summary>玻璃材质（对应原版 MaterialOptions 里 Dock 用到的部分）。</summary>
    public sealed class DockMaterial
    {
        public int Kind { get; set; }

        public string Tint { get; set; } = string.Empty;

        public double Gloss { get; set; } = 0.15;

        public double Grain { get; set; }

        public bool Shadow { get; set; } = true;

        public void Normalize()
        {
            Kind = Math.Clamp(Kind, 0, 3);
            Gloss = DockRules.Clamp(Gloss, 0.0, 1.0, 0.15);
            Grain = DockRules.Clamp(Grain, 0.0, 1.0, 0.0);

            if (string.IsNullOrEmpty(Tint) || !System.Text.RegularExpressions.Regex.IsMatch(Tint, "^#[a-fA-F0-9]{6}$"))
                Tint = string.Empty;
        }

        public DockMaterial Copy() => (DockMaterial)MemberwiseClone();
    }

    /// <summary>
    /// Dock 外观与行为选项（对应原版 DockOptions）。
    /// 默认值、取值范围与规范化规则都按原版照搬，界面观感才对得上。
    /// </summary>
    public sealed class DockOptions
    {
        public double IconSize { get; set; } = 44.0;
        public double Spacing { get; set; } = 6.0;
        public double Padding { get; set; } = 16.0;
        public double Radius { get; set; } = 28.0;
        public double OpacityPercent { get; set; } = 72.0;
        public double Zoom { get; set; } = 1.2;
        public double Offset { get; set; }
        public double MotionLevel { get; set; } = 1.0;

        /// <summary>0=左 1=中 2=右（原版 Alignment）。</summary>
        public int Alignment { get; set; } = 1;

        public int Accent { get; set; }
        public int MaxPinned { get; set; } = 9;
        public int ComputerStyle { get; set; }

        public bool ShowComputer { get; set; } = true;
        public bool ShowLabels { get; set; }
        public bool ShowSeparator { get; set; } = true;
        public bool AlwaysOnTop { get; set; }
        public bool MotionEnabled { get; set; } = true;
        public bool AutoHide { get; set; }
        public bool ShowRunning { get; set; } = true;
        public bool SpringReveal { get; set; } = true;
        public bool ShowPower { get; set; }
        public bool ShowNetwork { get; set; }
        public bool ShowSettings { get; set; } = true;

        public List<string> ItemOrder { get; set; } = new();
        public List<string> RunningOrder { get; set; } = new();

        public bool ShowShortcutOverlays { get; set; }
        public bool SharperIcons { get; set; }

        public DockMaterial Material { get; set; } = new();

        public double HideDelay { get; set; } = 900.0;
        public double EdgeRange { get; set; } = 12.0;
        public double BlurRadius { get; set; } = 18.0;
        public double EdgeStrength { get; set; } = 1.0;

        public void Normalize()
        {
            ItemOrder = (ItemOrder ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(30)
                .ToList();

            RunningOrder = (RunningOrder ?? new List<string>())
                .Where(x => !string.IsNullOrWhiteSpace(x))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .Take(200)
                .ToList();

            Material ??= new DockMaterial();
            Material.Normalize();

            IconSize = DockRules.Clamp(IconSize, 32.0, 64.0, 44.0);
            Spacing = DockRules.Clamp(Spacing, 0.0, 20.0, 6.0);
            Padding = DockRules.Clamp(Padding, 8.0, 28.0, 16.0);
            Radius = DockRules.Clamp(Radius, 0.0, 48.0, 28.0);
            OpacityPercent = DockRules.GlassOpacity(OpacityPercent);
            Zoom = DockRules.Clamp(Zoom, 1.0, 1.6, 1.2);
            Offset = DockRules.Clamp(Offset, -500.0, 500.0, 0.0);
            MotionLevel = DockRules.Clamp(MotionLevel, 0.5, 1.5, 1.0);
            HideDelay = DockRules.Clamp(HideDelay, 200.0, 3000.0, 900.0);
            EdgeRange = DockRules.Clamp(EdgeRange, 3.0, 48.0, 12.0);
            BlurRadius = DockRules.Clamp(BlurRadius, 0.0, 40.0, 18.0);
            EdgeStrength = DockRules.Clamp(EdgeStrength, 0.2, 2.0, 1.0);

            Alignment = Math.Clamp(Alignment, 0, 2);
            Accent = Math.Clamp(Accent, 0, 5);
            MaxPinned = Math.Clamp(MaxPinned, 3, 20);
            ComputerStyle = Math.Clamp(ComputerStyle, 0, 1);
        }

        /// <summary>悬浮放大时给图标留出的额外内边距（原版算法，别改数值）。</summary>
        public double HoverPadding()
            => Math.Max(Padding, (IconSize + 16.0) * (Math.Min(1.6, 1.0 + (Zoom - 1.0) * MotionLevel) - 1.0) / 2.0 + 2.0);

        /// <summary>系统快捷入口个数：大面板(▦) + 唤出 GameHub(▣) + 可选设置(⚙) + 隐藏图标。
        /// 「唤出 GameHub」是 2026-11-12 加的，早先漏算导致 ⚙ 设置按钮被挤到屏幕外。
        /// 「此电脑」入口已删，不再计入。</summary>
        public int SystemSlots()
            => 3 + (ShowSettings ? 1 : 0);

        /// <summary>在给定宽度下最多显示几个固定项。</summary>
        public int VisiblePinned(double availableWidth, int count)
        {
            double slot = IconSize + 16.0 + Spacing;
            int fit = Math.Max(0, (int)Math.Floor((availableWidth - 28.0 - HoverPadding() * 2.0 - (ShowSeparator ? 17 : 0)) / slot) - SystemSlots());
            return Math.Min(count, Math.Min(MaxPinned, fit));
        }

        /// <summary>给定固定项数量时的 Dock 总宽度。</summary>
        public double WidthFor(int pinnedCount)
            => 28.0 + HoverPadding() * 2.0
               + (SystemSlots() + pinnedCount) * (IconSize + 16.0 + Spacing)
               + (ShowSeparator ? 17 : 0);

        public DockOptions Copy()
        {
            var copy = (DockOptions)MemberwiseClone();
            copy.ItemOrder = (ItemOrder ?? new List<string>()).ToList();
            copy.RunningOrder = (RunningOrder ?? new List<string>()).ToList();
            copy.Material = (Material ?? new DockMaterial()).Copy();
            return copy;
        }
    }

    /// <summary>Dock 里的一个入口（对应原版 Entry，字段含义保持一致）。</summary>
    public sealed class DockEntry
    {
        public string Name { get; set; } = string.Empty;

        public string Path { get; set; } = string.Empty;

        public bool Pinned { get; set; }

        /// <summary>
        /// 人工把它从「系统工具」里移出来了（本地新增字段，原版没有这一位）。
        /// 自动对照 / 自动归类会不断把 Windows 自带项塞回系统工具，
        /// 有了这个标记，人工的决定就不会在下次启动被改回去。
        /// </summary>
        public bool ManualNonSystem { get; set; }

        public bool IsFolder { get; set; }

        public string? Group { get; set; }

        public List<string>? ExtraGroups { get; set; }

        public string? IconPath { get; set; }

        public string? LaunchTarget { get; set; }

        public string? LaunchArguments { get; set; }

        public string? ProductInfo { get; set; }

        public string? WindowTarget { get; set; }

        [JsonIgnore]
        public string DisplayName
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Name))
                    return Name;

                try
                {
                    return System.IO.Path.GetFileNameWithoutExtension(Path) is { Length: > 0 } name ? name : Path;
                }
                catch
                {
                    return Path;
                }
            }
        }

        public DockEntry Copy() => (DockEntry)MemberwiseClone();
    }

    /// <summary>
    /// Dock 的数据集合（对应原版 Preferences 中 Dock 关心的那部分）。
    /// 单独一份 JSON，不掺进 GameHub 主设置。
    /// </summary>
    public sealed class DockModel
    {
        public List<DockEntry> Apps { get; set; } = new();

        public List<string> DockOrder { get; set; } = new();

        /// <summary>分组名（原版 Preferences.Groups 的默认三个分组）。</summary>
        public List<string> Groups { get; set; } = new() { "创作", "办公", "游戏" };

        public DockOptions Dock { get; set; } = new();

        /// <summary>是否已记录过 Dock 位置（没记录过就按对齐设置自动摆放）。</summary>
        public bool DockPlaced { get; set; }

        public double DockLeft { get; set; }

        public double DockTop { get; set; }

        // ---------- 大面板（原版 Board） ----------

        /// <summary>大面板图标大小（原版默认 48，范围 28–80，和 Dock 图标大小是两套设置）。</summary>
        public double BoardIconSize { get; set; } = 48.0;

        /// <summary>大面板图标间距（原版默认 5，范围 0–20）。</summary>
        public double BoardIconGap { get; set; } = 5.0;

        /// <summary>隐藏搜索栏（原版 BoardHideSearch）。</summary>
        public bool BoardHideSearch { get; set; }

        /// <summary>隐藏分组栏（原版 BoardHideGroups）。</summary>
        public bool BoardHideGroups { get; set; }

        /// <summary>是否已经做过首次开始菜单导入（对应原版 Preferences.Imported）。</summary>
        public bool Imported { get; set; }

        /// <summary>是否已经手动选过图标大小档位；没选过就跟随 Windows 桌面图标大小。</summary>
        public bool BoardIconSizeFromDesktop { get; set; } = true;

        /// <summary>大面板位置与尺寸是否已记录过。</summary>
        public bool BoardPlaced { get; set; }

        public double BoardLeft { get; set; }

        public double BoardTop { get; set; }

        public double BoardWidth { get; set; }

        public double BoardHeight { get; set; }

        public void Normalize()
        {
            Apps = (Apps ?? new List<DockEntry>())
                .Where(a => a != null && !string.IsNullOrWhiteSpace(a.Path))
                .ToList();

            foreach (var app in Apps)
            {
                if (string.IsNullOrWhiteSpace(app.Name))
                {
                    try { app.Name = System.IO.Path.GetFileNameWithoutExtension(app.Path) ?? app.Path; }
                    catch { app.Name = app.Path; }
                }
            }

            Apps = EntryRules.NormalizeEntries(Apps);

            Dock ??= new DockOptions();
            Dock.Normalize();

            BoardIconSize = DockRules.Clamp(BoardIconSize, 28.0, 80.0, 48.0);
            BoardIconGap = DockRules.Clamp(BoardIconGap, 0.0, 20.0, 5.0);
            Groups = BoardRules.Groups(Groups, Apps);

            SynchronizeOrder();
        }

        /// <summary>把 DockOrder / ItemOrder 与 Apps 对齐（原版 Normalize 里的两步同步）。</summary>
        public void SynchronizeOrder()
        {
            Dock ??= new DockOptions();

            // DockOrder：只保留仍然固定着的入口，并按当前固定集合补齐
            var pinnedPaths = DockOrderRules.Ordered(this).Select(a => a.Path).ToList();
            DockOrder = pinnedPaths.ToList();

            DockLayoutRules.SyncApps(this);
        }
    }
}
