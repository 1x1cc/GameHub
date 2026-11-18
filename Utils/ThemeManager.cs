using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Media;

namespace GameHub.Utils
{
    /// <summary>
    /// GameHub 主题管理器（参考 Pavise 的 Theme.cs：同一套键名，白天 / 黑夜两组色值 + 主题强调色）。
    ///
    /// 关键设计：**就地修改画刷颜色**，而不是替换资源字典。
    ///   主题字典（Themes/PaviseDark.xaml）挂在 Application.Resources，各窗口用 {StaticResource XxxBrush}
    ///   引用的是**同一个 SolidColorBrush 实例**；只要把该实例的 Color 改掉，所有窗口（含已打开的）
    ///   会立刻重绘，无需重启、也无需把上百处引用改成 DynamicResource。
    ///   为此调色板画刷统一标了 po:Freeze="False"（见 Themes/PaviseDark.xaml）——冻结的画刷无法改色。
    /// </summary>
    public static class ThemeManager
    {
        /// <summary>主题色预设（名称 -> 强调色 / 强调色深色端）。第一个为 Pavise 默认绿。</summary>
        public static readonly (string Name, string Accent, string Accent2)[] AccentPresets =
        {
            ("Pavise 绿", "#FF34D094", "#FF14885E"),
            ("琥珀",     "#FFFFB020", "#FFA96900"),
            ("赤红",     "#FFFF4858", "#FFB3273A"),
            ("天蓝",     "#FF4A90D9", "#FF2E5F97"),
            ("紫罗兰",   "#FF9B78FF", "#FF5F3FB8"),
            ("青碧",     "#FF34D0D0", "#FF148A8A"),
        };

        public static bool IsLight { get; private set; }

        public static string CurrentAccentHex { get; private set; } = AccentPresets[0].Accent;

        /// <summary>应用主题：light=true 白天，false 黑夜；accentHex 形如 #FF34D094，传空则用默认绿。</summary>
        public static void Apply(bool light, string? accentHex)
        {
            var res = Application.Current?.Resources;
            if (res == null)
                return;

            IsLight = light;
            string accentHexResolved = string.IsNullOrWhiteSpace(accentHex) ? AccentPresets[0].Accent : accentHex!;
            Color accent = ParseColor(accentHexResolved) ?? ParseColor(AccentPresets[0].Accent) ?? Color.FromRgb(0x34, 0xD0, 0x94);
            Color accent2 = Shift(accent, -0.35);
            CurrentAccentHex = "#FF" + accent.R.ToString("X2") + accent.G.ToString("X2") + accent.B.ToString("X2");

            // 1) 中性色：白天 / 黑夜两组（数值取自 Pavise Theme.cs 的两个分支）
            if (light)
            {
                SetBrush(res, "BgBrush", C(0xEE, 0xF2, 0xF7));
                SetBrush(res, "NavBrush", C(0xE1, 0xE7, 0xEF));
                SetBrush(res, "SurfaceBrush", C(0xFB, 0xFC, 0xFE));
                SetBrush(res, "SurfaceHoverBrush", C(0xF5, 0xF8, 0xFC));
                SetBrush(res, "InsetBrush", C(0xE4, 0xEA, 0xF2));
                SetBrush(res, "StrokeBrush", C(0xC2, 0xCB, 0xD8));
                SetBrush(res, "StrokeHiBrush", C(0x89, 0x96, 0xA9));
                SetBrush(res, "FgBrush", C(0x11, 0x18, 0x24));
                SetBrush(res, "DimBrush", C(0x4C, 0x59, 0x6C));
                SetBrush(res, "FaintBrush", C(0x7C, 0x8B, 0x9F));
                SetBrush(res, "TrackOffBrush", C(0xC8, 0xCE, 0xD7));
                SetBrush(res, "WarningBrush", C(0xB4, 0x5E, 0x00));
                SetBrush(res, "DangerBrush", C(0xC0, 0x2B, 0x3B));
            }
            else
            {
                SetBrush(res, "BgBrush", C(0x09, 0x0A, 0x0C));
                SetBrush(res, "NavBrush", C(0x06, 0x07, 0x09));
                SetBrush(res, "SurfaceBrush", C(0x11, 0x13, 0x17));
                SetBrush(res, "SurfaceHoverBrush", C(0x17, 0x1B, 0x21));
                SetBrush(res, "InsetBrush", C(0x07, 0x08, 0x0A));
                SetBrush(res, "StrokeBrush", C(0x27, 0x2C, 0x34));
                SetBrush(res, "StrokeHiBrush", C(0x48, 0x51, 0x5E));
                SetBrush(res, "FgBrush", C(0xF4, 0xF6, 0xF9));
                SetBrush(res, "DimBrush", C(0xA3, 0xAA, 0xB5));
                SetBrush(res, "FaintBrush", C(0x5B, 0x64, 0x71));
                SetBrush(res, "TrackOffBrush", C(0x2B, 0x30, 0x39));
                SetBrush(res, "WarningBrush", C(0xFF, 0xBE, 0x4B));
                SetBrush(res, "DangerBrush", C(0xFF, 0x48, 0x58));
            }

            // 2) 主题强调色：画刷、选中底色、强调文字色、渐变、窗口底色渐变
            Color surface = light ? C(0xFB, 0xFC, 0xFE) : C(0x11, 0x13, 0x17);
            Color bg = light ? C(0xEE, 0xF2, 0xF7) : C(0x09, 0x0A, 0x0C);
            SetBrush(res, "AccentBrush", accent);
            SetBrush(res, "Accent2Brush", accent2);
            SetBrush(res, "SelBrush", Blend(surface, accent, 0.2));
            SetBrush(res, "OnAccentBrush", Luma(accent) > 150 ? C(0x17, 0x13, 0x0A) : Colors.White);
            SetBrush(res, "GreenBrush", light ? C(0x10, 0x96, 0x5C) : C(0x45, 0xE0, 0x9A));
            SetGradient(res, "AccentGradientBrush", Shift(accent, 0.08), accent2);
            SetGradient(res, "WindowGradientBrush", Shift(bg, 0.02), bg);

            // 3) 标题栏 / 非客户区跟随明暗（WPF 自己画不了那部分）
            DarkTitleBar.ApplyToAll(!light);
        }

