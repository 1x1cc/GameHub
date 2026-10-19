using System;
using System.Collections.Generic;
using System.Threading;

namespace GameHub.Utils
{
    /// <summary>前行者设备的一次电量读数。</summary>
    public sealed class EweadnBatteryReading
    {
        /// <summary>稳定标识（HID 接口路径）。</summary>
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        public ushort ProductId { get; init; }

        public bool IsWireless { get; init; }

        /// <summary>电量百分比；null = 读不到（通常是鼠标休眠）。</summary>
        public int? Percent { get; init; }

        public bool IsCharging { get; init; }

        /// <summary>读不到电量时的原因说明。</summary>
        public string Note { get; init; } = string.Empty;
    }

    /// <summary>
    /// 前行者（EWEADN）S9 系列电量读取（厂商私有 HID 协议 ms_pix_v1）。
    ///
    /// 协议来源：开源项目 ccziwe/eweadn-battery —— 作者逆向了前行者官方网页驱动
    /// （eweadn.hubx.pro）的 WebHID 报文并在真机验证通过；协议要点在其 README
    /// 中完整公开。本项目按协议文档独立实现（非抄代码），遵循其结论：
    ///
    ///   · VID 0x372E；型号：S9 Dongle 8K=0x1026、Dongle 1K=0x103D、
    ///     S9 Pro=0x1029、S9 Ultra=0x1027、S9 Max=0x105E（均取自官方设备表）；
    ///   · 控制通道：Usage Page 0xFF00 的厂商集合 + 64 字节 feature report（含 report id）；
    ///   · 请求：report id 0x03，63 字节负载 = [crc, 0x50, 0x00, cmd=0x02(基本信息),
    ///     0x4F, 0x81, 0...]，其中 crc = 负载[1..62] 求和 &amp; 0xFF；
    ///   · 回复：负载[3] 回显命令码 0x02，负载[5] = bit7 充电标志 + 低 7 位电量百分比
    ///     （官方网页驱动自己漏了 bit7 掩码，会把充电中的 0xE4 显示成 228% —— 我们按
    ///     正确规则解码）；
    ///   · 鼠标休眠时接收器不回有效数据 → 重试几次，仍失败则标注「动一下鼠标」。
    ///
    /// 注：S9 Se（0x100E/0x1047）用的是另一套 ms_sle_v1（report id 9/10 的输入输出
    /// 报文通道），官方驱动里有型号但参考实现未覆盖，暂不支持。
    /// </summary>
    public static class EweadnBatteryReader
    {
        private const ushort VendorId = 0x372E;

        /// <summary>64 字节 = 1 字节 report id + 63 字节负载。</summary>
        private const int WireLength = 64;

        private const byte ReportId = 0x03;

        /// <summary>命令码：读基本信息（含电量）。</summary>
        private const byte CommandBasicInfo = 0x02;

        private const int ReplyDelayMs = 100;
        private const int MaxAttempts = 3;
        private const int RetryDelayMs = 80;

        private static readonly (ushort Pid, string Name, bool Wireless)[] Models =
        {
            (0x1026, "EWEADN S9 (8K 接收器)", true),
            (0x103D, "EWEADN S9 (1K 接收器)", true),
            (0x1029, "EWEADN S9 Pro (有线)", false),
            (0x1027, "EWEADN S9 Ultra (有线)", false),
            (0x105E, "EWEADN S9 Max (有线)", false),
        };

        /// <summary>同一轮扫描里服务层与统一接口层都会调用，物理查询只做一次。</summary>
        private const int ResultCacheMs = 2500;

        private static readonly object Gate = new();
        private static List<EweadnBatteryReading>? _cached;
        private static DateTime _cachedAtUtc = DateTime.MinValue;

        public static List<EweadnBatteryReading> ReadAll()
        {
            lock (Gate)
            {
                if (_cached != null &&
                    (DateTime.UtcNow - _cachedAtUtc).TotalMilliseconds < ResultCacheMs)
                {
                    return new List<EweadnBatteryReading>(_cached);
                }

                var result = QueryAll();
                _cached = result;
                _cachedAtUtc = DateTime.UtcNow;
                return new List<EweadnBatteryReading>(result);
            }
        }

