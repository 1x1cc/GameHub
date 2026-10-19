using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace GameHub.Utils
{
    /// <summary>一个 XInput 玩家位的手柄电量读数。</summary>
    public sealed class XinputBatteryReading
    {
        public int UserIndex { get; init; }

        public string Name { get; init; } = string.Empty;

        /// <summary>近似百分比（XInput 只有空/低/中/满档位）；有线连接为 null。</summary>
        public int? Percent { get; init; }

        public bool IsCharging { get; init; }

        public bool IsWired { get; init; }

        public string Note { get; init; } = string.Empty;
    }

    /// <summary>
    /// XInput 手柄电量（XInputGetBatteryInformation —— 微软公开 API，零逆向）。
    ///
    /// 覆盖范围：XInput 会话内的手柄（有线 / 2.4G XInput 接收器），包括 Xbox
    /// 手柄与致动等第三方 XInput 手柄。蓝牙手柄不走这条通道（蓝牙模式下
    /// XInput 看不到设备），由 BLE / HID 标准电量通道负责。
    ///
    /// 局限（如实说清）：XInput 只报「空/低/中/满」档位，这里换算成
    /// 0/25/60/100 的近似百分比；有线连接没有百分比，只标记为充电中。
    ///
    /// 反作弊兼容：这是游戏自身就会调用的公开系统 API，不涉及注入/驱动。
    /// </summary>
    public static class XinputBatteryReader
    {
        private const byte BatteryDevtypeGamepad = 0x00;

        private const byte BatteryTypeDisconnected = 0;
        private const byte BatteryTypeWired = 1;

        private const int ErrorSuccess = 0;
        private const int MaxUsers = 4;

        [StructLayout(LayoutKind.Sequential)]
        private struct XInputBatteryInformation
        {
            public byte BatteryType;
            public byte BatteryLevel;
        }

        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern int XInputGetBatteryInformation1_4(
            int userIndex, byte deviceType, out XInputBatteryInformation batteryInformation);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern int XInputGetBatteryInformation9_1(
            int userIndex, byte deviceType, out XInputBatteryInformation batteryInformation);

        /// <summary>0=未探测，1=xinput1_4，2=xinput9_1_0，3=都不可用。</summary>
        private static int _library;

        private const int ResultCacheMs = 2500;

        private static readonly object Gate = new();
        private static List<XinputBatteryReading>? _cached;
        private static DateTime _cachedAtUtc = DateTime.MinValue;

        public static List<XinputBatteryReading> ReadAll()
        {
            lock (Gate)
            {
                if (_cached != null &&
                    (DateTime.UtcNow - _cachedAtUtc).TotalMilliseconds < ResultCacheMs)
                {
                    return new List<XinputBatteryReading>(_cached);
                }

                var result = QueryAll();
                _cached = result;
                _cachedAtUtc = DateTime.UtcNow;
                return new List<XinputBatteryReading>(result);
            }
        }

        private static List<XinputBatteryReading> QueryAll()
        {
            var result = new List<XinputBatteryReading>();

            for (int i = 0; i < MaxUsers; i++)
            {
                XInputBatteryInformation info;
                int hr = QueryOnce(i, out info);
                if (hr != ErrorSuccess)
                    continue;   // 这个玩家位没有手柄

                if (info.BatteryType == BatteryTypeDisconnected)
                    continue;

                string name = $"游戏手柄 (XInput 玩家 {i + 1})";

                if (info.BatteryType == BatteryTypeWired)
                {
                    result.Add(new XinputBatteryReading
                    {
                        UserIndex = i,
                        Name = name,
                        Percent = null,
                        IsCharging = true,
                        IsWired = true,
                        Note = "有线连接"
                    });
                    continue;
                }

                int percent = info.BatteryLevel switch
                {
                    0 => 0,    // 空
                    1 => 25,   // 低
                    2 => 60,   // 中
                    _ => 100   // 满
                };

                result.Add(new XinputBatteryReading
                {
                    UserIndex = i,
                    Name = name,
                    Percent = percent,
                    IsCharging = false,
                    IsWired = false,
                    Note = "XInput 档位换算（空/低/中/满）"
                });
            }

            return result;
        }

        /// <summary>优先 xinput1_4（Win8+），缺失时退回 xinput9_1_0；结果按库缓存。</summary>
        private static int QueryOnce(int userIndex, out XInputBatteryInformation info)
        {
            info = default;

            try
            {
                if (_library == 1)
                    return XInputGetBatteryInformation1_4(userIndex, BatteryDevtypeGamepad, out info);
                if (_library == 2)
                    return XInputGetBatteryInformation9_1(userIndex, BatteryDevtypeGamepad, out info);
                if (_library == 3)
                    return -1;

                try
                {
                    int hr = XInputGetBatteryInformation1_4(userIndex, BatteryDevtypeGamepad, out info);
                    _library = 1;
                    return hr;
                }
                catch (EntryPointNotFoundException)
                {
                    // 导出表异常：退回通用版
                }
                catch (DllNotFoundException)
                {
                    // 老系统没有 xinput1_4：退回通用版
                }

                try
                {
                    int hr = XInputGetBatteryInformation9_1(userIndex, BatteryDevtypeGamepad, out info);
                    _library = 2;
                    return hr;
                }
                catch (EntryPointNotFoundException)
                {
                    _library = 3;
                }
                catch (DllNotFoundException)
                {
                    _library = 3;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("XInput 电量查询失败", ex);
            }

            return -1;
        }
    }
}
