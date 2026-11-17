using System;
using System.Collections.Generic;
using System.Linq;

namespace GameHub.Utils.Battery
{
    /// <summary>
    /// 一次取数结果。厂商插件只负责「把能读到的读出来」，
    /// 保留语义、去重、排序都由上层统一做。
    /// </summary>
    public sealed class BatterySample
    {
        /// <summary>稳定硬件键（设备实例路径 / MAC / 序列号）。用于生成混淆 Id 和做保留缓存。</summary>
        public string HardwareKey { get; init; } = string.Empty;

        /// <summary>原始设备名。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>数据来源标识，如 razer / logitech / rog / atk。</summary>
        public string Source { get; init; } = "unknown";

        public BatteryDeviceKind Kind { get; init; } = BatteryDeviceKind.General;

        /// <summary>电量 0–100；读不到为 null（**不要用 0 代替**）。</summary>
        public int? Battery { get; init; }

        public bool IsCharging { get; init; }

        /// <summary>设备在（接口存在）但不应答 —— 典型是休眠。</summary>
        public bool IsSleeping { get; init; }

        /// <summary>确认该设备/该通道不支持读电量。</summary>
        public bool IsBatteryUnsupported { get; init; }

        public BatteryDeviceStatus Status { get; init; } = BatteryDeviceStatus.Online;

        public string ConnectionStatus { get; init; } = string.Empty;
    }

    /// <summary>
    /// 厂商电量插件接口。
    ///
    /// 这是从 EasyBluetooth 学来的核心结构：它的更新日志里几乎每次
    /// 「新增 XX 插件 + 新增若干型号」都是这个形状 —— **一个品牌一个插件，
    /// 插件内部维护 VID/PID 型号表**（截至 3.5.8 已有 50+ 品牌）。
    ///
    /// 这样加新厂商只需要新增一个实现类并注册，不用动主流程。
    /// </summary>
    public interface IBatteryProvider
    {
        /// <summary>来源标识（写进统一接口的 source 字段）。</summary>
        string Source { get; }

        /// <summary>优先级，数值小的先执行。标准通道用 0–99，厂商插件用 100+。</summary>
        int Priority { get; }

        /// <summary>是否启用（对应它的「插件管理页」开关；需要先开启才识别电量）。</summary>
        bool IsEnabled { get; }

        /// <summary>同步取数。耗时操作（HID 轮询）由实现自己控制重试与超时。</summary>
        IReadOnlyList<BatterySample> Read();
    }

    /// <summary>
    /// 插件注册表。
    ///
    /// 用法：在启动时注册各厂商插件，之后 <see cref="ReadAll"/> 按优先级依次取数，
    /// 前面已经产出的设备（按硬件键去重）后面的插件不再重复报告。
    /// </summary>
    public static class BatteryProviderRegistry
    {
        private static readonly List<IBatteryProvider> Providers = new();

        /// <summary>注册插件；同 Source 重复注册时后者覆盖前者。</summary>
        public static void Register(IBatteryProvider provider)
        {
            if (provider == null)
                return;

            Providers.RemoveAll(p => string.Equals(p.Source, provider.Source, StringComparison.OrdinalIgnoreCase));
            Providers.Add(provider);
            Providers.Sort((a, b) => a.Priority.CompareTo(b.Priority));
        }

        public static void Unregister(string source)
            => Providers.RemoveAll(p => string.Equals(p.Source, source, StringComparison.OrdinalIgnoreCase));

        public static IReadOnlyList<IBatteryProvider> All => Providers;

        public static IEnumerable<IBatteryProvider> Ordered() => Providers.OrderBy(p => p.Priority);

        /// <summary>
        /// 依次执行所有启用的插件。单个插件抛异常不影响其它插件
        /// （插件是第三方/新增代码，最不该因为它整体挂掉）。
        /// </summary>
        public static List<BatterySample> ReadAll(Action<string, Exception>? onError = null)
        {
            var list = new List<BatterySample>();
            var seenKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var provider in Ordered())
            {
                if (!provider.IsEnabled)
                    continue;

                try
                {
                    foreach (var sample in provider.Read())
                    {
                        var key = sample.HardwareKey;
                        if (!string.IsNullOrWhiteSpace(key) && !seenKeys.Add(key))
                            continue;   // 已被更高优先级的通道报告过

                        list.Add(sample);
                    }
                }
                catch (Exception ex)
                {
                    onError?.Invoke(provider.Source, ex);
                }
            }

            return list;
        }

        public static void Clear() => Providers.Clear();
    }
}
