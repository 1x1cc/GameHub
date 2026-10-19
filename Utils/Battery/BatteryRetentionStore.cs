using System;
using System.Collections.Generic;

namespace GameHub.Utils.Battery
{
    /// <summary>
    /// 电量保留（last-valid retention）存储。
    ///
    /// 这是 EasyBluetooth 统一接口里最容易被忽略、但体验差别最大的一条语义，
    /// 原文（2026-03 语义更新）：
    ///
    ///   · 设备休眠时，如果之前读到过有效值，battery 与 batteryLastUpdatedUtc
    ///     继续返回**那个保留值**；只有从来没有有效值时才为 null。
    ///   · 休眠刷新**不更新** batteryLastUpdatedUtc。
    ///   · battery = null 不等于 0，也不等于离线。
    ///
    /// 为什么必须这么做：无线外设大部分时间在休眠，只按「本次读没读到」决定显示，
    /// 结果就是电量数字疯狂闪烁 / 大部分时间显示 `--`，界面完全没法用。
    /// </summary>
    public static class BatteryRetentionStore
    {
        private sealed record Entry(int Battery, bool IsCharging, DateTimeOffset UpdatedUtc);

        private static readonly Dictionary<string, Entry> Cache = new(StringComparer.OrdinalIgnoreCase);

        /// <summary>记下一次成功读数。只应在真正读到有效值时调用。</summary>
        public static void Remember(string deviceId, int battery, bool isCharging, DateTimeOffset? atUtc = null)
        {
            if (string.IsNullOrWhiteSpace(deviceId) || battery < 0 || battery > 100)
                return;

            Cache[deviceId] = new Entry(battery, isCharging, atUtc ?? DateTimeOffset.UtcNow);
        }

        /// <summary>取回上一次有效读数。</summary>
        public static bool TryRecall(string deviceId, out int battery, out bool isCharging, out DateTimeOffset updatedUtc)
        {
            battery = 0;
            isCharging = false;
            updatedUtc = default;

            if (string.IsNullOrWhiteSpace(deviceId) || !Cache.TryGetValue(deviceId, out var entry))
                return false;

            battery = entry.Battery;
            isCharging = entry.IsCharging;
            updatedUtc = entry.UpdatedUtc;
            return true;
        }

        /// <summary>
        /// 对一台设备套用保留语义。调用点在「本轮扫描结束、准备呈现」之前。
        ///
        ///   读到有效值  → 记住它，时间戳 = 本轮时间；
        ///   没读到但历史有 → 回填历史值 + **历史时间戳**（不是现在）。
        /// </summary>
        public static void Apply(BatteryDevice device)
        {
            if (device == null)
                return;

            if (device.Battery.HasValue)
            {
                if (device.BatteryLastUpdatedUtc == null)
                    device.BatteryLastUpdatedUtc = DateTimeOffset.UtcNow;

                // 有效值才入库；否则会把「没读到」也覆盖成 0
                Remember(device.Id, device.Battery.Value, device.IsCharging, device.BatteryLastUpdatedUtc);
                return;
            }

            if (TryRecall(device.Id, out int battery, out bool charging, out var updatedUtc))
            {
                device.Battery = battery;
                device.IsCharging = charging;
                // 关键：沿用历史时间戳，不要刷成当前时间
                device.BatteryLastUpdatedUtc = updatedUtc;
            }
            // 没有历史值 → 保持 null，显示端渲染成 `--`
        }

        /// <summary>设备已解除配对/拔出时回收，避免无上限增长。</summary>
        public static void Forget(string deviceId)
        {
            if (!string.IsNullOrWhiteSpace(deviceId))
                Cache.Remove(deviceId);
        }

        /// <summary>只保留当前仍在列表里的设备（每轮扫描后调用）。</summary>
        public static void PruneTo(IEnumerable<string> liveDeviceIds)
        {
            var live = new HashSet<string>(liveDeviceIds, StringComparer.OrdinalIgnoreCase);
            var stale = new List<string>();
            foreach (var key in Cache.Keys)
            {
                if (!live.Contains(key))
                    stale.Add(key);
            }
            foreach (var key in stale)
                Cache.Remove(key);
        }

        public static int Count => Cache.Count;

        public static void Clear() => Cache.Clear();
    }
}
