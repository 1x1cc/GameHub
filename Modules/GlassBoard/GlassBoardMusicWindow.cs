using GameHub.Utils;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Windows.Foundation;
using Windows.Media.Control;
using Windows.Storage.Streams;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 桌面音乐组件（对应原版 GlassBoard 的「音乐」组件，系统播放模式）。
    ///
    /// 原版的系统模式靠 NPSMLib.dll + UIA + PowerShell 桥；这里改用系统自带的
    /// 媒体会话 API（SMTC，Windows.Media.Control），不带任何外部文件：
    /// 任何播放器（浏览器 / 网易云 / Spotify / QQ 音乐…）只要向系统注册了
    /// 「正在播放」，就能读到 曲名 / 歌手 / 专辑 / 封面，并直接下发
    /// 上一首 / 播放暂停 / 下一首。
    ///
    /// 按钮图标用矢量路径（取自原版 Polish.Icon 的几何），不用字体字符，
    /// 免得某些机器上缺字形显示成方块。
    ///
    /// 行为：每 2 秒轮询一次；按住卡片可拖动（位置跨次记忆，走组件基类）；
    /// 读取失败只在卡片上提示并低频记日志，不弹框打扰。
    /// </summary>
    public sealed class GlassBoardMusicWindow : GlassBoardWidgetWindow
    {
        private const double Pad = 14.0;

        private readonly Grid _root = new();
        private readonly Border _card = new();
        private readonly Grid _layout = new();
        private readonly Border _coverShell = new();
        private readonly Image _cover = new();
        private readonly TextBlock _title = new();
        private readonly TextBlock _sub = new();
        private readonly TextBlock _state = new();
        private readonly StackPanel _controls = new();
        private readonly StackPanel _equalizer = new();

        /// <summary>拾音块条数：横向铺满整个组件宽度（两侧各留一点白）。</summary>
        private const int EqBarCount = 48;

        /// <summary>拾音块：固定宽度的竖条，只有高度随电平变化。</summary>
        private readonly System.Windows.Shapes.Rectangle[] _bars = new System.Windows.Shapes.Rectangle[EqBarCount];

        /// <summary>没有封面时显示的占位音符。</summary>
        private readonly TextBlock _coverPlaceholder = new();

        private const double EqMaxHeight = 22.0;
        private const double EqBarWidth = 4.0;
        private const double EqBarGap = 3.0;
        private const double EqIdleHeight = 3.0;
        private readonly DispatcherTimer _eqTimer = new();
        private readonly Button _play = new();

        /// <summary>系统输出端点的峰值电平表（真实音频，不是随机数）。</summary>
        private readonly AudioLevelMeter _meter = new();

        /// <summary>各条拾音块的当前高度（0..1），用于“快起慢落”的跳动感。</summary>
        private readonly double[] _barLevel = new double[EqBarCount];

        /// <summary>SMTC 没有会话时的兜底曲名来源（读播放器窗口标题）。</summary>
        private readonly MusicWindowTitleSource _titleSource = new();

        /// <summary>上一次兜底判定的播放状态（用于只记变化日志）。</summary>
        private bool? _lastFallbackPlaying;

        private readonly DispatcherTimer _poll;

        private GlobalSystemMediaTransportControlsSessionManager? _manager;
        private string _coverKey = string.Empty;

        /// <summary>已经为哪首歌查过在线封面（避免每 2 秒轮询都去联网）。</summary>
        private string _onlineCoverKey = string.Empty;

        private bool _busy;
        private int _failures;

        public GlassBoardMusicWindow()
        {
            Width = 428;   // 344 太窄：扣掉封面与内边距后标题只剩 218px，长曲名必被截断
            Height = 208;  // 208：头部(可两行曲名+两行歌手) + 居中控件 + 底部拾音块 + 两侧留白
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

            // 统一玻璃外观（与其它悬浮窗同源，值都在 GlassBoardTheme 里）
            _card.Margin = new Thickness(Pad);
            GlassBoardTheme.ApplyGlassCard(_card);

            _coverShell.Width = 54;
            _coverShell.Height = 54;
            _coverShell.CornerRadius = new CornerRadius(12);
            _coverShell.Background = GlassBoardTheme.Card;
            _coverShell.BorderBrush = GlassBoardTheme.Line;
            _coverShell.BorderThickness = new Thickness(1);
            // 封面槽 = 占位音符 + 真实封面叠放：没有封面时不再只看到一个空方块
            _coverPlaceholder.Text = "♪";
            _coverPlaceholder.FontSize = 22;
            _coverPlaceholder.Foreground = GlassBoardTheme.Muted;
            _coverPlaceholder.HorizontalAlignment = HorizontalAlignment.Center;
            _coverPlaceholder.VerticalAlignment = VerticalAlignment.Center;
            _coverPlaceholder.IsHitTestVisible = false;

            _cover.Stretch = Stretch.UniformToFill;
            RenderOptions.SetBitmapScalingMode(_cover, BitmapScalingMode.HighQuality);

            var coverLayer = new Grid();
            coverLayer.Children.Add(_coverPlaceholder);
            coverLayer.Children.Add(_cover);
            _coverShell.Child = coverLayer;

            _title.Text = "没有正在播放的音乐";
            _title.FontSize = 15.0;
            _title.FontWeight = FontWeights.SemiBold;
            _title.Foreground = GlassBoardTheme.Ink;
            _title.TextWrapping = TextWrapping.Wrap;   // 长曲名折成两行，不再一上来就省略
            _title.MaxHeight = 40;
            _title.TextTrimming = TextTrimming.CharacterEllipsis;

            _sub.Text = "在任意播放器里播放，就会显示在这里";
            _sub.FontSize = 12.0;
            _sub.Margin = new Thickness(0, 3, 0, 0);
            _sub.Foreground = GlassBoardTheme.Muted;
            _sub.TextWrapping = TextWrapping.Wrap;      // 歌手 / 专辑过长时折行
            _sub.MaxHeight = 34;
            _sub.TextTrimming = TextTrimming.CharacterEllipsis;

            _state.FontSize = 11.0;
            _state.Foreground = GlassBoardTheme.Muted;
            _state.VerticalAlignment = VerticalAlignment.Center;

            // 三个控件水平居中；每个按钮左右留同样间距，视觉上才是对称的
            _controls.Orientation = Orientation.Horizontal;
            _controls.HorizontalAlignment = HorizontalAlignment.Center;
            _controls.VerticalAlignment = VerticalAlignment.Center;

            // 播放控制：有系统会话走 SMTC，没有就直接发系统媒体键
            // （本机实测网易云不注册会话，只走 SMTC 会变成“点了没反应”）
            _controls.Children.Add(MakeButton("previous", Previous));
            _play = MakeButton("play", PlayPause);
            _controls.Children.Add(_play);
            _controls.Children.Add(MakeButton("next", Next));

            // 拾音块（音频可视化）
            //
            // 这里以前是「竖排 8 个 '▮' 文本块、靠字号大小假装跳动」——
            // 竖排会把 8 条叠成一整根竖条，字号又同时改宽和高，屏幕上根本不是拾音块。
            // 现在改成：一排固定宽度的竖条，只有高度随电平变化，统一底部对齐。
            // 拾音块铺在组件最下面，横向铺满（两侧各留一点白）
            _equalizer.Orientation = Orientation.Horizontal;
            _equalizer.HorizontalAlignment = HorizontalAlignment.Center;
            _equalizer.VerticalAlignment = VerticalAlignment.Bottom;
            _equalizer.Height = EqMaxHeight;

            for (int i = 0; i < EqBarCount; i++)
            {
                var bar = new System.Windows.Shapes.Rectangle
                {
                    Width = EqBarWidth,
                    Height = EqIdleHeight,
                    Fill = GlassBoardTheme.Accent,
                    RadiusX = EqBarWidth / 2.0,
                    RadiusY = EqBarWidth / 2.0,
                    Margin = new Thickness(0, 0, EqBarGap, 0),
                    Opacity = 0.30,
                    VerticalAlignment = VerticalAlignment.Bottom
                };

                _bars[i] = bar;
                _equalizer.Children.Add(bar);
            }

            _eqTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
            _eqTimer.Tick += (_, _) => UpdateEqualizer();
            _eqTimer.Start();

            // 打开“系统输出峰值电平表”：只读，不改任何系统声音设置。
            // 打不开也不影响其它功能，只是拾音块保持静止。
            if (!_meter.Open())
                AppLog.Write($"GlassBoard 音乐：音频电平表打不开（拾音块将保持静止）：{_meter.LastError}");

            var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            text.Children.Add(_title);
            text.Children.Add(_sub);

            var head = new Grid();
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            head.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            Grid.SetColumn(_coverShell, 0);
            Grid.SetColumn(text, 1);
            text.Margin = new Thickness(12, 0, 0, 0);
            head.Children.Add(_coverShell);
            head.Children.Add(text);

            var controlsRow = new Grid { Margin = new Thickness(0, 10, 0, 0) };
            _state.HorizontalAlignment = HorizontalAlignment.Left;
            _state.VerticalAlignment = VerticalAlignment.Center;
            controlsRow.Children.Add(_controls);
            controlsRow.Children.Add(_state);

            // 竖排：头部 / 控件 / 弹性空白 / 拾音块。
            // 用 Grid + Star 行而不是 StackPanel —— 曲名长短不一时，
            // 拾音块才会始终贴着组件最下边；两侧留白由卡片 Padding 提供。
            _layout.RowDefinitions.Clear();
            _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
            _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
            Grid.SetRow(head, 0);
            Grid.SetRow(controlsRow, 1);
            Grid.SetRow(_equalizer, 3);
            _layout.Children.Add(head);
            _layout.Children.Add(controlsRow);
            _layout.Children.Add(_equalizer);

            _card.Child = _layout;

            _root.Children.Add(_card);
            Content = _root;

            // 按钮自己会吃掉点击，不会误触发拖动（ButtonBase 内部已 e.Handled = true）
            _card.MouseLeftButtonDown += DragWidget;
            _root.MouseRightButtonUp += ShowMenu;

            // 常规轮询 1 秒（原来 2 秒：点完要等下一轮才翻，感觉“不实时”）；
            // 点按钮后会临时加密到 300ms，见 StartFastPoll。
            _poll = new DispatcherTimer { Interval = NormalPollInterval };
            _poll.Tick += (_, _) =>
            {
                if (_fastPolls > 0 && --_fastPolls == 0)
                    _poll.Interval = NormalPollInterval;

                QueueRefresh();
            };

            Loaded += (_, _) =>
            {
                ReloadStyle();
                _poll.Start();
                QueueRefresh();
            };

            Closed += (_, _) => _poll.Stop();

            ReloadStyle();
        }

        // ---------------- 界面 ----------------

        /// <summary>图标目标框：字形墨迹会被**等比缩**到这个方框里（三个图标光学尺寸一致，且永不被裁）。</summary>
        private const double IconSize = 17.0;

        private static readonly TimeSpan NormalPollInterval = TimeSpan.FromSeconds(1);

        /// <summary>点击控制按钮后剩余的快轮询次数。</summary>
        private int _fastPolls;

        /// <summary>最近一次读到的播放状态（点击时用来做乐观翻转）。</summary>
        private bool _lastKnownPlaying;

        /// <summary>输出连续静音的采样数（暖音块每 90ms 采一次真实峰值）。</summary>
        private int _silentSamples;

        /// <summary>低于这个峰值就算“没声音”。</summary>
        private const float SilentPeak = 0.002f;

        /// <summary>连续静音多少个采样（90ms/次）就算“已暂停”—— 16 次 ≈ 1.4 秒。</summary>
        private const int SilentSamplesForPause = 16;

        /// <summary>点击后的乐观保护窗口：这段时间内不允许轮询覆写图标。</summary>
        private DateTime _optimisticUntilUtc = DateTime.MinValue;

        /// <summary>
        /// 点击控制按钮后临时把轮询加密到 300ms。
        /// 起因：播放器响应有延迟，点完立刻回读拿到的还是旧状态，
        /// 图标要等到下一轮常规轮询才翻 —— 就是用户反馈的“会切换但不实时”。
        /// </summary>
        private void StartFastPoll()
        {
            _fastPolls = 20;   // 20 × 300ms = 6 秒：网易云从收到指令到真正停声可能要好几秒
            _poll.Interval = TimeSpan.FromMilliseconds(300);
        }

        /// <summary>输出是不是确实停了（声音断了就算“已暂停”，不等音频会话消亡）。</summary>
        private bool OutputSilent => _silentSamples >= SilentSamplesForPause;

        /// <summary>
        /// 更新中间按钮图标。
        /// 点击后的“乐观窗口”内直接返回、不覆写 —— 播放器还没执行完，
        /// 这时回读到的仍是旧状态，覆写会把刚翻好的图标又打回去（暂停方向卡顿的一半原因）。
        /// </summary>
        private void SetPlayIcon(bool playing, bool force = false)
        {
            if (!force && DateTime.UtcNow < _optimisticUntilUtc)
                return;

            _play.Content = Glyph(playing ? "pause" : "play", IconSize);
        }

        private Button MakeButton(string glyph, Action click)
        {
            var button = new Button
            {
                Content = Glyph(glyph, IconSize),
                Width = 46,
                Height = 38,
                Margin = new Thickness(5, 0, 5, 0),
                Style = GlassBoardTheme.ButtonStyle(),
                // 图标按钮必须自己压掉样式里给“文字按钮”的 (16,10,16,10) 内边距：
                // 那套内边距会把 46×38 的钮只剩 14×18 内容区，图标（约 17px）两侧被裁。
                Padding = new Thickness(4, 4, 4, 4),
                VerticalAlignment = VerticalAlignment.Center
            };

            button.Click += (_, _) => click();
            return button;
        }

        /// <summary>
        /// 图标字体系列（Windows 自带的图标库）：Segoe Fluent Icons（Win11）优先，
        /// 其次 Segoe MDL2 Assets（Win10/11 都有）；两个都取不到则返回 null，
        /// 由 Glyph 退回矢量路径。
        ///
        /// 【为什么改用图标字体】原来的图标是几条手写路径，而且用了 Stretch=Uniform ——
        /// 它会把**每条路径各自**缩放到 15×15：播放三角的包围盒与暂停双竖条的包围盒不一样大，
        /// 于是三角被放大、竖条被压细，同一排里看上去“粗细不一”，三角也显得缺角。
        /// 图标字体里的媒体控件在同一张网格上设计，光学配重天然一致
        /// （本机实测墨迹覆盖率 13.4%~14.1%，非常接近）。
        /// </summary>
        private static readonly FontFamily? IconFont = ResolveIconFont();

        private static FontFamily? ResolveIconFont()
        {
            try
            {
                var installed = Fonts.SystemFontFamilies.Select(f => f.Source).ToList();

                foreach (string name in new[] { "Segoe Fluent Icons", "Segoe MDL2 Assets" })
                {
                    if (installed.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase)))
                        return new FontFamily(name);
                }
            }
            catch
            {
                // 枚举字体失败就按“没有图标字体”处理，走矢量兜底
            }

            AppLog.Write("GlassBoard 音乐：未找到 Segoe Fluent Icons / Segoe MDL2 Assets，图标改用矢量兜底");
            return null;
        }

        /// <summary>图标字体里的码位：传送控制（TransportControls）那一组。</summary>
        private static string IconGlyph(string kind) => kind switch
        {
            "previous" => "\uE892",
            "next" => "\uE893",
            "pause" => "\uE769",
            _ => "\uE768",      // play
        };

        /// <summary>按钮图标：优先图标字体；没有图标字体的机器退回矢量路径。</summary>
        private static FrameworkElement Glyph(string kind, double size)
        {
            if (IconFont != null)
            {
                var outline = BuildGlyphGeometry(kind, size);

                if (outline != null)
                {
                    return new System.Windows.Shapes.Path
                    {
                        Data = outline,
                        Fill = GlassBoardTheme.Ink,
                        // 几何已按目标尺寸生成，这里不能再缩放（否则又回到“各自缩放”的老问题）
                        Stretch = Stretch.None,
                        HorizontalAlignment = HorizontalAlignment.Center,
                        VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false
                    };
                }
            }

            string data = kind switch
            {
                "previous" => "M 17,4 L 8,10 L 17,16 Z M 3,4 L 6,4 L 6,16 L 3,16 Z",
                "next" => "M 3,4 L 12,10 L 3,16 Z M 14,4 L 17,4 L 17,16 L 14,16 Z",
                "pause" => "M 5,3 L 8,3 L 8,17 L 5,17 Z M 12,3 L 15,3 L 15,17 L 12,17 Z",
                _ => "M 4,4 L 16,4 L 10,16 Z",
            };

            return new System.Windows.Shapes.Path
            {
                Data = Geometry.Parse(data),
                Fill = GlassBoardTheme.Ink,
                Stretch = Stretch.Uniform,
                Width = size,
                Height = size,
                IsHitTestVisible = false
            };
        }

        /// <summary>
        /// 取图标字形的**矢量轮廓**（而不是直接拿 TextBlock 渲染文字）。
        ///
        /// 为什么要这样：音乐组件是 AllowsTransparency=true 的分层窗口，
        /// WPF 在这种窗口上会关掉 ClearType、退回灰度抗锯齿 —— 小字号下字形发虚，
        /// 这就是用户反馈的“图标分辨率有问题”。
        /// 用 FormattedText.BuildGeometry 把字形取成几何、再用 Path 画，
        /// 就是纯矢量：任何 DPI 下都锐利，同时保留图标字体那套一致的线宽与光学配重。
        ///
        /// 失败返回 null，调用方会退回手写矢量路径。
        /// </summary>
        private static Geometry? BuildGlyphGeometry(string kind, double size)
        {
            try
            {
                var text = new FormattedText(
                    IconGlyph(kind),
                    System.Globalization.CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    new Typeface(IconFont, FontStyles.Normal, FontWeights.Normal, FontStretches.Normal),
                    size + 4.0,
                    Brushes.Black,
                    1.0);

                // 注意：本文件同时 using 了 System.Windows 与 Windows.Foundation（SMTC 需要），
                // Point / Rect 两边都有，省略命名空间会报 CS0104，必须写全名。
                Geometry? outline = text.BuildGeometry(new System.Windows.Point(0.0, 0.0));
                if (outline == null)
                    return null;

                // 字形几何的原点在基线，直接画会上下不齐；按包围盒居中并取整到整像素
                System.Windows.Rect bounds = outline.Bounds;

                // 关键：等比缩到目标框，而不是只平移。
                // 图标字体在 em=size+4 时墨迹约 16.6×16.6，如果只居中、不缩放，
                // 16.6px 的墨迹会溢出目标框（按钮内容区更小）—— 用户反馈的“内容太大显示不全”就是这个。
                double longest = Math.Max(bounds.Width, bounds.Height);
                double fit = longest > 0.5 ? size / longest : 1.0;

                var group = new GeometryGroup();
                group.Children.Add(outline);
                group.Transform = new TransformGroup
                {
                    Children =
                    {
                        new ScaleTransform(fit, fit),
                        new TranslateTransform(
                            Math.Round(-bounds.X * fit + (size - bounds.Width * fit) / 2.0),
                            Math.Round(-bounds.Y * fit + (size - bounds.Height * fit) / 2.0))
                    }
                };
                group.Freeze();
                return group;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>按配置重画（控件开关 / 紧凑模式改了以后由模块调用）。</summary>
        public void ReloadStyle()
        {
            var settings = GlassBoardModule.Settings;

            // 透明分层窗口上不做像素对齐，边缘会落在半个像素上，图标和文字都会发虚
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;

            _card.Padding = settings.MusicCompact
                ? new Thickness(18, 10, 18, 10)
                : new Thickness(16, 14, 16, 14);

            _coverShell.Visibility = settings.MusicCompact ? Visibility.Collapsed : Visibility.Visible;
            _sub.Visibility = settings.MusicCompact ? Visibility.Collapsed : Visibility.Visible;
            _state.Visibility = settings.MusicCompact ? Visibility.Collapsed : Visibility.Visible;
            _controls.Visibility = settings.MusicShowControls && !settings.MusicCompact
                ? Visibility.Visible
                : Visibility.Collapsed;

            // 摾音块只跟“紧凑模式”走：控件可以关，但条子要一直在。
            // （用户反馈“摾音块也没有”的根因之一：它被绑在“显示播放控件”上了）
            _equalizer.Visibility = settings.MusicCompact ? Visibility.Collapsed : Visibility.Visible;

            // 紧凑模式只留一行，这时不折行
            _title.TextWrapping = settings.MusicCompact ? TextWrapping.NoWrap : TextWrapping.Wrap;
            _title.MaxHeight = settings.MusicCompact ? double.PositiveInfinity : 40;
            Width = settings.MusicCompact ? 320 : 428;
            Height = settings.MusicCompact ? 76 : 208;

            if (!settings.MusicCompact)
                _title.FontSize = 15.0;
        }

        private void ShowMenu(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();

            var refresh = new MenuItem { Header = "立即刷新" };
            refresh.Click += (_, _) =>
            {
                // 手动刷新要能重试在线封面，否则同一首歌失败一次就再也不查了
                _onlineCoverKey = string.Empty;
                _coverKey = string.Empty;
                QueueRefresh();
            };
            menu.Items.Add(refresh);

            var controls = new MenuItem
            {
                Header = "显示播放控件",
                IsCheckable = true,
                IsChecked = GlassBoardModule.Settings.MusicShowControls
            };
            controls.Click += (_, _) =>
            {
                GlassBoardModule.Settings.MusicShowControls = controls.IsChecked;
                GlassBoardModule.RefreshMusic();
            };
            menu.Items.Add(controls);

            var compact = new MenuItem
            {
                Header = "紧凑模式",
                IsCheckable = true,
                IsChecked = GlassBoardModule.Settings.MusicCompact
            };
            compact.Click += (_, _) =>
            {
                GlassBoardModule.Settings.MusicCompact = compact.IsChecked;
                GlassBoardModule.RefreshMusic();
            };
            menu.Items.Add(compact);

            menu.Items.Add(new Separator());

            var onlineCover = new MenuItem
            {
                Header = "在线获取封面（按曲名+歌手）",
                IsCheckable = true,
                IsChecked = GlassBoardModule.Settings.MusicOnlineCover,
                ToolTip = "网易云等播放器不向系统注册媒体会话，系统拿不到封面；打开后会联网按曲名+歌手查询封面"
            };
            onlineCover.Click += (_, _) =>
            {
                GlassBoardModule.Settings.MusicOnlineCover = onlineCover.IsChecked;
                _coverKey = string.Empty;
                _onlineCoverKey = string.Empty;
                GlassBoardModule.RefreshMusic();
                QueueRefresh();
            };
            menu.Items.Add(onlineCover);

            menu.Items.Add(new Separator());

            var hide = new MenuItem { Header = "隐藏音乐组件" };
            hide.Click += (_, _) => GlassBoardModule.HideMusic();
            menu.Items.Add(hide);

            // 菜单主题在 Dock 子命名空间下，必须限定名
            Dock.BoardMenuTheme.Apply(menu, 82.0);

            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }

        // ---------------- 位置记忆 ----------------

        protected override void RestorePlacement()
        {
            var s = GlassBoardModule.Settings;
            PlaceOnScreen(s.MusicLeft, s.MusicTop, s.MusicPlaced, 380);
        }

        protected override void PersistPlacement()
            => GlassBoardModule.SaveMusicPlacement(Left, Top);

        // ---------------- 数据 ----------------

        public void QueueRefresh() => _ = RefreshAsync();

        public async Task RefreshAsync()
        {
            if (_busy)
                return;

            _busy = true;

            try
            {
                // 延迟初始化 manager，避免网易云还没注册 SMTC 会话时失败
                if (_manager == null)
                {
                    try
                    {
                        _manager = await GlobalSystemMediaTransportControlsSessionManager.RequestAsync();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"GlassBoard 音乐：初始化 SMTC 管理器失败：{ex.GetType().Name}: {ex.Message}");
                        _title.Text = "音乐读取失败";
                        _sub.Text = "请确保播放器支持系统媒体控制";
                        _failures++;
                        return;
                    }
                }

                var session = CurrentSession();
                if (session == null)
                {
                    // 本机实测：网易云音乐等播放器不一定注册系统媒体会话
                    // （SMTC 会话数 = 0），所以退一步读播放器窗口标题兜底；
                    // 实测网易云的标题就是「曲名 - 歌手」。
                    if (_titleSource.TryRead(out string fallbackTitle, out string fallbackSub))
                    {
                        // 网易云这类播放器不注册 SMTC，没有系统状态可读；
                        // 改用它自己的音频渲染会话是否处于 Active 判断在不在播。
                        // （之前这里写死了“播放中 + 暂停图标”，所以按键永远不随状态变。）
                        // 会话还在 + 声音还没停，才算“播放中”。
                        // （网易云暂停后会话可能几十秒才消失，所以必须叠上“输出静音”这一条。）
                        bool fallbackPlaying = _titleSource.TryReadPlaying() && !OutputSilent;
                        _lastKnownPlaying = fallbackPlaying;

                        // 只在判定结果变化时记一条，方便确认“信号到底拿没拿到”，不刷屏
                        if (_lastFallbackPlaying != fallbackPlaying)
                        {
                            _lastFallbackPlaying = fallbackPlaying;
                            AppLog.Write($"GlassBoard 音乐：兜底播放状态 {fallbackPlaying}｜{_titleSource.LastPlayingDiagnostic}");
                        }

                        _title.Text = fallbackTitle;
                        _sub.Text = fallbackSub;
                        _state.Text = fallbackPlaying ? "播放中" : "已暂停";
                        SetPlayIcon(fallbackPlaying);

                        // 系统没有媒体会话就没有系统封面（网易云就是这种），
                        // 换成「曲名 + 歌手」联网查一次。
                        string trackKey = (fallbackTitle + "|" + fallbackSub).Trim();

                        if (trackKey != _coverKey)
                        {
                            _coverKey = trackKey;
                            _onlineCoverKey = string.Empty;
                            SetCover(null);            // 先把上一首的封面清掉
                            QueueOnlineCover(fallbackTitle, fallbackSub);
                        }
                    }
                    else
                    {
                        _title.Text = "没有正在播放的音乐";
                        _sub.Text = "在任意播放器里播放，就会显示在这里";
                        _state.Text = "已停止";
                        _lastKnownPlaying = false;
                        SetPlayIcon(false);

                        _coverKey = string.Empty;
                        _onlineCoverKey = string.Empty;
                        SetCover(null);
                    }

                    _failures = 0;
                    return;
                }

                var props = await session.TryGetMediaPropertiesAsync();
                var info = session.GetPlaybackInfo();
                bool playing = info.PlaybackStatus == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
                
                AppLog.Write($"GlassBoard 音乐：会话状态={info.PlaybackStatus} 曲名={props.Title}");

                _title.Text = string.IsNullOrWhiteSpace(props.Title) ? "未知曲目" : props.Title;

                string joined = string.Join(" · ", new[] { props.Artist, props.AlbumTitle }
                    .Where(t => !string.IsNullOrWhiteSpace(t)));
                _sub.Text = string.IsNullOrWhiteSpace(joined) ? "—" : joined;

                // 再宽也可能放不下超长曲名：悬停卡片看完整信息（用玻璃底色，不是系统小白框）
                _card.ToolTip = new ToolTip
                {
                    Content = _title.Text + "\n" + _sub.Text,
                    Background = GlassBoardTheme.SurfaceFill(96),
                    Foreground = GlassBoardTheme.Ink,
                    BorderBrush = GlassBoardTheme.Line,
                    Padding = new Thickness(12, 8, 12, 8)
                };

                // 三种状态给三种图标：播放中=暂停条、暂停=正三角、停止=倒三角
                var playback = info.PlaybackStatus;

                _state.Text = playing
                    ? "播放中"
                    : (playback == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Stopped ? "已停止" : "已暂停");
                _lastKnownPlaying = playing;
                SetPlayIcon(playing);
                _failures = 0;

                // 拾音块由定时器按“系统输出真实电平”统一驱动，这里不用再开关它：
                // 暂停 / 静音时电平自然归零，条子会自己落下去。

                await LoadCoverAsync(props);
            }
            catch (Exception ex)
            {
                _title.Text = "音乐读取失败";
                _sub.Text = ex.Message;
                _state.Text = string.Empty;

                _failures++;
                if (_failures == 1 || _failures % 30 == 0)
                {
                    AppLog.Write($"GlassBoard 音乐：读取失败（第 {_failures} 次）：{ex.GetType().Name}: {ex.Message}");
                }
            }
            finally
            {
                _busy = false;
            }
        }

        private GlobalSystemMediaTransportControlsSession? CurrentSession()
        {
            try
            {
                if (_manager == null)
                    return null;

                var sessions = _manager.GetSessions();
                if (sessions == null || sessions.Count == 0)
                    return null;

                // 优先找播放中的会话
                foreach (var session in sessions)
                {
                    if (session.GetPlaybackInfo().PlaybackStatus
                        == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing)
                    {
                        return session;
                    }
                }

                // 没有播放中的，返回第一个会话（可能是暂停状态）
                return sessions[0];
            }
            catch
            {
                return null;
            }
        }

        /// <summary>
        /// 拾音块：由系统输出的**真实峰值电平**驱动（IAudioMeterInformation）。
        /// 每根条子给一点不同权重，看起来像频谱；用“快起慢落”做跳动感。
        /// </summary>
        private void UpdateEqualizer()
        {
            if (!_meter.Ready && !_meter.Open())
                return;

            float peak = _meter.Peak();

            // 用“声音真的断了”作为暂停的快信号：
            // 网易云暂停后音频会话还会挂一阵子才消亡，只看会话就得多等十几秒。
            _silentSamples = peak < SilentPeak
                ? Math.Min(_silentSamples + 1, SilentSamplesForPause + 8)
                : 0;

            int last = Math.Max(1, _bars.Length - 1);

            for (int i = 0; i < _bars.Length; i++)
            {
                // 低频高、高频低，再叠一层固定起伏 —— 看起来更像频谱而不是随机柱子
                double band = 1.0 - 0.55 * ((double)i / last);
                double ripple = 0.72 + 0.28 * Math.Abs(Math.Sin(i * 1.13));
                double target = Math.Clamp(peak * 2.6 * band * ripple, 0.0, 1.0);

                _barLevel[i] = target > _barLevel[i]
                    ? target
                    : Math.Max(target, _barLevel[i] - 0.12);

                var bar = _bars[i];
                bar.Height = EqIdleHeight + _barLevel[i] * (EqMaxHeight - EqIdleHeight);
                bar.Opacity = 0.30 + _barLevel[i] * 0.70;
            }
        }

        // ---------- 播放控制通道 ----------
        //
        // 网易云不注册系统媒体会话（SMTC），本机日志里“会话状态”一行都没有 ——
        // 也就是系统媒体键根本没有接收者，所以组件原来那套 MediaKeys.Press 对它是石沉大海，
        // 用户看到的就是“点了按钮没反应”。
        //
        // 实测有效的通道：网易云自己向系统注册了全局快捷键
        // （Ctrl+Alt+P=播放/暂停、Ctrl+Alt+←/→=上一首/下一首、↑/↓=音量、M=静音），
        // 这类热键是系统级注册的，**不需要窗口焦点、不依赖 SMTC**。
        // 验证方式：发送“上一首”后歌名精确回跳到上一首、再发“下一首”又精确前进回来
        // （自然播放不可能往回跳，因此可排除“恰好播完切歌”的巧合）。
        private const byte HotkeyPlayPause = 0x50;   // Ctrl+Alt+P
        private const byte HotkeyPrevious = 0x25;    // Ctrl+Alt+←
        private const byte HotkeyNext = 0x27;        // Ctrl+Alt+→

        private const byte VkControl = 0x11;
        private const byte VkAlt = 0x12;
        private const uint KeyUpFlag = 0x0002;

        [System.Runtime.InteropServices.DllImport("user32.dll")]
        private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, UIntPtr dwExtraInfo);

        /// <summary>
        /// 发送 Ctrl+Alt+&lt;vk&gt;。
        ///
        /// 每个按键之间必须留间隔：实测零间隔连发时，目标应用还没看到 Ctrl/Alt 按下
        /// 就收到了主键，热键不会触发（本机对比验证过这个差异）。
        /// </summary>
        private static void SendPlayerHotkey(byte vk)
        {
            try
            {
                keybd_event(VkControl, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(45);
                keybd_event(VkAlt, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(45);
                keybd_event(vk, 0, 0, UIntPtr.Zero);
                System.Threading.Thread.Sleep(45);
                keybd_event(vk, 0, KeyUpFlag, UIntPtr.Zero);
                System.Threading.Thread.Sleep(45);
                keybd_event(VkAlt, 0, KeyUpFlag, UIntPtr.Zero);
                System.Threading.Thread.Sleep(45);
                keybd_event(VkControl, 0, KeyUpFlag, UIntPtr.Zero);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 音乐：发送控制热键失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>本机有没有在跑网易云（只有它需要走全局热键这条路）。</summary>
        private static bool HasCloudMusic()
        {
            try
            {
                return System.Diagnostics.Process.GetProcessesByName("cloudmusic").Length > 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 播放控制：优先用系统媒体会话；没有会话（网易云就是这种）就走它自己的全局热键。
        /// 只有网易云才用热键——其它播放器（Spotify 等）本来就有 SMTC 会话，走上面的分支。
        /// </summary>
        private void Send(Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> action, ushort fallbackKey, byte hotkeyVk)
        {
            var session = CurrentSession();
            if (session == null)
            {
                if (HasCloudMusic())
                    SendPlayerHotkey(hotkeyVk);
                else
                    MediaKeys.Press(fallbackKey);

                // 播放器响应有延迟：点完立刻回读往往还是旧状态，用快轮询追一段时间
                StartFastPoll();
                QueueRefresh();
                return;
            }

            _ = DispatchAsync(session, action);
            StartFastPoll();
            QueueRefresh();
        }

        private void Previous() => Send(s => s.TrySkipPreviousAsync(), MediaKeys.PreviousTrack, HotkeyPrevious);

        private void PlayPause()
        {
            // 点下去立刻把图标翻到目标状态，并在接下来 2.5 秒内**不允许轮询把它覆写回去**：
            // 播放器执行需要时间，点完立刻回读拿到的是旧状态，会把乐观翻转冲掉。
            _lastKnownPlaying = !_lastKnownPlaying;
            _optimisticUntilUtc = DateTime.UtcNow.AddSeconds(2.5);
            SetPlayIcon(_lastKnownPlaying, force: true);

            Send(s => s.TryTogglePlayPauseAsync(), MediaKeys.PlayPause, HotkeyPlayPause);
        }

        private void Next() => Send(s => s.TrySkipNextAsync(), MediaKeys.NextTrack, HotkeyNext);

        private static async Task DispatchAsync(
            GlobalSystemMediaTransportControlsSession session,
            Func<GlobalSystemMediaTransportControlsSession, IAsyncOperation<bool>> action)
        {
            try
            {
                await action(session);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 音乐：下发控制失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 系统不给封面时，用「曲名 + 歌手」去在线查一次。
        /// 同一首歌只查一次；网络请求在后台线程，查完切回 UI 线程贴图。
        /// </summary>
        private void QueueOnlineCover(string title, string artist)
        {
            if (!GlassBoardModule.Settings.MusicOnlineCover)
                return;

            if (string.IsNullOrWhiteSpace(title))
                return;

            string key = (title + "|" + artist).Trim();

            if (key == _onlineCoverKey)
                return;

            _onlineCoverKey = key;

            _ = Task.Run(async () =>
            {
                var image = await OnlineCoverSource.FindAsync(title, artist);

                if (image == null)
                    return;

                try
                {
                    Dispatcher.Invoke(() =>
                    {
                        // 期间没切歌才贴上去
                        if (_onlineCoverKey == key)
                            SetCover(image);
                    });
                }
                catch
                {
                    // 窗口已经关了就算了
                }
            });
        }

        /// <summary>设置封面（null = 回到占位音符）。</summary>
        private void SetCover(ImageSource? source)
        {
            _cover.Source = source;
            _coverPlaceholder.Visibility = source == null ? Visibility.Visible : Visibility.Collapsed;
        }

        /// <summary>封面是锦上添花：失败只回到占位音符，不影响播放信息。</summary>
        private async Task LoadCoverAsync(GlobalSystemMediaTransportControlsSessionMediaProperties props)
        {
            try
            {
                string key = (props.Title ?? string.Empty) + "|" + (props.Artist ?? string.Empty);
                if (key == _coverKey)
                    return;

                var reference = props.Thumbnail;

                if (reference == null)
                {
                    // 系统没给封面：清掉上一首的，再按「曲名 + 歌手」在线查一次
                    _coverKey = key;
                    SetCover(null);
                    QueueOnlineCover(props.Title ?? string.Empty, props.Artist ?? string.Empty);
                    return;
                }

                using IRandomAccessStreamWithContentType stream = await reference.OpenReadAsync();

                // 不要相信 stream.Size：部分播放器给的缩略图流 Size 是 0 或不准，
                // 之前按 Size 一次读完，遇到这种就直接 return —— 表现就是“封面一直不显示”。
                // 这里改成按块读到读空为止。
                var buffer = new System.IO.MemoryStream();
                var block = new byte[16 * 1024];

                using (var reader = new DataReader(stream.GetInputStreamAt(0)))
                {
                    reader.InputStreamOptions = InputStreamOptions.Partial;

                    while (true)
                    {
                        uint read = await reader.LoadAsync((uint)block.Length);
                        if (read == 0)
                            break;

                        var chunk = new byte[read];
                        reader.ReadBytes(chunk);
                        buffer.Write(chunk, 0, (int)read);
                    }
                }

                if (buffer.Length <= 0)
                {
                    SetCover(null);
                    return;
                }

                buffer.Position = 0;

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = buffer;
                image.EndInit();
                image.Freeze();

                SetCover(image);
                _coverKey = key;   // 只有真正成功才记 key，失败下次还能重试
            }
            catch (Exception ex)
            {
                SetCover(null);
                AppLog.Write($"GlassBoard 音乐：读取封面失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>设置界面的「播放器状态」：读不到音乐时用来定位是哪个环节。</summary>
        public string Diagnostics()
        {
            try
            {
                if (_manager == null)
                    return "尚未连上系统媒体会话（组件刚启动，或取数失败）";

                var sessions = _manager.GetSessions();
                if (sessions == null || sessions.Count == 0)
                    return "系统里没有媒体会话：先打开播放器并播放一首歌";

                var report = new StringBuilder();
                report.Append("媒体会话 ").Append(sessions.Count).Append(" 个");

                var session = CurrentSession();
                if (session != null)
                {
                    report.Append("；当前：").Append(session.SourceAppUserModelId);
                    report.Append("；状态：").Append(session.GetPlaybackInfo().PlaybackStatus);
                }

                return report.ToString();
            }
            catch (Exception ex)
            {
                return "读取媒体会话失败：" + ex.GetType().Name + ": " + ex.Message;
            }
        }
    }
}
