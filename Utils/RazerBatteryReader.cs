using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Threading;

namespace GameHub.Utils
{
    /// <summary>雷蛇设备的一次电量读数。</summary>
    public sealed class RazerBatteryReading
    {
        /// <summary>稳定标识（HID 接口路径）。</summary>
        public string Id { get; init; } = string.Empty;

        public string Name { get; init; } = string.Empty;

        /// <summary>产品 ID（VID_1532 下的 PID）。</summary>
        public ushort ProductId { get; init; }

        /// <summary>是否无线（2.4G 接收器）；false = 有线连接。</summary>
        public bool IsWireless { get; init; }

        /// <summary>电量百分比；null = 读不到。</summary>
        public int? Percent { get; init; }

        public bool IsCharging { get; init; }

        /// <summary>设备返回的原始电平（0–255），用于和 Synapse 对照校准。</summary>
        public int RawValue { get; init; }

        /// <summary>读不到电量时的原因（读到了则为空）。</summary>
        public string Note { get; init; } = string.Empty;
    }

    /// <summary>
    /// 雷蛇 2.4G 接收器 / 有线外设的电量读取（厂商私有 HID 协议）。
    ///
    /// 为什么必须单独做这一条通道：
    ///   实测（雷蛇 DeathAdder V3 Pro，接收器 VID_1532 / PID_00B7）该设备
    ///   **完全不向 Windows 暴露电量** —— PnP 上没有 DEVPKEY_Device_BatteryLevel，
    ///   Windows.Devices.Power.Battery 也枚举不到，所以
    ///   <see cref="WirelessBatteryService"/> 原有的「设备属性 / GATT / 系统电源接口」
    ///   三条通道对它全部失效，系统设置里同样看不到电量。只有厂商协议能读出来。
    ///
    /// 协议要点（均在本机实测通过）：
    ///   1) 走 HID **feature report**，缓冲区正好 91 字节（report id 0 + 90 字节载荷）。
    ///      识别接口不能靠 usage page —— 实测协议落在鼠标集合（0x0001/0x0002）上，
    ///      唯一可靠标志是「feature report 长度 = 91」；
    ///   2) 打开句柄必须 dwDesiredAccess = 0。申请读写权限会被 ACCESS_DENIED 拒绝
    ///      （键盘集合尤其如此）—— 这就是"设备明明在，却打不开/读不到"的原因；
    ///   3) SET_REPORT 之后要等约 60ms 再 GET_REPORT，否则读回来的是请求本身
    ///      （status = 0x01 BUSY），需要重试；
    ///   4) 必须校验回显（事务 ID / 命令类 / 命令 ID）与 CRC，才能确认拿到的是应答；
    ///   5) 电量按 0–255 上报（255 为满），需换算成百分比。
    ///
    /// 协议来源：MIT 许可的 xzeldon/razer-battery-report（帧格式与设备表）。
    /// </summary>
    public static class RazerBatteryReader
    {
        private const ushort RazerVendorId = 0x1532;

        /// <summary>90 字节载荷 + 1 字节 report id。</summary>
        private const int WireLength = 91;

        private const byte CommandClassPower = 0x07;
        private const byte CommandIdBatteryLevel = 0x80;
        private const byte CommandIdChargingStatus = 0x84;
        private const byte BatteryDataSize = 0x02;

        private const byte StatusNewCommand = 0x00;
        private const byte StatusBusy = 0x01;
        private const byte StatusSuccessful = 0x02;
        private const byte StatusTimeout = 0x04;

        /// <summary>设备需要时间准备应答；参考实现用 60ms。</summary>
        private const int SettleMs = 60;

        private const int RetryMs = 60;
        private const int MaxAttempts = 4;

        // 事务 ID 不统一：V3/V4 代与 Orochi V2 用 0x1F，V2 代用 0x3F。发错设备会沉默。
        private const byte TxnNewer = 0x1F;
        private const byte TxnV2 = 0x3F;

        private const uint FileShareRead = 0x00000001;
        private const uint FileShareWrite = 0x00000002;
        private const uint OpenExisting = 3;
        private const int DigcfPresent = 0x02;
        private const int DigcfDeviceInterface = 0x10;

        private sealed class RazerModel
        {
            public ushort ProductId { get; }
            public byte TransactionId { get; }
            public string Name { get; }
            public bool IsWireless { get; }

            public RazerModel(ushort productId, byte transactionId, string name, bool isWireless)
            {
                ProductId = productId;
                TransactionId = transactionId;
                Name = name;
                IsWireless = isWireless;
            }
        }

