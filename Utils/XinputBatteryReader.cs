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

        // xinput1_4.dll 是 Win8+ 的版本，导出表里有 XInputGetBatteryInformation；
        // 但老系统/精简化系统上可能只有 xinput1_3.dll，而 xinput9_1_0.dll **没有** 这个导出函数。
        // 实机报错记录：'Unable to find an entry point named XInputGetBatteryInformation in DLL xinput9_1_0.dll'
        // ——所以三个库要按 1_4 → 1_3 → 9_1_0 的顺序探测，且失败必须落到下一个候选，不能把结果永久标记为“不可用”。
        [DllImport("xinput1_4.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern int XInputGetBatteryInformation1_4(
            int userIndex, byte deviceType, out XInputBatteryInformation batteryInformation);

        [DllImport("xinput1_3.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern int XInputGetBatteryInformation1_3(
            int userIndex, byte deviceType, out XInputBatteryInformation batteryInformation);

        [DllImport("xinput9_1_0.dll", EntryPoint = "XInputGetBatteryInformation")]
        private static extern int XInputGetBatteryInformation9_1(
            int userIndex, byte deviceType, out XInputBatteryInformation batteryInformation);

        /// <summary>0=未探测，1=xinput1_4，2=xinput1_3，3=xinput9_1_0，4=都不可用。</summary>
        private static int _library;

        /// <summary>库不可用的报错只写一次日志，避免每轮轮询刷屏（旧版每 30 秒喷 4 条堆栈）。</summary>
        private static bool _probeFailureLogged;

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
                    // 实机：致动手柄 + 原厂 2.4G 接收器也是这里（接收器把自己仿真成
                    // “Xbox 360 有线手柄”，XInput 只肯回答「有线」，因此读不到百分比）。
                    // 如实告诉用户原因，而不是假装设备不存在。
                    result.Add(new XinputBatteryReading
                    {
                        UserIndex = i,
                        Name = name,
                        Percent = null,
                        IsCharging = true,
                        IsWired = true,
                        Note = "XInput 报告为有线供电：此通道只能给出“有线/档位”，有线手柄与" +
                               "不上报电量的 2.4G 接收器都拿不到百分比"
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

        /// <summary>
        /// 依次尝试 xinput1_4 → xinput1_3 → xinput9_1_0，命中后就固定在那个库上。
        ///
        /// 旧实现的两个问题（本机实机日志可复现）：
        ///   1) 1_4 抛 EntryPointNotFound 后只回退到 xinput9_1_0，而 9_1_0 根本没有这个导出，
        ///      于是继续抛异常，结果把 _library 永久置为「都不可用」；
        ///   2) 每次轮询都把异常栈写进日志（30 秒一轮，四条一刷）。
        /// </summary>
        private static int QueryOnce(int userIndex, out XInputBatteryInformation info)
        {
            info = default;

            if (_library == 4)
                return -1;

            try
            {
                switch (_library)
                {
                    case 1:
                        return XInputGetBatteryInformation1_4(userIndex, BatteryDevtypeGamepad, out info);
                    case 2:
                        return XInputGetBatteryInformation1_3(userIndex, BatteryDevtypeGamepad, out info);
                    case 3:
                        return XInputGetBatteryInformation9_1(userIndex, BatteryDevtypeGamepad, out info);
                }

                // 探测阶段：按 1_4 → 1_3 → 9_1_0 依次试，第一个能调通的固定下来
                try
                {
                    int hr = XInputGetBatteryInformation1_4(userIndex, BatteryDevtypeGamepad, out info);
                    _library = 1;
                    return hr;
                }
                catch (EntryPointNotFoundException) { }
                catch (DllNotFoundException) { }

                try
                {
                    int hr = XInputGetBatteryInformation1_3(userIndex, BatteryDevtypeGamepad, out info);
                    _library = 2;
                    return hr;
                }
                catch (EntryPointNotFoundException) { }
                catch (DllNotFoundException) { }

                try
                {
                    int hr = XInputGetBatteryInformation9_1(userIndex, BatteryDevtypeGamepad, out info);
                    _library = 3;
                    return hr;
                }
                catch (EntryPointNotFoundException) { }
                catch (DllNotFoundException) { }

                _library = 4;
                if (!_probeFailureLogged)
                {
                    _probeFailureLogged = true;
                    AppLog.Write("XInput：xinput1_4 / xinput1_3 / xinput9_1_0 都取不到 " +
                                 "XInputGetBatteryInformation，本机不显示 XInput 手柄电量（其它通道不受影响）");
                }
            }
            catch (Exception ex)
            {
                if (!_probeFailureLogged)
                {
                    _probeFailureLogged = true;
                    AppLog.Write("XInput 电量查询失败", ex);
                }
            }

            return -1;
        }
    }
}
