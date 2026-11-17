using System;
using System.Collections.Generic;
using System.Linq;

namespace GameHub.Modules.GlassBoard.Dock
{
    /// <summary>
    /// 分组规则（对应原版 FeatureRules 里与分组相关的那部分）。
    ///
    /// 大面板的「分组」下拉、顶部文件夹胶囊、右键“移动到分组”都靠它；
    /// 判定逻辑与原版保持一致：
    ///   - 每个入口有一个主分组 Group，外加若干 ExtraGroups（可同时属于多个分组）；
    ///   - “全部”不是真分组，代表不过滤；“未分组”代表主分组为空的入口。
    /// </summary>
    public static class BoardRules
    {
        public const string All = "全部";

        public const string None = "未分组";

        /// <summary>系统工具分组名（多处引用，集中在这里）。</summary>
        public const string SystemGroup = "系统工具";

        /// <summary>
        /// 是不是“系统工具”。人工从系统工具里移出过的（ManualNonSystem）一律不算，
        /// 这样它会按普通软件出现在「全部」里。
        /// </summary>
        public static bool IsSystemEntry(DockEntry? entry)
            => entry != null && !entry.ManualNonSystem && InGroup(entry, SystemGroup);

        /// <summary>「全部」里实际会显示的个数（必须跟过滤逻辑一致，否则计数和内容对不上）。</summary>
        public static int VisibleAllCount(DockModel model)
            => model.Apps.Count(a => !IsSystemEntry(a));

