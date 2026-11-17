using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace GameHub.Utils
{
    public enum AudioFlow
    {
        Playback,
        Recording
    }

    /// <summary>
    /// 音频端点信息（仅已连接/已启用的设备）。
    /// </summary>
    public sealed class AudioDeviceInfo
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public bool IsDefault { get; set; }
        public AudioFlow Flow { get; set; }

        public string DisplayText => IsDefault ? $"{Name}  ✔ 当前默认" : Name;
    }

    /// <summary>
    /// 音频设备服务：枚举活动端点、切换默认设备。
    /// 仅返回当前已连接（Active）的设备，不显示未连接设备。
    /// </summary>
    public static class AudioDeviceService
    {
        /// <summary>
        /// MMDeviceEnumerator 的 CLSID。
        ///
        /// 这里必须走 Type.GetTypeFromCLSID + Activator，**不能**写
        /// new CoreAudioInterop.MMDeviceEnumerator()：同一个 CLSID 在本项目里还被
        /// AudioSessionMute / WasapiAudioCapture / AudioLevelMeter 各自用 ComImport 声明过，
        /// 多个包装类共存时 RCW 会退化成 System.__ComObject，转成 ComImport 包装类就抛
        /// InvalidCastException（本机日志：AudioDeviceService.GetDevices 异常 … line 45，
        /// 每秒刷一条，设备枚举实际从未成功）。
        /// 与 WasapiAudioCapture.CreateDeviceEnumerator / AudioLevelMeter.Open 同一套绕法。
        /// </summary>
        private static readonly Guid EnumeratorClsid = new("BCDE0395-E52F-467C-8E3D-C4579291692E");

        public static List<AudioDeviceInfo> GetDevices(AudioFlow flow)
        {
            var list = new List<AudioDeviceInfo>();
            var dataFlow = flow == AudioFlow.Playback
                ? CoreAudioInterop.EDataFlow.eRender
                : CoreAudioInterop.EDataFlow.eCapture;

            CoreAudioInterop.IMMDeviceEnumerator? enumerator = null;
            CoreAudioInterop.IMMDeviceCollection? collection = null;

            try
            {
                var enumeratorType = Type.GetTypeFromCLSID(EnumeratorClsid);
                if (enumeratorType == null)
                {
                    AppLog.Write("AudioDeviceService：MMDeviceEnumerator CLSID 未注册，无法枚举音频设备");
                    return list;
                }

                enumerator = (CoreAudioInterop.IMMDeviceEnumerator)Activator.CreateInstance(enumeratorType)!;
                if (enumerator.EnumAudioEndpoints(dataFlow, CoreAudioInterop.DEVICE_STATE_ACTIVE, out collection) != 0)
                    return list;

                string defaultId = TryGetDefaultId(enumerator, dataFlow);

                if (collection.GetCount(out int count) != 0)
                    return list;

                for (int i = 0; i < count; i++)
                {
                    CoreAudioInterop.IMMDevice? device = null;
                    try
                    {
                        if (collection.Item(i, out device) != 0 || device == null)
                            continue;

                        if (device.GetId(out string id) != 0 || string.IsNullOrEmpty(id))
                            continue;

                        list.Add(new AudioDeviceInfo
                        {
                            Id = id,
                            Name = CoreAudioInterop.GetFriendlyName(device),
                            Flow = flow,
                            IsDefault = string.Equals(id, defaultId, StringComparison.OrdinalIgnoreCase)
                        });
                    }
                    finally
                    {
                        if (device != null)
                            Marshal.ReleaseComObject(device);
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"AudioDeviceService.GetDevices 异常：flow={flow}", ex);
            }
            finally
            {
                if (collection != null) Marshal.ReleaseComObject(collection);
                if (enumerator != null) Marshal.ReleaseComObject(enumerator);
            }

            return list.OrderByDescending(d => d.IsDefault).ThenBy(d => d.Name).ToList();
        }

        private static string TryGetDefaultId(CoreAudioInterop.IMMDeviceEnumerator enumerator,
            CoreAudioInterop.EDataFlow dataFlow)
        {
            CoreAudioInterop.IMMDevice? def = null;
            try
            {
                // 使用 eConsole 角色读取默认设备（和 SetDefaultDevice 设置的 eConsole 一致）
                // Windows 的默认设备优先级：eConsole > eMultimedia > eCommunications
                if (enumerator.GetDefaultAudioEndpoint(dataFlow, CoreAudioInterop.ERole.eConsole, out def) != 0 || def == null)
                    return string.Empty;
                return def.GetId(out string id) == 0 ? id : string.Empty;
            }
            catch
            {
                return string.Empty;
            }
            finally
            {
                if (def != null) Marshal.ReleaseComObject(def);
            }
        }

        /// <summary>
        /// 将指定设备设为默认设备。multimedia 与 communications 两个角色都会设置，
        /// 保证播放/录音的默认设备都被切换。
        /// </summary>
        public static bool SetDefaultDevice(string deviceId)
        {
            if (string.IsNullOrEmpty(deviceId))
            {
                AppLog.Write("SetDefaultDevice：deviceId 为空，放弃");
                return false;
            }

            bool okMultimedia = CoreAudioInterop.SetDefaultEndpoint(deviceId, CoreAudioInterop.ERole.eMultimedia);
            bool okConsole = CoreAudioInterop.SetDefaultEndpoint(deviceId, CoreAudioInterop.ERole.eConsole);
            bool okCommunications = CoreAudioInterop.SetDefaultEndpoint(deviceId, CoreAudioInterop.ERole.eCommunications);

            // eMultimedia 决定返回成功；但另外两个角色失败也会导致“界面上看着没切过去”，必须记录。
            if (!okMultimedia || !okConsole || !okCommunications)
                AppLog.Write($"SetDefaultDevice：部分角色失败 multimedia={okMultimedia} console={okConsole} communications={okCommunications} device={deviceId}");

            return okMultimedia;
        }

        /// <summary>切到下一个设备（用于热键循环切换）。</summary>
        public static AudioDeviceInfo? SwitchToNext(AudioFlow flow)
        {
            var devices = GetDevices(flow);
            if (devices.Count == 0)
            {
                AppLog.Write($"SwitchToNext：{flow} 无可用设备");
                return null;
            }

            int index = devices.FindIndex(d => d.IsDefault);
            int next = devices.Count == 1 ? 0 : (index < 0 ? 0 : (index + 1) % devices.Count);

            var target = devices[next];
            AppLog.Write($"SwitchToNext：{flow}，设备数={devices.Count}，当前默认索引={index}，切到 {target.Name}（{target.Id}）");

            // 切换失败时返回 null，避免调用方误报成功
            bool ok = SetDefaultDevice(target.Id);
            if (!ok)
                AppLog.Write($"SwitchToNext 失败：{flow} -> {target.Name}（{target.Id}）");
            return ok ? target : null;
        }
    }
}
