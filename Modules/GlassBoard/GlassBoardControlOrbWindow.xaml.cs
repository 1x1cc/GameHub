using GameHub.Modules.GlassBoard.Dock;
using GameHub.Utils;
using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 桌面控制球（控制中心）窗口，对应原版 GlassBoard 的 ControlOrbWindow。
    ///
    /// 行为逐项对齐原版：
    ///   · 单击：展开 / 收起控制中心面板；
    ///   · 按住拖动：换位置，松手记录，下次启动回到原位；
    ///     位移小于 8 像素仍算“单击”（原版就是用它区分点与拖）；
    ///   · 右键：改图标样式、小球大小、图标大小、不透明度，锁定位置、总在最前；
    ///   · 外观：窗口 = 直径 + 28（边上 14 是投影余量，对应原版 Root.Margin = 14），
    ///     球体是圆形玻璃底 + 1px 描边，球内字形尺寸 Min(IconSize, Size-12)；
    ///   · 不进 Alt+Tab（WS_EX_TOOLWINDOW），被系统藏掉时自动显示回来且不抢焦点。
    /// </summary>
    public partial class GlassBoardControlOrbWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TOOLWINDOW = 0x00000080;
        private const int SW_SHOWNOACTIVATE = 4;

        /// <summary>单击与拖动的判定阈值（原版：曼哈顿距离小于 8 就算单击）。</summary>
        private const double DragThreshold = 8.0;

        /// <summary>小球外的投影余量（原版 Root.Margin = 14，窗口 = 直径 + 2 × 14）。</summary>
        private const double ShadowMargin = 14.0;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
        private static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
        private static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW")]
        private static extern int GetWindowLong32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW")]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int command);

        private readonly DispatcherTimer _maintain;
        private readonly DispatcherTimer _persistDelay;

        private GlassBoardControlCenterWindow? _panel;

        private Point _down;
        private bool _moved;
        private bool _panelWasOpen;

        public GlassBoardControlOrbWindow()
        {
            InitializeComponent();

            _maintain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _maintain.Tick += (_, _) => Maintain();

            // 拖动时会疯狂触发位置变化，延迟一点再落盘，避免一次拖动写几百次文件。
            _persistDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _persistDelay.Tick += (_, _) =>
            {
                _persistDelay.Stop();

                try
                {
                    GlassBoardModule.SaveControlOrbPlacement(Left, Top);
                }
                catch (Exception ex)
                {
                    AppLog.Write($"控制中心小球：保存位置失败：{ex.GetType().Name}: {ex.Message}");
                }
            };

            HitArea.MouseLeftButtonDown += Orb_MouseLeftButtonDown;
            HitArea.MouseMove += Orb_MouseMove;
            HitArea.MouseLeftButtonUp += Orb_MouseLeftButtonUp;

            // 右键菜单每次弹出前重建：勾选状态与当前配置永远一致
            HitArea.ContextMenu = new ContextMenu();
            BoardMenuTheme.Apply(HitArea.ContextMenu, GlassBoardModule.Settings.ControlOrb.Opacity);
            HitArea.ContextMenuOpening += (_, _) => RebuildMenu();

            SourceInitialized += (_, _) => HideFromAltTab();

            Loaded += (_, _) =>
            {
                _maintain.Start();
                AppLog.Write($"控制中心小球：已显示（直径 {Width - ShadowMargin * 2:0}，位置 {Left:0},{Top:0}）");
            };

            Closed += (_, _) =>
            {
                _maintain.Stop();
                _persistDelay.Stop();
                HidePanel();
            };

            // 先按配置把尺寸/外观定下来，再摆位置（位置计算要用到窗口宽高）
            ReloadStyle();
            PlaceFromSettings();
        }

        /// <summary>控制中心面板是否正开着。</summary>
        public bool PanelVisible => _panel is { IsVisible: true };

        // ---------- 外观 ----------

        /// <summary>
        /// 按配置重画小球：直径、圆形圆角、玻璃底透明度、描边、字形与样式、
        /// 总在最前、锁定位置时的鼠标指针。
        /// </summary>
        public void ReloadStyle()
        {
            var orb = GlassBoardModule.Settings.ControlOrb;
            orb.Normalize();

            // 窗口 = 直径 + 投影余量 × 2；球体本身是里面那个尺寸为“直径”的圆
            Width = orb.Size + ShadowMargin * 2.0;
            Height = orb.Size + ShadowMargin * 2.0;

            Shell.CornerRadius = new CornerRadius(orb.Size / 2.0);
            Shell.Background = GlassBoardTheme.SurfaceFill(orb.Opacity);
            Shell.BorderBrush = GlassBoardTheme.Line;

            GlyphHost.Content = ControlIcons.Create(
                orb.IconStyle, GlassBoardTheme.Color(orb.Color), ControlIconRules.OrbGlyphSize(orb));

            Topmost = orb.Topmost;
            Cursor = orb.Locked ? Cursors.Arrow : Cursors.Hand;
            ToolTip = $"控制中心（{ControlIconRules.Name(orb.IconStyle)}）· 单击展开，拖动移动，右键调节图标与大小";

            ClampIntoWorkArea();
        }

        /// <summary>按配置摆放；没记录过就落到主屏右下角（原版默认位置）。</summary>
        private void PlaceFromSettings()
        {
            var orb = GlassBoardModule.Settings.ControlOrb;
            var area = SystemParameters.WorkArea;

            if (orb.Placed)
            {
                Left = DockRules.Position(orb.Left, area.Left, area.Width, Width);
                Top = DockRules.Position(orb.Top, area.Top, area.Height, Height);
                return;
            }

            Left = Math.Max(area.Left + 12.0, area.Right - Width - 24.0);
            Top = Math.Max(area.Top + 12.0, area.Bottom - Height - 104.0);
        }

        /// <summary>把小球收回可视区域（改大直径后可能有一半在屏幕外）。</summary>
        private void ClampIntoWorkArea()
        {
            var area = SystemParameters.WorkArea;

            Left = DockRules.Position(double.IsNaN(Left) ? area.Right - Width - 24.0 : Left,
                area.Left, area.Width, Width);
            Top = DockRules.Position(double.IsNaN(Top) ? area.Bottom - Height - 104.0 : Top,
                area.Top, area.Height, Height);
        }

        /// <summary>“回到默认位置”：忘掉记录，重新摆到主屏右下角。</summary>
        public void ResetPlacement()
        {
            GlassBoardModule.Settings.ControlOrb.Placed = false;
            PlaceFromSettings();
            GlassBoardModule.SaveControlOrbPlacement(Left, Top, placed: false);
        }

        // ---------- 单击 / 拖动 ----------

        private void Orb_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            _down = e.GetPosition(this);
            _moved = false;
            _panelWasOpen = PanelVisible;
        }

        private void Orb_MouseMove(object sender, MouseEventArgs e)
        {
            if (e.LeftButton != MouseButtonState.Pressed || _moved)
                return;

            if (GlassBoardModule.Settings.ControlOrb.Locked)
                return;

            var point = e.GetPosition(this);

            if (Math.Abs(point.X - _down.X) + Math.Abs(point.Y - _down.Y) < DragThreshold)
                return;

            _moved = true;
            HidePanel();

            try
            {
                // DragMove 会一直阻塞到松开鼠标，返回后位置就是最终位置
                DragMove();
                ClampIntoWorkArea();

                _persistDelay.Stop();
                _persistDelay.Start();
            }
            catch (InvalidOperationException)
            {
                // 没按住左键时 DragMove 会抛这个，忽略
            }
            catch (Exception ex)
            {
                AppLog.Write($"控制中心小球：拖动失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Orb_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
        {
            if (!_moved)
            {
                if (_panelWasOpen)
                    HidePanel();
                else
                    OpenPanel();
            }

            e.Handled = true;
        }

        // ---------- 控制中心面板 ----------

        public void OpenPanel()
        {
            try
            {
                if (_panel == null)
                {
                    _panel = new GlassBoardControlCenterWindow(this);
                    _panel.Closed += (_, _) => _panel = null;
                }

                _panel.OpenNear();
            }
            catch (Exception ex)
            {
                AppLog.Write($"控制中心小球：展开面板失败：{ex.GetType().Name}: {ex.Message}");
                _panel = null;
            }
        }

        public void HidePanel()
        {
            try
            {
                _panel?.Collapse();
            }
            catch (Exception ex)
            {
                AppLog.Write($"控制中心小球：收起面板失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------- 右键菜单 ----------

        /// <summary>右键菜单：样式、大小、图标大小、不透明度、锁定、总在最前、设置、隐藏。</summary>
        private void RebuildMenu()
        {
            if (HitArea.ContextMenu is not ContextMenu menu)
                return;

            var orb = GlassBoardModule.Settings.ControlOrb;
            orb.Normalize();

            menu.Items.Clear();
            BoardMenuTheme.Apply(menu, orb.Opacity);

            var style = new MenuItem { Header = "图标样式" };
            for (int i = 0; i < ControlIconRules.Styles.Length; i++)
            {
                string key = ControlIconRules.Styles[i];
                var item = new MenuItem
                {
                    Header = ControlIconRules.Names[i],
                    IsCheckable = true,
                    IsChecked = orb.IconStyle == key
                };

                item.Click += (_, _) => Change(o => o.IconStyle = key);
                style.Items.Add(item);
            }
            menu.Items.Add(style);

            var size = new MenuItem { Header = "小球大小" };
            foreach (var (label, value) in new[]
                     {
                         ("小 44", 44.0), ("标准 54", 54.0), ("大 64", 64.0), ("特大 74", 74.0)
                     })
            {
                var item = new MenuItem
                {
                    Header = label,
                    IsCheckable = true,
                    IsChecked = Math.Abs(orb.Size - value) < 0.5
                };

                item.Click += (_, _) => Change(o => o.Size = value);
                size.Items.Add(item);
            }
            menu.Items.Add(size);

            var icon = new MenuItem { Header = "图标大小" };
            foreach (var (label, value) in new[]
                     {
                         ("小 18", 18.0), ("标准 26", 26.0), ("大 34", 34.0), ("特大 42", 42.0)
                     })
            {
                var item = new MenuItem
                {
                    Header = label,
                    IsCheckable = true,
                    IsChecked = Math.Abs(orb.IconSize - value) < 0.5
                };

                item.Click += (_, _) => Change(o => o.IconSize = value);
                icon.Items.Add(item);
            }
            menu.Items.Add(icon);

            var fade = new MenuItem { Header = "不透明度" };
            foreach (var (label, value) in new[]
                     {
                         ("60%", 60.0), ("80%", 80.0), ("90%", 90.0), ("100%", 100.0)
                     })
            {
                var item = new MenuItem
                {
                    Header = label,
                    IsCheckable = true,
                    IsChecked = Math.Abs(orb.Opacity - value) < 0.5
                };

                item.Click += (_, _) => Change(o => o.Opacity = value);
                fade.Items.Add(item);
            }
            menu.Items.Add(fade);

            menu.Items.Add(new Separator());

            var locked = new MenuItem { Header = "锁定位置", IsCheckable = true, IsChecked = orb.Locked };
            locked.Click += (_, _) => Change(o => o.Locked = !o.Locked);
            menu.Items.Add(locked);

            var topmost = new MenuItem { Header = "总在最前", IsCheckable = true, IsChecked = orb.Topmost };
            topmost.Click += (_, _) => Change(o => o.Topmost = !o.Topmost);
            menu.Items.Add(topmost);

            menu.Items.Add(new Separator());

            var settings = new MenuItem { Header = "控制中心设置…" };
            settings.Click += (_, _) =>
            {
                HidePanel();
                GlassBoardModule.OpenSettings();
            };
            menu.Items.Add(settings);

            var hide = new MenuItem { Header = "隐藏小球（可在设置里重新显示）" };
            hide.Click += (_, _) => GlassBoardModule.HideControlOrb();
            menu.Items.Add(hide);
        }

        /// <summary>改一项外观设置：写盘 + 重画，失手出错也只写日志。</summary>
        private void Change(Action<ControlCenterOptions> change)
        {
            try
            {
                change(GlassBoardModule.Settings.ControlOrb);
                GlassBoardModule.RefreshControlOrb();
            }
            catch (Exception ex)
            {
                AppLog.Write($"控制中心小球：套用设置失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------- 层级维护 ----------

        /// <summary>不进 Alt+Tab 列表（对应原版工具窗口的处理）。</summary>
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
                AppLog.Write($"控制中心小球：设置工具窗口样式失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>自检：被系统或其它程序藏掉就重新显示，且不抢焦点。</summary>
        private void Maintain()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero || !IsVisible)
                    return;

                if (!IsWindowVisible(handle))
                    ShowWindow(handle, SW_SHOWNOACTIVATE);
            }
            catch (Exception ex)
            {
                AppLog.Write($"控制中心小球：自检失败，已停止自检：{ex.GetType().Name}: {ex.Message}");
                _maintain.Stop();
            }
        }
    }
}
