using System;
using System.Collections.Generic;
using System.Linq;

namespace GameHub.Modules.GlassBoard.Dock
{
    /// <summary>指针释放结果（对应原版 DockPointerResult）。</summary>
    public sealed class DockPointerResult
    {
        public bool Click { get; set; }

        public bool Dragged { get; set; }

        public DockDropTarget? Target { get; set; }
    }

    /// <summary>
    /// 点击 / 拖拽判定（对应原版 DockPointerGesture）：
    /// 超过阈值才算拖拽，否则松手时算点击——这条规则决定“点图标是启动、拖图标是排序”。
    /// </summary>
    public sealed class DockPointerGesture
    {
        private readonly double _startX;
        private readonly double _startY;
        private readonly double _thresholdX;
        private readonly double _thresholdY;

        public string Source { get; }

        public bool Active { get; private set; }

        public bool Dragging { get; private set; }

        public DockPointerGesture(string source, double x, double y, double minimumX, double minimumY)
        {
            Source = source;
            _startX = x;
            _startY = y;
            _thresholdX = Math.Max(1.0, minimumX);
            _thresholdY = Math.Max(1.0, minimumY);
            Active = true;
        }

        public void Move(double x, double y)
        {
            if (Active
                && !double.IsNaN(x) && !double.IsNaN(y)
                && !double.IsInfinity(x) && !double.IsInfinity(y)
                && (Math.Abs(x - _startX) >= _thresholdX || Math.Abs(y - _startY) >= _thresholdY))
            {
                Dragging = true;
            }
        }

        public DockPointerResult Release(double x, double y, bool inside, DockDropTarget? target, bool allowClick = true)
        {
            if (!Active)
                return new DockPointerResult();

            if (double.IsNaN(x) || double.IsNaN(y) || double.IsInfinity(x) || double.IsInfinity(y))
            {
                Active = false;
                return new DockPointerResult { Dragged = Dragging };
            }

            Move(x, y);
            Active = false;

            return new DockPointerResult
            {
                Click = !Dragging && allowClick,
                Dragged = Dragging,
                Target = Dragging && inside ? target : null
            };
        }

        public void Cancel() => Active = false;
    }

    /// <summary>
    /// 滚轮在“常用软件 / 运行中窗口”之间切换的状态机（对应原版 DockWheelState）：
    /// 累计 120 才触发一次，且 280ms 内不重复触发——避免松手时疯狂抖动。
    /// </summary>
    public sealed class DockWheelState
    {
        private long _last = long.MinValue;
        private int _accumulated;

        public bool Running { get; private set; }

        public bool Scroll(int delta, long milliseconds)
        {
            if (delta == 0)
                return false;

            if (_last != long.MinValue && milliseconds - _last < 280)
            {
                _accumulated = 0;
                return false;
            }

            if (Math.Sign(_accumulated) != Math.Sign(delta))
                _accumulated = 0;

            _accumulated += delta;

            if (Math.Abs(_accumulated) < 120)
                return false;

            bool target = _accumulated < 0;
            _accumulated = 0;

            if (target == Running)
                return false;

            Running = target;
            _last = milliseconds;
            return true;
        }

        public void Set(bool running)
        {
            Running = running;
            _accumulated = 0;
        }
    }

    /// <summary>伸出 / 收回的动画关键帧（对应原版 DockRevealRules），末段带回弹。</summary>
    public static class DockRevealRules
    {
        public static double[] Stops(double current, double target, double intensity, double top)
        {
            double step = Math.Min(12.0, Math.Abs(current - target) * 0.07)
                          * Math.Max(0.5, Math.Min(1.5, intensity));

            return new[] { current, Math.Max(top, target - step), target + step * 0.22, target };
        }
    }

    /// <summary>
    /// 自动隐藏判定（对应原版 AutoHidePolicy）：
    /// 鼠标悬停 / 贴边 / 被占用时都算“看过”，只有离开且超过延迟才真正藏起来。
    /// </summary>
    public sealed class AutoHidePolicy
    {
        private DateTime _lastSeen = DateTime.MinValue;

        public bool Hidden;

        public bool Update(DateTime now, bool enabled, bool over, bool edge, bool blocked, double delay)
        {
            if (!enabled || over || edge || blocked)
            {
                _lastSeen = now;
                Hidden = false;
                return Hidden;
            }

            if (_lastSeen == DateTime.MinValue)
            {
                _lastSeen = now;
                return Hidden;
            }

            if ((now - _lastSeen).TotalMilliseconds >= delay)
                Hidden = true;

            return Hidden;
        }
    }

    public sealed class DockPinResult
    {
        public int Added { get; set; }

        public int Skipped { get; set; }

        public List<string> Paths { get; } = new();
    }

    /// <summary>
    /// 把入口固定到 Dock（对应原版 DockPinRules.Apply）。
    /// 原版会在固定时同时清理“曾经移除”的记录；GameHub 侧暂不做移除流水，
    /// 因此这里只处理固定与插入位置，其余行为保持一致。
    /// </summary>
    public static class DockPinRules
    {
        /// <summary>用户可固定的最大图标数（用户要求：最多 8 个）。</summary>
        public const int MaxPinned = 8;

        public static DockPinResult Apply(DockModel model, IEnumerable<DockEntry> candidates, DockDropTarget? target)
        {
            var result = new DockPinResult();
            var moved = new List<DockEntry>();

            foreach (var candidate in candidates)
            {
                if (candidate == null || string.IsNullOrWhiteSpace(candidate.Path))
                    continue;

                var entry = model.Apps.FirstOrDefault(a => a.Pinned && string.Equals(a.Path, candidate.Path, StringComparison.OrdinalIgnoreCase))
                            ?? model.Apps.FirstOrDefault(a => string.Equals(a.Path, candidate.Path, StringComparison.OrdinalIgnoreCase))
                            ?? candidate;

                if (moved.Contains(entry))
                    continue;

                if (!entry.Pinned && model.Apps.Count(a => a.Pinned) >= MaxPinned)
                {
                    result.Skipped++;
                    continue;
                }

                if (!model.Apps.Contains(entry))
                    model.Apps.Add(entry);

                if (!entry.Pinned)
                {
                    entry.Pinned = true;
                    result.Added++;
                }

                moved.Add(entry);
                result.Paths.Add(entry.Path);
            }

            if (moved.Count == 0)
                return result;

            var ordered = DockOrderRules.Ordered(model);
            var anchor = string.IsNullOrEmpty(target?.Path)
                ? null
                : ordered.FirstOrDefault(a => string.Equals(a.Path, target!.Path, StringComparison.OrdinalIgnoreCase));

            if (moved.Count == 1 && anchor == moved[0])
                return result;

            int index = anchor == null ? ordered.Count : ordered.IndexOf(anchor) + (target!.Before ? 0 : 1);
            int ahead = ordered.Take(index).Count(a => moved.Contains(a));

            ordered.RemoveAll(a => moved.Contains(a));
            ordered.InsertRange(Math.Max(0, Math.Min(ordered.Count, index - ahead)), moved);

            model.DockOrder = ordered.Select(a => a.Path).ToList();
            return result;
        }
    }
}
