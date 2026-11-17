using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 桌面组件窗口基类（对应原版 DesktopPersistence 里的窗口宿主部分）。
    ///
    /// 统一处理四件事，所有桌面组件（时钟、备忘录，以后的天气/音乐）都复用：
    ///   1) 无边框透明窗口；
    ///   2) 贴在壁纸之上、普通窗口之下：把窗口 owner 设成 shell 窗口，也不进 Alt+Tab；
    ///   3) 每 1.5 秒自检：被系统藏掉就重新显示且不抢焦点（对应原版 RefreshOrder 的判断）；
    ///   4) 按住拖动 + 位置记忆（松手后延迟落盘，避免拖动时频繁写文件）。
    /// </summary>
    public abstract class GlassBoardWidgetWindow : Window
    {
        private const int GWLP_HWNDPARENT = -8;
        private const int SW_SHOWNOACTIVATE = 4;
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x00000020;
        private const int WS_EX_TOOLWINDOW = 0x00000080;

        [DllImport("user32.dll")]
        private static extern IntPtr GetShellWindow();

        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr value);

        [DllImport("user32.dll", EntryPoint = "SetWindowLongW", SetLastError = true)]
        private static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int value);

        [DllImport("user32.dll")]
        private static extern bool IsWindowVisible(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool IsIconic(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int cmd);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongW", SetLastError = true)]
        private static extern int GetWindowLongBase32(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW", SetLastError = true)]
        private static extern IntPtr GetWindowLongBase64(IntPtr hWnd, int nIndex);

        private readonly DispatcherTimer _maintain;
        private readonly DispatcherTimer _persistDelay;

        /// <summary>锁定移动：锁上以后鼠标拖不动。</summary>
        private bool _locked;

        /// <summary>鼠标穿透：点击直接穿过窗口。</summary>
        private bool _clickThrough;

        /// <summary>本组件的悬浮窗选项（设置变后由模块或自身重新套用）。</summary>
        protected FloatingOptions Floating { get; private set; } = new();

        protected GlassBoardWidgetWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ShowInTaskbar = false;
            ResizeMode = ResizeMode.NoResize;
            Topmost = false;
            WindowStartupLocation = WindowStartupLocation.Manual;

            _maintain = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1500) };
            _maintain.Tick += (_, _) => Maintain();

            _persistDelay = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
            _persistDelay.Tick += (_, _) =>
            {
                _persistDelay.Stop();
                try
                {
                    PersistPlacement();
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard 组件：保存位置失败：{ex.GetType().Name}: {ex.Message}");
                }
            };

            Loaded += OnWidgetLoaded;
            Closed += OnWidgetClosed;
        }

        /// <summary>把组件摆回上次的位置（没有记录时由实现方给默认位置）。</summary>
        protected abstract void RestorePlacement();

        /// <summary>把当前位置落盘。</summary>
        protected abstract void PersistPlacement();

        /// <summary>
        /// 按“上次位置是否还有效”摆放窗口；无效（没记录过、或掉到屏幕外）就落到默认位置。
        /// </summary>
        protected void PlaceOnScreen(double left, double top, bool placed, double defaultTop = 120.0)
        {
            var area = SystemParameters.WorkArea;

            bool valid = placed
                && !double.IsNaN(left) && !double.IsNaN(top)
                && left >= area.Left - Width + 40 && left <= area.Right - 40
                && top >= area.Top - Height + 40 && top <= area.Bottom - 40;

            if (valid)
            {
                Left = left;
                Top = top;
            }
            else
            {
                Left = Math.Max(area.Left + 20, area.Right - Width - 40);
                Top = Math.Max(area.Top + 20, area.Top + defaultTop);
            }
        }

        /// <summary>
        /// 按住组件拖动。
        /// 注意：只能绑在非交互元素上（例如标题条），绑在整个面板上会抢走
        /// 输入框和按钮的点击。
        /// </summary>
        protected void DragWidget(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left)
                return;

            // 锁定移动：锁上以后鼠标拖不动（设置里可随时解开）
            if (_locked)
                return;

            try
            {
                DragMove();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 组件：拖动失败：{ex.GetType().Name}: {ex.Message}");
            }

            QueuePersist();
        }

        /// <summary>位置变化后延迟落盘（拖动过程中不要反复写文件）。</summary>
        protected void QueuePersist()
        {
            _persistDelay.Stop();
            _persistDelay.Start();
        }

        /// <summary>
        /// 本组件对应的悬浮窗选项。
        /// 默认按窗口类型自动对应（四个组件的基类都是本类），
        /// 这样不必在每个组件里各写一份覆写；以后要更早绑定可以改成覆写。
        /// </summary>
        protected virtual FloatingOptions ResolveFloatingOptions()
        {
            var settings = GlassBoardModule.Settings;

            if (this is GlassBoardMusicWindow) return settings.MusicFloating;
            if (this is GlassBoardWeatherWindow) return settings.WeatherFloating;
            if (this is GlassBoardNotesWindow) return settings.NotesFloating;

            return new FloatingOptions();
        }

        /// <summary>
        /// 套用悬浮窗选项：锁定移动 / 始终置顶 / 鼠标穿透 / 不透明度。
        /// 窗口加载时自动套一次；设置面改完后由模块再调一次即可即时生效。
        /// </summary>
        public void ApplyFloatingOptions(FloatingOptions? options)
        {
            Floating = options ?? new FloatingOptions();
            Floating.Normalize();

            _locked = Floating.Locked;
            _clickThrough = Floating.ClickThrough;

            Opacity = Floating.Opacity;
            Topmost = Floating.Topmost;

            ApplyWindowStyles();
        }

        /// <summary>
        /// 鼠标穿透 = WS_EX_TRANSPARENT；顺手补 WS_EX_TOOLWINDOW 保证不进 Alt+Tab。
        /// 必须读-改-写：直接把扩展样式整个写掉会丢掉 WS_EX_LAYERED，
        /// 那一下透明分层窗口就没了（AllowsTransparency 靠它）。
        /// </summary>
        private void ApplyWindowStyles()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                    return;

                int exStyle = IntPtr.Size == 8
                    ? (int)GetWindowLongBase64(handle, GWL_EXSTYLE).ToInt64()
                    : GetWindowLongBase32(handle, GWL_EXSTYLE);

                exStyle |= WS_EX_TOOLWINDOW;

                if (_clickThrough)
                    exStyle |= WS_EX_TRANSPARENT;
                else
                    exStyle &= ~WS_EX_TRANSPARENT;

                if (IntPtr.Size == 8)
                    SetWindowLongPtr(handle, GWL_EXSTYLE, new IntPtr(exStyle));
                else
                    SetWindowLong32(handle, GWL_EXSTYLE, exStyle);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 组件：设置窗口样式失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnWidgetLoaded(object sender, RoutedEventArgs e)
        {
            try
            {
                RestorePlacement();
                Own();
                ApplyFloatingOptions(ResolveFloatingOptions());
                _maintain.Start();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 组件：初始化失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void OnWidgetClosed(object? sender, EventArgs e)
        {
            _maintain.Stop();
            _persistDelay.Stop();

            try
            {
                PersistPlacement();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 组件：退出时保存位置失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 把自己挂到 shell 窗口下：组件贴在壁纸之上、普通窗口之下，
        /// 也不会出现在 Alt+Tab 列表里（原版 DesktopPersistence.Own 的做法）。
        /// </summary>
        private void Own()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                var shell = GetShellWindow();
                if (handle == IntPtr.Zero || shell == IntPtr.Zero)
                    return;

                if (IntPtr.Size == 8)
                    SetWindowLongPtr(handle, GWLP_HWNDPARENT, shell);
                else
                    SetWindowLong32(handle, GWLP_HWNDPARENT, shell.ToInt32());
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 组件：贴近桌面失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 每 1.5 秒自检一次：重新贴一次桌面；若被系统藏起来或最小化了，
        /// 用“不抢焦点”的方式显示回来，避免打断用户正在输入的窗口。
        /// </summary>
        private void Maintain()
        {
            try
            {
                var handle = new WindowInteropHelper(this).Handle;
                if (handle == IntPtr.Zero)
                    return;

                Own();

                // 每 1.5 秒把悬浮窗选项对一次：设置面改完不必重启，最多 1.5 秒后生效。
                // 赋同值不会触发重排，开销可忽略。
                ApplyFloatingOptions(ResolveFloatingOptions());

                if (!IsVisible)
                    return;

                if (IsIconic(handle) || !IsWindowVisible(handle))
                    ShowWindow(handle, SW_SHOWNOACTIVATE);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 组件：自检失败，已停止自检：{ex.GetType().Name}: {ex.Message}");
                _maintain.Stop();
            }
        }
    }
}