        /// <summary>支持的型号（PID 与事务 ID 来自 xzeldon/razer-battery-report）。</summary>
        private static readonly RazerModel[] Models =
        {
            new(0x007A, TxnV2,    "Razer Viper Ultimate (有线)", false),
            new(0x007B, TxnV2,    "Razer Viper Ultimate", true),
            new(0x007C, TxnV2,    "Razer DeathAdder V2 Pro (有线)", false),
            new(0x007D, TxnV2,    "Razer DeathAdder V2 Pro", true),
            new(0x0094, TxnNewer, "Razer Orochi V2 (2.4G)", true),
            new(0x0095, TxnNewer, "Razer Orochi V2 (蓝牙)", true),
            new(0x00AA, TxnNewer, "Razer Basilisk V3 Pro (有线)", false),
            new(0x00AB, TxnNewer, "Razer Basilisk V3 Pro", true),
            new(0x00B6, TxnNewer, "Razer DeathAdder V3 Pro (有线)", false),
            new(0x00B7, TxnNewer, "Razer DeathAdder V3 Pro", true),
            new(0x00BE, TxnNewer, "Razer DeathAdder V4 Pro (有线)", false),
            new(0x00BF, TxnNewer, "Razer DeathAdder V4 Pro", true),
            new(0x00C4, TxnNewer, "Razer DeathAdder V3 HyperSpeed (有线)", false),
            new(0x00C5, TxnNewer, "Razer DeathAdder V3 HyperSpeed", true),
            new(0x00CC, TxnNewer, "Razer Basilisk V3 Pro 35K (有线)", false),
            new(0x00CD, TxnNewer, "Razer Basilisk V3 Pro 35K", true),
            new(0x00D6, TxnNewer, "Razer Basilisk V3 Pro 35K 幻影绿 (有线)", false),
            new(0x00D7, TxnNewer, "Razer Basilisk V3 Pro 35K 幻影绿", true)
        };

        /// <summary>
        /// 短周期结果缓存：一轮扫描里 <see cref="WirelessBatteryService"/> 和
        /// <see cref="GameHub.Utils.Battery.Providers.RazerBatteryProvider"/> 会各调一次本方法，
        /// 而一次物理 HID 查询带 60ms×多次重试，不该跑两遍。
        /// </summary>
        private const int ResultCacheMs = 2500;

        private static readonly object QueryGate = new();
        private static List<RazerBatteryReading>? _lastResult;
        private static DateTime _lastResultAtUtc = DateTime.MinValue;

        /// <summary>
        /// 读取所有在线的雷蛇外设电量。读不到电量的设备也会列出（Percent = null）。
        /// 结果在 <see cref="ResultCacheMs"/> 内复用，避免同一轮扫描重复查询 HID。
        /// </summary>
        public static List<RazerBatteryReading> ReadAll()
        {
            lock (QueryGate)
            {
                if (_lastResult != null &&
                    (DateTime.UtcNow - _lastResultAtUtc).TotalMilliseconds < ResultCacheMs)
                {
                    return new List<RazerBatteryReading>(_lastResult);
                }

                var result = QueryAll();
                _lastResult = result;
                _lastResultAtUtc = DateTime.UtcNow;
                return new List<RazerBatteryReading>(result);
            }
        }

        private static List<RazerBatteryReading> QueryAll()
        {
            var result = new List<RazerBatteryReading>();

            foreach (var path in EnumerateHidInterfacePaths())
            {
                // 关键：access = 0，否则键盘类集合会 ACCESS_DENIED
                IntPtr handle = CreateFileW(path, 0, FileShareRead | FileShareWrite,
                    IntPtr.Zero, OpenExisting, 0, IntPtr.Zero);
                if (handle == new IntPtr(-1))
                    continue;

                try
                {
                    if (!HidD_GetAttributes(handle, out var attributes))
                        continue;
                    if (attributes.VendorID != RazerVendorId)
                        continue;

                    var model = FindModel(attributes.ProductID);
                    if (model == null)
                        continue;

                    // 唯一可靠的识别标志：91 字节 feature report
                    if (GetFeatureReportLength(handle) != WireLength)
                        continue;

                    var arguments = Request(handle, model.TransactionId,
                        CommandClassPower, CommandIdBatteryLevel, BatteryDataSize);

                    if (arguments == null)
                    {
                        // 设备在（接口存在）但不应答：通常是鼠标休眠/关机。
                        // 依然把设备列出来并标注原因，否则用户会以为功能没生效。
                        AppLog.Write($"EasyBluetooth/雷蛇：{model.Name} 未应答（可能已休眠或关机）");
                        result.Add(new RazerBatteryReading
                        {
                            Id = path,
                            Name = model.Name,
                            ProductId = model.ProductId,
                            IsWireless = model.IsWireless,
                            Percent = null,
                            Note = "设备未应答（可能已休眠或关机）"
                        });
                        continue;
                    }

                    byte raw = arguments[1];
                    if (raw == 0)
                    {
                        // 关机/超范围的鼠标会回 0 而不是不回答，按"读不到"处理，避免误报 0%
                        AppLog.Write($"EasyBluetooth/雷蛇：{model.Name} 原始值 0，视为离线");
                        result.Add(new RazerBatteryReading
                        {
                            Id = path,
                            Name = model.Name,
                            ProductId = model.ProductId,
                            IsWireless = model.IsWireless,
                            Percent = null,
                            RawValue = 0,
                            Note = "设备离线（原始值 0）"
                        });
                        continue;
                    }

                    bool charging = false;
                    var chargingArguments = Request(handle, model.TransactionId,
                        CommandClassPower, CommandIdChargingStatus, BatteryDataSize);
                    if (chargingArguments != null && chargingArguments[1] == 0x01)
                        charging = true;

                    int percent = ToPercent(raw);
                    AppLog.Write($"EasyBluetooth/雷蛇：{model.Name} 原始 {raw}/255 → {percent}%（充电={charging}）");

                    result.Add(new RazerBatteryReading
                    {
                        Id = path,
                        Name = model.Name,
                        ProductId = model.ProductId,
                        IsWireless = model.IsWireless,
                        Percent = percent,
                        IsCharging = charging,
                        RawValue = raw
                    });
                }
                catch (Exception ex)
                {
                    AppLog.Write("EasyBluetooth/雷蛇：读取设备失败", ex);
                }
                finally
                {
                    CloseHandle(handle);
                }
            }

            return result;
        }

