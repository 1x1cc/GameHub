using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using GameHub.Utils.Battery.Providers;

namespace GameHub.Utils.Battery
{
    /// <summary>
    /// 外设电量中枢：把「标准通道」和「厂商插件」的结果合成一份统一模型。
    ///
    /// 取数顺序（与 EasyBluetooth 的分层思路一致）：
    ///   1) 厂商插件 —— <see cref="BatteryProviderRegistry"/> 里的插件。
    ///      对自己品牌的设备，插件是**权威来源**（型号表/事务ID/换算都在插件里），
    ///      对应 EasyBluetooth 的「需要先开启插件才能识别电量」；
    ///   2) 标准通道 —— 复用现有 <see cref="WirelessBatteryService"/>
    ///      （PnP 设备属性 / 蓝牙 GATT / 系统电源接口）。
    /// 插件先跑、按混淆 Id 去重，没被插件认领的设备才由标准通道补齐。
    ///
    /// 最后统一套用 <see cref="BatteryRetentionStore"/> 的保留语义 ——
    /// 这一步是体验的关键：休眠时沿用上次有效值，而不是显示 `--`。
    /// </summary>
    public static class BatteryHubService
    {
        private static bool _defaultsRegistered;

        /// <summary>注册内置厂商插件（幂等，可在启动时多次调用）。</summary>
        public static void EnsureDefaultProviders()
        {
            if (_defaultsRegistered)
                return;

            _defaultsRegistered = true;
            BatteryProviderRegistry.Register(new RazerBatteryProvider());
            // 后续新增品牌在这里注册，例如：
            //   BatteryProviderRegistry.Register(new LogitechBatteryProvider());
            //   BatteryProviderRegistry.Register(new RogBatteryProvider());
        }

        /// <summary>扫描一次，返回统一模型列表。</summary>
        public static async Task<List<BatteryDevice>> SnapshotAsync(
            bool includeStandardChannels = true,
            CancellationToken cancellationToken = default)
        {
            EnsureDefaultProviders();

            var devices = new List<BatteryDevice>();
            var seenIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 1) 厂商插件先跑：它们的设备型号表 / 事务 ID / 换算更准，
            //    且能给出来源（razer / logitech / rog ...）和充电状态。
            foreach (var sample in BatteryProviderRegistry.ReadAll(
                         (source, ex) => AppLog.Write($"电量插件「{source}」读取失败", ex)))
            {
                var device = FromSample(sample);
                if (seenIds.Add(device.Id))
                    devices.Add(device);
            }

            // 2) 标准通道补齐其余设备；已被插件认领的（同一硬件键 → 同一混淆 Id）跳过。
            if (includeStandardChannels)
            {
                try
                {
                    var existing = await WirelessBatteryService.ScanAsync(true, cancellationToken).ConfigureAwait(false);
                    foreach (var item in existing)
                    {
                        var device = FromExisting(item);
                        if (seenIds.Add(device.Id))
                            devices.Add(device);
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    AppLog.Write("电量中枢：标准通道扫描失败", ex);
                }
            }

            // 保留语义 + 回收已拔出设备的缓存
            foreach (var device in devices)
                BatteryRetentionStore.Apply(device);

            BatteryRetentionStore.PruneTo(devices.Select(d => d.Id));

            AppLog.Write($"电量中枢：共 {devices.Count} 台设备，其中有电量 {devices.Count(d => d.Battery.HasValue)} 台" +
                         $"，不支持 {devices.Count(d => d.IsBatteryUnsupported)} 台");

            return devices;
        }

        // ---------- 现有通道 → 统一模型 ----------

        private static BatteryDevice FromExisting(WirelessBatteryDevice item)
        {
            bool connected = item.IsConnected;

            return new BatteryDevice
            {
                Id = Obfuscate(item.Id),
                Name = item.Name,
                Kind = BatteryDevice.DetectKind(item.Name),
                Source = MapSource(item),
                Status = connected ? BatteryDeviceStatus.Online : BatteryDeviceStatus.Offline,

                // 现有服务的 SourceText 已经能说明「这个数字怎么来的」，直接保留最利于排查
                ConnectionStatus = item.SourceText,

                Battery = item.BatteryPercent,
                IsCharging = item.IsCharging,
                BatteryLastUpdatedUtc = item.BatteryReadAt?.ToUniversalTime(),

                // 标准 PnP/GATT 通道没有「休眠」信号：未连接就是 offline，
                // 不要把 offline 又标成 sleeping —— 那是两件独立的事
                // （EasyBluetooth 语义：isSleeping 是技术休眠标志，如「接口在但不应答」）。
                // PnP 属性里带出的电量本身就是系统保留的旧值，正好符合保留语义。
                IsSleeping = false,

                // 现有服务里 IsBatteryUnsupported 没有对应字段，先按「有电量=支持」近似
                IsBatteryUnsupported = false
            };
        }

        private static BatteryDevice FromSample(BatterySample sample)
        {
            return new BatteryDevice
            {
                Id = Obfuscate(sample.HardwareKey),
                Name = sample.Name,
                Kind = sample.Kind,
                Source = sample.Source,
                Status = sample.Status,
                ConnectionStatus = sample.ConnectionStatus,
                Battery = sample.Battery,
                IsCharging = sample.IsCharging,
                IsSleeping = sample.IsSleeping,
                IsBatteryUnsupported = sample.IsBatteryUnsupported,
                BatteryLastUpdatedUtc = sample.Battery.HasValue ? DateTimeOffset.UtcNow : null
            };
        }

        /// <summary>
        /// 把现有的 <see cref="BatterySource"/> 映射到统一接口的 source 取值表：
        /// classic / ble / airpods / xbox / playstation / logitech / razer /
        /// windowsbattery / rog / atk / hyperx / rapoo / mchose / valkyrie / unknown
        /// </summary>
        private static string MapSource(WirelessBatteryDevice item) => item.Source switch
        {
            BatterySource.RazerHid => "razer",
            BatterySource.EweadnHid => "eweadn",
            BatterySource.RogHid => "rog",
            BatterySource.WlMouseHid => "wlmouse",
            BatterySource.Xinput => "xbox",
            BatterySource.SystemBattery => "windowsbattery",
            BatterySource.Gatt => "ble",
            BatterySource.DeviceProperty => item.IsBluetooth ? "ble" : "windowsbattery",
            BatterySource.Cached => "unknown",
            _ => item.IsBluetooth ? "ble" : "unknown"
        };

        /// <summary>
        /// 生成 12 位十六进制混淆 Id（不暴露 MAC / 设备路径）。
        /// 必须**稳定**：同一硬件每次算出的 Id 要一致，否则保留缓存会失效。
        /// </summary>
        public static string Obfuscate(string hardwareKey)
        {
            var bytes = Encoding.UTF8.GetBytes(hardwareKey ?? string.Empty);
            var hash = SHA256.HashData(bytes);
            return Convert.ToHexString(hash, 0, 6).ToLowerInvariant();
        }
    }
}
