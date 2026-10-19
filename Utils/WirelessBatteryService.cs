using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Devices.Bluetooth;
using Windows.Devices.Bluetooth.GenericAttributeProfile;
using Windows.Devices.Enumeration;
using Windows.Storage.Streams;

namespace GameHub.Utils
{
    /// <summary>无线设备大类（用于列表分组/图标）。</summary>
    public enum WirelessDeviceKind
    {
        Audio,
        Input,
        Other
    }

    /// <summary>电量来源（决定列表里那行小字怎么写，方便排查“为什么读不到电量”）。</summary>
    public enum BatterySource
    {
        /// <summary>没有读到电量。</summary>
        None,

        /// <summary>PnP 设备节点上的蓝牙电量属性（和系统“蓝牙和其他设备”显示的是同一份数据）。</summary>
        DeviceProperty,

        /// <summary>蓝牙 GATT 电池服务（0x180F / 0x2A19）。</summary>
        Gatt,

        /// <summary>Windows 设备电源接口（覆盖 HID / 2.4G 无线接收器）。</summary>
        SystemBattery,

        /// <summary>
        /// HID 标准电量（用法页 0x06 Generic Device Controls / 用法 0x20 Battery Strength）。
        /// **跨厂商通用、零型号适配**：不看品牌，任何按 HID 规范上报电量的设备都能读到。
        /// </summary>
        HidBattery,

        /// <summary>雷蛇私有 HID 协议（Windows 完全不上报电量的 2.4G 接收器 / 有线设备）。</summary>
        RazerHid,

        /// <summary>前行者（EWEADN）私有 HID 协议（S9 系列 2.4G 接收器 / 有线）。</summary>
        EweadnHid,

        /// <summary>华硕 ROG 私有 HID 协议（夜魔 Falchion / Azoth 等无线键盘）。</summary>
        RogHid,

        /// <summary>WLMouse（万灵）私有 HID 协议（Beast/花兽 系列）。</summary>
        WlMouseHid,

        /// <summary>XInput 手柄电量（XInputGetBatteryInformation，公开 API；只有空/低/中/满档位）。</summary>
        Xinput,

        /// <summary>本次没读到，显示的是上一次的值。</summary>
        Cached
    }

    /// <summary>一台无线设备及其电量。</summary>
    public sealed class WirelessBatteryDevice
    {
        /// <summary>稳定标识（同一台设备在多次扫描间保持一致）。</summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>显示名。</summary>
        public string Name { get; init; } = string.Empty;

        /// <summary>蓝牙地址 AA:BB:CC:DD:EE:FF；非蓝牙设备为空。</summary>
        public string Address { get; init; } = string.Empty;

        /// <summary>是否蓝牙设备（false = 2.4G/其它无线外设）。</summary>
        public bool IsBluetooth { get; init; } = true;

        public WirelessDeviceKind Kind { get; init; }

        public bool IsConnected { get; init; }

        /// <summary>电量百分比；未知为 null。</summary>
        public int? BatteryPercent { get; set; }

        /// <summary>是否正在充电；只有私有协议（ROG/WLMouse）和 XInput 有线能给出。</summary>
        public bool IsCharging { get; init; }

        public BatterySource Source { get; set; } = BatterySource.None;

        /// <summary>
        /// 补充说明（来自各读取器）：例如「设备休眠未应答」「XInput 只报有线」
        /// 「OMNI 接收器在线但没有已配对的无线设备」。会拼到列表行末，
        /// 让用户一眼看出“为什么没有数字”，而不是干巴巴一个「电量未知」。
        /// </summary>
        public string Detail { get; set; } = string.Empty;

        /// <summary>该电量值的读取时间；未知为 null。</summary>
        public DateTimeOffset? BatteryReadAt { get; set; }

        public string KindText => Kind switch
        {
            WirelessDeviceKind.Audio => "音频",
            WirelessDeviceKind.Input => "输入",
            _ => "其他"
        };

        public string BatteryText => BatteryPercent.HasValue ? $"{BatteryPercent.Value}%" : "—";

        /// <summary>电量来源的中文说明。</summary>
        public string SourceText => Source switch
        {
            BatterySource.DeviceProperty => "设备属性",
            BatterySource.Gatt => "蓝牙 GATT",
            BatterySource.SystemBattery => "系统电源接口",
            BatterySource.HidBattery => "HID 标准电量",
            BatterySource.RazerHid => "雷蛇 HID 协议",
            BatterySource.EweadnHid => "前行者 HID 协议",
            BatterySource.RogHid => "ROG HID 协议",
            BatterySource.WlMouseHid => "WLMouse HID 协议",
            BatterySource.Xinput => "XInput 接口",
            BatterySource.Cached => "上次读数",
            _ => "未知"
        };

        /// <summary>列表单行文本。</summary>
        public string DisplayText
        {
            get
            {
                var parts = new List<string> { string.IsNullOrWhiteSpace(Name) ? "(未命名设备)" : Name };

                parts.Add(BatteryPercent.HasValue ? $"电量 {BatteryPercent.Value}%" : "电量未知");

                if (BatteryPercent.HasValue && Source == BatterySource.Cached)
                    parts.Add("上次读数");

                parts.Add(IsConnected ? "已连接" : "未连接");

                if (!string.IsNullOrWhiteSpace(Detail))
                    parts.Add(Detail);

                if (Address.Length > 0)
                    parts.Add(Address);

                return string.Join("   · ", parts);
            }
        }
    }

