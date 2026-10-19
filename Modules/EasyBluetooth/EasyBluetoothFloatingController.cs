using System;
using System.Windows;
using GameHub.Utils;

namespace GameHub.Modules.EasyBluetooth
{
    /// <summary>
    /// 悬浮窗控制器：负责按设置显示/隐藏悬浮窗、保存位置、随设置变更实时生效。
    ///
    /// 放在独立类型里而不是让模块或窗口互相持有，是为了避免
    /// 「设置窗口 → 悬浮窗 → 设置窗口」这种循环引用，也方便主界面卡片统一控制。
    /// </summary>
    internal static class EasyBluetoothFloatingController
    {
        private static EasyBluetoothFloatingWindow? _window;
        private static bool _exitHooked;

        /// <summary>拖动过程中限制写盘频率，避免每移动一像素就写一次 settings.json。</summary>
        private static DateTimeOffset _lastPositionSave = DateTimeOffset.MinValue;

        private static readonly TimeSpan PositionSaveThrottle = TimeSpan.FromMilliseconds(400);

        public static bool IsShown => _window != null;

        // ---------- 应用设置 ----------

        /// <summary>读取设置并让悬浮窗显示状态与之一致（设置保存后、程序启动时调用）。</summary>
        public static void Apply()
        {
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null)
                return;

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(Apply));
                return;
            }

            var settings = SettingsStore.Load().EasyBluetooth;
            settings.Normalize();

            if (!settings.FloatingEnabled)
            {
                Hide(persistSetting: false);
                return;
            }

            if (_window == null)
            {
                try
                {
                    _window = new EasyBluetoothFloatingWindow();
                    _window.Closed += (_, _) => _window = null;

                    HookApplicationExit();
                    _window.Show();

                    AppLog.Write("EasyBluetooth：悬浮窗已显示");
                }
                catch (Exception ex)
                {
                    _window = null;
                    AppLog.Write($"EasyBluetooth：显示悬浮窗失败：{ex.GetType().Name}: {ex.Message}");
                }
            }
            else
            {
                _window.ApplySettings();
            }
        }

        public static void Show(bool persistSetting = true)
        {
            if (persistSetting)
            {
                var settings = SettingsStore.Load().EasyBluetooth;
                settings.FloatingEnabled = true;
                SettingsStore.Save(settings);
            }

            Apply();
        }

        public static void Hide(bool persistSetting)
        {
            var dispatcher = Application.Current?.Dispatcher;

            void HideCore()
            {
                FlushPosition();

                var window = _window;
                _window = null;

                if (window != null)
                {
                    try
                    {
                        window.Close();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"EasyBluetooth：关闭悬浮窗失败：{ex.GetType().Name}: {ex.Message}");
                    }
                }
            }

            if (dispatcher != null && !dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() =>
                {
                    HideCore();
                    if (persistSetting)
                        PersistEnabled(false);
                }));
                return;
            }

            HideCore();

            if (persistSetting)
                PersistEnabled(false);
        }

        public static void Toggle()
        {
            if (IsShown)
                Hide(persistSetting: true);
            else
                Show(persistSetting: true);
        }

        private static void PersistEnabled(bool enabled)
        {
            try
            {
                var settings = SettingsStore.Load().EasyBluetooth;
                settings.FloatingEnabled = enabled;
                SettingsStore.Save(settings);
                EasyBluetoothModule.NotifySettingsChanged();
            }
            catch (Exception ex)
            {
                AppLog.Write($"EasyBluetooth：保存悬浮窗开关失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------- 位置记忆 ----------

        /// <summary>拖动过程中调用（自带节流）。</summary>
        public static void SavePosition(double left, double top)
        {
            var now = DateTimeOffset.Now;
            if (now - _lastPositionSave < PositionSaveThrottle)
                return;

            _lastPositionSave = now;
            WritePosition(left, top);
        }

        /// <summary>把当前窗口位置立即写回设置（关闭/隐藏前调用）。</summary>
        public static void FlushPosition()
        {
            var window = _window;
            if (window == null || window.IsApplyingPosition)
                return;

            try
            {
                WritePosition(window.Left, window.Top);
            }
            catch (Exception ex)
            {
                AppLog.Write($"EasyBluetooth：保存悬浮窗位置失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void WritePosition(double left, double top)
        {
            if (double.IsNaN(left) || double.IsNaN(top))
                return;

            var settings = SettingsStore.Load().EasyBluetooth;
            settings.FloatingLeft = left;
            settings.FloatingTop = top;
            SettingsStore.Save(settings);
        }

        /// <summary>
        /// 实时预览不透明度：只改窗口透明度，不写 settings.json。
        /// 拖动滑块会高频触发，落盘留给「保存」。
        /// </summary>
        public static void PreviewOpacity(double opacity)
        {
            var window = _window;
            if (window == null)
                return;

            double value = EasyBluetoothSettings.NormalizeOpacity(opacity);

            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action(() => window.Opacity = value));
                return;
            }

            window.Opacity = value;
        }

        /// <summary>把悬浮窗摆回默认位置（右上角）。</summary>
        public static void ResetPosition()
        {
            var settings = SettingsStore.Load().EasyBluetooth;
            settings.FloatingLeft = -1;
            settings.FloatingTop = -1;
            SettingsStore.Save(settings);

            if (_window != null)
                _window.ApplySettings();
        }

        // ---------- 退出 ----------

        private static void HookApplicationExit()
        {
            if (_exitHooked || Application.Current == null)
                return;

            _exitHooked = true;
            Application.Current.Exit += (_, _) => FlushPosition();
        }
    }
}