        /// <summary>
        /// 清洗分组字段：空字符串归零、“全部/未分组”不算真分组、
        /// 去掉重复项，并在主分组为空时把第一个附加分组成员提升为主分组（原版行为）。
        /// </summary>
        public static void NormalizeMemberships(DockEntry entry)
        {
            entry.Group = string.IsNullOrWhiteSpace(entry.Group) ? null : entry.Group.Trim();

            if (entry.Group == All || entry.Group == None)
                entry.Group = null;

            entry.ExtraGroups ??= new List<string>();

            entry.ExtraGroups = entry.ExtraGroups
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Select(g => g.Trim())
                .Where(g => g != All && g != None && !string.Equals(g, entry.Group, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

            if (string.IsNullOrWhiteSpace(entry.Group) && entry.ExtraGroups.Count > 0)
            {
                entry.Group = entry.ExtraGroups[0];
                entry.ExtraGroups.RemoveAt(0);
            }
        }

        /// <summary>判断入口是否属于某个分组（“全部”恒真）。</summary>
        public static bool InGroup(DockEntry entry, string group)
        {
            if (group == All)
                return true;

            if (group == None)
            {
                if (string.IsNullOrWhiteSpace(entry.Group))
                    return entry.ExtraGroups == null || entry.ExtraGroups.Count == 0;

                return false;
            }

            if (!string.Equals(entry.Group, group, StringComparison.OrdinalIgnoreCase))
            {
                return entry.ExtraGroups != null
                       && entry.ExtraGroups.Any(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));
            }

            return true;
        }

        public static IEnumerable<string> Memberships(DockEntry entry)
            => new[] { entry.Group }
                .Concat(entry.ExtraGroups ?? new List<string>())
                .Where(g => !string.IsNullOrWhiteSpace(g))!;

        /// <summary>加入分组：主分组空就占主分组，否则记进附分组。</summary>
        public static void AddMembership(DockEntry entry, string group)
        {
            NormalizeMemberships(entry);

            if (group == None)
            {
                entry.Group = null;
                entry.ExtraGroups!.Clear();
                return;
            }

            if (InGroup(entry, group))
                return;

            if (string.IsNullOrWhiteSpace(entry.Group))
                entry.Group = group;
            else
                entry.ExtraGroups!.Add(group);
        }

        public static void RemoveMembership(DockEntry entry, string group)
        {
            if (string.Equals(entry.Group, group, StringComparison.OrdinalIgnoreCase))
                entry.Group = null;

            entry.ExtraGroups?.RemoveAll(g => string.Equals(g, group, StringComparison.OrdinalIgnoreCase));

            NormalizeMemberships(entry);
        }

        /// <summary>入口显示用的分组名（没有就是“未分组”）。</summary>
        public static string GroupName(DockEntry entry)
            => string.IsNullOrWhiteSpace(entry.Group) ? None : entry.Group.Trim();

        /// <summary>
        /// 汇总所有分组名：默认三个 + 已保存的 + 入口里出现过的，
        /// 去掉“全部/未分组”和重复项。
        /// </summary>
        public static List<string> Groups(IEnumerable<string>? groups, IEnumerable<DockEntry> apps)
            => (groups ?? new[] { "创作", "办公", "游戏" })
                .Concat(apps.SelectMany(Memberships))
                .Select(g => (g ?? string.Empty).Trim())
                .Where(g => g.Length > 0 && g != All && g != None)
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .ToList();

        /// <summary>
        /// 分页数（对应原版 LayoutRules.Pages）：
        /// 容量非法时按 1 页算，避免除零；没有内容也至少 1 页。
        /// </summary>
        public static int Pages(int count, int capacity)
        {
            if (count <= 0)
                return 1;

            if (capacity < 1)
                capacity = 1;

            return Math.Max(1, (count + capacity - 1) / capacity);
        }

        /// <summary>
        /// 在大面板里拖拽重排（对应原版 C.ReorderEntry）：
        /// 把 source 移到 target 前面或后面。大面板的顺序就是 Apps 的顺序。
        /// </summary>
        public static bool Reorder(DockModel model, string source, string target, bool before)
        {
            if (model == null || string.IsNullOrWhiteSpace(source) || string.IsNullOrWhiteSpace(target))
                return false;

            var src = model.Apps.FirstOrDefault(a => string.Equals(a.Path, source, StringComparison.OrdinalIgnoreCase));
            var dst = model.Apps.FirstOrDefault(a => string.Equals(a.Path, target, StringComparison.OrdinalIgnoreCase));

            if (src == null || dst == null || ReferenceEquals(src, dst))
                return false;

            if (model.Apps.IndexOf(src) == model.Apps.IndexOf(dst))
                return false;

            model.Apps.Remove(src);

            int index = model.Apps.IndexOf(dst);
            if (index < 0)
                index = model.Apps.Count;

            model.Apps.Insert(before ? index : index + 1, src);
            return true;
        }

        /// <summary>某个分组里的入口数（“全部”返回总数）。</summary>
        public static int Count(DockModel model, string group)
            => group == All
                ? model.Apps.Count
                : model.Apps.Count(a => InGroup(a, group));

        /// <summary>
        /// 把一组入口整体设为某个分组（对应原版 FeatureRules.Move）：
        /// 主分组直接替换，附加分组清空，并记住这次人工选择（原版 LearnedGroups）。
        /// </summary>
        public static int Move(DockModel model, IEnumerable<string> paths, string group)
        {
            string target = (group ?? string.Empty).Trim();

            if (target == All)
                throw new ArgumentException("不能移动到「全部」");

            var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            var moved = model.Apps.Where(a => wanted.Contains(a.Path)).ToList();

            foreach (var entry in moved)
            {
                // 移动前它算不算“系统工具”
                bool wasSystem = !entry.ManualNonSystem && InGroup(entry, SystemGroup);

                entry.Group = target == None ? null : target;
                entry.ExtraGroups = new List<string>();

                // 人工挪动的方向要记住，否则自动规则会把用户的操作抹掉：
                //  挪出系统工具 → 以后自动对照/自动归类都别再塞回去；
                //  挪进系统工具 → 撤掉之前的“移出”标记。
                if (target == SystemGroup)
                    entry.ManualNonSystem = false;
                else if (wasSystem)
                    entry.ManualNonSystem = true;
            }

            model.Groups = Groups(model.Groups, model.Apps);
            return moved.Count;
        }

        /// <summary>从某分组转移到另一分组（原版 FeatureRules.Transfer 的常用分支）。</summary>
        public static int Transfer(DockModel model, IEnumerable<string> paths, string from, string to)
        {
            if (from == to)
                return 0;

            if (to == All)
                throw new ArgumentException("不能移动到「全部」");

            var wanted = new HashSet<string>(paths, StringComparer.OrdinalIgnoreCase);
            int count = 0;

            foreach (var entry in model.Apps.Where(a => wanted.Contains(a.Path) && InGroup(a, from)))
            {
                RemoveMembership(entry, from);
                AddMembership(entry, to);
                count++;
            }

            model.Groups = Groups(model.Groups, model.Apps);
            return count;
        }

        /// <summary>把未分组的入口按关键词表自动归组（对应原版 ApplyUnassigned）。</summary>
        public static int ApplyUnassigned(DockModel model)
        {
            int count = 0;

            foreach (var entry in model.Apps)
            {
                // 人工移出过系统工具的，自动归类不要碰它
                if (entry.ManualNonSystem)
                    continue;

                if (GroupName(entry) != None)
                    continue;

                string? guess = BoardAutoGroup.Guess(entry);
                if (guess == null)
                    continue;

                entry.Group = guess;
                count++;
            }

            model.Groups = Groups(model.Groups, model.Apps);
            return count;
        }
    }
}
