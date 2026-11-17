using GameHub.Utils;
using System;
using System.IO;
using System.Text.Json;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// GlassBoard 模块自己的持久化配置。
    ///
    /// 存放在 %LOCALAPPDATA%\GameHub\GlassBoard\settings.json，
    /// 与 GameHub 主设置分离，避免以后改主设置结构时互相影响。
    /// </summary>
    /// <summary>
    /// 一个桌面组件的悬浮窗选项（时钟 / 天气 / 音乐 / 备忘录 各自一份）。
    ///
    /// 显示/隐藏沿用各组件已有的 XxxVisible 开关（不在这里再存一份，
    /// 免得两个开关打架）；这里只管“显示之后怎么贴、怎么让路”。
    /// </summary>
    public sealed class FloatingOptions
    {
        /// <summary>锁定移动：锁上以后鼠标拖不动（避免游戏里误拖）。</summary>
        public bool Locked { get; set; }

        /// <summary>始终置顶：盖在普通窗口之上（默认是贴桌面，即“在普通窗口之下”）。</summary>
        public bool Topmost { get; set; }

        /// <summary>鼠标穿透：点击直接穿过组件，不挡游戏操作。</summary>
        public bool ClickThrough { get; set; }

        /// <summary>不透明度（0.25–1.0）。</summary>
        public double Opacity { get; set; } = 1.0;

        public void Normalize()
        {
            if (double.IsNaN(Opacity) || Opacity < 0.25) Opacity = 0.25;
            if (Opacity > 1.0) Opacity = 1.0;
        }
    }

    public sealed class GlassBoardSettings
    {
        /// <summary>卡片圆形总开关：跨次记忆（上次是开，下次启动继续开）。</summary>
        public bool ModuleEnabled { get; set; }

        /// <summary>桌面时钟是否显示。</summary>
        public bool ClockVisible { get; set; }

        /// <summary>是否已记录过时钟位置（没记录过就放到默认位置）。</summary>
        public bool ClockPlaced { get; set; }

        public double ClockLeft { get; set; }

        public double ClockTop { get; set; }

        /// <summary>时钟字号（原版默认 48，可用范围 28–100）。</summary>
        public double ClockSize { get; set; } = 48.0;

        /// <summary>时钟下方是否显示日期。</summary>
        public bool ShowDate { get; set; } = true;

        /// <summary>24 小时制。</summary>
        public bool Hour24 { get; set; } = true;

        /// <summary>无框：去掉玻璃底板，只留文字。</summary>
        public bool Frameless { get; set; }

        /// <summary>文字阴影（贴在浅色壁纸上时更清楚）。</summary>
        public bool TextShadow { get; set; } = true;

        /// <summary>文字颜色（#RRGGBB）。</summary>
        public string TextColor { get; set; } = "#FFFFFF";

        /// <summary>桌面备忘录是否显示。</summary>
        public bool NotesVisible { get; set; }

        /// <summary>是否已记录过备忘录位置（没记录过就放到默认位置）。</summary>
        public bool NotesPlaced { get; set; }

        public double NotesLeft { get; set; }

        public double NotesTop { get; set; }

        /// <summary>玻璃 Dock 是否显示。</summary>
        public bool DockVisible { get; set; }

        /// <summary>应用大面板是否显示（跨次记忆）。</summary>
        public bool BoardVisible { get; set; }

        // ---------- 桌面天气（对应原版 Widgets.Weather + City） ----------

        /// <summary>桌面天气是否显示。</summary>
        public bool WeatherVisible { get; set; }

        /// <summary>是否已记录过天气组件位置。</summary>
        public bool WeatherPlaced { get; set; }

        public double WeatherLeft { get; set; }

        public double WeatherTop { get; set; }

        /// <summary>城市名；留空用默认城市（北京）。</summary>
        public string WeatherCity { get; set; } = "";

        /// <summary>是否显示湿度一行。</summary>
        public bool WeatherShowHumidity { get; set; } = true;

        // ---------- 桌面音乐（对应原版 Widgets.Music，系统播放模式） ----------

        /// <summary>桌面音乐组件是否显示。</summary>
        public bool MusicVisible { get; set; }

        /// <summary>是否已记录过音乐组件位置。</summary>
        public bool MusicPlaced { get; set; }

        public double MusicLeft { get; set; }

        public double MusicTop { get; set; }

        /// <summary>是否显示 上一首 / 播放暂停 / 下一首 控件。</summary>
        public bool MusicShowControls { get; set; } = true;

        /// <summary>紧凑模式：只留曲名一行。</summary>
        public bool MusicCompact { get; set; }

        /// <summary>
        /// 系统媒体会话拿不到封面时，是否允许联网按「曲名 + 歌手」查询封面。
        /// 关掉就只显示占位音符，组件完全不联网。
        /// </summary>
        public bool MusicOnlineCover { get; set; } = true;

        // ---------- 桌面控制球（控制中心，对应原版 Preferences.ControlCenter） ----------

        /// <summary>控制中心小球是否显示（跨次记忆）。</summary>
        public bool ControlOrbVisible { get; set; }

        /// <summary>小球外观与行为：直径 / 不透明度 / 图标 / 样式 / 颜色 / 位置。</summary>
        public ControlCenterOptions ControlOrb { get; set; } = new();

        // ---------- 悬浮窗（逐个组件：锁定移动 / 始终置顶 / 鼠标穿透 / 不透明度） ----------

        public FloatingOptions ClockFloating { get; set; } = new();

        public FloatingOptions WeatherFloating { get; set; } = new();

        public FloatingOptions MusicFloating { get; set; } = new();

        public FloatingOptions NotesFloating { get; set; } = new();
    }

    /// <summary>
    /// GlassBoard 配置读写。读写失败一律只写日志、退回默认值，
    /// 绝不让“配置文件坏了”变成“程序起不来”。
    /// </summary>
    public static class GlassBoardStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        private static string Path => System.IO.Path.Combine(GlassBoardModule.DataFolder, "settings.json");

        public static GlassBoardSettings Load()
        {
            try
            {
                if (!File.Exists(Path))
                    return new GlassBoardSettings();

                var settings = JsonSerializer.Deserialize<GlassBoardSettings>(File.ReadAllText(Path)) ?? new GlassBoardSettings();

                // 老配置文件里没有这一节，读出来可能是 null；越界值也在这里一次收敛好。
                settings.ControlOrb ??= new ControlCenterOptions();
                settings.ControlOrb.Normalize();

                NormalizeFloating(settings);

                return settings;
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：读取配置失败，改用默认值：{ex.GetType().Name}: {ex.Message}");
                return new GlassBoardSettings();
            }
        }

        /// <summary>老配置里没有这几节，读出来可能是 null；越界值一并收敛。</summary>
        private static void NormalizeFloating(GlassBoardSettings settings)
        {
            settings.ClockFloating ??= new FloatingOptions();
            settings.WeatherFloating ??= new FloatingOptions();
            settings.MusicFloating ??= new FloatingOptions();
            settings.NotesFloating ??= new FloatingOptions();

            settings.ClockFloating.Normalize();
            settings.WeatherFloating.Normalize();
            settings.MusicFloating.Normalize();
            settings.NotesFloating.Normalize();
        }

        public static void Save(GlassBoardSettings settings)
        {
            try
            {
                settings.ControlOrb ??= new ControlCenterOptions();
                settings.ControlOrb.Normalize();

                NormalizeFloating(settings);

                Directory.CreateDirectory(GlassBoardModule.DataFolder);
                File.WriteAllText(Path, JsonSerializer.Serialize(settings, Options));
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：保存配置失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
