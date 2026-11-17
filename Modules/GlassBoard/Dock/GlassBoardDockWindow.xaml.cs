using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using GameHub.Utils;
using GameHub.Modules.GlassBoard;

namespace GameHub.Modules.GlassBoard.Dock
{
    /// <summary>
    /// 玻璃 Dock 底栏（对应原版 DockWindow）。
    ///
    /// 行为按原版逐项对齐：
    ///   - 宽度 = 系统入口 + 固定入口，用 VisiblePinned / WidthFor 算，超出的收进“更多”；
    ///   - 位置：Alignment 决定靠左/居中/靠右，Top 默认贴在任务栏上方，可拖动并记住；
    ///   - 图标：悬浮放大 min(1.6, 1+(Zoom-1)*MotionLevel) 并上移 5*MotionLevel，180ms 缓动；
    ///   - 运行指示：图标下方一个 5×4 小点，程序在跑就显示；
    ///   - 自动隐藏：用 AutoHidePolicy 判定（悬停/贴边/菜单打开都算“看过”），
    ///     藏起来时只留一条边，鼠标贴边再弹回来，动画末段带回弹。
    ///
    /// 与原版的差异（都是有意为之）：
    ///   - 不接原版的 Controller，改用回调：面板、设置由 GameHub 模块提供；
    ///   - 电源/网络快捷入口暂不做（涉及关机操作，等确认后再加）；
    ///   - 多显示器只按主屏工作区计算，后续再补。
    /// </summary>
    public partial class GlassBoardDockWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const double DockGap = 12.0;
        private const double EdgeReveal = 12.0;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

        private readonly DockModel _model;
        private readonly Action? _openPanel;
        private readonly Action? _openSettings;
        private readonly Action? _toggleGameHub;

        private readonly DispatcherTimer _autoTimer;
        private readonly DispatcherTimer _runningTimer;

        private AutoHidePolicy _hidePolicy = new();
        private bool _renderedHidden;
        private double _shownTop;
        private Rect _screenArea;
        private bool _dragging;

        public GlassBoardDockWindow(Action? openPanel = null, Action? openSettings = null, Action? toggleGameHub = null)
        {
            InitializeComponent();

            _openPanel = openPanel;
            _openSettings = openSettings;
            _toggleGameHub = toggleGameHub;
            _model = DockStore.Load();

            // 接受从大面板拖进来的入口（拖到 Dock 上＝固定）
            WireDropTarget();

            Opacity = 0;   // 首次布局完成前不显示，避免看到“跳一下”

            _autoTimer = new DispatcherTimer(DispatcherPriority.Input)
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _autoTimer.Tick += (_, _) => UpdateAutoHide();

            _runningTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(3000)
            };
            _runningTimer.Tick += (_, _) =>
            {
                if (!IsVisible)
                    return;

                try
                {
                    DockRunningWindows.Scan();
                    UpdateRunning();
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard Dock：扫描运行中窗口失败：{ex.GetType().Name}: {ex.Message}");
                }
            };
            _runningTimer.Start();

            Loaded += (_, _) =>
            {
                try
                {
                    Refresh();
                    HideFromAltTab();
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard Dock：初始化失败：{ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    // 无论初始化成不成功都要把窗口显示出来：
                    // 否则一旦中途抛异常，Dock 会以 Opacity=0 一直“隐形”，反而更难排查。
                    Opacity = 1;
                }
            };
        }

        // ---------------- 布局 ----------------

        /// <summary>按当前配置重建整条 Dock（对应原版 Refresh）。</summary>
        // ---------------- 从大面板拖入固定 ----------------

        /// <summary>大面板瓦片的拖拽格式（必须与 GlassBoardBoardWindow 一致）。</summary>
        private const string EntryDragFormat = "GlassBoard.BoardEntry";
        private const string EntryDragNameFormat = "GlassBoard.BoardEntryName";

        private void WireDropTarget()
        {
            AllowDrop = true;

            DragOver += (_, e) =>
                e.Effects = e.Data.GetDataPresent(EntryDragFormat) ? DragDropEffects.Move : DragDropEffects.None;

            Drop += OnBoardEntryDrop;
        }

