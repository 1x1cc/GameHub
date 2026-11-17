using System;
using System.Collections.Generic;
using System.Linq;

namespace GameHub.Modules.GlassBoard.Dock
{
    /// <summary>Dock 用到的通用数值规则（对应原版 LayoutRules 的相关部分）。</summary>
    public static class DockRules
    {
        public static double Clamp(double value, double min, double max, double fallback)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value))
                return Math.Max(min, Math.Min(max, value));

            return fallback;
        }

        public static double GlassOpacity(double value)
        {
            if (!double.IsNaN(value) && !double.IsInfinity(value))
                return Math.Max(0.0, Math.Min(100.0, value));

            return 72.0;
        }

        /// <summary>搜索匹配：空格分词，全部命中才算命中（原版 LayoutRules.Matches）。</summary>
        public static bool Matches(string name, string query)
            => (query ?? "").Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries)
                .All(word => (name ?? "").IndexOf(word, StringComparison.CurrentCultureIgnoreCase) >= 0);

        /// <summary>把窗口摆在可视区域内（原版 FeatureRules.Position）。</summary>
        public static double Position(double desired, double origin, double extent, double length)
            => Math.Max(origin, Math.Min(origin + Math.Max(0.0, extent - length), desired));
    }

    /// <summary>Dock 固定项顺序（对应原版 DockOrderRules）。</summary>
    public static class DockOrderRules
    {
        public static List<DockEntry> Ordered(DockModel model)
        {
            var pinned = model.Apps
                .Where(a => a != null && a.Pinned && !string.IsNullOrWhiteSpace(a.Path))
                .ToList();

            var byPath = new Dictionary<string, DockEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in pinned)
            {
                if (!byPath.ContainsKey(entry.Path))
                    byPath[entry.Path] = entry;
            }

            var ordered = new List<DockEntry>();
            foreach (var path in model.DockOrder ?? new List<string>())
            {
                if (path != null && byPath.TryGetValue(path, out var found))
                {
                    ordered.Add(found);
                    byPath.Remove(path);
                }
            }

            foreach (var entry in pinned)
            {
                if (byPath.Remove(entry.Path))
                    ordered.Add(entry);
            }

            return ordered;
        }

        public static bool Move(DockModel model, string source, string target, bool before)
        {
            var ordered = Ordered(model);
            var moving = ordered.FirstOrDefault(a => string.Equals(a.Path, source, StringComparison.OrdinalIgnoreCase));
            if (moving == null || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                return false;

            var anchor = string.IsNullOrEmpty(target)
                ? null
                : ordered.FirstOrDefault(a => string.Equals(a.Path, target, StringComparison.OrdinalIgnoreCase));

            if (!string.IsNullOrEmpty(target) && anchor == null)
                return false;

            var beforeOrder = ordered.Select(a => a.Path).ToArray();

            ordered.Remove(moving);
            int index = anchor == null ? ordered.Count : ordered.IndexOf(anchor) + (before ? 0 : 1);
            ordered.Insert(index, moving);

            var afterOrder = ordered.Select(a => a.Path).ToList();
            if (afterOrder.SequenceEqual(beforeOrder, StringComparer.OrdinalIgnoreCase))
                return false;

            model.DockOrder = afterOrder;
            DockLayoutRules.SyncApps(model);
            return true;
        }

        public static bool Step(DockModel model, string source, int step)
        {
            var ordered = Ordered(model);
            int index = ordered.FindIndex(a => string.Equals(a.Path, source, StringComparison.OrdinalIgnoreCase));
            int next = index + step;

            if (index < 0 || next < 0 || next >= ordered.Count || Math.Abs(step) != 1)
                return false;

            return Move(model, source, ordered[next].Path, step < 0);
        }
    }

    /// <summary>Dock 横向锚定（对应原版 DockAnchorRules）。</summary>
    public static class DockAnchorRules
    {
        public static double Left(double? center, double width, double fallback, double areaLeft, double areaWidth)
            => DockRules.Position(center.HasValue ? center.Value - width / 2.0 : fallback, areaLeft, areaWidth, width);
    }

    /// <summary>拖放落点（对应原版 DockDropTarget）。</summary>
    public sealed class DockDropTarget
    {
        public string? Path { get; set; }

        public bool Before { get; set; }
    }

    /// <summary>
    /// Dock 项目顺序与系统入口可见性（对应原版 DockLayoutRules）。
    /// 系统入口用 glassboard://dock/xxx 这套固定 key，和原版保持一致，
    /// 这样配置能互通、排序规则也无需另写一套。
    /// </summary>
    public static class DockLayoutRules
    {
        public const string Apps = "glassboard://dock/apps";
        public const string Computer = "glassboard://dock/computer";
        public const string Power = "glassboard://dock/power";
        public const string Network = "glassboard://dock/network";
        public const string Settings = "glassboard://dock/settings";

        public static readonly string[] SystemKeys = { Apps, Computer, Power, Network, Settings };

        public static bool SystemItem(string key)
            => SystemKeys.Contains(key, StringComparer.OrdinalIgnoreCase);

        public static bool Visible(DockOptions o, string key)
        {
            return key switch
            {
                Network => o.ShowNetwork,
                Power => o.ShowPower,
                Computer => o.ShowComputer,
                Settings => o.ShowSettings,
                _ => true
            };
        }

        private static List<string> Merge(IEnumerable<string>? saved, List<string> apps)
        {
            var allowed = new HashSet<string>(SystemKeys.Concat(apps), StringComparer.OrdinalIgnoreCase);

            var list = (saved ?? Array.Empty<string>())
                .Where(k => k != null && allowed.Contains(k))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (list.Count == 0)
            {
                return new[] { Apps, Computer }
                    .Concat(apps)
                    .Concat(new[] { Power, Network, Settings })
                    .ToList();
            }

            foreach (var key in SystemKeys)
            {
                if (!list.Contains(key, StringComparer.OrdinalIgnoreCase))
                    list.Add(key);
            }

            foreach (var app in apps)
            {
                if (list.Contains(app, StringComparer.OrdinalIgnoreCase))
                    continue;

                int index = list.FindLastIndex(k => !SystemItem(k));
                if (index < 0)
                    index = list.IndexOf(Computer);

                list.Insert(index < 0 ? list.Count : index + 1, app);
            }

            return list;
        }

        public static List<string> Common(DockModel model, bool visible = false)
        {
            var list = Merge(model.Dock.ItemOrder, DockOrderRules.Ordered(model).Select(a => a.Path).ToList());
            return visible ? list.Where(k => Visible(model.Dock, k)).ToList() : list;
        }

        public static List<string> Running(DockModel model, IEnumerable<string> tasks, bool visible = false)
        {
            var taskList = tasks.ToList();
            var order = model.Dock.RunningOrder;

            if (order == null || order.Count == 0)
            {
                var built = new List<string>();
                int used = 0;

                foreach (var key in Common(model))
                {
                    if (SystemItem(key))
                        built.Add(key);
                    else if (used < taskList.Count)
                        built.Add(taskList[used++]);
                }

                if (used < taskList.Count)
                {
                    int anchor = built.FindLastIndex(k => !SystemItem(k));
                    built.InsertRange(anchor < 0 ? Math.Min(2, built.Count) : anchor + 1, taskList.Skip(used));
                }

                order = built;
            }

            var merged = Merge(order, taskList);
            return visible ? merged.Where(k => Visible(model.Dock, k)).ToList() : merged;
        }

        /// <summary>把固定入口同步进 ItemOrder（原版 SyncApps）。</summary>
        public static void SyncApps(DockModel model)
        {
            if (model.Dock == null)
                return;

            var apps = DockOrderRules.Ordered(model).Select(a => a.Path).ToList();
            var common = Common(model);
            int next = 0;

            for (int i = 0; i < common.Count; i++)
            {
                if (!SystemItem(common[i]) && next < apps.Count)
                    common[i] = apps[next++];
            }

            model.Dock.ItemOrder = common;
        }

        public static bool Move(DockModel model, string source, string target, bool before, IEnumerable<string>? tasks = null)
        {
            var list = tasks == null ? Common(model) : Running(model, tasks);

            int from = list.FindIndex(k => string.Equals(k, source, StringComparison.OrdinalIgnoreCase));
            int to = target == null ? list.Count : list.FindIndex(k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));

            if (from < 0 || to < 0 || string.Equals(source, target, StringComparison.OrdinalIgnoreCase))
                return false;

            string moved = list[from];
            var beforeOrder = list.ToArray();
            list.RemoveAt(from);

            if (from < to)
                to--;

            if (target != null && !before)
                to++;

            list.Insert(Math.Max(0, Math.Min(list.Count, to)), moved);

            if (list.SequenceEqual(beforeOrder, StringComparer.OrdinalIgnoreCase))
                return false;

            if (tasks == null)
            {
                model.Dock.ItemOrder = list;
                model.DockOrder = list.Where(k => !SystemItem(k)).ToList();
            }
            else
            {
                model.Dock.RunningOrder = list;
            }

            return true;
        }

        public static bool Step(DockModel model, string source, int step, IEnumerable<string>? tasks = null)
        {
            var list = tasks == null ? Common(model, visible: true) : Running(model, tasks, visible: true);
            int index = list.FindIndex(k => string.Equals(k, source, StringComparison.OrdinalIgnoreCase));
            int next = index + step;

            if (index >= 0 && next >= 0 && next < list.Count && Math.Abs(step) == 1)
                return Move(model, source, list[next], step < 0, tasks);

            return false;
        }

        public static DockDropTarget? OnIcon(DockModel model, string source, string target, IEnumerable<string>? tasks = null)
        {
            var list = tasks == null ? Common(model) : Running(model, tasks);
            int from = list.FindIndex(k => string.Equals(k, source, StringComparison.OrdinalIgnoreCase));
            int to = list.FindIndex(k => string.Equals(k, target, StringComparison.OrdinalIgnoreCase));

            if (from >= 0 && to >= 0 && from != to)
                return new DockDropTarget { Path = target, Before = from > to };

            return null;
        }
    }

    /// <summary>拖到图标之间时的落点判定（对应原版 DockDropRules）。</summary>
    public static class DockDropRules
    {
        public static DockDropTarget? OnIcon(DockModel model, string source, string target)
        {
            var list = DockOrderRules.Ordered(model).Select(a => a.Path).ToList();
            int from = list.FindIndex(p => string.Equals(p, source, StringComparison.OrdinalIgnoreCase));
            int to = list.FindIndex(p => string.Equals(p, target, StringComparison.OrdinalIgnoreCase));

            if (from < 0 || to < 0 || from == to)
                return null;

            return new DockDropTarget { Path = target, Before = from > to };
        }

        public static DockDropTarget? OnGap(string source, string[]? visible, double[]? centers, double x)
        {
            if (visible == null || centers == null || visible.Length != centers.Length ||
                double.IsNaN(x) || double.IsInfinity(x))
            {
                return null;
            }

            for (int i = 0; i < visible.Length; i++)
            {
                if (!string.Equals(visible[i], source, StringComparison.OrdinalIgnoreCase) && x < centers[i])
                    return new DockDropTarget { Path = visible[i], Before = true };
            }

            return new DockDropTarget { Path = null, Before = false };
        }
    }

    /// <summary>入口规范化（对应原版 EntryRules.NormalizeEntries 的去重合并逻辑）。</summary>
    public static class EntryRules
    {
        public static List<DockEntry> NormalizeEntries(IEnumerable<DockEntry>? apps)
        {
            var result = new List<DockEntry>();
            var byPath = new Dictionary<string, DockEntry>(StringComparer.OrdinalIgnoreCase);

            foreach (var item in apps ?? Enumerable.Empty<DockEntry>())
            {
                if (item == null || string.IsNullOrWhiteSpace(item.Path))
                    continue;

                if (System.IO.Directory.Exists(item.Path))
                    item.IsFolder = true;

                if (!byPath.TryGetValue(item.Path, out var existing))
                {
                    byPath.Add(item.Path, item);
                    result.Add(item);
                    continue;
                }

                existing.Pinned |= item.Pinned;
                existing.IsFolder |= item.IsFolder;

                if (string.IsNullOrEmpty(existing.IconPath))
                    existing.IconPath = item.IconPath;

                if (string.IsNullOrEmpty(existing.LaunchTarget))
                {
                    existing.LaunchTarget = item.LaunchTarget;
                    existing.LaunchArguments = item.LaunchArguments;
                }

                if (string.IsNullOrEmpty(existing.ProductInfo))
                    existing.ProductInfo = item.ProductInfo;

                if (string.IsNullOrEmpty(existing.WindowTarget))
                    existing.WindowTarget = item.WindowTarget;
            }

            return result;
        }
    }

    /// <summary>入口路径规范化（对应原版 EntryPathRules 的取名与“文档/文件夹”判断）。</summary>
    public static class EntryPathRules
    {
        public static string Normalize(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            try
            {
                return System.IO.Path.GetFullPath(path).TrimEnd('\\');
            }
            catch
            {
                return path;
            }
        }

        public static string Name(string path)
        {
            try
            {
                string name = System.IO.Path.GetFileNameWithoutExtension(path);
                return string.IsNullOrWhiteSpace(name) ? path : name;
            }
            catch
            {
                return path;
            }
        }
    }
}