        /// <summary>按当前设置重新应用（供设置界面立刻生效用）。</summary>
        public static void Reapply()
        {
            var s = SettingsStore.Load();
            Apply(s.ThemeLight, s.ThemeAccent);
        }

        private static void SetBrush(ResourceDictionary res, string key, Color color)
        {
            try
            {
                if (res[key] is SolidColorBrush b && !b.IsFrozen)
                {
                    b.Color = color;
                    return;
                }

                // 冻结或缺失：换一个可改色的实例（新窗口立即生效；已打开窗口需重开才会更新）
                var nb = new SolidColorBrush(color);
                res[key] = nb;
            }
            catch
            {
                // 忽略
            }
        }

        private static void SetGradient(ResourceDictionary res, string key, Color from, Color to)
        {
            try
            {
                if (res[key] is LinearGradientBrush g && !g.IsFrozen && g.GradientStops.Count >= 2)
                {
                    g.GradientStops[0].Color = from;
                    g.GradientStops[1].Color = to;
                    return;
                }

                // 冻结的渐变改不了色：整支替换（引用方都是 DynamicResource，会立即重新解析）
                var ng = new LinearGradientBrush { StartPoint = new Point(0, 0), EndPoint = new Point(0, 1) };
                ng.GradientStops.Add(new GradientStop(from, 0));
                ng.GradientStops.Add(new GradientStop(to, 1));
                res[key] = ng;
            }
            catch
            {
                // 忽略
            }
        }

        private static Color C(byte r, byte g, byte b) => Color.FromRgb(r, g, b);

        private static Color? ParseColor(string hex)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(hex))
                    return null;

                string h = hex.Trim().TrimStart('#');
                if (h.Length == 6)
                    h = "FF" + h;
                if (h.Length != 8)
                    return null;

                byte a = Convert.ToByte(h.Substring(0, 2), 16);
                byte r = Convert.ToByte(h.Substring(2, 2), 16);
                byte g = Convert.ToByte(h.Substring(4, 2), 16);
                byte b = Convert.ToByte(h.Substring(6, 2), 16);
                return Color.FromArgb(a, r, g, b);
            }
            catch
            {
                return null;
            }
        }

        /// <summary>按比例变亮（f>0）或变暗（f&lt;0）。</summary>
        private static Color Shift(Color c, double f)
        {
            double t = f > 0 ? f : -f;
            double r = f > 0 ? c.R + (255 - c.R) * t : c.R * (1 - t);
            double g = f > 0 ? c.G + (255 - c.G) * t : c.G * (1 - t);
            double b = f > 0 ? c.B + (255 - c.B) * t : c.B * (1 - t);
            return Color.FromRgb(Clamp(r), Clamp(g), Clamp(b));
        }

        private static Color Blend(Color a, Color b, double t)
        {
            return Color.FromRgb(
                Clamp(a.R + (b.R - a.R) * t),
                Clamp(a.G + (b.G - a.G) * t),
                Clamp(a.B + (b.B - a.B) * t));
        }

        private static byte Clamp(double v) => (byte)(v < 0 ? 0 : (v > 255 ? 255 : v));

        private static double Luma(Color c) => 0.299 * c.R + 0.587 * c.G + 0.114 * c.B;
    }
}