        private static RazerModel? FindModel(ushort productId)
        {
            foreach (var model in Models)
            {
                if (model.ProductId == productId)
                    return model;
            }
            return null;
        }

        /// <summary>雷蛇按 0–255 上报电平，255 为满电。</summary>
        private static int ToPercent(byte raw)
        {
            int percent = (int)Math.Round(raw * 100.0 / 255.0);
            return percent < 1 ? 1 : (percent > 100 ? 100 : percent);
        }

        // ---------- 协议帧 ----------

        private static byte Checksum(byte[] wire)
        {
            byte crc = 0;
            for (int i = 2; i <= 87; i++)
                crc ^= wire[1 + i];
            return crc;
        }

        private static byte[] BuildFrame(byte transactionId, byte commandClass, byte commandId, byte dataSize)
        {
            var wire = new byte[WireLength];
            wire[0] = 0x00;                       // report id
            wire[1 + 0] = StatusNewCommand;       // status：新命令
            wire[1 + 1] = transactionId;
            wire[1 + 5] = dataSize;
            wire[1 + 6] = commandClass;
            wire[1 + 7] = commandId;
            wire[1 + 88] = Checksum(wire);
            return wire;
        }

        /// <summary>发一条雷蛇指令并返回 80 字节参数区；失败返回 null。</summary>
        private static byte[]? Request(IntPtr handle, byte transactionId, byte commandClass,
            byte commandId, byte dataSize)
        {
            var wire = BuildFrame(transactionId, commandClass, commandId, dataSize);

            for (int attempt = 0; attempt < MaxAttempts; attempt++)
            {
                if (attempt > 0)
                    Thread.Sleep(RetryMs);

                if (!HidD_SetFeature(handle, wire, WireLength))
                    return null;

                Thread.Sleep(SettleMs);

                var reply = new byte[WireLength];
                reply[0] = 0x00;
                if (!HidD_GetFeature(handle, reply, WireLength))
                    return null;

                byte status = reply[1];

                // 0x01 = BUSY（还没准备好）、0x04 = TIMEOUT（接收器暂时联系不上鼠标，
                // 常见于鼠标刚唤醒）。两者都值得再试一次，其余状态直接判失败。
                if (status == StatusBusy || status == StatusTimeout)
                    continue;
                if (status != StatusSuccessful)
                    return null;

                // 回显校验 + CRC：确认不是把自己的请求读了回来
                if (reply[1 + 1] != transactionId ||
                    reply[1 + 6] != commandClass ||
                    reply[1 + 7] != commandId)
                    return null;
                if (reply[1 + 88] != Checksum(reply))
                    return null;

                var arguments = new byte[80];
                Array.Copy(reply, 1 + 8, arguments, 0, 80);
                return arguments;
            }

            return null;
        }

        // ---------- HID 枚举（SetupAPI） ----------