        private void OnBoardEntryDrop(object sender, DragEventArgs e)
        {
            try
            {
                if (!e.Data.GetDataPresent(EntryDragFormat))
                    return;

                string? path = e.Data.GetData(EntryDragFormat) as string;
                if (string.IsNullOrWhiteSpace(path))
                    return;

                string name = e.Data.GetData(EntryDragNameFormat) as string ?? string.Empty;

                // Dock 自己的 _model 可能是较早读的盘（大面板导入过新入口后尤其明显），
                // 所以先重新读盘再固定，否则会出现“拖上去没反应”。
                var fresh = DockStore.Load();

                var entry = fresh.Apps.FirstOrDefault(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase));

                if (entry == null)
                {
                    entry = new DockEntry { Path = path!, Name = name };
                    fresh.Apps.Add(entry);
                }
                else if (!string.IsNullOrWhiteSpace(name))
                {
                    entry.Name = name;
                }

                // 落在某个图标上就插到它前面/后面；落在空白处就追加到最后
                var target = DockDropRules.OnIcon(fresh, path!, PathUnderPointer(e) ?? string.Empty);
                var result = DockPinRules.Apply(fresh, new[] { entry }, target);

                if (result.Skipped > 0)
                {
                    AppLog.Write($"GlassBoard Dock：固定已满（上限 {DockPinRules.MaxPinned}），{result.Skipped} 个未加入");
                }

                DockStore.Save(fresh);
                AdoptModel(fresh);
                Refresh();

                AppLog.Write($"GlassBoard Dock：从大面板拖入固定「{entry.DisplayName}」（新增 {result.Added} 个）");
                e.Handled = true;
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：拖入固定失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>鼠标下方那个图标的路径（图标没设 Tag 时返回 null，退化成追加）。</summary>
        private static string? PathUnderPointer(DragEventArgs e)
        {
            var element = e.OriginalSource as DependencyObject;

            while (element != null)
            {
                if (element is FrameworkElement fe && fe.Tag is string tag && !string.IsNullOrWhiteSpace(tag))
                    return tag;

                element = VisualTreeHelper.GetParent(element);
            }

            return null;
        }

        /// <summary>_model 是 readonly，只能把读盘结果灌进它的字段。</summary>
        private void AdoptModel(DockModel fresh)
        {
            _model.Apps = fresh.Apps;
            _model.Groups = fresh.Groups;
            _model.DockOrder = fresh.DockOrder;
            _model.Dock = fresh.Dock;
            _model.Imported = fresh.Imported;
        }

        public void Refresh()
        {
            var dock = _model.Dock;
            dock.Normalize();

            _screenArea = SystemParameters.WorkArea;
            Topmost = dock.AlwaysOnTop || dock.AutoHide;

            _renderedHidden = false;
            _hidePolicy = new AutoHidePolicy();

            Items.Children.Clear();
            Items.Margin = new Thickness(dock.HoverPadding(), 0, dock.HoverPadding(), 0);
            Surface.CornerRadius = new CornerRadius(dock.Radius);

            // 系统入口：大面板 + 唤出 GameHub
            AddButton(DockLayoutRules.Apps, GlyphButton("▦", "展开 GlassBoard 面板", () => Invoke(_openPanel)), owned: false);
            // 2026-11-12 用户要求：Dock 上加「唤出 GameHub」—— 点一次打开面板，再点一次最小化
            AddButton(DockLayoutRules.Apps, GlyphButton("▣", "唤出 / 最小化 GameHub", () => Invoke(_toggleGameHub)), owned: false);

            // 固定入口
            var ordered = DockOrderRules.Ordered(_model);
            int total = ordered.Count;
            int visible = dock.VisiblePinned(_screenArea.Width - 20.0, total);
            int shown = total > visible ? Math.Max(0, visible - 1) : visible;

            foreach (var entry in ordered.Take(shown))
                Items.Children.Add(EntryButton(entry));

            if (total > shown && visible > 0)
                Items.Children.Add(MoreButton(ordered.Skip(shown).ToList()));

            if (total == 0)
            {
                // 空 Dock 上的“+”直接拉添加窗口，不再去开面板：
                // 面板以前是模态打开的，会把整个应用（含 Dock）禁用掉，表现就是“点一下卡死”。
                var hint = GlyphButton("+", "添加软件到 Dock", AddFiles);
                Items.Children.Add(hint);
            }

            // 分隔线 + 系统入口：设置
            if (dock.ShowSeparator)
            {
                Items.Children.Add(new Border
                {
                    Width = 1,
                    Height = dock.IconSize * 0.72,
                    Margin = new Thickness(8, 0, 8, 0),
                    Background = new SolidColorBrush(Color.FromArgb(60, 255, 255, 255)),
                    VerticalAlignment = VerticalAlignment.Center
                });
            }

            // 任务栏“隐藏的图标”：左键展开/收起浮出窗，右键 = 任务栏设置（与系统那个 ∧ 一致）
            Items.Children.Add(TaskbarToggleButton());

            if (dock.ShowSettings)
                AddButton(DockLayoutRules.Settings, SettingsButton(), owned: false);

            // 尺寸与位置（算法照抄原版，别随手改数值）
            double slot = dock.IconSize + 16.0 + (dock.ShowLabels ? 18 : 0);
            double zoomMax = Math.Min(1.6, 1.0 + (dock.Zoom - 1.0) * dock.MotionLevel);

            Width = Math.Min(_screenArea.Width - 20.0, dock.WidthFor(total == 0 ? 1 : Math.Max(1, visible)));
            Height = 28.0 + slot + dock.Padding * 2.0
                     + Math.Max(0.0, slot * (zoomMax - 1.0) * 0.65 + 5.0 * dock.MotionLevel - dock.Padding) * 2.0;

            double fallbackLeft = dock.Alignment switch
            {
                0 => _screenArea.Left + 10.0,
                2 => _screenArea.Right - Width - 10.0,
                _ => _screenArea.Left + (_screenArea.Width - Width) / 2.0
            };

            double? center = _model.DockPlaced ? _model.DockLeft + Width / 2.0 : null;
            Left = DockAnchorRules.Left(center, Width, fallbackLeft + dock.Offset, _screenArea.Left, _screenArea.Width);

            double desiredTop = _model.DockPlaced
                ? _model.DockTop
                : _screenArea.Bottom - Height - DockGap;

            Top = DockRules.Position(desiredTop, _screenArea.Top, _screenArea.Height, Height);
            _shownTop = Top;

            Surface.Background = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(DockRules.GlassOpacity(dock.OpacityPercent) * 255.0 / 100.0), 0x18, 0x1A, 0x20));

