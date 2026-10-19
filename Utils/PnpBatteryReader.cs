using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;

namespace GameHub.Utils
{
    /// <summary>
    /// 通过 PnP（即插即用）设备节点属性读取“蓝牙设备电量”。
    ///
    /// 为什么需要它：
    ///   1) 蓝牙耳机/音箱这类经典蓝牙（A2DP/AVRCP）设备通常不提供 GATT 电池服务，
    ///      无法用 BLE 方式读到电量；
    ///   2) 系统“设置 → 蓝牙和其他设备”里能显示电量，是因为蓝牙协议栈把电量
    ///      写进了设备节点的属性存储（DEVPKEY），这里读的就是同一份数据，
    ///      因此数值和系统设置一致。
    ///
    /// 安全性说明（与反作弊相关）：
    ///   全程只调用 PnP 配置管理器的**只读**查询（CM_* 系列），
    ///   不安装驱动、不注入进程、不读写其它进程内存，属于普通用户权限下的常规设备枚举。
    ///
    /// 失败策略：任何一步失败都只返回 null/空列表并记日志，绝不抛异常到调用方。
    /// </summary>
    internal static class PnpBatteryReader
    {
        /// <summary>
        /// 蓝牙电量属性集（Windows 蓝牙栈写入的设备节点属性）。
        /// PID 2 = 电量百分比（0-100）。
        /// </summary>
        private static readonly Guid BluetoothBatteryPropertySet =
            new("104EA319-6EE2-4701-BD47-8DDBF425BBE5");

        private const uint BluetoothBatteryLevelPid = 2;

        /// <summary>DEVPKEY_Device_FriendlyName：设备节点友好名。</summary>
        private static readonly Guid DeviceFriendlyNamePropertySet =
            new("a45c254e-df1c-4efd-8020-67d146a850e0");

        private const uint DeviceFriendlyNamePid = 14;

        // DEVPROP_TYPE_*：属性数据类型
        private const uint DevPropTypeByte = 0x00000003;
        private const uint DevPropTypeUint16 = 0x00000004;
        private const uint DevPropTypeUint32 = 0x00000005;
        private const uint DevPropTypeString = 0x00000012;
        private const uint DevPropTypeStringList = 0x00002012;

        // CM_Get_Device_ID_List 过滤标志
        private const uint CmGetIdListFilterEnumerator = 0x00000001;
        private const uint CmGetIdListFilterPresent = 0x00000100;

        private const int CrSuccess = 0;
        private const int CrNoSuchValue = 0x0000000D;
        private const int CrBufferSmall = 26;

        /// <summary>要扫描的设备枚举器：蓝牙经典 / 蓝牙低功耗 / HID（2.4G 接收器及其子设备）/ USB。</summary>
        private static readonly string[] Enumerators = { "BTHENUM", "BTHLE", "HID", "USB" };

        private static readonly Regex AddressPattern =
            new(@"([0-9A-Fa-f]{12})", RegexOptions.Compiled);

