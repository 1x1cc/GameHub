using System;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 控制中心小球的图标规则（对应原版 ControlIconRules）。
    /// 三种样式与中文名照搬原版，配置里存的仍然是英文 key，和原版配置能对上。
    /// </summary>
    public static class ControlIconRules
    {
        /// <summary>默认样式（原版 ControlCenterOptions.IconStyle 默认值）。</summary>
        public const string Default = "switches";

        /// <summary>三种图标样式（顺序与原版一致）。</summary>
        public static readonly string[] Styles = { "switches", "tiles", "spark" };

        /// <summary>三种样式在界面上的名字（原版 ControlIconRules.Names）。</summary>
        public static readonly string[] Names = { "圆角双拨杆", "四格面板", "四瓣星芒" };

        /// <summary>把任意字符串收敛到合法样式，认不出来就用 switches。</summary>
        public static string Style(string? value)
            => Array.IndexOf(Styles, value) >= 0 ? value! : Default;

        /// <summary>样式对应的中文名（认不出来按下标 0 处理）。</summary>
        public static string Name(string? value)
        {
            int index = Array.IndexOf(Styles, Style(value));
            return Names[index < 0 ? 0 : index];
        }

        /// <summary>样式在列表里的下标（右键菜单勾选状态用）。</summary>
        public static int Index(string? value)
        {
            int index = Array.IndexOf(Styles, Style(value));
            return index < 0 ? 0 : index;
        }

        /// <summary>
        /// 球内字形尺寸：Min(图标大小, 直径-12)（原版逐字相同，别改公式）。
        /// </summary>
        public static double OrbGlyphSize(ControlCenterOptions options)
            => Math.Min(options.IconSize, options.Size - 12.0);
    }
}
