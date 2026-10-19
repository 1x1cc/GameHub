using System.Collections.Generic;
using System.Linq;
using GameHub.Utils;

namespace GameHub.Utils.Battery.Providers
{
    /// <summary>
    /// 雷蛇插件：2.4G 接收器 / 有线设备电量。
    ///
    /// 这是「厂商插件」的参考实现 —— 新增品牌时照这个形状写即可：
    ///   1) 声明 <see cref="Source"/>（写进统一接口的 source 字段）；
    ///   2) 声明 <see cref="Priority"/>；
    ///   3) 在 <see cref="Read"/> 里枚举本品牌设备并取数；
    ///   4) 读不到时返回 Battery = null 并如实标注原因，**不要编造数字**。
    ///
    /// 协议细节在 <see cref="RazerBatteryReader"/>（91 字节 feature report，
    /// 命令类 0x07 / 命令 0x80，事务 ID 按代际）。
    /// </summary>
    public sealed class RazerBatteryProvider : IBatteryProvider
    {
        public string Source => "razer";

        /// <summary>厂商插件统一排在标准通道之后。</summary>
        public int Priority => 100;

        /// <summary>对应「插件管理」里的开关，默认开启。</summary>
        public bool IsEnabled { get; set; } = true;

        public IReadOnlyList<BatterySample> Read()
        {
            var samples = new List<BatterySample>();

            foreach (var reading in RazerBatteryReader.ReadAll())
            {
                bool unsupported = reading.Note.Length > 0 && reading.Percent == null && reading.RawValue != 0;

                samples.Add(new BatterySample
                {
                    HardwareKey = reading.Id,
                    Name = reading.Name,
                    Source = Source,
                    Kind = BatteryDevice.DetectKind(reading.Name),
                    Battery = reading.Percent,
                    IsCharging = reading.IsCharging,

                    // 原始值 0 = 鼠标关机/超出范围，视为休眠；未应答同理
                    IsSleeping = reading.Percent == null,
                    IsBatteryUnsupported = unsupported,

                    // 接收器在 = 设备在；读不到电量只是休眠，不代表离线
                    Status = BatteryDeviceStatus.Online,
                    ConnectionStatus = reading.Percent.HasValue ? "CONNECTED" : "SLEEPING"
                });
            }

            return samples;
        }
    }
}
