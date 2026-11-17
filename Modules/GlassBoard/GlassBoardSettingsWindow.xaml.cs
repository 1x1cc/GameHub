using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// GlassBoard 设置（外观 / 组件 / 净化 / 维护），外观照逆解出来的原版玻璃配方。
    ///
    /// 与 GameHub 的分工（用户明确要求）：
    ///   · GameHub 第 6 卡片只是一个总开关，不放「打开」按钮；
    ///   · 真正的设置入口是桌面 Dock 上的 ⚙；
    ///   · 本窗口是独立的玻璃窗口，打开时不会牵动 GameHub 主窗口。
    ///
    /// 页面内容由代码生成（原版也是这个思路）：每次操作后整体重建，
    /// 按钮文字就是当前状态，不用再去别处找开关。
    /// </summary>
    public partial class GlassBoardSettingsWindow : Window
    {
        private Dock.DockModel _model;

        public GlassBoardSettingsWindow()
        {
            InitializeComponent();

            _model = Dock.DockStore.Load();

            ApplyGlassChrome();

            AppLog.Write("GlassBoard 设置：窗口已创建（GlassBoard 玻璃主题）");

            // 页面初始化失败也要让窗口开起来，并把原因写到状态栏和日志，
            // 否则用户只会看到“点了没反应”，无从排查。
            try
            {
                RefreshPages();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 设置：页面初始化失败：{ex.GetType().Name}: {ex.Message}");
                StatusHint.Text = "页面初始化失败：" + ex.Message;
            }
        }

        // ---------------- 窗口外观（原版玻璃风） ----------------

        private void ApplyGlassChrome()
        {
            GlassBoardTheme.Apply(this);

            Surface.Background = GlassBoardTheme.SurfaceFill(_model.Dock.OpacityPercent);
            Surface.BorderBrush = GlassBoardTheme.Line;

            TitleText.Foreground = GlassBoardTheme.Ink;
            TitleHint.Foreground = GlassBoardTheme.Muted;
            StatusHint.Foreground = GlassBoardTheme.Accent;
        }

        /// <summary>无边框窗口：按住标题栏拖动。</summary>
        private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            try
            {
                DragMove();
            }
            catch
            {
                // 没有按下左键时 DragMove 会抛异常，忽略即可
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>
        /// 设置窗口就是一个普通窗口：不置顶、失焦也不抢回焦点。
        /// 用户要求“不要挡住其他软件”——之前是“激活时临时置顶 + 失焦时又把 Dock 激活”，
        /// 结果你一点别的软件，它就把焦点抢回去。
        /// </summary>
        private void Window_Activated(object sender, EventArgs e)
        {
            Topmost = false;
        }

        /// <summary>失焦：什么都不做，窗口正常沉到其他软件后面。</summary>
        private void Window_Deactivated(object sender, EventArgs e)
        {
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (e.Key == Key.Escape)
            {
                Close();
                e.Handled = true;
            }
        }

        // ---------------- 通用构件 ----------------

        /// <summary>
        /// 按原版 Polish.Section 的版式加一节卡片，返回用来放按钮的容器。
        /// 容器是 UniformGrid：按钮**等宽铺满整卡宽度**（用户要求「气泡长度优先填满一整个卡片」），
        /// 列数在 RefreshPages() 里按按钮数量设成 Min(4, 数量)，放不下就换行、换行后左对齐。
        /// 传入 switchValue / switchChanged 时，说明下方会多一行
        /// 「圆形滑块开关 + 状态文字」——就是主界面卡片上那个 ToggleSlider。
        /// </summary>
        private Panel AddSection(Panel page, string title, string description,
            bool? switchValue = null, Action<bool>? switchChanged = null)
        {
            var card = GlassBoardTheme.Section(title, description);
            page.Children.Add(card);

            if (card.Child is not StackPanel stack)
                return new System.Windows.Controls.Primitives.UniformGrid();

            if (switchValue.HasValue && switchChanged != null)
            {
                var row = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 0, 0, 8)   // 2026-11-15 收紧留白（原 12）
                };

                var slider = new GameHub.Controls.ToggleSlider
                {
                    IsChecked = switchValue.Value,
                    Margin = new Thickness(0, 0, 10, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };

                var state = new TextBlock
                {
                    Foreground = GlassBoardTheme.Muted,
                    FontSize = 12.0,
                    VerticalAlignment = VerticalAlignment.Center
                };

                void Sync() => state.Text = slider.IsChecked == true ? "已开启" : "已关闭";

                Sync();

                slider.Checked += (_, _) =>
                {
                    Sync();
                    RunSwitch(switchChanged, true);
                };

                slider.Unchecked += (_, _) =>
                {
                    Sync();
                    RunSwitch(switchChanged, false);
                };

                row.Children.Add(slider);
                row.Children.Add(state);
                stack.Children.Add(row);
            }

            // 按钮宿主：等宽列 + 左对齐。用户要求「气泡长度优先填满一整个卡片的长度」，
            // 所以不能用 WrapPanel（它按文字自适应宽度，右边会参差不齐）。
            // 列数先给 4，RefreshPages() 会改成 Min(4, 按钮数) —— 只有 2 个按钮的卡就用 2 列，
            // 每列占一半，那一行同样正好铺满；放不下的按钮换行后从左边第一个格开始。
            var host = new System.Windows.Controls.Primitives.UniformGrid
            {
                Columns = 4,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                Margin = new Thickness(0)
            };

            stack.Children.Add(host);
            return host;
        }

        /// <summary>滑块回调统一兜底：出错只提示，不让设置窗口崩掉。</summary>
        private void RunSwitch(Action<bool> changed, bool value)
        {
            try
            {
                changed(value);
            }
            catch (Exception ex)
            {
                Hint($"操作失败：{ex.Message}");
                AppLog.Write($"GlassBoard 设置：开关失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Add(Panel host, string text, Action action, bool danger = false)
        {
            // 2026-11-15 用户反馈「气泡里的文字没显示完全」：
            // 等宽列把每个按钮压到 ~61px，长标签（如「〇 显示名称」「● 锁定位置」）
            // 实测约 78px，超出按钮宽度就被裁掉 —— 原来 Content 是纯字符串，既不换行也不省略。
            // 改成**可换行的 TextBlock**：宽度不够就折成两行，永不裁字；
            // 列数另有 TuneButtonHost() 按实测文字宽度反推，尽量避免换行。
            var button = new Button
            {
                Content = new TextBlock
                {
                    Text = text,
                    TextWrapping = TextWrapping.Wrap,
                    TextAlignment = TextAlignment.Center
                },
                Style = danger ? GlassBoardTheme.DangerButtonStyle() : GlassBoardTheme.ButtonStyle(),
                // 2026-11-15 用户要求：按钮留白别太大、间距 4px。
                // 主题里的内边距(16,10)与最小高度(38)太大，这里按实例覆盖掉。
                Padding = new Thickness(7, 3, 7, 3),
                MinHeight = 24,
                FontSize = 11.0,
                MinWidth = 0,
                Margin = new Thickness(0, 0, 4, 4)
            };

            button.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    Hint($"操作失败：{ex.Message}");
                    AppLog.Write($"GlassBoard 设置：操作失败：{ex.GetType().Name}: {ex.Message}");
                }
            };

            host.Children.Add(button);
        }

        private void Hint(string text) => StatusHint.Text = text;

        /// <summary>状态圆点：实心=当前生效，空心=当前未生效（点一下切换）。</summary>
        private static string Mark(bool active) => active ? "● " : "○ ";

        /// <summary>配置改完：落盘 + 让已打开的 Dock / 大面板重建一次，立刻生效。</summary>
        private void SaveModel()
        {
            Dock.DockStore.Save(_model);
            GlassBoardModule.ApplyDockAndBoardSettings();
        }

        private void RefreshPages()
        {
            foreach (var page in new Panel[] { BoardPage, DockPage, WidgetPage, OrbPage, CleanPage, GeneralPage, FloatingPage })
                page.Children.Clear();

            _model = Dock.DockStore.Load();

            BuildBoardPage();
            BuildDockPage();
            BuildWidgetPage();
            BuildOrbPage();
            BuildCleanPage();
            BuildGeneralPage();

            // 2026-11-15 用户要求：“悬浮窗”不再各占一张卡片 —— 四个组件的悬浮窗设置
            // 已并入各自的组件卡片（见 AddFloatingBlock），所以不再调 BuildFloatingPage()。
            // 该方法保留着（未被调用），将来要恢复独立卡片只需删掉这行注释、加回调用即可。

            // 2026-11-15 用户要求（本轮排版规则）：
            // ① 第一行固定为 总开关 / 逐项控制 / 兜底恢复 / 配置与维护 / 行为，其余卡片保持原顺序；
            // ② 按钮宿主列数 = Min(4, 按钮数) —— 每行铺满卡片宽度，放不下换行后左对齐；
            // ③ **每行**以该行内容最高的卡片定高（其余 4 张拉伸到同高），行与行之间留白 8px。
            //    所以不能用 UniformGrid：它只能让**全表**等高，做不到「每行各自定高」。
            // 注：这里用完全限定名 —— 本文件没有 using System.Collections.Generic。
            var cards = new System.Collections.Generic.List<Border>();

            foreach (var page in new Panel[] { BoardPage, DockPage, WidgetPage, OrbPage, CleanPage, GeneralPage, FloatingPage })
            {
                foreach (UIElement child in page.Children)
                {
                    if (child is Border card)
                    {
                        TuneButtonHost(card);   // ② 按钮列数按数量定，保证每行都铺满
                        cards.Add(card);
                    }
                }

                page.Children.Clear();
            }

            // ① 用户指定的五张卡先按给定顺序排到最前，其余卡片顺序不变
            var ordered = new System.Collections.Generic.List<Border>();
            var first = new System.Collections.Generic.List<Border>();

            foreach (string want in FirstRowTitles)
            {
                int at = cards.FindIndex(c => string.Equals(CardTitle(c), want, StringComparison.Ordinal));

                if (at >= 0)
                {
                    first.Add(cards[at]);
                    cards.RemoveAt(at);
                }
            }

            ordered.AddRange(first);
            ordered.AddRange(cards);

            // ③ 每 5 张一行：每行一个 5 列等宽 Grid —— 行高取该行最高的卡片，
            // 同一行其余卡片因为 VerticalAlignment=Stretch 自动撑到同高（"依次类推"）。
            RowHost.Children.Clear();

            for (int start = 0; start < ordered.Count; start += Columns)
            {
                var row = new Grid { HorizontalAlignment = HorizontalAlignment.Stretch };

                for (int c = 0; c < Columns; c++)
                    row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

                for (int c = 0; c < Columns && start + c < ordered.Count; c++)
                {
                    Border card = ordered[start + c];
                    Grid.SetColumn(card, c);
                    row.Children.Add(card);
                }

                RowHost.Children.Add(row);
            }
        }

        /// <summary>第一行固定的五张卡片（用户 2026-11-15 指定），其余卡片保持原顺序排在后面。</summary>
        private static readonly string[] FirstRowTitles = { "总开关", "逐项控制", "兜底恢复", "配置与维护", "行为" };

        /// <summary>一行放几张卡片。</summary>
        private const int Columns = 5;

        /// <summary>取卡片标题：Section() 生成的卡片内部第一个元素就是标题 TextBlock。</summary>
        private static string CardTitle(Border card)
            => card.Child is StackPanel stack && stack.Children.Count > 0 && stack.Children[0] is TextBlock title
                ? title.Text
                : string.Empty;

        /// <summary>
        /// 按**实测文字宽度**决定按钮宿主的列数（2026-11-15 用户反馈文字被裁之后改成这样）：
        /// 先用 TextBlock 量出该卡最长标签要占多宽，再看一格能不能放下 —— 放不下就减少列数
        /// （4 → 3 → 2 → 1），保证每行仍然铺满卡宽、且不裁字；万一还长就靠 TextBlock 换行兜底。
        /// 这样「Dock 外观」（5 个按钮、标签带 ●/○ 前缀）会自动降列，而不是把字裁掉。
        /// </summary>
        private static void TuneButtonHost(Border card)
        {
            if (card.Child is not StackPanel stack)
                return;

            foreach (UIElement child in stack.Children)
            {
                if (child is not System.Windows.Controls.Primitives.UniformGrid host || host.Children.Count == 0)
                    continue;

                double widest = 0;

                foreach (UIElement item in host.Children)
                {
                    string label = LabelOf(item);
                    if (label.Length == 0)
                        continue;

                    var probe = new TextBlock { Text = label, FontSize = 11.0, FontWeight = FontWeights.Medium };
                    probe.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
                    widest = Math.Max(widest, probe.DesiredSize.Width);
                }

                // 每格需要放下：最宽标签 + 左右内边距(7+7) + 列间距 4
                double need = widest + 18;
                int fit = need <= 1 ? 4 : (int)Math.Floor(CardContentWidth / need);

                host.Columns = Math.Clamp(Math.Min(fit, host.Children.Count), 1, 4);
            }
        }

        /// <summary>取按钮上的文字（Content 现在是可换行的 TextBlock；兼容旧的字面量 Content）。</summary>
        private static string LabelOf(UIElement item)
            => item is Button button
                ? button.Content switch
                {
                    TextBlock tb => tb.Text ?? string.Empty,
                    string s => s,
                    _ => string.Empty
                }
                : string.Empty;

        /// <summary>
        /// 卡片内可放按钮的宽度：窗口 1400 − 外层 28 − 内容 8 − 滚动条 16 = 1348，
        /// 除以 5 列 = 269.6，再减卡片左右 Margin 4 与内边距 20 → ≈ 245。
        /// 窗口是固定尺寸（ResizeMode=NoResize），所以这个常量可靠；
        /// 以后如果改窗口宽度，把这个值同步改一下即可。
        /// </summary>
        private const double CardContentWidth = 245.0;

        // ---------------- 应用大面板 ----------------

        private void BuildBoardPage()
        {
            var look = AddSection(BoardPage, "面板显示",
                "Dock 上那个图标按钮打开的就是它。",
                GlassBoardModule.BoardVisible,
                on => { if (on) GlassBoardModule.ShowBoard(); else GlassBoardModule.HideBoard(); RefreshPages(); });

            Add(look, Mark(!_model.BoardHideSearch) + "搜索栏", () =>
            {
                _model.BoardHideSearch = !_model.BoardHideSearch;
                SaveModel();
                RefreshPages();
            });

            Add(look, Mark(!_model.BoardHideGroups) + "分组栏", () =>
            {
                _model.BoardHideGroups = !_model.BoardHideGroups;
                SaveModel();
                RefreshPages();
            });

            Add(look, Mark(_model.Dock.ShowRunning) + "运行点", () =>
            {
                _model.Dock.ShowRunning = !_model.Dock.ShowRunning;
                SaveModel();
                RefreshPages();
            });

            var entries = AddSection(BoardPage, "入口与分组",
                "首次运行自动扫描开始菜单；装了新软件点一次补进来。");

            Add(entries, "导入开始菜单", () =>
            {
                int added = GlassBoardModule.ImportStartMenuApps();
                RefreshPages();
                Hint(added > 0 ? $"已新增 {added} 个入口（已按关键词自动分组）" : "没有新入口可导入");
            });

            Add(entries, "入口统计", () =>
                Hint($"共 {_model.Apps.Count} 个入口 · 已分组 {_model.Apps.Count(a => !string.IsNullOrWhiteSpace(a.Group))} 个 · 分组：{string.Join(" / ", _model.Groups)}"));
        }

        // ---------------- 玻璃 Dock ----------------

        private void BuildDockPage()
        {
            var dock = _model.Dock;

            var look = AddSection(DockPage, "Dock 外观",
                "图标大小三档；悬浮放大与运行点在这里控制。");

            Add(look, Mark(dock.IconSize <= 38) + "小 32", () => SetIconSize(32));
            Add(look, Mark(dock.IconSize > 38 && dock.IconSize <= 50) + "中 44", () => SetIconSize(44));
            Add(look, Mark(dock.IconSize > 50) + "大 56", () => SetIconSize(56));

            Add(look, Mark(dock.ShowLabels) + "显示名称", () =>
            {
                dock.ShowLabels = !dock.ShowLabels;
                SaveModel();
                RefreshPages();
            });

            Add(look, Mark(dock.ShowSeparator) + "分隔线", () =>
            {
                dock.ShowSeparator = !dock.ShowSeparator;
                SaveModel();
                RefreshPages();
            });

            var behavior = AddSection(DockPage, "位置与行为",
                "改对齐会回到自动摆放；手动拖过位置后以拖动结果为准。");

            Add(behavior, Mark(dock.Alignment == 0) + "靠左", () => SetAlignment(0));
            Add(behavior, Mark(dock.Alignment == 1) + "居中", () => SetAlignment(1));
            Add(behavior, Mark(dock.Alignment == 2) + "靠右", () => SetAlignment(2));

            Add(behavior, Mark(dock.AutoHide) + "自动隐藏", () =>
            {
                dock.AutoHide = !dock.AutoHide;
                SaveModel();
                RefreshPages();
            });

            Add(behavior, Mark(dock.AlwaysOnTop) + "总在最前", () =>
            {
                dock.AlwaysOnTop = !dock.AlwaysOnTop;
                SaveModel();
                RefreshPages();
            });

            var content = AddSection(DockPage, "内容",
                "选中的软件会固定到 Dock。",
                GlassBoardModule.DockVisible,
                on => { if (on) GlassBoardModule.ShowDock(); else GlassBoardModule.HideDock(); RefreshPages(); });

            Add(content, "添加软件", () =>
            {
                if (!GlassBoardModule.DockVisible)
                    GlassBoardModule.ShowDock();

                Hint(GlassBoardModule.AddDockFiles() ? "已弹出选择窗口，选中的软件会固定到 Dock" : "Dock 未就绪，请稍后再试");
            });

            Add(content, "回到默认位置", () =>
            {
                _model.DockPlaced = false;
                SaveModel();
                RefreshPages();
                Hint("Dock 已回到按对齐方式自动摆放的位置");
            });
        }

        private void SetIconSize(double size)
        {
            _model.Dock.IconSize = size;
            SaveModel();
            RefreshPages();
        }

        private void SetAlignment(int alignment)
        {
            _model.Dock.Alignment = alignment;
            _model.DockPlaced = false;
            SaveModel();
            RefreshPages();
        }

        // ---------------- 桌面组件 ----------------

        private void BuildWidgetPage()
        {
            var settings = GlassBoardModule.Settings;

            var clock = AddSection(WidgetPage, "桌面时钟",
                "拖动换位；右键改样式。",
                GlassBoardModule.ClockVisible,
                on => { if (on) GlassBoardModule.ShowClock(); else GlassBoardModule.HideClock(); RefreshPages(); });

            Add(clock, Mark(settings.ShowDate) + "日期", () =>
            {
                settings.ShowDate = !settings.ShowDate;
                GlassBoardModule.RefreshClockStyle();
                RefreshPages();
            });

            Add(clock, Mark(settings.Hour24) + "24 小时", () =>
            {
                settings.Hour24 = !settings.Hour24;
                GlassBoardModule.RefreshClockStyle();
                RefreshPages();
            });

            Add(clock, Mark(settings.Frameless) + "无框", () =>
            {
                settings.Frameless = !settings.Frameless;
                GlassBoardModule.RefreshClockStyle();
                RefreshPages();
            });

            AddFloatingBlock(clock, settings.ClockFloating);          // 时钟的悬浮窗设置并入本卡

            var notes = AddSection(WidgetPage, "桌面备忘录",
                "停手约 0.65 秒自动保存。",
                GlassBoardModule.NotesVisible,
                on => { if (on) GlassBoardModule.ShowNotes(); else GlassBoardModule.HideNotes(); RefreshPages(); });

            Add(notes, "立即保存", () =>
                Hint(GlassBoardModule.FlushNotes() ? "已写入待保存内容" : "备忘录未打开"));

            Add(notes, "导出 TXT", ExportNotes);

            AddFloatingBlock(notes, settings.NotesFloating);          // 备忘录的悬浮窗设置并入本卡

            var weather = AddSection(WidgetPage, "桌面天气",
                "Open-Meteo，每 15 分钟刷新。",
                GlassBoardModule.WeatherVisible,
                on => { if (on) GlassBoardModule.ShowWeather(); else GlassBoardModule.HideWeather(); RefreshPages(); });

            Add(weather, Mark(GlassBoardModule.Settings.WeatherShowHumidity) + "湿度", () =>
            {
                GlassBoardModule.Settings.WeatherShowHumidity = !GlassBoardModule.Settings.WeatherShowHumidity;
                GlassBoardModule.RefreshWeather();
                RefreshPages();
            });

            Add(weather, "刷新", () =>
            {
                GlassBoardModule.RefreshWeather();
                RefreshPages();
                Hint("已请求刷新天气数据");
            });

            AddFloatingBlock(weather, settings.WeatherFloating);      // 天气的悬浮窗设置并入本卡

            AddCityCard(WidgetPage);

            var music = AddSection(WidgetPage, "桌面音乐",
                "读系统「正在播放」，每 2 秒刷新。",
                GlassBoardModule.MusicVisible,
                on => { if (on) GlassBoardModule.ShowMusic(); else GlassBoardModule.HideMusic(); RefreshPages(); });

            Add(music, Mark(GlassBoardModule.Settings.MusicShowControls) + "播放控件", () =>
            {
                GlassBoardModule.Settings.MusicShowControls = !GlassBoardModule.Settings.MusicShowControls;
                GlassBoardModule.RefreshMusic();
                RefreshPages();
            });

            Add(music, Mark(GlassBoardModule.Settings.MusicCompact) + "紧凑模式", () =>
            {
                GlassBoardModule.Settings.MusicCompact = !GlassBoardModule.Settings.MusicCompact;
                GlassBoardModule.RefreshMusic();
                RefreshPages();
            });

            Add(music, "播放器状态", () => Hint(GlassBoardModule.MusicDiagnostics()));

            AddFloatingBlock(music, settings.MusicFloating);          // 音乐的悬浮窗设置并入本卡
        }

        /// <summary>
        /// 城市输入卡片：天气组件按城市名反查坐标。
        /// 输入框用 GlassBoardTheme 的玻璃样式（原版 Polish.Apply 里也有 TextBox 样式）。
        /// </summary>
        private void AddCityCard(Panel page)
        {
            var settings = GlassBoardModule.Settings;
            var card = GlassBoardTheme.Section("城市", "留空用默认城市（北京）。中文或英文都行：上海 / 深圳 / Tokyo。");

            if (card.Child is not StackPanel stack)
                return;

            // 2026-11-15：一行 5 张后卡片只有 ~200px 宽，原来写死 220 的输入框 + 120 的按钮
            // 会被裁掉（用户反馈“输城市的气泡没办法完全显示”）→ 输入框占满卡片宽、按钮换行居中。
            var col = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var box = new TextBox
            {
                Text = settings.WeatherCity ?? string.Empty,
                Style = GlassBoardTheme.TextBoxStyle(),
                HorizontalAlignment = HorizontalAlignment.Stretch,
                MinWidth = 0
            };

            var save = new Button
            {
                Content = "保存城市",
                Style = GlassBoardTheme.ButtonStyle(),
                MinWidth = 96,
                Margin = new Thickness(0, 6, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Center
            };

            save.Click += (_, _) =>
            {
                try
                {
                    settings.WeatherCity = (box.Text ?? string.Empty).Trim();
                    GlassBoardStore.Save(settings);
                    GlassBoardModule.RefreshWeather();

                    Hint(string.IsNullOrWhiteSpace(settings.WeatherCity)
                        ? "城市已清空，改用默认城市（北京）"
                        : "城市已保存：" + settings.WeatherCity);
                }
                catch (Exception ex)
                {
                    Hint("保存城市失败：" + ex.Message);
                }
            };

            col.Children.Add(box);
            col.Children.Add(save);
            stack.Children.Add(col);
            page.Children.Add(card);
        }

        // ---------------- 悬浮窗 ----------------

        /// <summary>
        /// 「悬浮窗」栏：四个桌面组件各自一张卡片。
        /// 显示沿用各组件已有的 XxxVisible；锁定移动 / 始终置顶 / 鼠标穿透 / 不透明度
        /// 写进对应的 FloatingOptions。
        /// 组件侧每 1.5 秒自检时会把选项重新套一遍，所以改完最多 1.5 秒生效，不用重启。
        /// </summary>
        private void BuildFloatingPage()
        {
            var settings = GlassBoardModule.Settings;

            AddFloatingSection("时钟", "桌面时钟组件。锁定移动后鼠标拖不动；始终置顶会盖在普通窗口之上；鼠标穿透则点击直接穿过。",
                settings.ClockVisible,
                on => { if (on) GlassBoardModule.ShowClock(); else GlassBoardModule.HideClock(); RefreshPages(); },
                settings.ClockFloating);

            AddFloatingSection("天气", "桌面天气组件。",
                settings.WeatherVisible,
                on => { if (on) GlassBoardModule.ShowWeather(); else GlassBoardModule.HideWeather(); RefreshPages(); },
                settings.WeatherFloating);

            AddFloatingSection("音乐", "桌面音乐组件。",
                settings.MusicVisible,
                on => { if (on) GlassBoardModule.ShowMusic(); else GlassBoardModule.HideMusic(); RefreshPages(); },
                settings.MusicFloating);

            AddFloatingSection("备忘录", "桌面备忘录组件。",
                settings.NotesVisible,
                on => { if (on) GlassBoardModule.ShowNotes(); else GlassBoardModule.HideNotes(); RefreshPages(); },
                settings.NotesFloating);
        }

        /// <summary>一个组件的悬浮窗卡片：4 个开关 + 1 条不透明度滑块。</summary>
        private void AddFloatingSection(string name, string description,
            bool visible, Action<bool> setVisible, FloatingOptions floating)
        {
            var card = GlassBoardTheme.Section(name + " · 悬浮窗", description);
            FloatingPage.Children.Add(card);

            if (card.Child is not StackPanel stack)
                return;

            stack.Children.Add(MakeFloatingToggle("显示悬浮窗", visible, on =>
            {
                setVisible(on);
                GlassBoardStore.Save(GlassBoardModule.Settings);
            }));

            stack.Children.Add(MakeFloatingToggle("锁定移动", floating.Locked, on =>
            {
                floating.Locked = on;
                GlassBoardStore.Save(GlassBoardModule.Settings);
            }));

            stack.Children.Add(MakeFloatingToggle("始终置顶", floating.Topmost, on =>
            {
                floating.Topmost = on;
                GlassBoardStore.Save(GlassBoardModule.Settings);
            }));

            stack.Children.Add(MakeFloatingToggle("鼠标穿透", floating.ClickThrough, on =>
            {
                floating.ClickThrough = on;
                GlassBoardStore.Save(GlassBoardModule.Settings);
            }));

            stack.Children.Add(MakeOpacityRow(floating));
        }

        /// <summary>
        /// 把某个组件的「悬浮窗」设置并入它自己的卡片（2026-11-15 用户要求：不再各占一张卡片）。
        /// 显示 / 隐藏沿用卡片顶部那个总开关，这里只补：锁定移动 / 始终置顶 / 鼠标穿透 / 不透明度。
        /// 参数传 AddSection 返回的按钮容器（钮扣 host），通过它的 Parent 找到卡片内部 StackPanel，
        /// 这样新加的行与按钮在同一列里，且都在卡片内。
        /// </summary>
        private static void AddFloatingBlock(Panel buttonHost, FloatingOptions floating)
        {
            if (buttonHost.Parent is not StackPanel stack)
                return;

            stack.Children.Add(MakeFloatingToggle("锁定移动", floating.Locked, on =>
            {
                floating.Locked = on;
                GlassBoardStore.Save(GlassBoardModule.Settings);
            }));

            stack.Children.Add(MakeFloatingToggle("始终置顶", floating.Topmost, on =>
            {
                floating.Topmost = on;
                GlassBoardStore.Save(GlassBoardModule.Settings);
            }));

            stack.Children.Add(MakeFloatingToggle("鼠标穿透", floating.ClickThrough, on =>
            {
                floating.ClickThrough = on;
                GlassBoardStore.Save(GlassBoardModule.Settings);
            }));

            stack.Children.Add(MakeOpacityRow(floating));
        }

        /// <summary>
        /// 一行「圆形滑块开关 + 名称」。
        /// 注意：IsChecked 要在订阅事件**之前**赋值（对象初始化器里），
        /// 否则初始化就会触发 Checked、进而触发一次保存与重建。
        /// </summary>
        private static StackPanel MakeFloatingToggle(string label, bool value, Action<bool> changed)
        {
            var row = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                Margin = new Thickness(0, 0, 0, 8)
            };

            var toggle = new GameHub.Controls.ToggleSlider
            {
                IsChecked = value,
                Margin = new Thickness(0, 0, 10, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            toggle.Checked += (_, _) => changed(true);
            toggle.Unchecked += (_, _) => changed(false);

            row.Children.Add(toggle);
            row.Children.Add(new TextBlock
            {
                Text = label,
                Foreground = GlassBoardTheme.Ink,
                FontSize = 13.0,
                VerticalAlignment = VerticalAlignment.Center,
                // 窄卡片（一行 5 张）里长标签要能换行，否则溢出被裁
                TextWrapping = TextWrapping.Wrap,
                MaxWidth = 140
            });

            return row;
        }

        /// <summary>
        /// 不透明度（25%–100%）。2026-11-15 用户要求：**文字占一行、滑块独占一行** ——
        /// 原来是「文字 + 滑块 + 数值」挤在同一行，卡片变窄后滑块只剩几十像素，根本拉不动。
        /// 拖动时只落盘、不重建页面（重建会把滑块从手指下抽走）。
        /// </summary>
        private static StackPanel MakeOpacityRow(FloatingOptions floating)
        {
            var col = new StackPanel
            {
                Orientation = Orientation.Vertical,
                Margin = new Thickness(0, 2, 0, 0)
            };

            var value = new TextBlock
            {
                Text = System.Math.Round(floating.Opacity * 100) + "%",
                Foreground = GlassBoardTheme.Muted,
                FontSize = 12.0,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 第一行：左「不透明度」、右数值（用 DockPanel 才能真的贴到右边界）
            var head = new DockPanel
            {
                LastChildFill = false,
                Margin = new Thickness(0, 0, 0, 2)
            };

            var label = new TextBlock
            {
                Text = "不透明度",
                Foreground = GlassBoardTheme.Ink,
                FontSize = 12.5,
                VerticalAlignment = VerticalAlignment.Center
            };

            // 注意：本文件所在命名空间下有一个 GameHub.Modules.GlassBoard.Dock 子命名空间，
            // 直接写 Dock.Left 会被解析成那个命名空间（CS0234）→ 这里必须完全限定枚举。
            DockPanel.SetDock(label, System.Windows.Controls.Dock.Left);
            DockPanel.SetDock(value, System.Windows.Controls.Dock.Right);
            head.Children.Add(label);
            head.Children.Add(value);

            // 第二行：滑块独占整行 → 有足够的拖动区
            var slider = new Slider
            {
                Minimum = 0.25,
                Maximum = 1.0,
                Value = floating.Opacity,
                MinWidth = 0,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center
            };

            slider.ValueChanged += (_, e) =>
            {
                floating.Opacity = e.NewValue;
                floating.Normalize();
                value.Text = System.Math.Round(floating.Opacity * 100) + "%";
                GlassBoardStore.Save(GlassBoardModule.Settings);
            };

            col.Children.Add(head);
            col.Children.Add(slider);

            return col;
        }

        // ---------------- 控制中心小球 ----------------

        /// <summary>
        /// 控制中心小球（对应原版“控制中心 · 小球外观”那页设置）。
        /// 数值范围与原版一致：直径 36–100（默认 54）、图标 12–72（默认 26）、
        /// 不透明度 15–100（默认 80）；字形尺寸 = Min(图标大小, 直径-12)。
        /// </summary>
        private void BuildOrbPage()
        {
            var orb = GlassBoardModule.Settings.ControlOrb;
            orb.Normalize();

            var master = AddSection(OrbPage, "控制中心小球",
                "桌面上的那个小球：单击展开控制中心面板，拖动换位置，右键调样式与大小。" +
                $"当前：直径 {orb.Size:0} · 不透明度 {orb.Opacity:0}% · 图标 {orb.IconSize:0} · " +
                $"样式 {ControlIconRules.Name(orb.IconStyle)} · 球内字形 {orb.GlyphSize:0}。",
                GlassBoardModule.ControlOrbVisible,
                on =>
                {
                    if (on)
                        GlassBoardModule.ShowControlOrb();
                    else
                        GlassBoardModule.HideControlOrb();

                    RefreshPages();
                });

            Add(master, "回到默认位置", () =>
            {
                GlassBoardModule.ResetControlOrbPlacement();
                RefreshPages();
                Hint("小球已回到主屏右下角（下次显示也在那里）");
            });

            var style = AddSection(OrbPage, "图标样式",
                "三种样式与原版一致：圆角双拨杆 / 四格面板 / 四瓣星芒。");

            for (int i = 0; i < ControlIconRules.Styles.Length; i++)
            {
                string key = ControlIconRules.Styles[i];
                Add(style, Mark(orb.IconStyle == key) + ControlIconRules.Names[i], () =>
                {
                    GlassBoardModule.Settings.ControlOrb.IconStyle = key;
                    GlassBoardModule.RefreshControlOrb();
                    RefreshPages();
                });
            }

            var size = AddSection(OrbPage, "小球大小",
                "直径范围 36–100，原版默认 54；投影与字形会跟着一起收放。");

            foreach (var (label, value) in new[]
                     {
                         ("小 44", 44.0), ("标准 54", 54.0), ("大 64", 64.0), ("特大 74", 74.0)
                     })
            {
                double target = value;
                Add(size, Mark(Math.Abs(orb.Size - target) < 0.5) + label, () =>
                {
                    GlassBoardModule.Settings.ControlOrb.Size = target;
                    GlassBoardModule.RefreshControlOrb();
                    RefreshPages();
                });
            }

            var icon = AddSection(OrbPage, "图标大小",
                "球内字形大小，范围 12–72，原版默认 26；超过“直径-12”时按直径自动收敛。");

            foreach (var (label, value) in new[]
                     {
                         ("小 18", 18.0), ("标准 26", 26.0), ("大 34", 34.0), ("特大 42", 42.0)
                     })
            {
                double target = value;
                Add(icon, Mark(Math.Abs(orb.IconSize - target) < 0.5) + label, () =>
                {
                    GlassBoardModule.Settings.ControlOrb.IconSize = target;
                    GlassBoardModule.RefreshControlOrb();
                    RefreshPages();
                });
            }

            var fade = AddSection(OrbPage, "不透明度",
                "球体玻璃底的不透明度，范围 15–100，原版默认 80。");

            foreach (var (label, value) in new[]
                     {
                         ("60%", 60.0), ("80%", 80.0), ("90%", 90.0), ("100%", 100.0)
                     })
            {
                double target = value;
                Add(fade, Mark(Math.Abs(orb.Opacity - target) < 0.5) + label, () =>
                {
                    GlassBoardModule.Settings.ControlOrb.Opacity = target;
                    GlassBoardModule.RefreshControlOrb();
                    RefreshPages();
                });
            }

            var behavior = AddSection(OrbPage, "行为",
                "锁定位置之后拖动不再生效（小球依然可点开面板）；总在最前让小球浮在普通窗口之上。");

            Add(behavior, Mark(orb.Locked) + "锁定位置", () =>
            {
                GlassBoardModule.Settings.ControlOrb.Locked = !GlassBoardModule.Settings.ControlOrb.Locked;
                GlassBoardModule.RefreshControlOrb();
                RefreshPages();
            });

            Add(behavior, Mark(orb.Topmost) + "总在最前", () =>
            {
                GlassBoardModule.Settings.ControlOrb.Topmost = !GlassBoardModule.Settings.ControlOrb.Topmost;
                GlassBoardModule.RefreshControlOrb();
                RefreshPages();
            });

            Add(behavior, "重置小球外观与位置", () =>
            {
                var reset = GlassBoardModule.Settings.ControlOrb;
                reset.Size = 54.0;
                reset.Opacity = 80.0;
                reset.IconSize = 26.0;
                reset.IconStyle = ControlIconRules.Default;
                reset.Color = "#A9C9FF";
                reset.Locked = false;
                reset.Topmost = false;

                GlassBoardModule.RefreshControlOrb();
                GlassBoardModule.ResetControlOrbPlacement();
                RefreshPages();
                Hint("小球已恢复原版默认外观（直径 54 / 不透明度 80 / 图标 26 / 双拨杆）");
            }, danger: true);
        }

        // ---------------- 桌面净化 ----------------

        private void BuildCleanPage()
        {
            var master = AddSection(CleanPage, "总开关",
                "关闭后整体停运：时钟、备忘录、Dock、大面板全部收起，桌面还原；" +
                "开启时按记忆恢复，开机自启动跟随该记忆。",
                GlassBoardModule.ModuleEnabled,
                on => { new GlassBoardModule().SetEnabled(on); RefreshPages(); });

            var detail = AddSection(CleanPage, "逐项控制",
                "只影响任务栏与桌面图标，不改变总开关的记忆。");

            Add(detail, "任务栏", () =>
            {
                Hint(DesktopVisibility.ToggleTaskbar());
                RefreshPages();
            });

            Add(detail, "桌面图标", () =>
            {
                Hint(DesktopVisibility.ToggleIcons());
                RefreshPages();
            });

            Add(detail, "任务栏自动隐藏", () =>
            {
                try
                {
                    bool enable = !TaskbarAutoHide.Enabled;
                    TaskbarAutoHide.Set(enable);
                    Hint(enable ? "任务栏自动隐藏：已开启" : "任务栏自动隐藏：已关闭");
                }
                catch (Exception ex)
                {
                    Hint("任务栏自动隐藏不可用：" + ex.Message);
                }

                RefreshPages();
            });

            var rescue = AddSection(CleanPage, "兜底恢复",
                "隐藏后找不回来时点它，无条件显示出来。");

            Add(rescue, "一键恢复", () =>
            {
                GlassBoardModule.MarkDisabledByRestore();
                DesktopVisibility.ShowAll();

                try
                {
                    TaskbarAutoHide.ShowAndClear();
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard 设置：恢复任务栏自动隐藏失败：{ex.GetType().Name}: {ex.Message}");
                }

                RefreshPages();
                Hint("已无条件恢复任务栏与桌面图标，并关闭任务栏自动隐藏");
            }, danger: true);
        }

        // ---------------- 通用 ----------------

        private void BuildGeneralPage()
        {
            var maintenance = AddSection(GeneralPage, "配置与维护",
                "配置目录里放着 Dock 布局、组件开关与备忘录数据。");

            Add(maintenance, "打开配置目录", () =>
            {
                Directory.CreateDirectory(GlassBoardModule.DataFolder);
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{GlassBoardModule.DataFolder}\"") { UseShellExecute = true });
                Hint(GlassBoardModule.DataFolder);
            });

            Add(maintenance, "重置布局", () =>
            {
                GlassBoardModule.ResetLayout();
                RefreshPages();
                Hint("布局配置已重置：Dock 回到默认位置，大面板回到默认尺寸");
            }, danger: true);

            // 2026-11-15 用户要求：删掉「关于」卡片（内容是版本说明，没必要占一格）。
            // 原来这里是一大段多行介绍，现已移除；配置目录仍在上面“打开配置目录”按钮与 StatusHint 里可见。
        }

        // ---------------- 窗口行为 ----------------

        protected override void OnClosed(EventArgs e)
        {
            base.OnClosed(e);
            GlassBoardModule.NotifySettingsClosed();
        }

        // ---------- 备忘录导出 ----------

        /// <summary>
        /// 导出当前备忘录为 TXT。
        /// 这个功能原来在旧面板里；面板已并入本窗口，功能不能丢。
        /// </summary>
        private void ExportNotes()
        {
            try
            {
                GlassBoardModule.FlushNotes();

                using var repo = new NotesRepository();
                var note = repo.Book.Selected;

                var dialog = new Microsoft.Win32.SaveFileDialog
                {
                    Title = "导出备忘录",
                    FileName = SafeName(note.DisplayTitle) + ".txt",
                    Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                    DefaultExt = ".txt"
                };

                if (dialog.ShowDialog(this) != true)
                {
                    Hint("已取消导出");
                    return;
                }

                repo.Export(dialog.FileName);
                Hint("已导出：" + System.IO.Path.GetFileName(dialog.FileName));
            }
            catch (Exception ex)
            {
                Hint("导出失败：" + ex.Message);
                AppLog.Write($"GlassBoard 设置：导出备忘录失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string SafeName(string name)
        {
            foreach (var bad in System.IO.Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');

            return string.IsNullOrWhiteSpace(name) ? "备忘录" : name;
        }
    }
}