            Surface.Effect = dock.Material.Shadow
                ? new DropShadowEffect { BlurRadius = 18, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black }
                : null;

            if (dock.AutoHide)
                _autoTimer.Start();
            else
            {
                _autoTimer.Stop();
                _renderedHidden = false;
            }

            UpdateRunning();
        }

        private void AddButton(string key, Button button, bool owned)
        {
            Items.Children.Add(button);
        }

        // ---------------- 图标按钮 ----------------

        /// <summary>纯文字入口（系统入口用它）。</summary>
        private Button GlyphButton(string glyph, string tip, Action action)
            => BuildButton(glyph, null, action, tip, null);

        /// <summary>
        /// 设置入口（Dock 末尾那个 ⚙）：
        ///   左键 = 开关设置窗口（开着就关掉，用户要求“再点一次关闭”）；
        ///   右键 = Dock 的全部设置（添加软件、对齐、图标大小、显示名称、自动隐藏、总在最前、隐藏 Dock…）。
        /// 用户要求：这些“剩下的交互”都挂在 ⚙ 上，软件图标右键只留 打开 / 排序 / 移出。
        /// </summary>
        private Button SettingsButton()
        {
            var button = BuildButton("⚙", null, () => Invoke(_openSettings), "外观与 Dock 设置（右键 = Dock 设置）", null);

            var menu = DockMenu();
            menu.PlacementTarget = button;
            menu.Placement = PlacementMode.Top;
            button.ContextMenu = menu;

            return button;
        }

        /// <summary>固定软件入口（带图标与运行指示点）。</summary>
        private Button EntryButton(DockEntry entry)
        {
            var button = BuildButton(null, DockIcons.For(entry), () => Launch(entry), entry.DisplayName, entry);
            button.ContextMenu = EntryMenu(entry);
            return button;
        }

        private Button MoreButton(List<DockEntry> rest)
        {
            var button = BuildButton("···", null, () => { }, "更多固定应用", null);

            var menu = new ContextMenu();
            BoardMenuTheme.Apply(menu, _model.Dock.OpacityPercent);
            foreach (var entry in rest)
            {
                var item = new MenuItem { Header = entry.DisplayName };
                item.Click += (_, _) => Launch(entry);
                menu.Items.Add(item);
            }

            button.ContextMenu = menu;
            button.Click += (_, _) =>
            {
                menu.PlacementTarget = button;
                menu.Placement = PlacementMode.Top;
                menu.IsOpen = true;
            };

            return button;
        }

        /// <summary>
        /// 造一个 Dock 图标按钮（对应原版 DockButton）：
        /// 尺寸/间距/悬浮放大/运行指示点/悬浮提示的数值都照原版。
        /// </summary>
        private Button BuildButton(string? text, ImageSource? icon, Action action, string tip, DockEntry? entry)
        {
            var dock = _model.Dock;
            double iconSize = dock.IconSize;

            var button = new Button
            {
                Width = iconSize + 16.0,
                Height = iconSize + 16.0 + (dock.ShowLabels ? 18.0 : 0.0),
                Margin = new Thickness(dock.Spacing / 2.0, 0, dock.Spacing / 2.0, 0),
                ToolTip = tip,
                Cursor = Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Focusable = false
            };

            ToolTipService.SetInitialShowDelay(button, 200);
            ToolTipService.SetShowDuration(button, 3500);

            var surface = new FrameworkElementFactory(typeof(Border));
            surface.SetValue(Border.BackgroundProperty, new SolidColorBrush(Color.FromArgb(1, 255, 255, 255)));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

            // 顺序很重要：必须先把工厂拼完，再赋给 ControlTemplate.VisualTree。
            // 赋值那一刻工厂就会被密封，之后再 AppendChild / SetValue 会抛
            // “使用 FrameworkElementFactory 之后(密封)，无法对其进行修改”。
            surface.AppendChild(presenter);

            button.Template = new ControlTemplate(typeof(Button)) { VisualTree = surface };

            FrameworkElement content = icon != null
                ? new Image { Source = icon, Width = iconSize, Height = iconSize, Stretch = Stretch.Uniform }
                : new TextBlock
                {
                    Text = text ?? "□",
                    FontSize = iconSize * 0.72,
                    Width = iconSize,
                    Height = iconSize,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White
                };

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            stack.Children.Add(content);

            if (dock.ShowLabels)
            {
                stack.Children.Add(new TextBlock
                {
                    Text = text switch { "···" => "更多", "⚙" => "设置", "▦" => "应用", _ => tip },
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
                    TextAlignment = TextAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    MaxWidth = iconSize + 12,
                    Margin = new Thickness(0, 4, 0, 0)
                });
            }

            var root = new Grid { IsHitTestVisible = false };
            root.Children.Add(stack);

            if (entry != null)
            {
                button.Tag = entry;
                root.Children.Add(new Border
                {
                    Tag = "running",
                    Width = 5,
                    Height = 4,
                    CornerRadius = new CornerRadius(2),
                    Background = new SolidColorBrush(Color.FromRgb(135, 205, 255)),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Bottom,
                    Visibility = Visibility.Collapsed
                });
            }

            button.Content = root;

            // 悬浮放大 + 上移（原版数值：180ms CubicEase，缩放上限 1.6）
            var scale = new ScaleTransform(1.0, 1.0);
            var lift = new TranslateTransform();
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(lift);
            button.RenderTransform = group;
            button.RenderTransformOrigin = new Point(0.5, 0.65);

            void Animate(bool hover)
            {
                if (!dock.MotionEnabled)
                    return;

                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                double to = hover ? Math.Min(1.6, 1.0 + (dock.Zoom - 1.0) * dock.MotionLevel) : 1.0;

                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
                lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(hover ? -5.0 * dock.MotionLevel : 0.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = easing });
            }

            button.MouseEnter += (_, _) => Animate(true);
            button.MouseLeave += (_, _) => Animate(false);

            button.Click += (_, _) =>
            {
                if (!_dragging)
                    action();
            };

            return button;
        }

        // ---------------- 运行指示 ----------------

        /// <summary>刷新每个图标的“正在运行”小点（对应原版 UpdateRunning）。</summary>
        private void UpdateRunning()
        {
            foreach (var button in Items.Children.OfType<Button>())
            {
                if (button.Tag is not DockEntry entry || button.Content is not Grid root)
                    continue;

                bool running = _model.Dock.ShowRunning && DockRunningWindows.IsRunning(entry);

                foreach (var dot in root.Children.OfType<Border>().Where(b => (b.Tag as string) == "running"))
                    dot.Visibility = running ? Visibility.Visible : Visibility.Collapsed;
            }
        }

        // ---------------- 自动隐藏 ----------------

        /// <summary>自动隐藏判定与位移动画（对应原版 UpdateAutoHide）。</summary>
        private void UpdateAutoHide()
        {
            if (!IsVisible || !_model.Dock.AutoHide)
                return;

            try
            {
                var mouse = System.Windows.Forms.Cursor.Position;
                var source = PresentationSource.FromVisual(this);
                if (source?.CompositionTarget == null)
                    return;

                Point point = source.CompositionTarget.TransformFromDevice.Transform(new Point(mouse.X, mouse.Y));

                bool over = !_renderedHidden
                            && point.X >= Left && point.X <= Left + Width
                            && point.Y >= _shownTop && point.Y <= _shownTop + Height;

                bool edge = point.X >= Left - EdgeReveal && point.X <= Left + Width + EdgeReveal
                            && point.Y >= _screenArea.Bottom - _model.Dock.EdgeRange;

                bool menuOpen = (ContextMenu?.IsOpen ?? false)
                                || Items.Children.OfType<Button>().Any(b => b.ContextMenu?.IsOpen == true);

                bool hidden = _hidePolicy.Update(
                    DateTime.UtcNow,
                    _model.Dock.AutoHide,
                    over,
                    edge,
                    _dragging || menuOpen,
                    _model.Dock.HideDelay);

                if (hidden == _renderedHidden)
                    return;

                _renderedHidden = hidden;
                double target = hidden ? _screenArea.Bottom - 20.0 : _shownTop;
                AnimateVisibility(hidden, target);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：自动隐藏失败：{ex.GetType().Name}: {ex.Message}");
                _autoTimer.Stop();
            }
        }

        /// <summary>伸出/收回动画：用原版的回弹关键帧。</summary>
        private void AnimateVisibility(bool hide, double target)
        {
            double[] stops = DockRevealRules.Stops(Top, target, _model.Dock.EdgeStrength, _screenArea.Top);

            var animation = new DoubleAnimationUsingKeyFrames();
            double ms = 0;

            for (int i = 1; i < stops.Length; i++)
            {
                ms += hide ? 55 : 70;
                animation.KeyFrames.Add(new LinearDoubleKeyFrame(stops[i], KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms))));
            }

            BeginAnimation(TopProperty, animation);
        }

        // ---------------- 拖动与位置记忆 ----------------

        private void MoveGrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            _dragging = true;

            try
            {
                DragMove();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：拖动失败：{ex.GetType().Name}: {ex.Message}");
            }

            _dragging = false;

            Left = DockRules.Position(Left, _screenArea.Left, _screenArea.Width, Width);
            Top = DockRules.Position(Top, _screenArea.Top, _screenArea.Height, Height);
            _shownTop = Top;

            _model.DockPlaced = true;
            _model.DockLeft = Left;
            _model.DockTop = Top;
            DockStore.Save(_model);
        }

        // ---------------- 启动与切换 ----------------

        /// <summary>点图标：在运行就切窗口，没运行就启动（对应原版 C.Launch）。</summary>
        private void Launch(DockEntry entry)
        {
            if (_dragging)
                return;

            try
            {
                if (DockRunningWindows.IsRunning(entry) && DockRunningWindows.Activate(entry))
                    return;

                if (entry.LaunchTarget == null)
                    LaunchInfo.Enrich(entry);

                string? target = entry.LaunchTarget;

                if (string.IsNullOrWhiteSpace(target))
                    target = entry.Path;

                if (string.IsNullOrWhiteSpace(target))
                    return;

                if (entry.IsFolder || Directory.Exists(target))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
                    AppLog.Write($"GlassBoard Dock：打开文件夹 {target}");
                    return;
                }

                var info = new ProcessStartInfo(target) { UseShellExecute = true };
                if (!string.IsNullOrWhiteSpace(entry.LaunchArguments))
                    info.Arguments = entry.LaunchArguments;

                Process.Start(info);
                AppLog.Write($"GlassBoard Dock：启动 {target}");
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：启动失败 {entry.Path}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void Invoke(Action? action)
        {
            try
            {
                action?.Invoke();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：回调失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------------- 右键菜单 ----------------

        /// <summary>单个软件图标的右键菜单。</summary>
        private ContextMenu EntryMenu(DockEntry entry)
        {
            var menu = new ContextMenu();
            BoardMenuTheme.Apply(menu, _model.Dock.OpacityPercent);

            var open = new MenuItem { Header = "打开" };
            open.Click += (_, _) => Launch(entry);
            menu.Items.Add(open);

            var forward = new MenuItem { Header = "前移" };
            forward.Click += (_, _) => { DockOrderRules.Step(_model, entry.Path, -1); DockStore.Save(_model); Refresh(); };
            menu.Items.Add(forward);

            var backward = new MenuItem { Header = "后移" };
            backward.Click += (_, _) => { DockOrderRules.Step(_model, entry.Path, 1); DockStore.Save(_model); Refresh(); };
            menu.Items.Add(backward);

            menu.Items.Add(new Separator());

            var unpin = new MenuItem { Header = "从 Dock 取消固定" };
            unpin.Click += (_, _) =>
            {
                entry.Pinned = false;
                _model.Normalize();
                DockStore.Save(_model);
                Refresh();
            };
            menu.Items.Add(unpin);
            menu.Items.Add(new Separator());

            var location = new MenuItem { Header = "打开文件所在位置" };
            location.Click += (_, _) => RevealInExplorer(entry);
            menu.Items.Add(location);

            var properties = new MenuItem { Header = "属性" };
            properties.Click += (_, _) => ShowProperties(entry);
            menu.Items.Add(properties);

            return menu;
        }

        /// <summary>Dock 自身的右键菜单（空白处）。</summary>
        private ContextMenu DockMenu()
        {
            var menu = new ContextMenu();
            BoardMenuTheme.Apply(menu, _model.Dock.OpacityPercent);

            var add = new MenuItem { Header = "添加软件或文件到 Dock" };
            add.Click += (_, _) => AddFiles();
            menu.Items.Add(add);

            var panel = new MenuItem { Header = "打开 GlassBoard 面板" };
            panel.Click += (_, _) => Invoke(_openPanel);
            menu.Items.Add(panel);

            menu.Items.Add(new Separator());
            AddDockOptions(menu);

            menu.Items.Add(new Separator());

            var hide = new MenuItem { Header = "隐藏 Dock（可在卡片面板里重新显示）" };
            hide.Click += (_, _) => Close();
            menu.Items.Add(hide);

            return menu;
        }

        /// <summary>Dock 的位置 / 大小 / 对齐 / 自动隐藏等开关。</summary>
        private void AddDockOptions(ContextMenu menu)
        {
            var dock = _model.Dock;

            var alignment = new MenuItem { Header = "对齐" };
            foreach (var (label, value) in new[] { ("靠左", 0), ("居中", 1), ("靠右", 2) })
            {
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = dock.Alignment == value };
                item.Click += (_, _) =>
                {
                    dock.Alignment = value;
                    _model.DockPlaced = false;   // 改对齐就回到自动摆放
                    DockStore.Save(_model);
                    Refresh();
                };
                alignment.Items.Add(item);
            }
            menu.Items.Add(alignment);

            var size = new MenuItem { Header = "图标大小" };
            foreach (var (label, value) in new[] { ("小 32", 32.0), ("中 44", 44.0), ("大 56", 56.0), ("特大 64", 64.0) })
            {
                var item = new MenuItem { Header = label, IsCheckable = true, IsChecked = Math.Abs(dock.IconSize - value) < 0.5 };
                item.Click += (_, _) =>
                {
                    dock.IconSize = value;
                    DockStore.Save(_model);
                    Refresh();
                };
                size.Items.Add(item);
            }
            menu.Items.Add(size);

            var labels = new MenuItem { Header = "显示名称", IsCheckable = true, IsChecked = dock.ShowLabels };
            labels.Click += (_, _) => { dock.ShowLabels = !dock.ShowLabels; DockStore.Save(_model); Refresh(); };
            menu.Items.Add(labels);

            var running = new MenuItem { Header = "显示运行中指示点", IsCheckable = true, IsChecked = dock.ShowRunning };
            running.Click += (_, _) => { dock.ShowRunning = !dock.ShowRunning; DockStore.Save(_model); Refresh(); };
            menu.Items.Add(running);

            var autoHide = new MenuItem { Header = "自动隐藏", IsCheckable = true, IsChecked = dock.AutoHide };
            autoHide.Click += (_, _) => { dock.AutoHide = !dock.AutoHide; DockStore.Save(_model); Refresh(); };
            menu.Items.Add(autoHide);

            var top = new MenuItem { Header = "总在最前", IsCheckable = true, IsChecked = dock.AlwaysOnTop };
            top.Click += (_, _) => { dock.AlwaysOnTop = !dock.AlwaysOnTop; DockStore.Save(_model); Refresh(); };
            menu.Items.Add(top);
        }

        /// <summary>添加软件/文件/文件夹到 Dock 并固定。</summary>
        public void AddFiles()
        {
            try
            {
                // 检查是否已经达到上限
                int currentPinned = _model.Apps.Count(a => a.Pinned);
                if (currentPinned >= DockPinRules.MaxPinned)
                {
                    ShowGlassMessage($"Dock 固定图标已达上限（{DockPinRules.MaxPinned} 个），请先从右键菜单移除不需要的图标。");
                    return;
                }

                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "添加到 Dock",
                    Multiselect = true,
                    Filter = "可执行与快捷方式 (*.exe;*.lnk;*.url;*.bat;*.cmd)|*.exe;*.lnk;*.url;*.bat;*.cmd|所有文件 (*.*)|*.*"
                };

                // 归属窗口用主窗口，而不是 Dock：Dock 是无边框的工具窗口，
                // 把文件对话框挂在它下面容易出现“对话框跑到屏幕外/看不见”，那也会像卡死。
                var owner = Application.Current?.MainWindow;
                bool? picked = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
                if (picked != true)
                    return;

                int added = 0;

                foreach (var path in dialog.FileNames)
                {
                    if (_model.Apps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // 再次检查是否达到上限（防止批量添加时超限）
                    if (currentPinned + added >= DockPinRules.MaxPinned)
                    {
                        ShowGlassMessage($"Dock 固定图标已达上限（{DockPinRules.MaxPinned} 个），只能添加 {DockPinRules.MaxPinned - currentPinned} 个。");
                        break;
                    }

                    var entry = new DockEntry
                    {
                        Path = path,
                        Name = EntryPathRules.Name(path),
                        Pinned = true
                    };

                    LaunchInfo.Enrich(entry);
                    _model.Apps.Add(entry);
                    added++;
                }

                if (added > 0)
                {
                    _model.Normalize();
                    DockStore.Save(_model);
                    Refresh();
                    AppLog.Write($"GlassBoard Dock：添加了 {added} 个入口");
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：添加入口失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------------- 任务栏隐藏图标（托盘溢出区） ----------------

        [DllImport("user32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr FindWindow(string? lpClassName, string? lpWindowName);

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        /// <summary>任务栏“隐藏的图标”浮出窗：Win11 是 XAML 岛，Win10 是老式窗口。
        /// 本机实测（2560x1440 / Win11 26300）：
        ///   TopLevelWindowForOverflowXamlIsland  2209,1278 234x114  ← 真实生效的就是这个
        ///   NotifyIconOverflowWindow            不存在
        /// </summary>
        private const string OverflowClassWin11 = "TopLevelWindowForOverflowXamlIsland";
        private const string OverflowClassLegacy = "NotifyIconOverflowWindow";

        private const int SW_HIDE = 0;
        private const int SW_SHOWNOACTIVATE = 4;

        /// <summary>Win10 老式溢出窗用的消息（WM_USER + 1）。</summary>
        private const int TrayOverflowToggle = 0x0401;

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);

        /// <summary>
        /// Dock 上的任务栏开关：左键 = 显示 / 隐藏**原生任务栏**，右键 = 任务栏设置。
        /// 用户要求：直接用原生任务栏，不要再把系统托盘浮出窗搬出来。
        /// </summary>
        private Button TaskbarToggleButton()
        {
            var button = GlyphButton("▤", "显示 / 隐藏任务栏", ToggleTaskbar);

            var menu = new ContextMenu();
            BoardMenuTheme.Apply(menu, _model.Dock.OpacityPercent);

            var show = new MenuItem { Header = "显示任务栏" };
            show.Click += (_, _) => SetTaskbarHidden(false);
            menu.Items.Add(show);

            var hide = new MenuItem { Header = "隐藏任务栏" };
            hide.Click += (_, _) => SetTaskbarHidden(true);
            menu.Items.Add(hide);

            menu.Items.Add(new Separator());

            var taskbar = new MenuItem { Header = "任务栏设置" };
            taskbar.Click += (_, _) =>
            {
                try
                {
                    Process.Start(new ProcessStartInfo("ms-settings:taskbar") { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard Dock：打开任务栏设置失败：{ex.GetType().Name}: {ex.Message}");
                }
            };
            menu.Items.Add(taskbar);

            button.ContextMenu = menu;
            return button;
        }

        /// <summary>左键：在“显示 / 隐藏原生任务栏”之间切换。</summary>
        public void ToggleTaskbar()
        {
            try
            {
                AppLog.Write($"GlassBoard Dock：任务栏 {DesktopVisibility.ToggleTaskbar()}");
                Refresh();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：切换任务栏失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>直接指定任务栏显示 / 隐藏（右键菜单用）。</summary>
        private void SetTaskbarHidden(bool hidden)
        {
            try
            {
                DesktopVisibility.SetHidden(taskbar: true, hide: hidden);
                AppLog.Write($"GlassBoard Dock：任务栏已{(hidden ? "隐藏" : "显示")}");
                Refresh();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：设置任务栏可见性失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>展开 / 收起任务栏里隐藏的图标（等价于点任务栏的 ∧）。</summary>
        public void ToggleHiddenIcons()
        {
            try
            {
                // Win11：浮出窗是个 XAML 岛，直接切换它的可见性就等于点 ∧（本机实测到的真实窗体）
                var win11 = FindWindow(OverflowClassWin11, null);
                if (win11 != IntPtr.Zero)
                {
                    bool visible = IsWindowVisible(win11);
                    ShowWindow(win11, visible ? SW_HIDE : SW_SHOWNOACTIVATE);
                    AppLog.Write($"GlassBoard Dock：Win11 托盘溢出窗已{(visible ? "收起" : "展开")}");
                    return;
                }

                // Win10：老式溢出窗，发 WM_USER+1
                var legacy = FindWindow(OverflowClassLegacy, null);
                if (legacy != IntPtr.Zero)
                {
                    SendMessage(legacy, TrayOverflowToggle, IntPtr.Zero, IntPtr.Zero);
                    AppLog.Write("GlassBoard Dock：Win10 托盘溢出窗已切换");
                    return;
                }

                AppLog.Write("GlassBoard Dock：找不到托盘溢出窗（Win10 / Win11 两种实现都没找到）");
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：切换隐藏图标失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>在资源管理器里定位到这个软件。</summary>
        private void RevealInExplorer(DockEntry entry)
        {
            try
            {
                if (entry.IsFolder || Directory.Exists(entry.Path))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{entry.Path}\"") { UseShellExecute = true });
                    return;
                }

                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{entry.Path}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：打开所在位置失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>系统“属性”对话框（与资源管理器右键的属性一样）。</summary>
        private void ShowProperties(DockEntry entry)
        {
            try
            {
                Process.Start(new ProcessStartInfo(entry.Path)
                {
                    Verb = "properties",
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：打开属性失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>显示玻璃材质的提示消息（用户要求：弹窗得是玻璃材质）。</summary>
        private void ShowGlassMessage(string message)
        {
            try
            {
                var window = new Window
                {
                    Title = "GlassBoard",
                    Width = 400,
                    Height = 120,
                    WindowStyle = WindowStyle.None,
                    AllowsTransparency = true,
                    Background = Brushes.Transparent,
                    ShowInTaskbar = false,
                    Topmost = true,
                    WindowStartupLocation = WindowStartupLocation.CenterOwner,
                    Owner = Application.Current?.MainWindow
                };

                var content = new Grid
                {
                    Margin = new Thickness(14)
                };

                var border = new Border
                {
                    CornerRadius = new CornerRadius(12),
                    BorderThickness = new Thickness(1),
                    BorderBrush = GlassBoardTheme.Line,
                    Background = GlassBoardTheme.SurfaceFill(80),
                    Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 5, Opacity = 0.3, Color = Colors.Black }
                };

                var textBlock = new TextBlock
                {
                    Text = message,
                    Foreground = GlassBoardTheme.Ink,
                    FontSize = 13,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(16, 12, 16, 12)
                };

                border.Child = textBlock;
                content.Children.Add(border);

                window.Content = content;
                window.Show();

                // 3 秒后自动关闭
                var timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
                timer.Tick += (_, _) =>
                {
                    timer.Stop();
                    window.Close();
                };
                timer.Start();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：显示提示失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------------- 窗口行为 ----------------

        // 背景（空白处）不再弹任何菜单：Dock 级设置全部挂在末尾的 ⚙ 右键上（用户要求）

        protected override void OnClosed(EventArgs e)
        {
            _autoTimer.Stop();
            _runningTimer.Stop();
            base.OnClosed(e);
        }

        /// <summary>加 WS_EX_TOOLWINDOW：Dock 不该出现在 Alt+Tab 里。</summary>
        private void HideFromAltTab()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                    return;

                if (IntPtr.Size == 8)
                    SetWindowLongPtr64(handle, GWL_EXSTYLE, new IntPtr(GetWindowLongPtr64(handle, GWL_EXSTYLE).ToInt64() | WS_EX_TOOLWINDOW));
                else
                    SetWindowLong32(handle, GWL_EXSTYLE, GetWindowLongPtr64(handle, GWL_EXSTYLE).ToInt32() | WS_EX_TOOLWINDOW);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：设置工具窗口样式失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
