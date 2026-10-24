using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace GameHub.Utils
{
    /// <summary>雷柏（Rapoo）设备的一次电量读数。</summary>
    public sealed class RapooBatteryReading
    {
        public string Id { get; init; } = string.Empty;
        public string Name { get; init; } = string.Empty;
        public ushort ProductId { get; init; }
        public int? Percent { get; init; }
        public string Note { get; init; } = string.Empty;
        public string Channel { get; init; } = string.Empty;
    }

    /// <summary>
    /// 雷柏（Rapoo，VID 0x24AE）2.4G 外设电量读取。
    ///
    /// 【为什么走独立进程】
    /// Windows HID 输入报告只会投递给「最先打开该集合句柄」的一方。GameHub 主进程里
    /// 还有通用 HID / 雷蛇 / 罗技等读取器，它们先枚举了 HID 接口，抢占了雷柏 vendor 集合
    /// 的句柄，导致本进程内「写触发帧成功、读却持续超时」（n=-1），怎么也读不到电量。
    ///
    /// 独立进程（Tools/RapooCli）则完全没有竞争，实测连续 8/8 命中 82%。
    /// 本类只负责启动 RapooCli.exe、解析它 stdout 输出的 JSON，不再直接碰 HID。
    ///
    /// 协议细节（已在本机实机验证）见 RapooCli/Program.cs 头部注释，要点：
    ///   触发帧 = report id 6 + A5 A3 00 00 00 00 00 00，写给可写的 vendor 集合（col05）；
    ///   电量报告走 col09：len>=19 && buf[0]==0x07 -> 电量=buf[8]；len>=7 && buf[0]==0xBB -> buf[6]。
    /// </summary>
    public static class RapooBatteryReader
    {
        private const int ProcessTimeoutMs = 6000;   // CLI 最多约 2 秒，给足余量
        private const int ResultCacheMs = 2500;

        private static readonly object Gate = new();
        private static List<RapooBatteryReading>? _cached;
        private static DateTime _cachedAtUtc = DateTime.MinValue;

        private static readonly TimeSpan LastKnownValid = TimeSpan.FromMinutes(5);
        private static readonly Dictionary<string, (int Percent, DateTimeOffset At)> LastKnown =
            new(StringComparer.OrdinalIgnoreCase);

        public static List<RapooBatteryReading> ReadAll()
        {
            lock (Gate)
            {
                if (_cached != null &&
                    (DateTime.UtcNow - _cachedAtUtc).TotalMilliseconds < ResultCacheMs)
                {
                    return new List<RapooBatteryReading>(_cached);
                }

                var result = RunCli();
                _cached = result;
                _cachedAtUtc = DateTime.UtcNow;
                return new List<RapooBatteryReading>(result);
            }
        }

        private static string? LocateCli()
        {
            string baseDir = AppContext.BaseDirectory;
            string exe = Path.Combine(baseDir, "RapooCli.exe");
            if (File.Exists(exe))
                return exe;

            // 开发期回退：直接找 Tools 下的 Release 输出
            var probe = new[]
            {
                Path.Combine(baseDir, "..", "..", "..", "Tools", "RapooCli", "bin", "Release", "net8.0-windows", "RapooCli.exe"),
                Path.Combine(baseDir, "..", "..", "..", "Tools", "RapooCli", "bin", "Debug", "net8.0-windows", "RapooCli.exe"),
            };
            foreach (string p in probe)
            {
                try
                {
                    string full = Path.GetFullPath(p);
                    if (File.Exists(full))
                        return full;
                }
                catch { }
            }

            return null;
        }

        private static List<RapooBatteryReading> RunCli()
        {
            string? exe = LocateCli();
            if (exe == null)
            {
                AppLog.Write("EasyBluetooth/Rapoo：找不到 RapooCli.exe（独立读取进程），跳过雷柏");
                return new List<RapooBatteryReading>();
            }

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = exe,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                };

                using Process? proc = Process.Start(psi);
                if (proc == null)
                    return new List<RapooBatteryReading>();

                string stdout = proc.StandardOutput.ReadToEnd();
                string stderr = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(ProcessTimeoutMs))
                {
                    try { proc.Kill(entireProcessTree: true); } catch { }
                    AppLog.Write("EasyBluetooth/Rapoo：RapooCli 超时");
                    return new List<RapooBatteryReading>();
                }

                if (proc.ExitCode != 0)
                {
                    AppLog.Write($"EasyBluetooth/Rapoo：RapooCli 退出码 {proc.ExitCode}：{stderr.Trim()}");
                    return new List<RapooBatteryReading>();
                }

                return ParseAndAugment(stdout);
            }
            catch (Exception ex)
            {
                AppLog.Write("EasyBluetooth/Rapoo：调用 RapooCli 失败", ex);
                return new List<RapooBatteryReading>();
            }
        }

        private sealed class CliDevice
        {
            public ushort pid { get; set; }
            public string name { get; set; } = "";
            public string channel { get; set; } = "";
            public int? percent { get; set; }
        }

        private static List<RapooBatteryReading> ParseAndAugment(string stdout)
        {
            var result = new List<RapooBatteryReading>();

            List<CliDevice>? devices = null;
            try
            {
                devices = JsonSerializer.Deserialize<List<CliDevice>>(stdout);
            }
            catch (Exception ex)
            {
                AppLog.Write($"EasyBluetooth/Rapoo：解析 RapooCli 输出失败：{stdout.Trim()}", ex);
                return result;
            }

            if (devices == null)
                return result;

            foreach (var d in devices)
            {
                string id = $"rapoo:{d.pid:X4}";
                string name = string.IsNullOrWhiteSpace(d.name)
                    ? $"雷柏 2.4G 设备 (PID 0x{d.pid:X4})"
                    : d.name;

                int? percent = d.percent;
                string note = string.Empty;

                if (percent is > 0)
                {
                    LastKnown[id] = (percent.Value, DateTimeOffset.Now);
                    AppLog.Write($"EasyBluetooth/Rapoo：{name}（{d.channel}）电量 {percent.Value}%（PID 0x{d.pid:X4}）");
                }
                else if (LastKnown.TryGetValue(id, out var last) &&
                         DateTimeOffset.Now - last.At < LastKnownValid)
                {
                    percent = last.Percent;
                    note = $"本次未取到电量，显示 {last.At:HH:mm:ss} 的读数（设备休眠时读不到，动一下设备即可刷新）";
                }
                else
                {
                    percent = null;
                    note = "已识别雷柏设备，但本次未取到电量：设备可能已休眠/关机，动一下设备后刷新";
                }

                result.Add(new RapooBatteryReading
                {
                    Id = id,
                    Name = name,
                    ProductId = d.pid,
                    Percent = percent,
                    Channel = d.channel,
                    Note = note
                });
            }

            return result;
        }
    }
}
