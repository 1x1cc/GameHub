using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using WinForms = System.Windows.Forms;

namespace GameHub.Utils
{
    /// <summary>
    /// 系统托盘图标（"最小化到托盘"后用来恢复窗口 / 退出程序）。
    ///
    /// 关键约束（踩过的坑）：
    ///   1. NotifyIcon 必须在 UI 线程创建、也必须在该线程销毁；
    ///      否则托盘区会留下一个点不动、也去不掉的"幽灵图标"。
    ///   2. 退出前一定要 Dispose，并且要先把 Visible 置 false。
    ///   3. NotifyIcon 只接受 System.Drawing.Icon，而项目里没有 .ico，
    ///      所以这里在运行时把 Assets/Icons/gamehub.png 画进 32bpp ARGB 位图再转成
    ///      HICON —— 显式用 ARGB 位图是为了保留透明通道，不然托盘里会出现一块黑底。
    /// </summary>
    public sealed class TrayIconManager : IDisposable
    {
        private const string IconPackUri = "pack://application:,,,/Assets/Icons/gamehub.png";

        private const int IconSize = 32;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool DestroyIcon(IntPtr hIcon);

        private readonly Action _onShowMainWindow;
        private readonly Action _onOpenSettings;
        private readonly Action _onExit;

        private WinForms.NotifyIcon? _notifyIcon;
        private Icon? _icon;
        private bool _disposed;

        /// <param name="onShowMainWindow">双击图标 / 菜单"显示主界面"时调用。</param>
        /// <param name="onOpenSettings">菜单"软件设置"时调用。</param>
        /// <param name="onExit">菜单"完全退出"时调用。</param>
        public TrayIconManager(Action onShowMainWindow, Action onOpenSettings, Action onExit)
        {
            _onShowMainWindow = onShowMainWindow;
            _onOpenSettings = onOpenSettings;
            _onExit = onExit;
        }

        /// <summary>
        /// 托盘图标是否已经创建成功。
        /// 没创建成功时**绝不能**把主窗口藏起来，否则用户就再也找不到程序了。
        /// </summary>
        public bool IsReady => _notifyIcon != null;

        /// <summary>创建托盘图标。必须在 UI 线程调用；重复调用无副作用。</summary>
        public void EnsureCreated()
        {
            if (_notifyIcon != null || _disposed)
                return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            if (!dispatcher.CheckAccess())
            {
                dispatcher.Invoke(new Action(EnsureCreated));
                return;
            }

            _icon = LoadIconFromResource();
            if (_icon == null)
            {
                Log("托盘图标创建失败：图标资源加载失败");
                return;
            }

            var menu = new WinForms.ContextMenuStrip();
            menu.Items.Add(CreateMenuItem("显示主界面", _onShowMainWindow));
            menu.Items.Add(CreateMenuItem("软件设置...", _onOpenSettings));
            menu.Items.Add(new WinForms.ToolStripSeparator());
            menu.Items.Add(CreateMenuItem("完全退出", _onExit));

            var notifyIcon = new WinForms.NotifyIcon
            {
                Icon = _icon,
                Text = "GameHub · 音频与游戏工具箱",
                ContextMenuStrip = menu,
                Visible = false   // 先不显示，属性都设置好再显示，避免托盘区闪一下空图标
            };
            notifyIcon.DoubleClick += (_, _) => Dispatch(_onShowMainWindow);

            notifyIcon.Visible = true;
            _notifyIcon = notifyIcon;
        }

        // ---------- 内部实现 ----------

        private static WinForms.ToolStripMenuItem CreateMenuItem(string text, Action onClick)
        {
            var item = new WinForms.ToolStripMenuItem(text);
            // 托盘菜单的回调在 UI 线程触发，但仍然统一走 Dispatch，避免线程假设出错
            item.Click += (_, _) => Dispatch(onClick);
            return item;
        }

        private static void Dispatch(Action action)
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            if (dispatcher.CheckAccess())
                action();
            else
                dispatcher.BeginInvoke(action);
        }

        /// <summary>把 PNG 资源转换成托盘可用的 Icon。</summary>
        private static Icon? LoadIconFromResource()
        {
            IntPtr hIcon = IntPtr.Zero;
            try
            {
                var info = System.Windows.Application.GetResourceStream(new Uri(IconPackUri));
                if (info?.Stream == null)
                    return null;

                using var stream = info.Stream;
                using var source = new Bitmap(stream);

                // 画进显式的 32bpp ARGB 位图：直接 GetHicon 有可能丢掉透明通道
                using var square = new Bitmap(IconSize, IconSize, PixelFormat.Format32bppArgb);
                using (var g = Graphics.FromImage(square))
                {
                    g.Clear(Color.Transparent);
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.SmoothingMode = SmoothingMode.HighQuality;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.DrawImage(source, 0, 0, IconSize, IconSize);
                }

                hIcon = square.GetHicon();

                // Icon.FromHandle 不持有 handle 的所有权，Clone 出来再销毁 handle，避免句柄泄漏
                return (Icon)Icon.FromHandle(hIcon).Clone();
            }
            catch (Exception ex)
            {
                Log($"图标转换失败 {ex.GetType().Name}: {ex.Message}");
                return null;
            }
            finally
            {
                if (hIcon != IntPtr.Zero)
                    DestroyIcon(hIcon);
            }
        }

        /// <summary>记录日志到 %TEMP%\gamehub.log（与程序其它部分的日志保持一致）。</summary>
        private static void Log(string message)
        {
            try
            {
                System.IO.File.AppendAllText(
                    System.IO.Path.Combine(System.IO.Path.GetTempPath(), "gamehub.log"),
                    $"[{DateTime.Now:HH:mm:ss}] {message}\n");
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>
        /// 销毁托盘图标。在 UI 线程（主窗口 Closed 事件里）调用。
        /// </summary>
        public void Dispose()
        {
            if (_disposed)
                return;

            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                // NotifyIcon 必须在创建它的线程销毁，否则会留下幽灵图标
                try
                {
                    dispatcher.Invoke(new Action(Dispose));
                }
                catch
                {
                    // 调度器已关闭：只能在当前线程尽力而为
                    DisposeCore();
                }
                return;
            }

            DisposeCore();
        }

        private void DisposeCore()
        {
            if (_disposed)
                return;
            _disposed = true;

            if (_notifyIcon != null)
            {
                try
                {
                    // 先 Visible=false 再 Dispose：只 Dispose 有时会在托盘区留下残留图标
                    _notifyIcon.Visible = false;
                    _notifyIcon.ContextMenuStrip?.Dispose();
                    _notifyIcon.Dispose();
                }
                catch
                {
                    // 忽略
                }
                _notifyIcon = null;
            }

            _icon?.Dispose();
            _icon = null;
        }
    }
}