        [StructLayout(LayoutKind.Sequential)]
        private struct DevPropKey
        {
            public Guid FormatId;
            public uint PropertyId;
        }

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int CM_Locate_DevNodeW(out uint devInst, string deviceId, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int CM_Get_DevNode_PropertyW(
            uint devInst,
            ref DevPropKey propertyKey,
            out uint propertyType,
            IntPtr propertyBuffer,
            ref uint propertyBufferSize,
            uint flags);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        private static extern int CM_Get_Child(out uint childDevInst, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", SetLastError = true)]
        private static extern int CM_Get_Sibling(out uint siblingDevInst, uint devInst, uint flags);

        [DllImport("cfgmgr32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        private static extern int CM_Get_Device_ID_ListW(
            IntPtr filter,
            IntPtr buffer,
            uint bufferLength,
            uint flags);

        /// <summary>一个设备节点上读到的信息。</summary>
        internal sealed class PnpDeviceBattery
        {
            /// <summary>设备实例 ID，例如 BTHENUM\{0000110b-...}\7&amp;1f2e3d4c&amp;0&amp;5CADBA7F7EBA_C00000000。</summary>
            public string InstanceId { get; init; } = string.Empty;

            /// <summary>设备友好名（可能为空）。</summary>
            public string Name { get; init; } = string.Empty;

            /// <summary>从实例 ID 里解析出来的 12 位十六进制蓝牙地址（无冒号），非蓝牙设备为空。</summary>
            public string AddressHex { get; init; } = string.Empty;

            /// <summary>设备节点的枚举器（BTHENUM / BTHLE / HID / USB）。</summary>
            public string Enumerator { get; init; } = string.Empty;

            /// <summary>电量百分比；读不到为 null。</summary>
            public int? BatteryPercent { get; init; }
        }

        /// <summary>
        /// 枚举蓝牙 / HID / USB 设备节点并读取其中的电量属性。
        /// 任何异常都被吞掉：读不到就是读不到，不影响主流程。
        /// </summary>
        public static List<PnpDeviceBattery> ReadAll()
        {
            var result = new List<PnpDeviceBattery>();

            try
            {
                foreach (var instanceId in EnumerateInstanceIds())
                {
                    try
                    {
                        int? battery = ReadBatteryFromDevice(instanceId);

                        result.Add(new PnpDeviceBattery
                        {
                            InstanceId = instanceId,
                            Name = ReadFriendlyName(instanceId),
                            AddressHex = ExtractAddressHex(instanceId),
                            Enumerator = ExtractEnumerator(instanceId),
                            BatteryPercent = battery
                        });
                    }
                    catch
                    {
                        // 单个设备读失败不影响其它设备
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"PnpBatteryReader.ReadAll 异常：{ex.GetType().Name}: {ex.Message}");
            }

            return result;
        }

        // ---------- 枚举 ----------

        private static List<string> EnumerateInstanceIds()
        {
            var ids = new List<string>();

            // 每个枚举器单独调用一次再合并。
            // 实测：CM_Get_Device_ID_ListW 的“多字符串枚举器过滤”在本机只生效了第一项
            // （传 BTHENUM/BTHLE/HID/USB 只返回了 BTHENUM 节点，HID/USB 一台都没返回），
            // 分开调用行为可控，也便于逐个排查。
            foreach (var enumerator in Enumerators)
            {
                try
                {
                    ids.AddRange(EnumerateInstanceIds(enumerator));
                }
                catch (Exception ex)
                {
                    AppLog.Write($"PnpBatteryReader：枚举 {enumerator} 节点失败：{ex.GetType().Name}: {ex.Message}");
                }
            }

            return ids;
        }

        private static List<string> EnumerateInstanceIds(string enumerator)
        {
            var ids = new List<string>();
            IntPtr filterPtr = Marshal.StringToHGlobalUni(enumerator);

            try
            {
                uint size = 256 * 1024;
                IntPtr buffer = Marshal.AllocHGlobal((int)size);

                try
                {
                    int cr = CM_Get_Device_ID_ListW(
                        filterPtr, buffer, size,
                        CmGetIdListFilterEnumerator | CmGetIdListFilterPresent);

                    // 缓冲区不够时会返回 CR_BUFFER_SMALL，换更大的缓冲区再试一次
                    if (cr == CrBufferSmall)
                    {
                        size *= 4;
                        Marshal.FreeHGlobal(buffer);
                        buffer = Marshal.AllocHGlobal((int)size);

                        cr = CM_Get_Device_ID_ListW(
                            filterPtr, buffer, size,
                            CmGetIdListFilterEnumerator | CmGetIdListFilterPresent);
                    }

                    if (cr != CrSuccess)
                    {
                        // 该枚举器没有设备时不刷日志（CR_NO_SUCH_VALUE = 0xD）
                        if (cr != CrNoSuchValue)
                            AppLog.Write($"PnpBatteryReader：枚举 {enumerator} 返回 CR=0x{cr:X}");

                        return ids;
                    }

                    // 解析 REG_MULTI_SZ：逐个 \0 分隔的字符串，直到空串
                    int offset = 0;
                    while (true)
                    {
                        IntPtr current = IntPtr.Add(buffer, offset);
                        string? item = Marshal.PtrToStringUni(current);

                        if (string.IsNullOrEmpty(item))
                            break;

                        ids.Add(item);
                        offset += (item.Length + 1) * sizeof(char);

                        if (offset > size)
                            break;
                    }
                }
                finally
                {
                    Marshal.FreeHGlobal(buffer);
                }
            }
            finally
            {
                Marshal.FreeHGlobal(filterPtr);
            }

            return ids;
        }

        private static string ExtractEnumerator(string instanceId)
        {
            int index = instanceId.IndexOf('\\');
            return index > 0 ? instanceId[..index].ToUpperInvariant() : string.Empty;
        }

        private static string ExtractAddressHex(string instanceId)
        {
            // 蓝牙地址出现在实例 ID 末尾的 “&<地址>_” 片段里，而前半段是服务 GUID
            // （例如 BTHENUM\{0000110b-...}_LOCALMFG&0002\7&1f2e3d4c&0&5CADBA7F7EBA_C00000000），
            // 直接匹配第一个 12 位十六进制串会取到 GUID 内容（如 00805F9B34FB）。
            // 因此从最后一个 & 之后开始找。
            if (string.IsNullOrEmpty(instanceId))
                return string.Empty;

            int lastAmpersand = instanceId.LastIndexOf('&');
            string tail = lastAmpersand >= 0 ? instanceId[(lastAmpersand + 1)..] : instanceId;

            var match = AddressPattern.Match(tail);
            return match.Success ? match.Groups[1].Value.ToUpperInvariant() : string.Empty;
        }

        // ---------- 读取属性 ----------

        private static string ReadFriendlyName(string instanceId)
        {
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CrSuccess)
                return string.Empty;

            return ReadStringProperty(devInst, DeviceFriendlyNamePropertySet, DeviceFriendlyNamePid);
        }

        /// <summary>
        /// 在设备节点自身及其子节点上尝试读取蓝牙电量属性。
        /// 电量通常挂在“该蓝牙设备的一个子节点”上，所以必须往子节点找一层。
        /// </summary>
        private static int? ReadBatteryFromDevice(string instanceId)
        {
            if (CM_Locate_DevNodeW(out uint devInst, instanceId, 0) != CrSuccess)
                return null;

            int? value = ReadBatteryProperty(devInst);
            if (value.HasValue)
                return value;

            // 遍历子节点（同一台设备可能有多个协议子节点，其中一个带电量）
            if (CM_Get_Child(out uint child, devInst, 0) == CrSuccess)
            {
                while (true)
                {
                    value = ReadBatteryProperty(child);
                    if (value.HasValue)
                        return value;

                    if (CM_Get_Sibling(out uint sibling, child, 0) != CrSuccess)
                        break;

                    child = sibling;
                }
            }

            return null;
        }

        private static int? ReadBatteryProperty(uint devInst)
        {
            uint size = 64;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);

            try
            {
                var key = new DevPropKey
                {
                    FormatId = BluetoothBatteryPropertySet,
                    PropertyId = BluetoothBatteryLevelPid
                };

                int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buffer, ref size, 0);
                if (cr != CrSuccess)
                    return null;

                int? raw = ReadNumeric(buffer, type, size);
                if (raw is >= 0 and <= 100)
                    return raw;

                return null;
            }
            catch
            {
                return null;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        private static string ReadStringProperty(uint devInst, Guid formatId, uint propertyId)
        {
            uint size = 2048;
            IntPtr buffer = Marshal.AllocHGlobal((int)size);

            try
            {
                var key = new DevPropKey { FormatId = formatId, PropertyId = propertyId };

                int cr = CM_Get_DevNode_PropertyW(devInst, ref key, out uint type, buffer, ref size, 0);
                if (cr != CrSuccess)
                    return string.Empty;

                if (type != DevPropTypeString && type != DevPropTypeStringList)
                    return string.Empty;

                return Marshal.PtrToStringUni(buffer) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }

        /// <summary>按属性类型解析数值（蓝牙电量一般是 BYTE，个别设备用 UINT16/UINT32）。</summary>
        private static int? ReadNumeric(IntPtr buffer, uint type, uint size)
        {
            if (size == 0)
                return null;

            switch (type)
            {
                case DevPropTypeByte:
                    return Marshal.ReadByte(buffer);

                case DevPropTypeUint16:
                    return Marshal.ReadInt16(buffer);

                case DevPropTypeUint32:
                    return Marshal.ReadInt32(buffer);

                default:
                    return null;
            }
        }
    }
}