        [StructLayout(LayoutKind.Sequential)]
        private struct SpDeviceInterfaceData
        {
            public int cbSize;
            public Guid InterfaceClassGuid;
            public int Flags;
            public IntPtr Reserved;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HiddAttributes
        {
            public int Size;
            public ushort VendorID;
            public ushort ProductID;
            public ushort VersionNumber;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct HidpCaps
        {
            public ushort Usage;
            public ushort UsagePage;
            public ushort InputReportByteLength;
            public ushort OutputReportByteLength;
            public ushort FeatureReportByteLength;
            [MarshalAs(UnmanagedType.ByValArray, SizeConst = 17)]
            public ushort[] Reserved;
            public ushort NumberLinkCollectionNodes;
            public ushort NumberInputButtonCaps;
            public ushort NumberInputValueCaps;
            public ushort NumberInputDataIndices;
            public ushort NumberOutputButtonCaps;
            public ushort NumberOutputValueCaps;
            public ushort NumberOutputDataIndices;
            public ushort NumberFeatureButtonCaps;
            public ushort NumberFeatureValueCaps;
            public ushort NumberFeatureDataIndices;
        }

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr SetupDiGetClassDevs(ref Guid classGuid, IntPtr enumerator, IntPtr hwndParent, int flags);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiEnumDeviceInterfaces(IntPtr deviceInfoSet, IntPtr deviceInfoData,
            ref Guid interfaceClassGuid, int memberIndex, ref SpDeviceInterfaceData deviceInterfaceData);

        [DllImport("setupapi.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern bool SetupDiGetDeviceInterfaceDetail(IntPtr deviceInfoSet,
            ref SpDeviceInterfaceData deviceInterfaceData, IntPtr deviceInterfaceDetailData,
            int deviceInterfaceDetailDataSize, out int requiredSize, IntPtr deviceInfoData);

        [DllImport("setupapi.dll", SetLastError = true)]
        private static extern bool SetupDiDestroyDeviceInfoList(IntPtr deviceInfoSet);

        [DllImport("hid.dll")]
        private static extern void HidD_GetHidGuid(out Guid hidGuid);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetAttributes(IntPtr handle, out HiddAttributes attributes);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetPreparsedData(IntPtr handle, out IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_FreePreparsedData(IntPtr preparsedData);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern int HidP_GetCaps(IntPtr preparsedData, ref HidpCaps capabilities);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_SetFeature(IntPtr handle, byte[] reportBuffer, int reportBufferLength);

        [DllImport("hid.dll", SetLastError = true)]
        private static extern bool HidD_GetFeature(IntPtr handle, byte[] reportBuffer, int reportBufferLength);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern IntPtr CreateFileW(string fileName, uint desiredAccess, uint shareMode,
            IntPtr securityAttributes, uint creationDisposition, uint flagsAndAttributes, IntPtr templateFile);

        [DllImport("kernel32.dll", SetLastError = true)]
        private static extern bool CloseHandle(IntPtr handle);

        private static ushort GetFeatureReportLength(IntPtr handle)
        {
            if (!HidD_GetPreparsedData(handle, out IntPtr preparsed))
                return 0;
            try
            {
                var caps = new HidpCaps { Reserved = new ushort[17] };
                if (HidP_GetCaps(preparsed, ref caps) != 0x00110000)   // HIDP_STATUS_SUCCESS
                    return 0;
                return caps.FeatureReportByteLength;
            }
            finally
            {
                HidD_FreePreparsedData(preparsed);
            }
        }

        /// <summary>枚举所有 HID 设备接口路径。</summary>
        private static List<string> EnumerateHidInterfacePaths()
        {
            var paths = new List<string>();

            HidD_GetHidGuid(out Guid hidGuid);
            IntPtr set = SetupDiGetClassDevs(ref hidGuid, IntPtr.Zero, IntPtr.Zero,
                DigcfPresent | DigcfDeviceInterface);
            if (set == IntPtr.Zero || set == new IntPtr(-1))
                return paths;

            try
            {
                var data = new SpDeviceInterfaceData { cbSize = Marshal.SizeOf<SpDeviceInterfaceData>() };
                for (int i = 0; ; i++)
                {
                    if (!SetupDiEnumDeviceInterfaces(set, IntPtr.Zero, ref hidGuid, i, ref data))
                        break;

                    IntPtr buffer = Marshal.AllocHGlobal(4096);
                    try
                    {
                        // 64 位下 cbSize 固定为 8；路径字符串从偏移 4 开始
                        Marshal.WriteInt32(buffer, IntPtr.Size == 8 ? 8 : 6);
                        if (SetupDiGetDeviceInterfaceDetail(set, ref data, buffer, 4096, out _, IntPtr.Zero))
                        {
                            string path = Marshal.PtrToStringUni(IntPtr.Add(buffer, 4)) ?? string.Empty;
                            if (path.StartsWith(@"\\?\", StringComparison.Ordinal))
                                paths.Add(path);
                        }
                    }
                    finally
                    {
                        Marshal.FreeHGlobal(buffer);
                    }
                }
            }
            finally
            {
                SetupDiDestroyDeviceInfoList(set);
            }

            return paths;
        }
    }
}
