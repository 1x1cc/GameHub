using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 曲名兜底来源：读播放器窗口标题。
    ///
    /// 为什么需要它（本机实测）：Win11 上 SMTC 会话可能是 0 ——
    /// 网易云音乐正在播放时，GlobalSystemMediaTransportControlsSessionManager
    /// 依然返回「会话数 = 0 / GetCurrentSession = null」，系统媒体会话里根本没有它。
    /// 但网易云的窗口标题就是「曲名 - 歌手」（实测：theDOGS - 澤野弘之/mpi），
    /// 所以这里按进程名找到播放器窗口、从标题里解析曲名与歌手，作为兜底。
    /// </summary>
    public sealed class MusicWindowTitleSource
    {
        /// <summary>认识的播放器进程名（大小写不敏感，去掉 .exe）。</summary>
        private static readonly string[] PlayerProcesses =
        {
            "cloudmusic", "QQMusic", "Spotify", "Music.UI", "KuGou",
            "kwmusic", "foobar2000", "AIMP", "PotPlayerMini64"
        };

        /// <summary>标题里出现这些字样说明不是“曲名 - 歌手”，直接跳过。</summary>
        private static readonly string[] JunkMarkers =
        {
            ".exe)", "Default IME", "MSCTFIME", "GDI+ Window",
            "桌面歌词", "迷你播放器", "网易云音乐", "QQ音乐"
        };

        private readonly List<uint> _playerPids = new();
        private DateTime _lastScan = DateTime.MinValue;

        [DllImport("user32.dll")]
        private static extern bool EnumWindows(EnumProc callback, IntPtr param);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder text, int maxCount);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern int GetWindowTextLengthW(IntPtr hWnd);

        [DllImport("user32.dll")]
        private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);

        private delegate bool EnumProc(IntPtr hWnd, IntPtr param);

        /// <summary>
        /// 试着读一条「曲名 - 歌手」。读不到返回 false（调用方显示“没有正在播放的音乐”）。
        /// 不要求窗口可见：播放器最小化到托盘时窗口是隐藏的，标题依然有效。
        /// </summary>
        public bool TryRead(out string title, out string sub)
        {
            title = string.Empty;
            sub = string.Empty;

            try
            {
                RefreshPlayerPids();

                if (_playerPids.Count == 0)
                    return false;

                string? best = null;

                EnumWindows((hWnd, _) =>
                {
                    GetWindowThreadProcessId(hWnd, out uint pid);
                    if (!_playerPids.Contains(pid))
                        return true;

                    int length = GetWindowTextLengthW(hWnd);
                    if (length <= 0)
                        return true;

                    var buffer = new StringBuilder(length + 2);
                    GetWindowTextW(hWnd, buffer, buffer.Capacity);
                    string text = buffer.ToString();

                    if (string.IsNullOrWhiteSpace(text) || text.Length < 5)
                        return true;

                    foreach (var junk in JunkMarkers)
                    {
                        if (text.IndexOf(junk, StringComparison.OrdinalIgnoreCase) >= 0)
                            return true;
                    }

                    // 只认「曲名 - 歌手」这种带分隔符的标题
                    if (text.IndexOf(" - ", StringComparison.Ordinal) < 0)
                        return true;

                    // 多个候选时取最长的那条（通常就是标题栏最完整的那个）
                    if (best == null || text.Length > best.Length)
                        best = text;

                    return true;
                }, IntPtr.Zero);

                if (string.IsNullOrWhiteSpace(best))
                    return false;

                int split = best.IndexOf(" - ", StringComparison.Ordinal);
                title = best[..split].Trim();
                sub = best[(split + 3)..].Trim();

                if (string.IsNullOrWhiteSpace(title))
                    return false;

                if (string.IsNullOrWhiteSpace(sub))
                    sub = "—";

                return true;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 播放器当前是不是**真的在出声**。
        ///
        /// 为什么需要它：网易云这类播放器不向系统注册媒体会话（SMTC 会话数 = 0），
        /// 只读窗口标题的话无法区分“播放中”和“暂停”—— 之前音乐组件因此写死了
        /// “播放中 + 暂停图标”，按键永远不随状态变。
        /// 唯一可靠又轻量的信号是该进程自己的渲染会话状态：
        ///   播放中 → Active；暂停/停止 → Inactive。
        /// </summary>
        /// <summary>诊断用：最近一次“在不在播”判定的过程摘要。</summary>
        public string LastPlayingDiagnostic { get; private set; } = string.Empty;

        public bool TryReadPlaying()
        {
            try
            {
                RefreshPlayerPids();

                if (_playerPids.Count == 0)
                {
                    LastPlayingDiagnostic = "没有播放器进程";
                    return false;
                }

                foreach (uint pid in _playerPids)
                {
                    bool on = AudioSessionMute.IsProcessPlaying((int)pid);

                    LastPlayingDiagnostic = $"pids={string.Join("/", _playerPids)}｜{AudioSessionMute.LastDetail}｜err={AudioSessionMute.LastError}";

                    if (on)
                        return true;
                }
            }
            catch (Exception ex)
            {
                LastPlayingDiagnostic = ex.GetType().Name + ": " + ex.Message;
            }

            return false;
        }

        /// <summary>每 10 秒重新扫一次播放器进程（启动/退出都能跟上）。</summary>
        private void RefreshPlayerPids()
        {
            if ((DateTime.UtcNow - _lastScan).TotalSeconds < 10.0 && _playerPids.Count > 0)
                return;

            _lastScan = DateTime.UtcNow;
            _playerPids.Clear();

            foreach (var process in Process.GetProcesses())
            {
                try
                {
                    foreach (var name in PlayerProcesses)
                    {
                        if (string.Equals(process.ProcessName, name, StringComparison.OrdinalIgnoreCase))
                        {
                            _playerPids.Add((uint)process.Id);
                            break;
                        }
                    }
                }
                catch
                {
                    // 进程可能刚好退出，忽略
                }
                finally
                {
                    process.Dispose();
                }
            }
        }
    }
}