    /// <summary>
    /// 无线设备电量读取服务（EasyBluetooth 模块的数据层）。
    ///
    /// 取数通道（按实机验证后的性价比排序）：
    ///
    ///   1) PnP 设备节点属性（见 PnpBatteryReader）
    ///      —— 与系统“设置 → 蓝牙和其他设备”显示的电量同源；
    ///      —— 只读设备枚举，不需要连接设备，占用最低；
    ///      —— 本机实测：Logi POP Mouse 80%、DURGOD K330w 90% 等 BLE 外设都能读到。
    ///
    ///   2) 蓝牙 GATT 电池服务（0x180F / 0x2A19）
    ///      —— 通道 1 读不到、且设备已连接时的兜底；
    ///      —— GATT 会话按设备缓存复用，不每次重连。
    ///
    ///   3) Windows 设备电源接口（Windows.Devices.Power.Battery）
    ///      —— 覆盖通过 HID 上报电量的设备，也就是常见的 2.4G 无线键鼠接收器。
    ///
    /// 蓝牙设备枚举同时走两条选择器：
    ///   · BluetoothDevice   —— 经典蓝牙（A2DP 耳机/音箱等）；
    ///   · BluetoothLEDevice —— BLE（大多数现代无线鼠标/键盘/耳机）。
    ///   只用前者会漏掉 BLE 外设（本机实测：只有 BLE 外设才有电量）。
    ///
    /// 反作弊兼容：全程只使用 WinRT 蓝牙/设备枚举 API 与只读 PnP 查询，
    /// 不安装驱动、不注入进程、不挂钩键鼠、不模拟输入。
    /// </summary>
    public static class WirelessBatteryService
    {
        private static readonly Guid BatteryServiceUuid = new("0000180F-0000-1000-8000-00805F9B34FB");
        private static readonly Guid BatteryLevelCharacteristicUuid = new("00002A19-0000-1000-8000-00805F9B34FB");

        /// <summary>打开 GATT 服务/特征的超时（毫秒）。</summary>
        private const int GattOpenTimeoutMs = 4000;

        /// <summary>读取电量特征值的超时（毫秒）。</summary>
        private const int GattReadTimeoutMs = 1500;

        /// <summary>GATT 失败后的重试间隔：避免对不支持电量的设备反复尝试。</summary>
        private static readonly TimeSpan GattRetryInterval = TimeSpan.FromMinutes(5);

        // AEP（Association Endpoint）属性名：蓝牙设备的地址/类别/连接状态
        private const string AepDeviceAddress = "System.Devices.Aep.DeviceAddress";
        private const string AepCategory = "System.Devices.Aep.Category";
        private const string AepIsConnected = "System.Devices.Aep.IsConnected";

        private sealed class GattSession
        {
            public BluetoothLEDevice Device = null!;
            public GattDeviceService Service = null!;
            public GattCharacteristic Characteristic = null!;
        }

        /// <summary>GATT 会话缓存：复用连接，避免每次轮询都重连设备。</summary>
        private static readonly Dictionary<string, GattSession> Sessions =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, DateTimeOffset> GattFailedAt =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>电量缓存：设备断开后仍显示最后一次读到的值（标注为“上次读数”）。</summary>
        private static readonly Dictionary<string, int> BatteryCache =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, BatterySource> BatteryCacheSource =
            new(StringComparer.OrdinalIgnoreCase);

        private static readonly Dictionary<string, DateTimeOffset> BatteryCacheTime =
            new(StringComparer.OrdinalIgnoreCase);

        /// <summary>串行化扫描：避免定时轮询与手动刷新同时跑两遍。</summary>
        private static readonly SemaphoreSlim Gate = new(1, 1);

        /// <summary>是否为 Windows 10 2004+（蓝牙 WinRT API 可用）。</summary>
        public static bool IsSupported => Environment.OSVersion.Version.Build >= 19041;

        // ---------- 对外入口 ----------

        /// <summary>
        /// 扫描一次无线设备。
        /// </summary>
        /// <param name="allowGattRead">
        /// 是否允许在 PnP 属性读不到时用 GATT 兜底（需要设备已连接）。
        /// </param>
        public static async Task<List<WirelessBatteryDevice>> ScanAsync(
            bool allowGattRead, CancellationToken cancellationToken = default)
        {
            var result = new List<WirelessBatteryDevice>();
            if (!IsSupported)
                return result;

            await Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var pnpDevices = PnpBatteryReader.ReadAll();
                var pnpBatteryByAddress = BuildPnpBatteryMap(pnpDevices);

                var seenAddresses = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var liveIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                await EnumerateClassicBluetoothAsync(
                    result, seenAddresses, liveIds, pnpBatteryByAddress, allowGattRead, cancellationToken)
                    .ConfigureAwait(false);

                await EnumerateBluetoothLowEnergyAsync(
                    result, seenAddresses, liveIds, pnpBatteryByAddress, allowGattRead, cancellationToken)
                    .ConfigureAwait(false);

                var systemBatteries = await ReadSystemBatteriesAsync().ConfigureAwait(false);

                // 安全网：PnP 有电量、但蓝牙枚举没列出来的设备也要显示（否则会“有数据却看不到”）
                AppendUnmatchedPnpBatteries(result, pnpDevices, seenAddresses);

                // 非蓝牙设备：只有确实读到电量的才进列表（否则 USB 设备会刷屏）
                AppendNonBluetoothDevices(result, systemBatteries, pnpDevices);

                SortDevices(result);

                // 设备已不在（解除配对）→ 回收 GATT 会话，避免连接泄漏
                foreach (var id in Sessions.Keys.Where(k => !liveIds.Contains(k)).ToList())
                    DropGattSession(id);

                AppLog.Write($"EasyBluetooth：扫描完成，共 {result.Count} 台设备（蓝牙 {result.Count(d => d.IsBluetooth)} 台），" +
                             $"读到电量 {result.Count(d => d.BatteryPercent.HasValue)} 台，GATT 兜底={(allowGattRead ? "开" : "关")}");
            }
            catch (OperationCanceledException)
            {
                // 正常取消
            }
            catch (Exception ex)
            {
                AppLog.Write("EasyBluetooth 扫描异常", ex);
            }
            finally
            {
                Gate.Release();
            }

