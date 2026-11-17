using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameHub.Utils
{
    /// <summary>
    /// 让窗口标题栏 / 边框跟随主题（深色）。
    ///
    /// 背景：WPF 的内容区可以自己画黑，但**标题栏与非客户区边框是系统绘制的**，
    /// 默认永远是浅色——表现就是"内容变黑了、边框还是白的"。
    /// Windows 10 1809+ 提供 DWMWA_USE_IMMERSIVE_DARK_MODE：
    ///   20H1+ 属性号是 20；1809/1903 是 19。两个都试，哪个成功算哪个；
    /// 系统不支持时静默忽略（只是标题栏保持浅色，不影响功能）。
    /// </summary>
    public static class DarkTitleBar
    {
        [DllImport("dwmapi.dll", PreserveSig = true)]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

        private const int DwmaUseImmersiveDarkModeNew = 20;
        private const int DwmaUseImmersiveDarkModeOld = 19;

        /// <summary>给单个窗口套用深色/浅色标题栏（窗口尚未创建句柄时会自动跳过）。</summary>
        public static void Apply(Window? window, bool dark)
        {
            if (window == null)
                return;

            try
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                if (hwnd == IntPtr.Zero)
                    return;

                int value = dark ? 1 : 0;
                if (DwmSetWindowAttribute(hwnd, DwmaUseImmersiveDarkModeNew, ref value, sizeof(int)) != 0)
                    DwmSetWindowAttribute(hwnd, DwmaUseImmersiveDarkModeOld, ref value, sizeof(int));
            }
            catch
            {
                // 旧系统 / 非桌面窗口：忽略
            }
        }

        /// <summary>给当前所有已创建的窗口套用（主题切换时调用）。</summary>
        public static void ApplyToAll(bool dark)
        {
            try
            {
                if (Application.Current == null)
                    return;

                foreach (Window w in Application.Current.Windows)
                    Apply(w, dark);
            }
            catch
            {
                // 忽略
            }
        }
    }
}
