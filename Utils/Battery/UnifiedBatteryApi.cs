using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace GameHub.Utils.Battery
{
    /// <summary>
    /// 统一标准数据接口的 JSON 输出（对齐 EasyBluetooth 的
    /// <c>GET /api/v1/status</c> 响应格式）。
    ///
    /// 外层固定为 <c>{ code, message, data }</c>，data 里再放
    /// <c>schemaVersion / generatedAtUtc / appVersion / devices[]</c>。
    ///
    /// 这里刻意保留了几个「容易被优化掉、但不能」的点：
    ///   · 未知电量输出 <c>null</c>，**不是 0**（0 是「真的没电了」，语义完全不同）；
    ///   · <c>batteryLastUpdatedUtc</c> 允许为 null，不要用请求时间兜底；
    ///   · <c>schemaVersion</c> 与 <c>appVersion</c> 分离：前者只在破坏性变更时 +1。
    /// </summary>
    public static class UnifiedBatteryApi
    {
        /// <summary>接口结构版本；新增非破坏性字段时保持不变。</summary>
        public const int SchemaVersion = 1;

        private static readonly JsonSerializerOptions Options = new()
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
            DefaultIgnoreCondition = JsonIgnoreCondition.Never,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
        };

        public static UnifiedBatteryEnvelope Build(IReadOnlyList<BatteryDevice> devices)
        {
            return new UnifiedBatteryEnvelope
            {
                Code = 200,
                Message = "OK",
                Data = new UnifiedBatteryPayload
                {
                    SchemaVersion = SchemaVersion,
                    GeneratedAtUtc = DateTimeOffset.UtcNow,
                    AppVersion = AppVersion,
                    Devices = devices.Select(ToDto).ToList()
                }
            };
        }

        public static string ToJson(IReadOnlyList<BatteryDevice> devices)
            => JsonSerializer.Serialize(Build(devices), Options);

        private static UnifiedBatteryDeviceDto ToDto(BatteryDevice device) => new()
        {
            Id = device.Id,
            Name = device.Name,
            RenamedName = string.IsNullOrWhiteSpace(device.RenamedName) ? null : device.RenamedName,
            DeviceType = device.KindToWire(),
            Source = device.Source,
            Status = device.StatusToWire(),
            ConnectionStatus = device.ConnectionStatus,
            Battery = device.Battery,
            IsCharging = device.IsCharging,
            IsSleeping = device.IsSleeping,
            IsBatteryUnsupported = device.IsBatteryUnsupported,
            IsShownInTray = device.IsShownInTray,
            BatteryLastUpdatedUtc = device.BatteryLastUpdatedUtc,
            AirPodsLeftBattery = device.AirPodsLeftBattery,
            AirPodsRightBattery = device.AirPodsRightBattery,
            AirPodsCaseBattery = device.AirPodsCaseBattery
        };

        /// <summary>应用版本，用于第三方做兼容分支与排错定位。</summary>
        public static string AppVersion
        {
            get
            {
                try
                {
                    var asm = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
                    var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
                    if (!string.IsNullOrWhiteSpace(info))
                        return info!;

                    var v = asm.GetName().Version;
                    return v == null ? "0.0.0" : $"{v.Major}.{v.Minor}.{v.Build}";
                }
                catch
                {
                    return "0.0.0";
                }
            }
        }
    }

    public sealed class UnifiedBatteryEnvelope
    {
        public int Code { get; set; } = 200;
        public string Message { get; set; } = "OK";
        public UnifiedBatteryPayload? Data { get; set; }
    }

    public sealed class UnifiedBatteryPayload
    {
        public int SchemaVersion { get; set; } = UnifiedBatteryApi.SchemaVersion;
        public DateTimeOffset GeneratedAtUtc { get; set; }
        public string AppVersion { get; set; } = string.Empty;
        public List<UnifiedBatteryDeviceDto> Devices { get; set; } = new();
    }

    /// <summary>设备对象的线上形态（字段名经 camelCase 策略转换为接口约定名）。</summary>
    public sealed class UnifiedBatteryDeviceDto
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? RenamedName { get; set; }
        public string DeviceType { get; set; } = "general";
        public string Source { get; set; } = "unknown";
        public string Status { get; set; } = "unknown";
        public string ConnectionStatus { get; set; } = string.Empty;
        public int? Battery { get; set; }
        public bool IsCharging { get; set; }
        public bool IsSleeping { get; set; }
        public bool IsBatteryUnsupported { get; set; }
        public bool IsShownInTray { get; set; }
        public DateTimeOffset? BatteryLastUpdatedUtc { get; set; }
        public int? AirPodsLeftBattery { get; set; }
        public int? AirPodsRightBattery { get; set; }
        public int? AirPodsCaseBattery { get; set; }
    }
}
