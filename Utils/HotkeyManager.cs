using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace GameHub.Utils
{
    /// <summary>
    /// 全局热键管理器（对应 SoundSwitch 的热键切换能力）。
    /// </summary>
    public sealed class HotkeyManager : IDisposable
    {
        private const int WM_HOTKEY = 0x0312;
        private const uint MOD_ALT = 0x0001;
        private const uint MOD_CONTROL = 0x0002;
        private const uint MOD_SHIFT = 0x0004;
        private const uint MOD_WIN = 0x0008;
        private const uint MOD_NOREPEAT = 0x4000;

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

        [DllImport("user32.dll", SetLastError = true)]
        private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

        private readonly Dictionary<int, Action> _handlers = new();
        private HwndSource? _source;
        private IntPtr _hwnd = IntPtr.Zero;
        private int _nextId = 0x4A01;
        private bool _disposed;

        /// <summary>热键被触发（用于状态提示）。</summary>
        public event Action<string>? HotkeyTriggered;

        public bool Attach(Window window)
        {
            // 已附加过则直接复用，避免重复挂钩
            if (_hwnd != IntPtr.Zero)
                return true;

            var helper = new WindowInteropHelper(window);
            _hwnd = helper.Handle;
            if (_hwnd == IntPtr.Zero)
                return false;

            _source = HwndSource.FromHwnd(_hwnd);
            _source?.AddHook(WndProc);
            return true;
        }

        /// <summary>
        /// 注册热键。gesture 形如 "Ctrl+Alt+F11"。
        /// </summary>
        public bool Register(string gesture, Action callback, out string error)
        {
            error = string.Empty;
            if (_hwnd == IntPtr.Zero)
            {
                error = "窗口句柄未就绪";
                return false;
            }

            if (!HotkeyGesture.TryParse(gesture, out uint modifiers, out uint vk))
            {
                error = "热键格式无效";
                return false;
            }

            int id = _nextId++;
            if (!RegisterHotKey(_hwnd, id, modifiers | MOD_NOREPEAT, vk))
            {
                int winError = Marshal.GetLastWin32Error();
                error = $"热键已被其他程序占用（WinError={winError}）";
                AppLog.Write($"RegisterHotKey 失败：gesture={gesture}，WinError={winError}");
                return false;
            }

            _handlers[id] = callback;
            AppLog.Write($"热键注册成功：gesture={gesture}，id={id}");
            return true;
        }

        /// <summary>注册所有热键并记录日志。</summary>
        public void RegisterAll(Dictionary<int, Action> handlers)
        {
            foreach (var kv in handlers)
            {
                _handlers[kv.Key] = kv.Value;
            }
        }

        private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
        {
            if (msg == WM_HOTKEY)
            {
                int id = wParam.ToInt32();
                if (_handlers.TryGetValue(id, out var handler))
                {
                    AppLog.Write($"热键触发：id={id}");
                    handler();
                    handled = true;
                }
                else
                {
                    // 会到这里通常代表：热键被外部注销 / 回调表与系统状态不一致
                    AppLog.Write($"热键触发但找不到处理器：id={id}（可能已被注销或注册表不一致）");
                }
            }
            return IntPtr.Zero;
        }

        /// <summary>注销全部热键，但保留窗口挂钩（用于设置变更后重新注册）。</summary>
        public void UnregisterAll()
        {
            if (_hwnd != IntPtr.Zero)
            {
                AppLog.Write($"UnregisterAll：正在注销 {_handlers.Count} 个热键");
                foreach (var id in _handlers.Keys)
                    UnregisterHotKey(_hwnd, id);
            }
            _handlers.Clear();
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;

            UnregisterAll();
            _source?.RemoveHook(WndProc);
        }

        internal static (uint modifiers, uint vk) Parse(string gesture)
        {
            HotkeyGesture.TryParse(gesture, out uint m, out uint vk);
            return (m, vk);
        }
    }

    internal static class HotkeyGesture
    {
        public static bool TryParse(string gesture, out uint modifiers, out uint vk)
        {
            modifiers = 0;
            vk = 0;
            if (string.IsNullOrWhiteSpace(gesture))
                return false;

            var parts = gesture.Split('+', StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in parts)
            {
                var part = raw.Trim();
                switch (part.ToUpperInvariant())
                {
                    case "CTRL":
                    case "CONTROL":
                        modifiers |= 0x0002; // MOD_CONTROL
                        break;
                    case "ALT":
                        modifiers |= 0x0001; // MOD_ALT
                        break;
                    case "SHIFT":
                        modifiers |= 0x0004; // MOD_SHIFT
                        break;
                    case "WIN":
                        modifiers |= 0x0008; // MOD_WIN
                        break;
                    default:
                        if (!Enum.TryParse<System.Windows.Input.Key>(part, true, out var key))
                            return false;
                        vk = (uint)System.Windows.Input.KeyInterop.VirtualKeyFromKey(key);
                        break;
                }
            }

            return vk != 0;
        }
    }
}
