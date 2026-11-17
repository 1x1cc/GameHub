using System;
using System.Runtime.InteropServices;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 系统媒体键发送（对应原版 GlassBoard 的 SystemMedia.Key / Shortcut，见 MusicEngine.cs:516）。
    ///
    /// 为什么必须用它：本机实测**网易云不注册系统媒体会话**（SMTC 会话数 = 0），
    /// 此时 SMTC 的 TrySkipNextAsync / TryTogglePlayPauseAsync 全是空转，
    /// 于是上一首 / 下一首 / 播放暂停 点了没反应 —— 用户反馈的“完全没用”。
    /// 原版就是直接 SendInput 发系统媒体键（177=上一首、176=下一首、179=播放暂停），
    /// 系统会把按键转给当前正在播放的播放器，和键盘上的媒体键完全等效。
    /// </summary>
    public static class MediaKeys
    {
        /// <summary>虚拟键码（数值与原版写死的一致，别改）。</summary>
        public const ushort PreviousTrack = 0xB1;   // 177 上一首
        public const ushort NextTrack = 0xB0;       // 176 下一首
        public const ushort PlayPause = 0xB3;       // 179 播放 / 暂停
        public const ushort VolumeUp = 0xAF;        // 175 音量 +
        public const ushort VolumeDown = 0xAE;      // 174 音量 -

        private const uint INPUT_KEYBOARD = 1;
        private const uint KEYEVENTF_KEYUP = 0x0002;

        // 注意：SendInput 要求 cbSize == sizeof(INPUT)。INPUT 在 x64 上是 40 字节
        // （4 字节 type + 4 字节对齐 + 32 字节联合体）。只写 KEYBDINPUT 会变成 24 字节，
        // SendInput 直接返回 0 且不干活 —— 这正是“发键没反应”最常见的原因。
        [StructLayout(LayoutKind.Sequential)]
        private struct MOUSEINPUT
        {
            public int dx;
            public int dy;
            public uint mouseData;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct KEYBDINPUT
        {
            public ushort wVk;
            public ushort wScan;
            public uint dwFlags;
            public uint time;
            public IntPtr dwExtraInfo;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HARDWAREINPUT
        {
            public uint uMsg;
            public ushort wParamL;
            public ushort wParamH;
        }

        [StructLayout(LayoutKind.Explicit)]
        private struct InputUnion
        {
            [FieldOffset(0)] public MOUSEINPUT mi;
            [FieldOffset(0)] public KEYBDINPUT ki;
            [FieldOffset(0)] public HARDWAREINPUT hi;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct INPUT
        {
            public uint type;
            public InputUnion u;
        }

        [DllImport("user32.dll", SetLastError = true)]
        private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

        /// <summary>按一下媒体键（按下 + 抬起）。失败只记日志，不抛异常。</summary>
        public static bool Press(ushort virtualKey)
        {
            try
            {
                var inputs = new INPUT[2];

                inputs[0].type = INPUT_KEYBOARD;
                inputs[0].u.ki.wVk = virtualKey;

                inputs[1].type = INPUT_KEYBOARD;
                inputs[1].u.ki.wVk = virtualKey;
                inputs[1].u.ki.dwFlags = KEYEVENTF_KEYUP;

                uint sent = SendInput(2, inputs, Marshal.SizeOf(typeof(INPUT)));
                if (sent != 2)
                {
                    AppLog.Write($"GlassBoard 音乐：发送媒体键 0x{virtualKey:X2} 失败（SendInput 返回 {sent}）");
                    return false;
                }

                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 音乐：发送媒体键异常 0x{virtualKey:X2}：{ex.GetType().Name}: {ex.Message}");
                return false;
            }
        }
    }
}