            return result;
        }

        /// <summary>清理缓存（程序退出/关闭模块时调用）。</summary>
        public static void Shutdown()
        {
            foreach (var id in Sessions.Keys.ToList())
                DropGattSession(id);

            Sessions.Clear();
            GattFailedAt.Clear();
        }

        // ---------- 蓝牙枚举：经典蓝牙 ----------

        private static async Task EnumerateClassicBluetoothAsync(
            List<WirelessBatteryDevice> result,
            HashSet<string> seenAddresses,
            HashSet<string> liveIds,
            IReadOnlyDictionary<string, int> pnpBatteryByAddress,
            bool allowGattRead,
            CancellationToken cancellationToken)
        {
            try
            {
                string selector = BluetoothDevice.GetDeviceSelectorFromPairingState(true);
                var infos = await DeviceInformation.FindAllAsync(selector).AsTask(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var info in infos)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    BluetoothDevice? device = null;
                    try
                    {
                        device = await AwaitWithTimeout(
                            BluetoothDevice.FromIdAsync(info.Id).AsTask(), GattOpenTimeoutMs)
                            .ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"EasyBluetooth：读取经典蓝牙设备 {info.Id} 失败：{ex.GetType().Name}: {ex.Message}");
                    }

                    try
                    {
                        AddBluetoothDevice(
                            result, seenAddresses, liveIds, pnpBatteryByAddress, allowGattRead, cancellationToken,
                            id: info.Id,
                            name: FirstNonEmpty(device?.Name, info.Name),
                            address: device != null ? FormatBluetoothAddress(device.BluetoothAddress) : string.Empty,
                            connected: device?.ConnectionStatus == BluetoothConnectionStatus.Connected,
                            kind: ResolveClassicKind(device));
                    }
                    finally
                    {
                        // BluetoothDevice 对象本身不承载连接，读完即释放
                        try { device?.Dispose(); } catch { }
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write("EasyBluetooth：枚举经典蓝牙设备异常", ex);
            }
        }

        // ---------- 蓝牙枚举：BLE ----------

        private static async Task EnumerateBluetoothLowEnergyAsync(
            List<WirelessBatteryDevice> result,
            HashSet<string> seenAddresses,
            HashSet<string> liveIds,
            IReadOnlyDictionary<string, int> pnpBatteryByAddress,
            bool allowGattRead,
            CancellationToken cancellationToken)
        {
            try
            {
                string selector = BluetoothLEDevice.GetDeviceSelectorFromPairingState(true);

                // 直接向 AEP 属性存储要地址/类别/连接状态：
                // 比逐台 BluetoothLEDevice.FromIdAsync 更便宜，也不会带来连接切换。
                var requestedProperties = new[] { AepDeviceAddress, AepCategory, AepIsConnected };

                var infos = await DeviceInformation.FindAllAsync(
                        selector, requestedProperties, DeviceInformationKind.AssociationEndpoint)
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);

                foreach (var info in infos)
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    try
                    {
                        string address = GetPropertyString(info, AepDeviceAddress);

                        AddBluetoothDevice(
                            result, seenAddresses, liveIds, pnpBatteryByAddress, allowGattRead, cancellationToken,
                            id: info.Id,
                            name: info.Name,
                            address: address,
                            connected: GetPropertyBool(info, AepIsConnected) ?? false,
                            kind: ResolveBleKind(GetPropertyString(info, AepCategory)));
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"EasyBluetooth：读取 BLE 设备 {info.Id} 失败：{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                AppLog.Write("EasyBluetooth：枚举 BLE 设备异常", ex);
            }
        }