        private static List<EweadnBatteryReading> QueryAll()
        {
            var byPid = new Dictionary<ushort, EweadnBatteryReading>();

            foreach (string path in HidNative.EnumerateInterfacePaths())
            {
                if (path.IndexOf("vid_372e", StringComparison.OrdinalIgnoreCase) < 0)
                    continue;

                IntPtr handle = HidNative.Open(path,
                    HidNative.GenericRead | HidNative.GenericWrite, overlapped: false);
                if (handle == IntPtr.Zero)
                    continue;

                try
                {
                    if (!HidNative.TryGetAttributes(handle, out var attrs) ||
                        attrs.VendorID != VendorId)
                    {
                        continue;
                    }

                    var model = FindModel(attrs.ProductID);
                    if (model == null)
                        continue;

                    // 已有有效读数的型号不再重复查询（一个接收器会枚举出多个集合）
                    if (byPid.TryGetValue(model.Value.Pid, out var existing) &&
                        existing.Percent != null)
                    {
                        continue;
                    }

                    if (!HidNative.TryGetCaps(handle, out var caps))
                        continue;

                    // 控制通道的唯一可靠标志：FF00 厂商集合 + 64 字节 feature report
                    if (caps.UsagePage != 0xFF00 || caps.FeatureReportByteLength < WireLength)
                        continue;

                    var reading = TryRead(handle, path, model.Value);
                    if (reading == null)
                        continue;

                    if (!byPid.TryGetValue(model.Value.Pid, out var old) ||
                        (old.Percent == null && reading.Percent != null))
                    {
                        byPid[model.Value.Pid] = reading;
                    }
                }
                catch (Exception ex)
                {
                    AppLog.Write("EasyBluetooth/前行者：读取设备失败", ex);
                }
                finally
                {
                    HidNative.Close(handle);
                }
            }

            return new List<EweadnBatteryReading>(byPid.Values);
        }

        private static (ushort Pid, string Name, bool Wireless)? FindModel(ushort productId)
        {
            foreach (var m in Models)
            {
                if (m.Pid == productId)
                    return m;
            }
            return null;
        }

        /// <summary>
        /// 对一个已确认的 FF00 控制通道做一次电量往返。
        /// 返回读数（可能 Percent=null 表示休眠）；通道本身不可用时返回 null。
        /// </summary>
        private static EweadnBatteryReading? TryRead(
            IntPtr handle, string path, (ushort Pid, string Name, bool Wireless) model)
        {
            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (attempt > 0)
                    Thread.Sleep(RetryDelayMs);

                var payload = BuildPayload(CommandBasicInfo, 0x81);

                var set = new byte[WireLength];
                set[0] = ReportId;
                Array.Copy(payload, 0, set, 1, payload.Length);
                if (!HidNative.SetFeature(handle, set))
                    return null;   // 通道打不开，重试无意义

                Thread.Sleep(ReplyDelayMs);

                var get = new byte[WireLength];
                get[0] = ReportId;
                if (!HidNative.GetFeature(handle, get))
                    continue;

                // get[1..] 是 63 字节回复负载
                if (get[1 + 3] != CommandBasicInfo)
                    continue;   // 命令回显不对，可能是没应答或读回了请求本身

                int raw = get[1 + 5];
                bool charging = (raw & 0x80) != 0;
                int percent = raw & 0x7F;
                if (percent > 100)
                    continue;

                AppLog.Write($"EasyBluetooth/前行者：{model.Name} 原始 0x{raw:X2} → {percent}%（充电={charging}）");
                return new EweadnBatteryReading
                {
                    Id = path,
                    Name = model.Name,
                    ProductId = model.Pid,
                    IsWireless = model.Wireless,
                    Percent = percent,
                    IsCharging = charging
                };
            }

            AppLog.Write($"EasyBluetooth/前行者：{model.Name} 未应答（可能休眠）");
            return new EweadnBatteryReading
            {
                Id = path,
                Name = model.Name,
                ProductId = model.Pid,
                IsWireless = model.Wireless,
                Percent = null,
                Note = "设备未应答（可能休眠，动一下鼠标）"
            };
        }

        /// <summary>
        /// 构造 63 字节负载：[crc, 0x50, 0x00, cmd, 0x4F, param, 0...]，
        /// crc = 除 crc 外所有字节之和 &amp; 0xFF（即负载[1..62]）。
        /// </summary>
        private static byte[] BuildPayload(byte cmd, byte param)
        {
            var payload = new byte[WireLength - 1];
            payload[1] = 0x50;
            payload[2] = 0x00;
            payload[3] = cmd;
            payload[4] = 0x4F;
            payload[5] = param;

            byte sum = 0;
            for (int i = 1; i < payload.Length; i++)
                sum += payload[i];
            payload[0] = sum;
            return payload;
        }
    }
}
