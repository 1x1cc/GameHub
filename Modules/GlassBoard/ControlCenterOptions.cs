using System.Text.RegularExpressions;
using GameHub.Modules.GlassBoard.Dock;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 控制中心小球的外观与行为选项（对应原版 ControlCenterOptions）。
    ///
    /// 默认值与取值范围照搬原版，观感才对得上：
    ///   直径 54 · 不透明度 80 · 图标 26 · 样式 switches · 图标色 #A9C9FF。
    /// 与原版一样，这份配置跟着 GlassBoard 自己的 settings.json 走（不另开文件），
    /// 只是从原版扁平的首选项挪成了 GlassBoardSettings.ControlOrb 一节。
    /// </summary>
    public sealed class ControlCenterOptions
    {
        /// <summary>小球直径（原版默认 54，可用范围 36–100）。</summary>
        public double Size { get; set; } = 54.0;

        /// <summary>玻璃不透明度百分比（原版默认 80，可用范围 15–100）。</summary>
        public double Opacity { get; set; } = 80.0;

        /// <summary>球内字形大小（原版默认 26，可用范围 12–72）。</summary>
        public double IconSize { get; set; } = 26.0;

        /// <summary>控制中心面板里的图标大小（原版默认 24，可用范围 12–36）。</summary>
        public double PanelIconSize { get; set; } = 24.0;

        /// <summary>图标样式：switches / tiles / spark（原版就这三种）。</summary>
        public string IconStyle { get; set; } = ControlIconRules.Default;

        /// <summary>字形颜色（原版默认 #A9C9FF）。</summary>
        public string Color { get; set; } = "#A9C9FF";

        /// <summary>锁定位置：锁上以后拖动不再生效（原版 ControlCenter.Locked）。</summary>
        public bool Locked { get; set; }

        /// <summary>总在最前（原版 ControlCenter.Topmost，与 Dock 的那一项分开记）。</summary>
        public bool Topmost { get; set; }

        /// <summary>是否已记录过位置（没记录过就摆到主屏右下角，对应原版 Left/Top 为 null）。</summary>
        public bool Placed { get; set; }

        public double Left { get; set; }

        public double Top { get; set; }

        public void Normalize()
        {
            Size = DockRules.Clamp(Size, 36.0, 100.0, 54.0);
            Opacity = DockRules.Clamp(Opacity, 15.0, 100.0, 80.0);
            IconSize = DockRules.Clamp(IconSize, 12.0, 72.0, 26.0);
            PanelIconSize = DockRules.Clamp(PanelIconSize, 12.0, 36.0, 24.0);
            IconStyle = ControlIconRules.Style(IconStyle);

            if (string.IsNullOrWhiteSpace(Color) || !Regex.IsMatch(Color, "^#[0-9a-fA-F]{6}$"))
                Color = "#A9C9FF";

            if (!double.IsFinite(Left) || !double.IsFinite(Top))
            {
                // 位置被写坏（NaN / ±∞）就等于没记过，回到默认摆放
                Left = 0;
                Top = 0;
                Placed = false;
            }
        }

        /// <summary>
        /// 球内字形尺寸：Min(图标大小, 直径-12)（原版 ControlIconRules.OrbGlyphSize）。
        /// 小球调小的时候字形跟着收，不会顶到球沿上。
        /// </summary>
        public double GlyphSize => ControlIconRules.OrbGlyphSize(this);

        public ControlCenterOptions Copy() => (ControlCenterOptions)MemberwiseClone();
    }
}