        /// <summary>把一台蓝牙设备加入结果列表，并按“PnP 属性 → GATT → 缓存”的顺序取电量。</summary>
        private static async Task AddBluetoothDevice(
            List<WirelessBatteryDevice> result,
            HashSet<string> seenAddresses,
            HashSet<string> liveIds,
            IReadOnlyDictionary<string, int> pnpBatteryByAddress,
            bool allowGattRead,
            CancellationToken cancellationToken,
            string id,
            string name,
            string address,
            bool connected,
            WirelessDeviceKind kind)
        {
            string addressHex = ToHexAddress(address);

            // 同一台设备可能同时被经典蓝牙与 BLE 枚举到，也可能有多条配对记录：按地址去重
            if (addressHex.Length == 12 && !seenAddresses.Add(addressHex))
                return;

            if (addressHex.Length != 12 && !seenAddresses.Add(id))
                return;

            liveIds.Add(id);

            var item = new WirelessBatteryDevice
            {
                Id = id,
                Name = name,
                Address = address,
                IsBluetooth = true,
                Kind = kind,
                IsConnected = connected
            };

            // 通道 1：PnP 设备属性（与系统设置同源，最省资源）
            if (addressHex.Length == 12 && pnpBatteryByAddress.TryGetValue(addressHex, out int pnpBattery))
                SetBattery(item, pnpBattery, BatterySource.DeviceProperty, persist: true);

            // 通道 2：GATT 电池服务（仅在属性读不到且设备已连接时兜底）
            if (!item.BatteryPercent.HasValue && allowGattRead && connected)
            {
                int? gatt = await TryReadGattBatteryAsync(id, cancellationToken).ConfigureAwait(false);
                if (gatt.HasValue)
                    SetBattery(item, gatt.Value, BatterySource.Gatt, persist: true);
            }

            // 通道 3：上次读到的值
            if (!item.BatteryPercent.HasValue)
                ApplyCachedBattery(item);

            result.Add(item);
        }

        // ---------- PnP 电量索引 ----------

        /// <summary>12 位十六进制蓝牙地址 → PnP 里读到的电量。</summary>
        private static Dictionary<string, int> BuildPnpBatteryMap(List<PnpBatteryReader.PnpDeviceBattery> pnpDevices)
        {
            var map = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var device in pnpDevices)
            {
                if (!device.BatteryPercent.HasValue || device.AddressHex.Length != 12)
                    continue;

                // 同一地址有多个节点时取第一个（数值相同，来源一致）
                map.TryAdd(device.AddressHex, device.BatteryPercent.Value);
            }

