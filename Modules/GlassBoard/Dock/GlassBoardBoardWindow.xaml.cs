using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Effects;
using System.Windows.Threading;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard.Dock
{
    /// <summary>
    /// 应用大面板 / 启动台（对应原版 BoardWindow）——Dock 上 ▦ 打开的就是它。
    ///
    /// 布局、过滤与分页、图标瓦片的动效数值都按原版照搬：
    ///   行高 55 / 自动 / 多选栏 / 星号 / 45 / 32；内容边距 (38,22,38,20)；
    ///   瓦片圆角 18、悬浮放大 1+0.025*MotionLevel 并上移 3*MotionLevel；
    ///   入场动画从 min(序号,14)*12ms 起、190ms 淡入 + 230ms 上浮；
    ///   每页容量 = 列数 × 行数（列/行由 BoardIconRules 的公式按窗口尺寸算）。
    ///
    /// 未做的部分（后续再补）：把桌面文件拖进面板、瓦片之间拖动排序。
    /// </summary>
    public partial class GlassBoardBoardWindow : Window
    {
        private readonly DockModel _model;

        private Grid? _layout;
        private TextBox? _search;
        private TextBlock? _hint;
        private Panel? _tiles;

        /// <summary>图标区的滚动宿主（不再分页，多了就滚动）。</summary>
        private ScrollViewer? _scroller;

        /// <summary>每个瓦片的固定宽度：图标 48 + 两边留白，名字才有位置换行。</summary>
        private const double TileWidth = 108.0;
        private TextBlock? _pager;
        private TextBlock? _status;
        private TextBlock? _empty;
        private ComboBox? _groups;
        private ComboBox? _moveGroup;
        private WrapPanel? _folderBar;
        private WrapPanel? _bulkBar;
        private RowDefinition? _bulkRow;
        private Button? _editSelection;
        private Button? _selectAll;
        private Button? _clearSelection;
        private Button? _deleteSelected;
        private Button? _undoRemoval;
        private Button? _moveSelected;
        private TextBlock? _selectionCount;
        /// <summary>搜索框外壳：底色需要跟着透明度滑块一起变淡。</summary>
        private Border? _searchShell;

        /// <summary>大面板内部拖拽用的数据格式（与 Dock 分开，避免互相误收）。</summary>
        private const string EntryDragFormat = "GlassBoard.BoardEntry";
        private const string EntryDragNameFormat = "GlassBoard.BoardEntryName";
        private Slider? _opacitySlider;
        private TextBlock? _opacityText;

        private readonly DispatcherTimer _runningTimer;

        private bool _syncingGroups;
        private bool _building;
        private bool _resizing;
        private int _page;
        private int _capacity = 35;
        private string _currentGroup = BoardRules.All;
        private bool _editing;
        private readonly HashSet<string> _selected = new(StringComparer.OrdinalIgnoreCase);

        public GlassBoardBoardWindow()
        {
            InitializeComponent();

            _model = DockStore.Load();

            MoveGrip.DragDelta += (_, e) =>
            {
                if (_resizing)
                    return;

                var area = SystemParameters.WorkArea;
                Left = DockRules.Position(Left + e.HorizontalChange, area.Left, area.Width, Width);
                Top = DockRules.Position(Top + e.VerticalChange, area.Top, area.Height, Height);
            };
            MoveGrip.DragCompleted += (_, _) => SavePlacement();

            ResizeGrip.DragStarted += (_, _) => _resizing = true;
            ResizeGrip.DragDelta += (_, e) =>
            {
                var area = SystemParameters.WorkArea;
                Width = Math.Max(Math.Min(860.0, area.Width - 32.0), Math.Min(area.Width - 32.0, Width + e.HorizontalChange));
                Height = Math.Max(Math.Min(560.0, area.Height - 32.0), Math.Min(area.Height - 32.0, Height + e.VerticalChange));
                Left = DockRules.Position(Left, area.Left, area.Width, Width);
                Top = DockRules.Position(Top, area.Top, area.Height, Height);
                RebuildForSize();
            };
            ResizeGrip.DragCompleted += (_, _) =>
            {
                _resizing = false;
                SavePlacement();
                RebuildForSize();
            };

            PreviewKeyDown += OnPreviewKeyDown;

            _runningTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
            _runningTimer.Tick += (_, _) =>
            {
                if (!IsVisible)
                    return;

                try
                {
                    DockRunningWindows.Scan();
                    Render();
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard 大面板：刷新运行状态失败：{ex.GetType().Name}: {ex.Message}");
                }
            };

            Loaded += (_, _) =>
            {
                try
                {
                    OpenBoard();
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard 大面板：初始化失败：{ex.GetType().Name}: {ex.Message}");
                }
                finally
                {
                    Opacity = 1;
                    _runningTimer.Start();
                }
            };

            Closed += (_, _) =>
            {
                _runningTimer.Stop();
                SavePlacement();
            };
        }

        // ---------------- 展开 / 收起 ----------------

        /// <summary>按原版 Open()：先定尺寸与位置，再构建界面、聚焦搜索框。</summary>
        private void OpenBoard()
        {
            if (_building)
                return;

            var area = SystemParameters.WorkArea;

            double defaultWidth = _model.BoardPlaced && _model.BoardWidth > 0 ? _model.BoardWidth : 1280.0;
            double defaultHeight = _model.BoardPlaced && _model.BoardHeight > 0 ? _model.BoardHeight : 730.0;

            Width = Math.Max(Math.Min(860.0, area.Width - 32.0), Math.Min(area.Width - 32.0, defaultWidth));
            Height = Math.Max(Math.Min(560.0, area.Height - 32.0), Math.Min(area.Height - 32.0, defaultHeight));

            Left = DockRules.Position(
                _model.BoardPlaced ? _model.BoardLeft : area.Left + (area.Width - Width) / 2.0,
                area.Left, area.Width, Width);

            Top = DockRules.Position(
                _model.BoardPlaced ? _model.BoardTop : area.Top + (area.Height - Height) / 2.0,
                area.Top, area.Height, Height);

            _page = 0;
            _editing = false;
            _selected.Clear();

            Build();
            ApplyOpacity();
            Render(animate: true);

            if (_search != null && !_model.BoardHideSearch)
                _search.Focus();
        }

        /// <summary>收起面板。</summary>
        public void Collapse() => Close();

        private void SavePlacement()
        {
            try
            {
                _model.BoardPlaced = true;
                _model.BoardLeft = Left;
                _model.BoardTop = Top;
                _model.BoardWidth = Width;
                _model.BoardHeight = Height;
                DockStore.Save(_model);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 大面板：保存位置失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------------- 构建界面（对应原版 Build） ----------------

        private void Build()
        {
            _building = true;
            try
            {
                ContentGrid.Children.Clear();

                _layout = new Grid { Margin = new Thickness(38, 22, 38, 20) };
                _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(55) });
                _layout.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                _bulkRow = new RowDefinition { Height = new GridLength(0) };
                _layout.RowDefinitions.Add(_bulkRow);
                _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
                _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(45) });
                _layout.RowDefinitions.Add(new RowDefinition { Height = new GridLength(32) });
                ContentGrid.Children.Add(_layout);

                // 顶栏：搜索 + 批量添加 + 多选 + 设置 + 更多 + 关闭
                var header = new Grid();
                header.ColumnDefinitions.Add(new ColumnDefinition());
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(116) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(40) });
                header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(44) });
                _layout.Children.Add(header);

                var searchShell = new Border
                {
                    CornerRadius = new CornerRadius(18),
                    BorderThickness = new Thickness(1),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                    Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                    Margin = new Thickness(10, 0, 18, 9),
                    Visibility = _model.BoardHideSearch ? Visibility.Collapsed : Visibility.Visible
                };
                _searchShell = searchShell;

                var searchGrid = new Grid();
                searchShell.Child = searchGrid;

                _hint = new TextBlock
                {
                    Text = "搜索应用、文件夹…",
                    Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
                    Margin = new Thickness(18, 0, 0, 0),
                    VerticalAlignment = VerticalAlignment.Center,
                    IsHitTestVisible = false
                };
                searchGrid.Children.Add(_hint);

                _search = new TextBox
                {
                    FontSize = 15,
                    Foreground = Brushes.White,
                    Background = Brushes.Transparent,
                    BorderThickness = new Thickness(0),
                    CaretBrush = Brushes.White,
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Padding = new Thickness(18, 0, 18, 0)
                };
                _search.TextChanged += (_, _) =>
                {
                    if (_hint != null)
                        _hint.Visibility = _search.Text.Length != 0 ? Visibility.Hidden : Visibility.Visible;

                    _page = 0;
                    Render(animate: true);
                };
                searchGrid.Children.Add(_search);
                header.Children.Add(searchShell);

                if (_model.BoardHideSearch)
                {
                    var groupName = new TextBlock
                    {
                        Text = _model.BoardHideGroups ? "全部应用" : _currentGroup,
                        Foreground = Brushes.White,
                        FontSize = 18,
                        Margin = new Thickness(12, 0, 0, 8),
                        VerticalAlignment = VerticalAlignment.Center,
                        IsHitTestVisible = false
                    };
                    header.Children.Add(groupName);
                }

                var addButton = BoardButton("+ 批量添加", AddFiles, 106);
                Grid.SetColumn(addButton, 1);
                header.Children.Add(addButton);

                _editSelection = BoardButton(_editing ? "完成" : "多选", () =>
                {
                    _editing = !_editing;
                    _selected.Clear();
                    if (_editSelection != null)
                        _editSelection.Content = _editing ? "完成" : "多选";

                    Render();
                }, 68);
                _editSelection.ToolTip = "跨页勾选，一次移除多个入口";
                Grid.SetColumn(_editSelection, 2);
                header.Children.Add(_editSelection);

                var settingsButton = BoardButton("⚙", () => BoardRequestSettings?.Invoke(), 30);
                settingsButton.ToolTip = "外观、组件与快捷键";
                Grid.SetColumn(settingsButton, 3);
                header.Children.Add(settingsButton);

                var moreButton = BoardButton("···", ShowMoreMenu, 32);
                moreButton.ToolTip = "更多与显示选项";
                Grid.SetColumn(moreButton, 4);
                header.Children.Add(moreButton);

                var closeButton = BoardButton("×", Collapse, 32);
                Grid.SetColumn(closeButton, 5);
                header.Children.Add(closeButton);

                // 分组栏
                var groupBar = new WrapPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    Visibility = _model.BoardHideGroups ? Visibility.Collapsed : Visibility.Visible
                };
                Grid.SetRow(groupBar, 1);
                _layout.Children.Add(groupBar);

                _groups = new ComboBox
                {
                    Width = 145,
                    Height = 30,
                    Margin = new Thickness(8, 0, 10, 0),
                    Foreground = Brushes.White,
                    Background = new SolidColorBrush(Color.FromArgb(25, 255, 255, 255)),
                    BorderBrush = new SolidColorBrush(Color.FromArgb(90, 255, 255, 255)),
                    VerticalContentAlignment = VerticalAlignment.Center,
                    Cursor = Cursors.Hand
                };
                ApplyGroupBoxTheme(_groups);
                _groups.SelectionChanged += (_, _) =>
                {
                    if (_syncingGroups || _groups.SelectedItem is not string picked)
                        return;

                    if (picked != _currentGroup)
                        _selected.Clear();

                    _currentGroup = picked;
                    _page = 0;
                    Render(animate: true);
                };
                groupBar.Children.Add(_groups);


                groupBar.Children.Add(BoardButton("添加已有图标", OpenGroup, 112));
                groupBar.Children.Add(BoardButton("管理分组", ManageGroups, 96));
                groupBar.Children.Add(BoardButton("更多", ShowMoreMenu, 68));

                // 多选操作栏
                _bulkBar = new WrapPanel
                {
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Visibility = Visibility.Collapsed
                };
                Grid.SetRow(_bulkBar, 2);
                _layout.Children.Add(_bulkBar);

                _selectAll = BoardButton("全选结果", () =>
                {
                    foreach (var entry in Filtered())
                        _selected.Add(entry.Path);

                    Render();
                }, 90);
                _bulkBar.Children.Add(_selectAll);

                _clearSelection = BoardButton("取消全选", () =>
                {
                    _selected.Clear();
                    Render();
                }, 90);
                _bulkBar.Children.Add(_clearSelection);

                _deleteSelected = BoardButton("移除已选", RemoveSelected, 112);
                _deleteSelected.Foreground = new SolidColorBrush(Color.FromRgb(255, 155, 155));
                _bulkBar.Children.Add(_deleteSelected);

                _selectionCount = new TextBlock
                {
                    Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
                    VerticalAlignment = VerticalAlignment.Center,
                    Margin = new Thickness(10, 0, 0, 0)
                };
                _bulkBar.Children.Add(_selectionCount);

                _moveGroup = new ComboBox
                {
                    Width = 112,
                    Height = 30,
                    Margin = new Thickness(12, 0, 4, 0),
                    VerticalAlignment = VerticalAlignment.Center
                };
                _bulkBar.Children.Add(_moveGroup);

                _moveSelected = BoardButton("移动", () =>
                {
                    if (_moveGroup?.SelectedItem is string target && _selected.Count > 0)
                    {
                        int moved = BoardRules.Transfer(_model, _selected.ToArray(), _currentGroup, target);
                        DockStore.Save(_model);
                        _selected.Clear();
                        SyncGroups();
                        Render();
                        AppLog.Write($"GlassBoard 大面板：移动 {moved} 个入口到「{target}」");
                    }
                }, 100);
                _bulkBar.Children.Add(_moveSelected);

                _undoRemoval = BoardButton("撤销", () => { });
                _undoRemoval.Visibility = Visibility.Collapsed;

                // 文件夹胶囊 + 图标网格
                var folderArea = new DockPanel { Margin = new Thickness(0, 10, 0, 4) };
                Grid.SetRow(folderArea, 3);
                _layout.Children.Add(folderArea);

                _folderBar = new WrapPanel
                {
                    Margin = new Thickness(0, 0, 0, 8),
                    Visibility = _model.BoardHideGroups ? Visibility.Collapsed : Visibility.Visible
                };
                // 必须全限定：本文件的命名空间就叫 ...GlassBoard.Dock，
                // 直接写 Dock.Top 会被解析成命名空间而不是 Dock 枚举。
                DockPanel.SetDock(_folderBar, System.Windows.Controls.Dock.Top);
                folderArea.Children.Add(_folderBar);

                var tileHost = new Grid();
                folderArea.Children.Add(tileHost);

                _tiles = new WrapPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 0, 0, 8) };
                _scroller = new ScrollViewer
                {
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    PanningMode = PanningMode.VerticalOnly
                };
                _scroller.Content = _tiles;
                tileHost.Children.Add(_scroller);

                _empty = new TextBlock
                {
                    Text = "这里还没有快捷方式\n点「+ 批量添加」把软件、文件或文件夹加进来；也可以从 Dock 右键添加",
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
                    FontSize = 19,
                    LineHeight = 32,
                    Visibility = Visibility.Collapsed
                };
                tileHost.Children.Add(_empty);

                // 翻页
                var pagerBar = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    HorizontalAlignment = HorizontalAlignment.Center
                };
                Grid.SetRow(pagerBar, 4);
                _layout.Children.Add(pagerBar);

                pagerBar.Children.Add(BoardButton("‹", () => ChangePage(-1), 40));

                _pager = new TextBlock
                {
                    Width = 100,
                    TextAlignment = TextAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center,
                    Foreground = Brushes.White,
                    FontSize = 14
                };
                pagerBar.Children.Add(_pager);
                pagerBar.Children.Add(BoardButton("›", () => ChangePage(1), 40));

                // 底部状态栏
                var statusBar = new Grid();
                statusBar.ColumnDefinitions.Add(new ColumnDefinition());
                statusBar.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(230) });
                Grid.SetRow(statusBar, 5);
                _layout.Children.Add(statusBar);

                var statusStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
                _status = new TextBlock
                {
                    Text = "图标可拖到 Dock 固定 · Esc 收起 · ··· 显示选项",
                    Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
                    FontSize = 11,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    VerticalAlignment = VerticalAlignment.Center
                };
                statusStack.Children.Add(_status);

                // 作者要求：不加任何署名水印。
                statusBar.Children.Add(statusStack);

                var opacityPanel = new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Right
                };
                Grid.SetColumn(opacityPanel, 1);
                statusBar.Children.Add(opacityPanel);

                _opacityText = new TextBlock
                {
                    Text = "透明度 " + Math.Round(100 - _model.Dock.OpacityPercent) + "%",
                    Foreground = new SolidColorBrush(Color.FromArgb(170, 255, 255, 255)),
                    Width = 83,
                    VerticalAlignment = VerticalAlignment.Center
                };
                opacityPanel.Children.Add(_opacityText);

                _opacitySlider = new Slider
                {
                    Minimum = 0,
                    Maximum = 100,
                    Value = 100 - _model.Dock.OpacityPercent,
                    Width = 140,
                    TickFrequency = 5,
                    IsSnapToTickEnabled = true,
                    ToolTip = "只改变玻璃背景，图标和文字保持清晰"
                };
                _opacitySlider.ValueChanged += (_, e) =>
                {
                    _model.Dock.OpacityPercent = 100 - e.NewValue;
                    if (_opacityText != null)
                        _opacityText.Text = "透明度 " + Math.Round(e.NewValue) + "%";

                    ApplyOpacity();
                    DockStore.Save(_model);
                };
                opacityPanel.Children.Add(_opacitySlider);

                SyncGroups();
            }
            finally
            {
                _building = false;
            }
        }

        /// <summary>面板打开时请求打开设置（由模块提供）。</summary>
        public Action? BoardRequestSettings { get; set; }

        private void ApplyOpacity()
        {
            double percent = DockRules.GlassOpacity(_model.Dock.OpacityPercent);
            Surface.Background = new SolidColorBrush(Color.FromArgb(
                (byte)Math.Round(percent * 255.0 / 100.0), 0x18, 0x1A, 0x20));

            // 关键：搜索框这类白色叠加层必须跟着一起变淡。
            // 以前只改 Surface.Background，所以把透明度拉到底时左上角还是一块实的白条。
            double overlay = percent / 100.0;

            if (_searchShell != null)
            {
                _searchShell.Background = new SolidColorBrush(
                    Color.FromArgb((byte)Math.Round(25 * overlay), 255, 255, 255));
                _searchShell.BorderBrush = new SolidColorBrush(
                    Color.FromArgb((byte)Math.Round(90 * overlay), 255, 255, 255));
            }

            // 分组下拉同样要跟着变淡（它以前是系统默认的浅色实底，完全不透明）
            if (_groups != null)
            {
                _groups.Background = new SolidColorBrush(
                    Color.FromArgb((byte)Math.Round(25 * overlay), 255, 255, 255));
                _groups.BorderBrush = new SolidColorBrush(
                    Color.FromArgb((byte)Math.Round(90 * overlay), 255, 255, 255));
            }

            Surface.Effect = _model.Dock.Material.Shadow
                ? new DropShadowEffect { BlurRadius = 26, ShadowDepth = 4, Opacity = 0.4, Color = Colors.Black }
                : null;

            // 文件夹胶囊的底色是在 RefreshFolders 里生成的，重画一次让它们跟随透明度
            RefreshFolders();
        }

        /// <summary>尺寸变了就重算每页容量并重建（对应原版 BuildForSize）。</summary>
        private void RebuildForSize()
        {
            if (_building || _tiles == null)
                return;

            // 没手动选过档位时，跟随 Windows 桌面图标大小（读注册表；读不到就用中号）
            if (_model.BoardIconSizeFromDesktop)
                _model.BoardIconSize = NearestIconPreset(DesktopIconSize());

            var area = SystemParameters.WorkArea;
            // WrapPanel 里每行 tile 数 = 面板宽度 / (图标大小 + 间距)
            // 翻页 = 上下滚动，所以 _capacity = 每行 tile 数，翻页按行翻
            int tilesPerRow = Math.Max(1, (int)(Width / (_model.BoardIconSize + _model.BoardIconGap * 2)));
            _capacity = tilesPerRow;

            // 不能因为行列数没变就提前返回：图标本身的大小变了也必须重画，
            // 否则拖动“图标大小”滑块会经常毫无反应（用户反馈的问题）。
            Build();
            Render();
        }

        // ---------------- 过滤 / 分页 / 渲染 ----------------

        private List<DockEntry> Filtered()
        {
            string query = _model.BoardHideSearch || _search == null ? string.Empty : _search.Text.Trim();

            return _model.Apps
                .Where(a => {
                    // 分组过滤：
                    //  - 「全部」= 日常软件：系统工具默认藏起来（否则一屏全是英文系统项），
                    //    但被人工“移出系统工具”的会回到这里显示（给误判软件留出口）；
                    //  - 具体分组只显示该分组的入口；
                    //  - 隐藏分组栏时不做分组过滤（多选 / 搜索要用全量）。
                    bool groupMatch = _model.BoardHideGroups
                        || (_currentGroup == BoardRules.All
                            ? !BoardRules.IsSystemEntry(a)
                            : BoardRules.InGroup(a, _currentGroup));

                    return groupMatch && DockRules.Matches(a.DisplayName, query);
                })
                .ToList();
        }

        /// <summary>对应原版 Render：更新多选栏、文件夹胶囊、瓦片与翻页文字。</summary>
        private void Render(bool animate = false)
        {
            if (_tiles == null && !_building)
                return;

            try
            {
                UpdateSelectionBar();
                RefreshFolders();

                if (_tiles == null)
                    return;

                _tiles.Children.Clear();

                                var list = Filtered();

                if (_pager != null)
                    _pager.Text = "共 " + list.Count + " 个";

                if (_empty != null)
                    _empty.Visibility = list.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

                // 不再分页：所有入口一次铺满，多了用滚轮 / PageUp / PageDown 滚动。
                // 之前按“每页 N 个”分页，第二页的图标永远进不到前面一页（用户反馈的问题）。
                int index = 0;
                foreach (var item in list)
                {
                    _tiles.Children.Add(Tile(item, index++, animate));
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 大面板：渲染失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>翻页改为滚动：PageUp / PageDown 上下翻一屏。</summary>
        private void ChangePage(int delta)
        {
            try
            {
                if (_scroller == null)
                    return;

                double step = Math.Max(80, _scroller.ViewportHeight * 0.9);
                _scroller.ScrollToVerticalOffset(_scroller.VerticalOffset + delta * step);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 大面板：滚动失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnPreviewKeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Escape:
                    Collapse();
                    e.Handled = true;
                    break;

                case Key.Next:
                    // 翻页：下一页（下一行）
                    int pages = BoardRules.Pages(Filtered().Count, _capacity);
                    _page = Math.Min(pages - 1, _page + 1);
                    Render(animate: true);
                    e.Handled = true;
                    break;

                case Key.Prior:
                    // 翻页：上一页（上一行）
                    _page = Math.Max(0, _page - 1);
                    Render(animate: true);
                    e.Handled = true;
                    break;

                case Key.Enter:
                    if (_search != null && _search.IsKeyboardFocusWithin && !_editing)
                    {
                        var first = Filtered().FirstOrDefault();
                        if (first != null)
                            Launch(first);

                        e.Handled = true;
                    }
                    break;
            }
        }

        // ---------------- 瓦片（对应原版 Tile） ----------------

        private FrameworkElement Tile(DockEntry entry, int index, bool animate)
        {
            bool isSelected = _editing && _selected.Contains(entry.Path);
            var selectedBrush = new SolidColorBrush(Color.FromArgb(48, 111, 167, 255));

            var tile = new Border
            {
                Tag = entry,
                Width = TileWidth,
                Background = isSelected ? selectedBrush : Brushes.Transparent,
                BorderThickness = new Thickness(1),
                BorderBrush = isSelected ? new SolidColorBrush(Color.FromArgb(150, 146, 187, 255)) : Brushes.Transparent,
                CornerRadius = new CornerRadius(18),
                Margin = new Thickness(_model.BoardIconGap),
                Padding = new Thickness(4),
                Cursor = Cursors.Hand,
                ToolTip = entry.DisplayName,
                ContextMenu = _editing ? null : EntryMenu(entry)
            };

            var grid = new Grid();
            tile.Child = grid;

            var stack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            grid.Children.Add(stack);

            var icon = DockIcons.For(entry);
            if (icon != null)
            {
                stack.Children.Add(new Image
                {
                    Source = icon,
                    Width = _model.BoardIconSize,
                    Height = _model.BoardIconSize,
                    // 明确等比缩放：不加这句时不同长宽比的图标会被拉伸变形（用户反馈的“拉扁”）
                    Stretch = Stretch.Uniform,
                    StretchDirection = StretchDirection.Both,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }
            else
            {
                stack.Children.Add(new TextBlock
                {
                    Text = "▫",
                    Foreground = new SolidColorBrush(Color.FromArgb(120, 255, 255, 255)),
                    FontSize = _model.BoardIconSize * 0.6,
                    Height = _model.BoardIconSize,
                    TextAlignment = TextAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Center,
                    Margin = new Thickness(0, 0, 0, 8)
                });
            }

            // 瓦片是固定宽度，名字按这个宽度换行（英文在空格处断行，不会把单词劈开）。
            double nameWidth = TileWidth - 16.0;
            stack.Children.Add(new TextBlock
            {
                Text = entry.DisplayName,
                Foreground = Brushes.White,
                FontSize = 12,
                TextAlignment = TextAlignment.Center,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxWidth = nameWidth
            });

            // 运行指示点（原版：5×5 圆点，RGB(135,205,255)）
            grid.Children.Add(new Border
            {
                Tag = "running",
                Width = 5,
                Height = 5,
                CornerRadius = new CornerRadius(3),
                Background = new SolidColorBrush(Color.FromRgb(135, 205, 255)),
                VerticalAlignment = VerticalAlignment.Bottom,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 1),
                ToolTip = "正在运行，点击切回窗口",
                Visibility = _model.Dock.ShowRunning && DockRunningWindows.IsRunning(entry)
                    ? Visibility.Visible
                    : Visibility.Collapsed
            });

            if (_editing)
            {
                grid.Children.Add(new CheckBox
                {
                    IsChecked = isSelected,
                    IsHitTestVisible = false,
                    Focusable = false,
                    HorizontalAlignment = HorizontalAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Top,
                    Margin = new Thickness(2)
                });
            }

            // 悬浮：放大 1+0.025*MotionLevel、上移 3*MotionLevel，170ms 缓动
            var scale = new ScaleTransform(1, 1);
            var lift = new TranslateTransform();
            var group = new TransformGroup();
            group.Children.Add(scale);
            group.Children.Add(lift);
            tile.RenderTransform = group;
            tile.RenderTransformOrigin = new Point(0.5, 0.5);

            bool motion = _model.Dock.MotionEnabled;
            double level = _model.Dock.MotionLevel;

            void Hover(bool over)
            {
                tile.Background = isSelected
                    ? selectedBrush
                    : over ? new SolidColorBrush(Color.FromArgb(24, 255, 255, 255)) : Brushes.Transparent;

                if (!motion)
                    return;

                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                double to = over ? 1.0 + 0.025 * level : 1.0;

                scale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(170)) { EasingFunction = easing });
                scale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(to, TimeSpan.FromMilliseconds(170)) { EasingFunction = easing });
                lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(over ? -3.0 * level : 0.0, TimeSpan.FromMilliseconds(170)) { EasingFunction = easing });
            }

            tile.MouseEnter += (_, _) => Hover(true);
            tile.MouseLeave += (_, _) => Hover(false);

            if (animate && motion)
            {
                var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
                var delay = TimeSpan.FromMilliseconds(Math.Min(index, 14) * 12);

                tile.Opacity = 0;
                tile.BeginAnimation(OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(190))
                {
                    BeginTime = delay,
                    EasingFunction = easing
                });
                lift.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(9.0 * level, 0, TimeSpan.FromMilliseconds(230))
                {
                    BeginTime = delay,
                    EasingFunction = easing
                });
            }

            // ---- 拖拽：拖到另一个图标上调整顺序 ----
            var dragStart = new Point();
            bool pending = false;

            tile.MouseLeftButtonDown += (_, e) =>
            {
                dragStart = e.GetPosition(this);
                pending = !_editing;
            };

            tile.MouseMove += (_, e) =>
            {
                if (!pending || e.LeftButton != MouseButtonState.Pressed)
                    return;

                var now = e.GetPosition(this);
                if (Math.Abs(now.X - dragStart.X) + Math.Abs(now.Y - dragStart.Y) < 10)
                    return;

                pending = false;

                try
                {
                    // 同时带上显示名：Dock 拿到后能直接显示正确名字（不必再反查）
                    var payload = new DataObject();
                    payload.SetData(EntryDragFormat, entry.Path);
                    payload.SetData(EntryDragNameFormat, entry.DisplayName);

                    DragDrop.DoDragDrop(tile, payload, DragDropEffects.Move);
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard 大面板：拖动失败：{ex.GetType().Name}: {ex.Message}");
                }
            };

            // 拖动过程中鼠标可能会拖到 Dock 上，交给 Dock 处理固定；
            // 这里只负责面板内部的重排。
            if (!_editing)
            {
                tile.AllowDrop = true;

                tile.Drop += (_, e) =>
                {
                    try
                    {
                        if (!e.Data.GetDataPresent(EntryDragFormat))
                            return;

                        string? source = e.Data.GetData(EntryDragFormat) as string;

                        if (string.IsNullOrWhiteSpace(source))
                            return;

                        int from = _model.Apps.FindIndex(a => string.Equals(a.Path, source, StringComparison.OrdinalIgnoreCase));
                        int to = _model.Apps.FindIndex(a => string.Equals(a.Path, entry.Path, StringComparison.OrdinalIgnoreCase));

                        if (from < 0 || to < 0 || from == to)
                            return;

                        // 原版语义：从右往左拖就插到目标前面
                        if (BoardRules.Reorder(_model, source, entry.Path, before: from > to))
                        {
                            DockStore.Save(_model);
                            Render();
                        }

                        e.Handled = true;
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"GlassBoard 大面板：重排失败：{ex.GetType().Name}: {ex.Message}");
                    }
                };
            }

            tile.MouseLeftButtonUp += (_, e) =>
            {
                pending = false;

                if (_editing)
                {
                    if (!_selected.Remove(entry.Path))
                        _selected.Add(entry.Path);

                    Render();
                    e.Handled = true;
                    return;
                }

                Launch(entry);
            };

            return tile;
        }

        // ---------------- 文件夹胶囊（对应原版 RefreshFolders） ----------------

        private void RefreshFolders()
        {
            if (_folderBar == null)
                return;

            _folderBar.Children.Clear();

            // 胶囊顺序：全部 → 系统工具 → 未分组 → 其余分组（系统工具固定放第二栏）
            var chipNames = new List<string> { BoardRules.All };

            if (_model.Groups.Any(g => string.Equals(g, "系统工具", StringComparison.Ordinal)))
                chipNames.Add("系统工具");

            chipNames.Add(BoardRules.None);
            chipNames.AddRange(_model.Groups.Where(g => !string.Equals(g, "系统工具", StringComparison.Ordinal)));

            foreach (string name in chipNames)
            {
                // 「全部」显示的是排除系统工具后的个数，计数必须跟实际内容一致
                int count = name == BoardRules.All
                    ? BoardRules.VisibleAllCount(_model)
                    : BoardRules.Count(_model, name);
                double overlay = DockRules.GlassOpacity(_model.Dock.OpacityPercent) / 100.0;
                var normal = name == _currentGroup
                    ? new SolidColorBrush(Color.FromArgb((byte)Math.Round(55 * overlay), 130, 180, 255))
                    : new SolidColorBrush(Color.FromArgb((byte)Math.Round(22 * overlay), 255, 255, 255));

                var chip = new Border
                {
                    Tag = name,
                    CornerRadius = new CornerRadius(12),
                    Background = normal,
                    Padding = new Thickness(10, 6, 10, 6),
                    Margin = new Thickness(0, 0, 6, 6),
                    Cursor = Cursors.Hand,
                    ToolTip = name == BoardRules.All ? "查看全部入口" : "点击筛选该分组"
                };

                var row = new StackPanel { Orientation = Orientation.Horizontal };
                row.Children.Add(new TextBlock
                {
                    Text = name + "  " + count,
                    Foreground = Brushes.White,
                    FontSize = 12,
                    VerticalAlignment = VerticalAlignment.Center
                });
                chip.Child = row;

                string captured = name;
                chip.MouseLeftButtonUp += (_, e) =>
                {
                    _currentGroup = captured;
                    _page = 0;
                    SyncGroups();
                    Render(animate: true);
                    e.Handled = true;
                };

                // 拖到分组胶囊 = 把入口移进这个分组。
                // 「全部」只是筛选器，不能当落点（BoardRules.Move 对它也会抛异常）。
                if (!_editing && name != BoardRules.All)
                {
                    chip.AllowDrop = true;

                    var highlight = new SolidColorBrush(Color.FromArgb((byte)Math.Round(70 * overlay), 130, 200, 255));

                    chip.DragOver += (_, e) =>
                    {
                        if (!e.Data.GetDataPresent(EntryDragFormat))
                            return;

                        e.Effects = DragDropEffects.Move;
                        chip.Background = highlight;
                        e.Handled = true;
                    };

                    chip.DragLeave += (_, _) => chip.Background = normal;

                    chip.Drop += (_, e) =>
                    {
                        chip.Background = normal;

                        try
                        {
                            if (!e.Data.GetDataPresent(EntryDragFormat))
                                return;

                            string? source = e.Data.GetData(EntryDragFormat) as string;
                            if (string.IsNullOrWhiteSpace(source))
                                return;

                            int moved = BoardRules.Move(_model, new[] { source! }, captured);
                            if (moved > 0)
                            {
                                DockStore.Save(_model);
                                SyncGroups();
                                Render(animate: true);
                                AppLog.Write($"GlassBoard 大面板：拖动 {moved} 个入口到「{captured}」");
                            }

                            e.Handled = true;
                        }
                        catch (Exception ex)
                        {
                            AppLog.Write($"GlassBoard 大面板：拖入分组失败：{ex.GetType().Name}: {ex.Message}");
                        }
                    };
                }

                _folderBar.Children.Add(chip);
            }
        }

        // ---------------- 分组同步与菜单 ----------------

        private void SyncGroups()
        {
            if (_groups == null)
                return;

            _syncingGroups = true;
            try
            {
                var list = new[] { BoardRules.All, BoardRules.None }.Concat(_model.Groups).ToList();

                _groups.ItemsSource = list;
                if (!list.Contains(_currentGroup))
                {
                    _currentGroup = BoardRules.All;
                    _selected.Clear();
                    _page = 0;
                }

                _groups.SelectedItem = _currentGroup;

                if (_moveGroup != null)
                {
                    var previous = _moveGroup.SelectedItem as string;
                    var targets = list.Skip(1).ToList();

                    _moveGroup.ItemsSource = targets;
                    _moveGroup.SelectedItem = targets.Contains(previous ?? string.Empty) ? previous : targets.FirstOrDefault();
                }
            }
            finally
            {
                _syncingGroups = false;
            }
        }

        private void UpdateSelectionBar()
        {
            if (_bulkBar == null || _bulkRow == null)
                return;

            _bulkBar.Visibility = _editing ? Visibility.Visible : Visibility.Collapsed;
            _bulkRow.Height = _editing ? GridLength.Auto : new GridLength(0);

            if (_selectionCount != null)
                _selectionCount.Text = _editing ? $"已选 {_selected.Count} · 跨页保留" : string.Empty;

            if (_deleteSelected != null)
                _deleteSelected.Content = $"移除已选（{_selected.Count}）";

            if (_deleteSelected != null) _deleteSelected.IsEnabled = _editing && _selected.Count > 0;
            if (_clearSelection != null) _clearSelection.IsEnabled = _editing && _selected.Count > 0;
            if (_moveSelected != null) _moveSelected.IsEnabled = _editing && _selected.Count > 0;
            if (_selectAll != null) _selectAll.IsEnabled = _editing;
        }

        private void RemoveSelected()
        {
            if (_selected.Count == 0)
                return;

            var wanted = new HashSet<string>(_selected, StringComparer.OrdinalIgnoreCase);
            int removed = _model.Apps.RemoveAll(a => wanted.Contains(a.Path));

            _selected.Clear();
            _model.Normalize();
            DockStore.Save(_model);
            SyncGroups();
            Render();

            AppLog.Write($"GlassBoard 大面板：移除 {removed} 个入口");
        }

        private void ManageGroups() => ShowMoreMenu();

        private void OpenGroup()
        {
            try
            {
                var dialog = new Microsoft.Win32.OpenFileDialog
                {
                    Title = "添加已有图标（多选）",
                    Multiselect = true,
                    Filter = "可执行与快捷方式 (*.exe;*.lnk;*.url;*.bat;*.cmd)|*.exe;*.lnk;*.url;*.bat;*.cmd|所有文件 (*.*)|*.*"
                };

                var owner = Application.Current?.MainWindow;
                bool? picked = owner != null ? dialog.ShowDialog(owner) : dialog.ShowDialog();
                if (picked != true)
                    return;

                int added = AddPaths(dialog.FileNames);
                SyncGroups();
                Render();
                AppLog.Write($"GlassBoard 大面板：添加 {added} 个入口");
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 大面板：添加失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void AddFiles() => OpenGroup();

        /// <summary>加入入口并按关键词自动分组（原版导入后的行为）。</summary>
        private int AddPaths(IEnumerable<string> paths)
        {
            int added = 0;

            foreach (string path in paths)
            {
                if (_model.Apps.Any(a => string.Equals(a.Path, path, StringComparison.OrdinalIgnoreCase)))
                    continue;

                var entry = new DockEntry
                {
                    Path = path,
                    Name = EntryPathRules.Name(path),
                    Pinned = false
                };

                LaunchInfo.Enrich(entry);

                if (_currentGroup != BoardRules.All && _currentGroup != BoardRules.None)
                    entry.Group = _currentGroup;

                _model.Apps.Add(entry);
                added++;
            }

            _model.Normalize();

            if (added > 0)
                BoardRules.ApplyUnassigned(_model);

            DockStore.Save(_model);
            return added;
        }

        private ContextMenu EntryMenu(DockEntry entry)
        {
            var menu = new ContextMenu();
            BoardMenuTheme.Apply(menu, _model.Dock.OpacityPercent);

            var open = new MenuItem { Header = "打开" };
            open.Click += (_, _) => Launch(entry);
            menu.Items.Add(open);

            var locate = new MenuItem { Header = "打开文件所在位置" };
            locate.Click += (_, _) => RevealInExplorer(entry);
            menu.Items.Add(locate);

            var pin = new MenuItem { Header = entry.Pinned ? "取消 Dock 固定" : "固定到 Dock" };
            pin.Click += (_, _) =>
            {
                entry.Pinned = !entry.Pinned;
                _model.Normalize();
                DockStore.Save(_model);
                AppLog.Write($"GlassBoard 大面板：{(entry.Pinned ? "固定到" : "取消固定")} Dock：{entry.DisplayName}");
            };
            menu.Items.Add(pin);

            menu.Items.Add(new Separator());

            var move = new MenuItem { Header = "移动到分组" };
            foreach (string group in new[] { BoardRules.None }.Concat(_model.Groups))
            {
                var item = new MenuItem { Header = group, IsChecked = BoardRules.InGroup(entry, group) };
                string target = group;
                item.Click += (_, _) =>
                {
                    BoardRules.Move(_model, new[] { entry.Path }, target);
                    DockStore.Save(_model);
                    SyncGroups();
                    Render();
                };
                move.Items.Add(item);
            }
            menu.Items.Add(move);

            // 被自动归类误判进「系统工具」的软件，可以一键移出来：
            // 移出后回到「全部」显示，且以后每次启动的自动对照不会再把它塞回去。
            if (BoardRules.IsSystemEntry(entry))
            {
                var rescue = new MenuItem { Header = "移出系统工具（回到全部显示）" };
                rescue.Click += (_, _) =>
                {
                    entry.ManualNonSystem = true;
                    entry.Group = null;
                    entry.ExtraGroups = new List<string>();
                    _model.Normalize();
                    DockStore.Save(_model);
                    SyncGroups();
                    Render();
                    AppLog.Write($"GlassBoard 大面板：人工移出系统工具「{entry.DisplayName}」");
                };
                menu.Items.Add(rescue);

                menu.Items.Add(new Separator());
            }

            var remove = new MenuItem { Header = "移除入口" };
            remove.Click += (_, _) =>
            {
                _model.Apps.RemoveAll(a => string.Equals(a.Path, entry.Path, StringComparison.OrdinalIgnoreCase));
                _model.Normalize();
                DockStore.Save(_model);
                SyncGroups();
                Render();
            };
            menu.Items.Add(remove);

            return menu;
        }

        private void ShowMoreMenu()
        {
            var menu = new ContextMenu();
            BoardMenuTheme.Apply(menu, _model.Dock.OpacityPercent);

            var search = new MenuItem { Header = "显示搜索栏", IsCheckable = true, IsChecked = !_model.BoardHideSearch };
            search.Click += (_, _) =>
            {
                _model.BoardHideSearch = !_model.BoardHideSearch;
                DockStore.Save(_model);
                Build();
                Render();
            };
            menu.Items.Add(search);

            var groups = new MenuItem { Header = "显示分组栏", IsCheckable = true, IsChecked = !_model.BoardHideGroups };
            groups.Click += (_, _) =>
            {
                _model.BoardHideGroups = !_model.BoardHideGroups;
                DockStore.Save(_model);
                Build();
                Render();
            };
            menu.Items.Add(groups);

            var autoGroup = new MenuItem { Header = "把未分组自动归类" };
            autoGroup.Click += (_, _) =>
            {
                int count = BoardRules.ApplyUnassigned(_model);
                DockStore.Save(_model);
                SyncGroups();
                Render();
                AppLog.Write($"GlassBoard 大面板：自动归组 {count} 个入口");
            };
            menu.Items.Add(autoGroup);

            menu.Items.Add(new Separator());

            var reset = new MenuItem { Header = "重置面板位置与大小" };
            reset.Click += (_, _) =>
            {
                _model.BoardPlaced = false;
                DockStore.Save(_model);
                OpenBoard();
            };
            menu.Items.Add(reset);

            menu.Items.Add(new Separator());

            var close = new MenuItem { Header = "收起面板（Esc）" };
            close.Click += (_, _) => Collapse();
            menu.Items.Add(close);

            menu.PlacementTarget = this;
            menu.IsOpen = true;
        }

        // ---------------- 启动与工具 ----------------

        private void Launch(DockEntry entry)
        {
            try
            {
                if (DockRunningWindows.IsRunning(entry) && DockRunningWindows.Activate(entry))
                    return;

                entry.LaunchTarget ??= entry.Path;
                string target = string.IsNullOrWhiteSpace(entry.LaunchTarget) ? entry.Path : entry.LaunchTarget;

                if (string.IsNullOrWhiteSpace(target))
                    return;

                if (entry.IsFolder || Directory.Exists(target))
                {
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
                    return;
                }

                var info = new ProcessStartInfo(target) { UseShellExecute = true };
                if (!string.IsNullOrWhiteSpace(entry.LaunchArguments))
                    info.Arguments = entry.LaunchArguments;

                Process.Start(info);
                AppLog.Write($"GlassBoard 大面板：启动 {target}");

                Collapse();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 大面板：启动失败 {entry.Path}：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RevealInExplorer(DockEntry entry)
        {
            try
            {
                string target = string.IsNullOrWhiteSpace(entry.LaunchTarget) ? entry.Path : entry.LaunchTarget;

                if (File.Exists(target))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{target}\"") { UseShellExecute = true });
                else if (Directory.Exists(target))
                    Process.Start(new ProcessStartInfo("explorer.exe", $"\"{target}\"") { UseShellExecute = true });
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 大面板：打开所在位置失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>面板上的小按钮（原版 Button 的等价实现）。</summary>
        private static Button BoardButton(string content, Action action, double width = double.NaN)
        {
            var button = new Button
            {
                Content = content,
                Height = 30,
                Width = width,
                Margin = new Thickness(4, 0, 4, 0),
                FontSize = 12,
                Foreground = Brushes.White,
                Background = new SolidColorBrush(Color.FromArgb(38, 255, 255, 255)),
                BorderThickness = new Thickness(0),
                Cursor = Cursors.Hand,
                Template = RoundedButtonTemplate()
            };

            button.Click += (_, _) =>
            {
                try
                {
                    action();
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard 大面板：操作失败：{ex.GetType().Name}: {ex.Message}");
                }
            };

            return button;
        }

        private static ControlTemplate RoundedButtonTemplate()
        {
            var border = new FrameworkElementFactory(typeof(Border));
            border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));

            var presenter = new FrameworkElementFactory(typeof(ContentPresenter));
            presenter.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
            presenter.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);

            border.AppendChild(presenter);
            return new ControlTemplate(typeof(Button)) { VisualTree = border };
        }

        /// <summary>Windows 桌面图标大小（注册表 IconSize），读不到返回 48。</summary>
        private static int DesktopIconSize()
        {
            try
            {
                using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                    @"Software\Microsoft\Windows\Shell\Bags\1\Desktop");

                if (key?.GetValue("IconSize") is int size && size > 0)
                    return size;
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 大面板：读取桌面图标大小失败：{ex.GetType().Name}: {ex.Message}");
            }

            return 48;
        }

        /// <summary>把任意数值归到最接近的档位（小 32 / 中 48 / 大 96）。</summary>
        private static double NearestIconPreset(int size)
        {
            if (size <= 40)
                return 32.0;

            if (size <= 72)
                return 48.0;

            return 96.0;
        }

        /// <summary>
        /// 给分组下拉套一套和面板一致的玻璃外观。
        /// WPF 自带的 ComboBox 是浅色实底，放在深色玻璃上很突兵（用户反馈“下拉没透明化”）。
        /// 模板里底色/描边绑定到 ComboBox 自身属性，于是 ApplyOpacity() 能一并把它们变淡；
        /// 模板解析失败就退回默认外观，不影响功能。
        /// </summary>
        private static void ApplyGroupBoxTheme(ComboBox box)
        {
            const string templateXaml = @"<ControlTemplate xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"" TargetType=""ComboBox""><Grid><ToggleButton Focusable=""False"" ClickMode=""Press"" Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}"" IsChecked=""{Binding IsDropDownOpen, Mode=TwoWay, RelativeSource={RelativeSource TemplatedParent}}""><ToggleButton.Template><ControlTemplate TargetType=""ToggleButton""><Border CornerRadius=""8"" BorderThickness=""1"" Background=""{TemplateBinding Background}"" BorderBrush=""{TemplateBinding BorderBrush}""/></ControlTemplate></ToggleButton.Template></ToggleButton><ContentPresenter Margin=""10,0,26,0"" VerticalAlignment=""Center"" IsHitTestVisible=""False"" Content=""{TemplateBinding SelectionBoxItem}"" ContentTemplate=""{TemplateBinding SelectionBoxItemTemplate}""/><Path HorizontalAlignment=""Right"" VerticalAlignment=""Center"" Margin=""0,0,10,0"" Data=""M 0 0 L 4 4 L 8 0"" Stroke=""#B0FFFFFF"" StrokeThickness=""1.5""/><Popup x:Name=""PART_Popup"" AllowsTransparency=""True"" Placement=""Bottom"" Focusable=""False"" IsOpen=""{TemplateBinding IsDropDownOpen}""><Border Margin=""0,4,0,0"" MinWidth=""145"" MaxHeight=""280"" CornerRadius=""10"" BorderThickness=""1"" Background=""#F01A1D24"" BorderBrush=""#5AFFFFFF""><ScrollViewer VerticalScrollBarVisibility=""Auto""><ItemsPresenter/></ScrollViewer></Border></Popup></Grid></ControlTemplate>";

            const string itemXaml = @"<Style xmlns=""http://schemas.microsoft.com/winfx/2006/xaml/presentation"" xmlns:x=""http://schemas.microsoft.com/winfx/2006/xaml"" TargetType=""ComboBoxItem""><Setter Property=""Foreground"" Value=""White""/><Setter Property=""Padding"" Value=""10,6""/><Setter Property=""Template""><Setter.Value><ControlTemplate TargetType=""ComboBoxItem""><Border x:Name=""b"" CornerRadius=""6"" Background=""Transparent"" Padding=""{TemplateBinding Padding}""><ContentPresenter/></Border><ControlTemplate.Triggers><Trigger Property=""IsHighlighted"" Value=""True""><Setter TargetName=""b"" Property=""Background"" Value=""#33FFFFFF""/></Trigger></ControlTemplate.Triggers></ControlTemplate></Setter.Value></Setter></Style>";

            try
            {
                box.Template = (ControlTemplate)XamlReader.Parse(templateXaml);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 大面板：下拉模板解析失败，改用默认外观：{ex.GetType().Name}: {ex.Message}");
            }

            try
            {
                box.ItemContainerStyle = (Style)XamlReader.Parse(itemXaml);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 大面板：下拉项样式解析失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

    }
}
