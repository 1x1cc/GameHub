using System;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 任务栏“自动隐藏”开关。
    ///
    /// 移植自 GlassBoard v0.9.17 反编译源码（TaskbarAutoHide.cs）：
    /// 通过 SHAppBarMessage 的 ABM_GETSTATE(4) / ABM_SETSTATE(10) 读写任务栏状态，
    /// 只改最低位（ABS_AUTOHIDE）。
    ///
    /// 兜底：改动之前先把原始状态记入 taskbar-autohide.json，
    /// 还原时按记录恢复，绝不硬写一个“猜出来”的值。
    /// </summary>
    public static class TaskbarAutoHide
    {
        [StructLayout(LayoutKind.Sequential)]
        private struct RECT
        {
            public int Left;
            public int Top;
            public int Right;
            public int Bottom;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct APPBARDATA
        {
            public uint Size;
            public IntPtr Window;
            public uint Message;
            public uint Edge;
            public RECT Rect;
            public IntPtr Param;
        }

        private const uint ABM_GETSTATE = 4;
        private const uint ABM_SETSTATE = 10;
        private const uint ABS_AUTOHIDE = 1;

        private static string Journal => Path.Combine(GlassBoardModule.DataFolder, "taskbar-autohide.json");

        [DllImport("shell32.dll")]
        private static extern UIntPtr SHAppBarMessage(uint message, ref APPBARDATA data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr FindWindow(string? cls, string? name);

        public static bool Enabled => (State() & ABS_AUTOHIDE) != 0;

        private static APPBARDATA Data()
        {
            var data = default(APPBARDATA);
            data.Size = (uint)Marshal.SizeOf(typeof(APPBARDATA));
            data.Window = FindWindow("Shell_TrayWnd", null);

            if (data.Window == IntPtr.Zero)
                throw new InvalidOperationException("未找到 Windows 任务栏");

            return data;
        }

        private static uint State()
        {
            APPBARDATA data = Data();
            return (uint)SHAppBarMessage(ABM_GETSTATE, ref data).ToUInt64();
        }

        private static void Apply(uint state)
        {
            APPBARDATA data = Data();
            data.Param = new IntPtr(state);
            SHAppBarMessage(ABM_SETSTATE, ref data);
        }

        /// <summary>只改自动隐藏这一位，其它位保持系统原样。</summary>
        private static uint AutoHideState(uint original, bool enabled)
            => enabled ? (original | ABS_AUTOHIDE) : (original & ~ABS_AUTOHIDE);

        public static void Set(bool enabled)
        {
            uint state = State();
            if ((state & ABS_AUTOHIDE) != 0 == enabled)
                return;

            Directory.CreateDirectory(GlassBoardModule.DataFolder);
            if (!File.Exists(Journal))
                File.WriteAllText(Journal, state.ToString(CultureInfo.InvariantCulture));

            Apply(AutoHideState(state, enabled));
        }

        /// <summary>按记录还原自动隐藏状态（如果本程序改过）。</summary>
        public static void Restore()
        {
            if (!File.Exists(Journal))
                return;

            uint saved = uint.Parse(File.ReadAllText(Journal), CultureInfo.InvariantCulture);
            Apply(AutoHideState(State(), (saved & ABS_AUTOHIDE) != 0));
            File.Delete(Journal);
        }

        /// <summary>无条件关掉自动隐藏并清掉记录（兜底用）。</summary>
        public static void ShowAndClear()
        {
            Apply(AutoHideState(State(), enabled: false));

            if (File.Exists(Journal))
                File.Delete(Journal);
        }
    }
}