            return map;
        }

        /// <summary>
        /// 安全网：PnP 节点上有电量、但蓝牙枚举没有列出来的设备也补进列表。
        /// 这类设备确实在系统里活跃（否则不会有电量读数），所以标记为已连接。
        /// </summary>
        private static void AppendUnmatchedPnpBatteries(
            List<WirelessBatteryDevice> result,
            List<PnpBatteryReader.PnpDeviceBattery> pnpDevices,
            HashSet<string> seenAddresses)
        {
            foreach (var pnp in pnpDevices)
            {
                if (!pnp.BatteryPercent.HasValue)
                    continue;

                if (pnp.Enumerator is not ("BTHENUM" or "BTHLE"))
                    continue;

                if (pnp.AddressHex.Length != 12 || !seenAddresses.Add(pnp.AddressHex))
                    continue;

                string name = string.IsNullOrWhiteSpace(pnp.Name)
                    ? "(未命名蓝牙设备)"
                    : CleanSystemBatteryName(pnp.Name);

                result.Add(new WirelessBatteryDevice
                {
                    Id = pnp.InstanceId,
                    Name = name,
                    Address = FormatHexAddress(pnp.AddressHex),
                    IsBluetooth = true,
                    Kind = WirelessDeviceKind.Other,
                    IsConnected = true,
                    BatteryPercent = pnp.BatteryPercent,
                    Source = BatterySource.DeviceProperty,
                    BatteryReadAt = DateTimeOffset.Now
                });

                AppLog.Write($"EasyBluetooth：PnP 安全网补入设备 {name}（{pnp.AddressHex}）电量 {pnp.BatteryPercent}%");
            }
        }

        // ---------- 非蓝牙设备 ----------

        private static void AppendNonBluetoothDevices(
            List<WirelessBatteryDevice> result,
            List<SystemBatteryInfo> systemBatteries,
            List<PnpBatteryReader.PnpDeviceBattery> pnpDevices)
        {
            var knownAddresses = new HashSet<string>(seenAddressesOf(result), StringComparer.OrdinalIgnoreCase);
            var knownNames = new HashSet<string>(
                result.Select(d => NormalizeName(d.Name)).Where(n => n.Length > 0), StringComparer.OrdinalIgnoreCase);

            // 1) 系统电源接口（HID / 2.4G 接收器）
            foreach (var battery in systemBatteries)
            {
                if (!battery.Percent.HasValue)
                    continue;

                if (ExtractHexTokens(battery.Id).Any(knownAddresses.Contains))
                    continue;

                string name = CleanSystemBatteryName(battery.Name);
                string normalized = NormalizeName(name);
                if (normalized.Length == 0 || !knownNames.Add(normalized))
                    continue;

                result.Add(new WirelessBatteryDevice
                {
                    Id = battery.Id,
                    Name = name,
                    Address = string.Empty,
                    IsBluetooth = false,
                    Kind = WirelessDeviceKind.Other,
                    IsConnected = true,
                    BatteryPercent = battery.Percent,
                    Source = BatterySource.SystemBattery,
                    BatteryReadAt = DateTimeOffset.Now
                });
            }

            // 2) PnP 节点：只收“确实读到电量”的非蓝牙设备（HID/USB 接收器及子设备）
            foreach (var pnp in pnpDevices.Where(p => p.BatteryPercent.HasValue))
            {
                if (pnp.Enumerator is "BTHENUM" or "BTHLE")
                    continue;

                if (pnp.AddressHex.Length > 0 && knownAddresses.Contains(pnp.AddressHex))
                    continue;

                string name = CleanSystemBatteryName(pnp.Name);
                string normalized = NormalizeName(name);
                if (normalized.Length == 0 || !knownNames.Add(normalized))
                    continue;

                result.Add(new WirelessBatteryDevice
                {
                    Id = pnp.InstanceId,
                    Name = name,
                    Address = string.Empty,
                    IsBluetooth = false,
                    Kind = WirelessDeviceKind.Other,
                    IsConnected = true,
                    BatteryPercent = pnp.BatteryPercent,
                    Source = BatterySource.DeviceProperty,
                    BatteryReadAt = DateTimeOffset.Now
                });
            }

            // 3) HID 标准电量（用法页 0x06 / 用法 0x20）—— 跨厂商通用通道。
            //    不看品牌、不建型号表：任何厂商只要按 HID 规范上报电量都能读到，
            //    这是「任意型号都能显示」唯一能真正通用的一条路。
            foreach (var hid in HidBatteryReader.ReadAll())
            {
                string hidName = hid.Name;
                string hidNormalized = NormalizeName(hidName);
                if (hidNormalized.Length == 0 || !knownNames.Add(hidNormalized))
                    continue;

                bool hidHasBattery = hid.Percent.HasValue;

                result.Add(new WirelessBatteryDevice
                {
                    Id = hid.Id,
                    Name = hidName,
                    Address = string.Empty,
                    IsBluetooth = false,
                    Kind = WirelessDeviceKind.Input,
                    IsConnected = hidHasBattery,
                    BatteryPercent = hid.Percent,
                    Source = hidHasBattery ? BatterySource.HidBattery : BatterySource.None,
                    BatteryReadAt = hidHasBattery ? DateTimeOffset.Now : (DateTimeOffset?)null
                });
            }

            // 4) 雷蛇私有 HID 协议：这类接收器**完全不向 Windows 上报电量**
            //    （实测 DeathAdder V3 Pro 接收器 VID_1532/PID_00B7 没有
            //     DEVPKEY_Device_BatteryLevel，系统电源接口也枚举不到），
            //    上面的 PnP / 系统电源通道对它全部失效，只能走厂商协议。
            foreach (var razer in RazerBatteryReader.ReadAll())
            {
                string name = razer.Name;
                string normalized = NormalizeName(name);
                if (normalized.Length == 0 || !knownNames.Add(normalized))
                    continue;

                // 与其它非蓝牙设备不同：雷蛇设备即使暂时读不到电量也列入列表。
                // 因为它是「用户明确关心的外设」，鼠标休眠时若整行消失，
                // 用户会以为功能没生效；列出来才能看出「设备认到了、只是休眠」。
                bool hasBattery = razer.Percent.HasValue;

                result.Add(new WirelessBatteryDevice
                {
                    Id = razer.Id,
                    Name = name,
                    Address = string.Empty,
                    IsBluetooth = false,
                    Kind = WirelessDeviceKind.Input,
                    IsConnected = hasBattery,
                    BatteryPercent = razer.Percent,
                    Source = hasBattery ? BatterySource.RazerHid : BatterySource.None,
                    BatteryReadAt = hasBattery ? DateTimeOffset.Now : (DateTimeOffset?)null
                });
            }

            // 5) 前行者（EWEADN）私有协议（ms_pix_v1）：与雷蛇同理，S9 系列接收器
            //    不向 Windows 上报电量，只能走厂商协议。协议逆向自前行者官方网页驱动，
            //    开源参考 ccziwe/eweadn-battery。
            foreach (var eweadn in EweadnBatteryReader.ReadAll())
            {
                string name = eweadn.Name;
                string normalized = NormalizeName(name);
                if (normalized.Length == 0 || !knownNames.Add(normalized))
                    continue;

                bool eweadnHasBattery = eweadn.Percent.HasValue;

                result.Add(new WirelessBatteryDevice
                {
                    Id = eweadn.Id,
                    Name = name,
                    Address = string.Empty,
                    IsBluetooth = false,
                    Kind = WirelessDeviceKind.Input,
                    IsConnected = eweadnHasBattery,
                    BatteryPercent = eweadn.Percent,
                    Source = eweadnHasBattery ? BatterySource.EweadnHid : BatterySource.None,
                    BatteryReadAt = eweadnHasBattery ? DateTimeOffset.Now : (DateTimeOffset?)null
                });
            }

            // 6) ROG（华硕）私有协议：夜魔 Falchion / Azoth 等无线键盘 + 鼠标 + OMNI 接收器。
            //    参考 SteveWang92/peripheral-battery-tray 的 rog-battery 模块（协议事实源自 G-Helper）。
            foreach (var rog in RogBatteryReader.ReadAll())
            {
                string name = rog.Name;
                string normalized = NormalizeName(name);
                if (normalized.Length == 0 || !knownNames.Add(normalized))
                    continue;

                bool rogHasBattery = rog.Percent.HasValue;

                result.Add(new WirelessBatteryDevice
                {
                    Id = rog.Id,
                    Name = name,
                    Address = string.Empty,
                    IsBluetooth = false,
                    Kind = rog.IsReceiverStatus ? WirelessDeviceKind.Other : WirelessDeviceKind.Input,
                    IsConnected = rogHasBattery,
                    BatteryPercent = rog.Percent,
                    IsCharging = rog.IsCharging,
                    Source = rogHasBattery ? BatterySource.RogHid : BatterySource.None,
                    Detail = rog.Note,
                    BatteryReadAt = rogHasBattery ? DateTimeOffset.Now : (DateTimeOffset?)null
                });
            }

            // 7) WLMouse（万灵 / 花兽）私有协议：Beast/花兽 家族 2024–2026 全系。
            //    参考 mee7ya/wlmouse-cli 与 Creationsss/wl-mouse（后者解出了充电状态）。
            foreach (var wl in WlMouseBatteryReader.ReadAll())
            {
                string name = wl.Name;
                string normalized = NormalizeName(name);
                if (normalized.Length == 0 || !knownNames.Add(normalized))
                    continue;

                bool wlHasBattery = wl.Percent.HasValue;

                result.Add(new WirelessBatteryDevice
                {
                    Id = wl.Id,
                    Name = name,
                    Address = string.Empty,
                    IsBluetooth = false,
                    Kind = WirelessDeviceKind.Input,
                    IsConnected = wlHasBattery,
                    BatteryPercent = wl.Percent,
                    IsCharging = wl.IsCharging,
                    Source = wlHasBattery ? BatterySource.WlMouseHid : BatterySource.None,
                    Detail = wl.Note,
                    BatteryReadAt = wlHasBattery ? DateTimeOffset.Now : (DateTimeOffset?)null
                });
            }

            // 8) XInput 手柄（有线 / 2.4G XInput 接收器；含致动等第三方 XInput 手柄）。
            //    蓝牙手柄不走这条通道，由上面的 BLE/HID 通道负责。
            //    XInput 只报「空/低/中/满」档位，换算成近似百分比；
            //    有线连接无百分比，只标记充电中。
            foreach (var pad in XinputBatteryReader.ReadAll())
            {
                string normalized = NormalizeName(pad.Name);
                if (normalized.Length == 0 || !knownNames.Add(normalized))
                    continue;

                result.Add(new WirelessBatteryDevice
                {
                    Id = $"xinput:{pad.UserIndex}",
                    Name = pad.Name,
                    Address = string.Empty,
                    IsBluetooth = false,
                    Kind = WirelessDeviceKind.Input,
                    IsConnected = true,
                    BatteryPercent = pad.Percent,
                    IsCharging = pad.IsCharging,
                    Source = (pad.Percent.HasValue || pad.IsCharging)
                        ? BatterySource.Xinput
                        : BatterySource.None,
                    Detail = pad.Note,
                    BatteryReadAt = pad.Percent.HasValue ? DateTimeOffset.Now : (DateTimeOffset?)null
                });
            }
        }

        private static IEnumerable<string> seenAddressesOf(IEnumerable<WirelessBatteryDevice> devices)
        {
            foreach (var device in devices)
            {
                string hex = ToHexAddress(device.Address);
                if (hex.Length == 12)
                    yield return hex;
            }
        }

        // ---------- 通道 3：Windows 设备电源接口 ----------

        private sealed class SystemBatteryInfo
        {
            public string Id { get; init; } = string.Empty;
            public string Name { get; init; } = string.Empty;
            public int? Percent { get; init; }
        }

        private static async Task<List<SystemBatteryInfo>> ReadSystemBatteriesAsync()
        {
            var list = new List<SystemBatteryInfo>();

            try
            {
                // 注意：Windows.Devices.Power.Battery 没有 FindAllAsync，
                // 必须用 GetDeviceSelector() + DeviceInformation.FindAllAsync 枚举，
                // 再对每个设备 ID 调 Battery.FromIdAsync。
                string selector = Windows.Devices.Power.Battery.GetDeviceSelector();
                var infos = await DeviceInformation.FindAllAsync(selector).AsTask().ConfigureAwait(false);

                foreach (var info in infos)
                {
                    // 排除笔记本自身电池（ACPI 设备），它不是“无线设备”
                    if (IsHostPowerDevice(info.Id))
                        continue;

                    try
                    {
                        var battery = await Windows.Devices.Power.Battery.FromIdAsync(info.Id)
                            .AsTask()
                            .ConfigureAwait(false);

                        if (battery == null)
                            continue;

                        var report = battery.GetReport();
                        int? percent = null;

                        if (report != null &&
                            report.RemainingCapacityInMilliwattHours is int remaining &&
                            report.FullChargeCapacityInMilliwattHours is int full &&
                            full > 0)
                        {
                            int value = (int)Math.Round(remaining * 100.0 / full);
                            percent = Math.Clamp(value, 0, 100);
                        }

                        list.Add(new SystemBatteryInfo
                        {
                            Id = info.Id,
                            Name = info.Name,
                            Percent = percent
                        });
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"EasyBluetooth：读取系统电池 {info.Id} 失败：{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"EasyBluetooth：枚举系统电池异常：{ex.GetType().Name}: {ex.Message}");
            }

            return list;
        }

        /// <summary>排除笔记本自身电池（ACPI 设备）。</summary>
        private static bool IsHostPowerDevice(string id)
            => id.Contains("ACPI", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("PNP0C0A", StringComparison.OrdinalIgnoreCase) ||
               id.Contains("MSACPI", StringComparison.OrdinalIgnoreCase);

        // ---------- 通道 2：GATT 电量服务 ----------

        private static async Task<int?> TryReadGattBatteryAsync(string deviceId, CancellationToken cancellationToken)
        {
            if (GattFailedAt.TryGetValue(deviceId, out var failedAt) &&
                DateTimeOffset.Now - failedAt < GattRetryInterval)
                return null;

            try
            {
                if (!Sessions.TryGetValue(deviceId, out var session))
                {
                    session = await OpenGattSessionAsync(deviceId, cancellationToken).ConfigureAwait(false);
                    if (session == null)
                    {
                        GattFailedAt[deviceId] = DateTimeOffset.Now;
                        return null;
                    }

                    Sessions[deviceId] = session;
                    GattFailedAt.Remove(deviceId);
                    AppLog.Write($"EasyBluetooth：已打开 GATT 电池服务（{deviceId}）");
                }

                var read = await AwaitWithTimeout(
                    session.Characteristic.ReadValueAsync(BluetoothCacheMode.Uncached).AsTask(),
                    GattReadTimeoutMs).ConfigureAwait(false);

                if (read == null || read.Status != GattCommunicationStatus.Success)
                {
                    AppLog.Write($"EasyBluetooth：读取 GATT 电量失败（{deviceId}），" +
                                 $"状态={(read == null ? "超时" : read.Status.ToString())}，将重建会话");
                    DropGattSession(deviceId);
                    return null;
                }

                using var reader = DataReader.FromBuffer(read.Value);
                var buffer = new byte[(int)read.Value.Length];
                reader.ReadBytes(buffer);

                if (buffer.Length == 0)
                    return null;

                int level = buffer[0];
                return level is >= 0 and <= 100 ? level : null;
            }
            catch (OperationCanceledException)
            {
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Write($"EasyBluetooth：GATT 读电量异常（{deviceId}）：{ex.GetType().Name}: {ex.Message}");
                DropGattSession(deviceId);
                GattFailedAt[deviceId] = DateTimeOffset.Now;
                return null;
            }
        }

        private static async Task<GattSession?> OpenGattSessionAsync(string deviceId, CancellationToken cancellationToken)
        {
            BluetoothLEDevice? ble = null;
            GattDeviceService? service = null;

            try
            {
                ble = await AwaitWithTimeout(
                    BluetoothLEDevice.FromIdAsync(deviceId).AsTask(), GattOpenTimeoutMs).ConfigureAwait(false);

                if (ble == null)
                    return null;

                var services = await AwaitWithTimeout(
                    ble.GetGattServicesForUuidAsync(BatteryServiceUuid, BluetoothCacheMode.Uncached).AsTask(),
                    GattOpenTimeoutMs).ConfigureAwait(false);

                if (services == null ||
                    services.Status != GattCommunicationStatus.Success ||
                    services.Services.Count == 0)
                {
                    ble.Dispose();
                    return null;
                }

                service = services.Services[0];

                var characteristics = await AwaitWithTimeout(
                    service.GetCharacteristicsForUuidAsync(BatteryLevelCharacteristicUuid, BluetoothCacheMode.Uncached).AsTask(),
                    GattOpenTimeoutMs).ConfigureAwait(false);

                if (characteristics == null ||
                    characteristics.Status != GattCommunicationStatus.Success ||
                    characteristics.Characteristics.Count == 0)
                {
                    service.Dispose();
                    ble.Dispose();
                    return null;
                }

                return new GattSession
                {
                    Device = ble,
                    Service = service,
                    Characteristic = characteristics.Characteristics[0]
                };
            }
            catch (Exception ex)
            {
                AppLog.Write($"EasyBluetooth：建立 GATT 会话失败（{deviceId}）：{ex.GetType().Name}: {ex.Message}");

                try { service?.Dispose(); } catch { }
                try { ble?.Dispose(); } catch { }

                return null;
            }
        }

        private static void DropGattSession(string deviceId)
        {
            if (!Sessions.Remove(deviceId, out var session))
                return;

            // 释放失败不影响功能：会话已在表里移除，下次会重建
            try { session.Characteristic.Service?.Dispose(); } catch { }
            try { session.Device?.Dispose(); } catch { }
        }

        // ---------- 电量缓存 ----------

        private static void SetBattery(WirelessBatteryDevice device, int percent, BatterySource source, bool persist)
        {
            device.BatteryPercent = percent;
            device.Source = source;
            device.BatteryReadAt = DateTimeOffset.Now;

            if (!persist)
                return;

            BatteryCache[device.Id] = percent;
            BatteryCacheSource[device.Id] = source;
            BatteryCacheTime[device.Id] = DateTimeOffset.Now;
        }

        private static void ApplyCachedBattery(WirelessBatteryDevice device)
        {
            if (!BatteryCache.TryGetValue(device.Id, out int cached))
                return;

            device.BatteryPercent = cached;

            // 保留原始来源，让用户知道这个数字是怎么来的
            device.Source = BatteryCacheSource.TryGetValue(device.Id, out var source)
                ? source
                : BatterySource.Cached;

            if (BatteryCacheTime.TryGetValue(device.Id, out var time))
                device.BatteryReadAt = time;
        }

        // ---------- 工具 ----------

        private static void SortDevices(List<WirelessBatteryDevice> devices)
        {
            devices.Sort((a, b) =>
            {
                // 已连接优先 → 有电量优先 → 名称
                int byConnected = b.IsConnected.CompareTo(a.IsConnected);
                if (byConnected != 0)
                    return byConnected;

                int byBattery = b.BatteryPercent.HasValue.CompareTo(a.BatteryPercent.HasValue);
                if (byBattery != 0)
                    return byBattery;

                return string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase);
            });
        }

        private static WirelessDeviceKind ResolveClassicKind(BluetoothDevice? device)
        {
            try
            {
                if (device == null)
                    return WirelessDeviceKind.Other;

                return device.ClassOfDevice.MajorClass switch
                {
                    BluetoothMajorClass.AudioVideo => WirelessDeviceKind.Audio,
                    BluetoothMajorClass.Peripheral => WirelessDeviceKind.Input,
                    _ => WirelessDeviceKind.Other
                };
            }
            catch
            {
                return WirelessDeviceKind.Other;
            }
        }

        /// <summary>把 AEP 的 Category（Mouse / Keyboard / Headset…）映射成设备大类。</summary>
        private static WirelessDeviceKind ResolveBleKind(string category)
        {
            if (string.IsNullOrWhiteSpace(category))
                return WirelessDeviceKind.Other;

            string value = category.Trim().ToUpperInvariant();

            if (value.Contains("MOUSE") || value.Contains("KEYBOARD") ||
                value.Contains("JOYSTICK") || value.Contains("GAMEPAD") || value.Contains("REMOTE"))
                return WirelessDeviceKind.Input;

            if (value.Contains("AUDIO") || value.Contains("HEADSET") || value.Contains("SPEAKER") ||
                value.Contains("HEADPHONE"))
                return WirelessDeviceKind.Audio;

            return WirelessDeviceKind.Other;
        }

        private static string GetPropertyString(DeviceInformation info, string key)
        {
            try
            {
                return info.Properties.TryGetValue(key, out object? value) && value is string text
                    ? text
                    : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static bool? GetPropertyBool(DeviceInformation info, string key)
        {
            try
            {
                if (!info.Properties.TryGetValue(key, out object? value) || value == null)
                    return null;

                return value switch
                {
                    bool flag => flag,
                    int number => number != 0,
                    _ => null
                };
            }
            catch
            {
                return null;
            }
        }

        private static string FormatBluetoothAddress(ulong address)
        {
            if (address == 0)
                return string.Empty;

            var parts = new string[6];
            for (int i = 0; i < 6; i++)
                parts[i] = ((address >> (8 * (5 - i))) & 0xFF).ToString("X2");

            return string.Join(":", parts);
        }

        private static string FormatHexAddress(string hex12)
        {
            if (hex12.Length != 12)
                return string.Empty;

            var parts = new string[6];
            for (int i = 0; i < 6; i++)
                parts[i] = hex12.Substring(i * 2, 2).ToUpperInvariant();

            return string.Join(":", parts);
        }

        /// <summary>把 "AA:BB:CC:DD:EE:FF" 归一化成 12 位大写十六进制；不符合则返回空串。</summary>
        private static string ToHexAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
                return string.Empty;

            var chars = address.Where(Uri.IsHexDigit).ToArray();
            return chars.Length == 12 ? new string(chars).ToUpperInvariant() : string.Empty;
        }

        private static string FirstNonEmpty(params string?[] values)
            => values.FirstOrDefault(v => !string.IsNullOrWhiteSpace(v))?.Trim() ?? string.Empty;

        /// <summary>
        /// 从任意字符串里抽出 12 位十六进制串（蓝牙地址），用于跨通道匹配同一台设备。
        /// 会跳过蓝牙 SIG 的基础 UUID 片段（00805F9B34FB）：设备路径里几乎每条
        /// 蓝牙服务记录都包含它，留着会制造大量假匹配。
        /// </summary>
        private static IEnumerable<string> ExtractHexTokens(string value)
        {
            if (string.IsNullOrEmpty(value))
                yield break;

            var cleaned = value.Replace(":", string.Empty).Replace("-", string.Empty).Replace("&", string.Empty);

            for (int i = 0; i + 12 <= cleaned.Length; i++)
            {
                string candidate = cleaned.Substring(i, 12);
                if (!candidate.All(Uri.IsHexDigit))
                    continue;

                i += 11;   // 跳过已匹配的片段

                if (IsSIGUuidFragment(candidate))
                    continue;

                yield return candidate.ToUpperInvariant();
            }
        }

        /// <summary>蓝牙 SIG 基础 UUID 里反复出现的十六进制片段，不能当成设备地址。</summary>
        private static bool IsSIGUuidFragment(string candidate)
            => string.Equals(candidate, "00805F9B34FB", StringComparison.OrdinalIgnoreCase) ||
               string.Equals(candidate, "000010008000", StringComparison.OrdinalIgnoreCase);

        /// <summary>把系统电池名整理成人能看懂的名字（去掉“(Battery)”之类的后缀）。</summary>
        private static string CleanSystemBatteryName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            string cleaned = name.Trim();
            foreach (var suffix in new[] { " (Battery)", "(Battery)", " Battery", "电池" })
            {
                if (cleaned.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    cleaned = cleaned[..^suffix.Length].Trim();
            }

            return cleaned;
        }

        /// <summary>归一化名称，用于跨通道去重。</summary>
        private static string NormalizeName(string name)
        {
            if (string.IsNullOrWhiteSpace(name))
                return string.Empty;

            var chars = name.Where(char.IsLetterOrDigit).ToArray();
            return new string(chars).ToUpperInvariant();
        }

        /// <summary>等待一个 Task 完成，超时返回 null（并观察其异常，避免未处理异常）。</summary>
        private static async Task<T?> AwaitWithTimeout<T>(Task<T> task, int milliseconds) where T : class
        {
            var finished = await Task.WhenAny(task, Task.Delay(milliseconds)).ConfigureAwait(false);

            if (finished != task)
            {
                _ = task.ContinueWith(t => { _ = t.Exception; }, TaskScheduler.Default);
                return null;
            }

            try
            {
                return await task.ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
        }
    }
}
