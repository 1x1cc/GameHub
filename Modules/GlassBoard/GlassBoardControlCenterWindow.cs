using GameHub.Modules.GlassBoard.Dock;
using GameHub.Utils;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Effects;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 控制中心面板：单击小球展开的那块玻璃面板。
    ///
    /// 与小球同一套外观配方（GlassBoardTheme），所以球和面板永远是一体的；
    /// 面板里的每一项都是现成功能（桌面净化 / 桌面组件 / 入口），点完立刻重画，
    /// 按钮文字就是当前状态——和设置窗口一个套路。
    ///
    /// 收起方式：再点一次小球、面板右上角 ✕、Esc。
    /// 后续要加“音频输出 / 音量 / 亮度 / 蓝牙”这类行时，直接往 build 里加一行即可。
    /// </summary>
    public sealed class GlassBoardControlCenterWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

        private readonly GlassBoardControlOrbWindow _orb;
        private readonly StackPanel _body = new();
        private readonly Border _surface;

        public GlassBoardControlCenterWindow(GlassBoardControlOrbWindow orb)
        {
            _orb = orb;

            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.Manual;
            SizeToContent = SizeToContent.Height;
            Width = 372;
            UseLayoutRounding = true;
            SnapsToDevicePixels = true;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

            GlassBoardTheme.Apply(this);

            var orbOptions = GlassBoardModule.Settings.ControlOrb;

            _surface = new Border
            {
                CornerRadius = new CornerRadius(18),
                BorderThickness = new Thickness(1),
                BorderBrush = GlassBoardTheme.Line,
                Background = GlassBoardTheme.SurfaceFill(orbOptions.Opacity),
                Padding = new Thickness(16),
                Margin = new Thickness(14),
                Effect = new DropShadowEffect
                {
                    BlurRadius = 22,
                    ShadowDepth = 4,
                    Opacity = 0.32,
                    Color = Colors.Black
                },
                Child = new ScrollViewer
                {
                    // 面板按内容高度自适应；屏幕太矮时自动出现滚动条，不裁切按钮
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
                    Content = _body
                }
            };

            Content = _surface;

            SourceInitialized += (_, _) => HideFromAltTab();

            AppLog.Write("控制中心面板：窗口已创建（GlassBoard 玻璃主题）");
        }

        /// <summary>贴着小球展开：小球在屏幕右半边就摆到左边，否则摆到右边。</summary>
        public void OpenNear()
        {
            var area = SystemParameters.WorkArea;

            // 先定最大高度，再排版：矮屏上宁可滚动，也不要按钮被切掉
            MaxHeight = Math.Max(240.0, area.Height - 28.0);

            Rebuild();

            if (!IsVisible)
                Show();

            UpdateLayout();

            double height = ActualHeight <= 0 ? 420.0 : ActualHeight;

            bool toLeft = _orb.Left + _orb.Width / 2.0 > area.Left + area.Width / 2.0;
            double desired = toLeft ? _orb.Left - Width - 6.0 : _orb.Left + _orb.Width + 6.0;

            Left = DockRules.Position(desired, area.Left, area.Width, Width);
            Top = DockRules.Position(_orb.Top + _orb.Height / 2.0 - height / 2.0, area.Top, area.Height, height);

            try
            {
                Activate();
            }
            catch (Exception ex)
            {
                AppLog.Write($"控制中心面板：激活失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>收起面板（小球侧和面板按钮都走这里，不销毁实例，重开更快）。</summary>
        public void Collapse()
        {
            try
            {
                Hide();
            }
            catch (Exception ex)
            {
                AppLog.Write($"控制中心面板：收起失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        protected override void OnPreviewKeyDown(KeyEventArgs e)
        {
            base.OnPreviewKeyDown(e);

            if (e.Key == Key.Escape)
            {
                Collapse();
                e.Handled = true;
            }
        }

        // ---------------- 内容 ----------------

        private void Rebuild()
        {
            // 透明度可能刚在小球右键菜单里改过，每次展开都按最新配置重刷一遍玻璃底
            var orb = GlassBoardModule.Settings.ControlOrb;
            orb.Normalize();
            _surface.Background = GlassBoardTheme.SurfaceFill(orb.Opacity);

            _body.Children.Clear();

            var header = new Grid { Margin = new Thickness(0, 0, 0, 2) };
            header.ColumnDefinitions.Add(new ColumnDefinition());
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

            header.Children.Add(new TextBlock
            {
                Text = "控制中心",
                FontSize = 16.0,
                FontWeight = FontWeights.SemiBold,
                Foreground = GlassBoardTheme.Ink,
                VerticalAlignment = VerticalAlignment.Center
            });

            var close = new Button
            {
                Content = "✕",
                Width = 34,
                Height = 30,
                MinHeight = 0,
                Padding = new Thickness(0),
                Style = GlassBoardTheme.ButtonStyle()
            };

            close.Click += (_, _) => Collapse();
            Grid.SetColumn(close, 1);
            header.Children.Add(close);
            _body.Children.Add(header);

            _body.Children.Add(new TextBlock
            {
                Text = "单击小球收起；右键小球可调样式与大小。",
                FontSize = 11.5,
                Foreground = GlassBoardTheme.Muted,
                TextWrapping = TextWrapping.Wrap,
                LineHeight = 17.0,
                LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
                Margin = new Thickness(0, 6, 0, 14)
            });

            BuildCleanSection();
            BuildWidgetSection();
            BuildMoreSection();
        }

        /// <summary>桌面净化：任务栏 / 桌面图标 / 自动隐藏 / 一键恢复。</summary>
        private void BuildCleanSection()
        {
            AddSection("桌面净化", "任务栏与桌面图标，随点随还原。", host =>
            {
                bool? taskbar = DesktopVisibility.IsHidden(taskbar: true);
                bool? icons = DesktopVisibility.IsHidden(taskbar: false);

                Add(host, taskbar == true ? "显示任务栏" : "隐藏任务栏",
                    () => Hint(DesktopVisibility.ToggleTaskbar()));

                Add(host, icons == true ? "显示桌面图标" : "隐藏桌面图标",
                    () => Hint(DesktopVisibility.ToggleIcons()));

                try
                {
                    Add(host, TaskbarAutoHide.Enabled ? "关闭自动隐藏" : "开启自动隐藏", () =>
                    {
                        bool enable = !TaskbarAutoHide.Enabled;
                        TaskbarAutoHide.Set(enable);
                        Hint(enable ? "任务栏自动隐藏：已开启" : "任务栏自动隐藏：已关闭");
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Write($"控制中心面板：任务栏自动隐藏不可用：{ex.GetType().Name}: {ex.Message}");
                }

                Add(host, "一键恢复", () =>
                {
                    GlassBoardModule.MarkDisabledByRestore();
                    DesktopVisibility.ShowAll();

                    try
                    {
                        TaskbarAutoHide.ShowAndClear();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"控制中心面板：恢复任务栏自动隐藏失败：{ex.GetType().Name}: {ex.Message}");
                    }

                    Hint("任务栏与桌面图标已全部恢复");
                }, danger: true);
            });
        }

        /// <summary>桌面组件：时钟 / 备忘录 / 天气 / 音乐 / Dock / 大面板。</summary>
        private void BuildWidgetSection()
        {
            AddSection("桌面组件", "显示 / 隐藏，状态会记住。", host =>
            {
                Add(host, ToggleLabel("时钟", GlassBoardModule.ClockVisible), () =>
                {
                    GlassBoardModule.ToggleClock();
                    Hint(GlassBoardModule.ClockVisible ? "时钟已显示" : "时钟已隐藏");
                });

                Add(host, ToggleLabel("备忘录", GlassBoardModule.NotesVisible), () =>
                {
                    GlassBoardModule.ToggleNotes();
                    Hint(GlassBoardModule.NotesVisible ? "备忘录已显示" : "备忘录已隐藏");
                });

                Add(host, ToggleLabel("天气", GlassBoardModule.WeatherVisible), () =>
                {
                    GlassBoardModule.ToggleWeather();
                    Hint(GlassBoardModule.WeatherVisible ? "天气已显示" : "天气已隐藏");
                });

                Add(host, ToggleLabel("音乐", GlassBoardModule.MusicVisible), () =>
                {
                    GlassBoardModule.ToggleMusic();
                    Hint(GlassBoardModule.MusicVisible ? "音乐已显示" : "音乐已隐藏");
                });

                Add(host, ToggleLabel("玻璃 Dock", GlassBoardModule.DockVisible), () =>
                {
                    GlassBoardModule.ToggleDock();
                    Hint(GlassBoardModule.DockVisible ? "Dock 已显示" : "Dock 已隐藏");
                });

                Add(host, ToggleLabel("应用大面板", GlassBoardModule.BoardVisible), () =>
                {
                    GlassBoardModule.ToggleBoard();
                    Hint(GlassBoardModule.BoardVisible ? "大面板已打开" : "大面板已收起");
                });
            });
        }

        /// <summary>更多：设置入口与小球的开关。</summary>
        private void BuildMoreSection()
        {
            var orb = GlassBoardModule.Settings.ControlOrb;

            AddSection("小球", $"样式 {ControlIconRules.Name(orb.IconStyle)} · 直径 {orb.Size:0} · 不透明度 {orb.Opacity:0}% · 图标 {orb.IconSize:0}", host =>
            {
                Add(host, orb.Locked ? "解除位置锁定" : "锁定位置", () =>
                {
                    orb.Locked = !orb.Locked;
                    GlassBoardModule.RefreshControlOrb();
                    Hint(orb.Locked ? "小球位置已锁定" : "小球可以拖动了");
                });

                Add(host, "回到默认位置", () =>
                {
                    _orb.ResetPlacement();
                    Hint("小球已回到主屏右下角");
                });

                Add(host, "GlassBoard 设置", () => GlassBoardModule.OpenSettings());

                Add(host, "隐藏小球", () => GlassBoardModule.HideControlOrb(), danger: true);
            });
        }

        // ---------------- 通用构件 ----------------

        private void AddSection(string title, string description, Action<WrapPanel> build)
        {
            var card = GlassBoardTheme.Section(title, description);

            if (card.Child is StackPanel stack)
            {
                var host = new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Margin = new Thickness(0, 2, 0, 0)
                };

                build(host);
                stack.Children.Add(host);
            }

            _body.Children.Add(card);
        }

        private void Add(Panel host, string text, Action action, bool danger = false)
        {
            var button = new Button
            {
                Content = text,
                Style = danger ? GlassBoardTheme.DangerButtonStyle() : GlassBoardTheme.ButtonStyle(),
                MinWidth = 118,
                FontSize = 12.5,
                Padding = new Thickness(10, 8, 10, 8),
                Margin = new Thickness(0, 0, 8, 8)
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
                    AppLog.Write($"控制中心面板：操作失败：{ex.GetType().Name}: {ex.Message}");
                }

                // 状态变了（显示/隐藏、锁定与否）——整块重画一次，按钮文字永远跟得上
                Rebuild();
                UpdateLayout();
            };

            host.Children.Add(button);
        }

        /// <summary>开关类按钮的文字：“显示 X / 隐藏 X”。</summary>
        private static string ToggleLabel(string name, bool visible)
            => (visible ? "隐藏 " : "显示 ") + name;

        private void Hint(string text)
            => AppLog.Write("控制中心面板：" + text);

        /// <summary>不进 Alt+Tab 列表。</summary>
        private void HideFromAltTab()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                    return;

                if (IntPtr.Size == 8)
                {
                    long style = GetWindowLongPtr64(handle, GWL_EXSTYLE).ToInt64();
                    SetWindowLongPtr64(handle, GWL_EXSTYLE, new IntPtr(style | WS_EX_TOOLWINDOW));
                }
                else
                {
                    int style = GetWindowLong32(handle, GWL_EXSTYLE);
                    SetWindowLong32(handle, GWL_EXSTYLE, style | WS_EX_TOOLWINDOW);
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"控制中心面板：设置工具窗口样式失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
