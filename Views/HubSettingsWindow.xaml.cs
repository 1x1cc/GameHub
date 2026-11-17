using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameHub.Utils;

namespace GameHub.Views
{
    /// <summary>
    /// 软件设置窗口（主程序级设置）。
    ///
    /// 三项：
    ///   1) 关闭主窗口时的行为；
    ///   2) 开机自启动是否只驻留托盘；
    ///   3) 热键绑定：集中显示 / 修改所有模块的热键（输出、输入、蓝牙暂停、
    ///      Dronesee 录制 / 即时重放），保存后立即生效并同步到各模块卡片。
    ///
    /// 写入 settings.json 后立刻通知主窗口重新读取，否则主窗口内存里的旧设置
    /// 会在退出时把新设置覆盖掉。
    /// </summary>
    public partial class HubSettingsWindow : Window
    {
        private ModuleSettings _settings = SettingsStore.Load();

        /// <summary>热键绑定的默认值：清空输入框时回落到这里，避免出现空热键。</summary>
        private const string DefaultPlaybackHotkey = "Ctrl+Alt+F11";
        private const string DefaultRecordingHotkey = "Ctrl+Alt+F7";
        private const string DefaultBluetoothHotkey = "Ctrl+Alt+F12";
        private const string DefaultRecordHotkey = "Ctrl+Alt+R";
        private const string DefaultReplayHotkey = "Ctrl+Alt+P";

        public HubSettingsWindow()
        {
            InitializeComponent();
            LoadSettings();
        }

        private void LoadSettings()
        {
            // 载入期间控件赋值会触发 Checked / SelectionChanged（会误触发"改设置即应用"并写盘）
            _loadingThemeUi = true;
            _settings = SettingsStore.Load();
            if (_settings.Dronesee == null) _settings.Dronesee = new DroneseeSettings();

            SelectBehavior(CloseBehavior.Normalize(_settings.CloseAction));
            AutoStartTrayCheck.IsChecked = _settings.AutoStartMinimizedToTray;

            // 外观：白天 / 黑夜 + 主题色
            LightRadio.IsChecked = _settings.ThemeLight;
            DarkRadio.IsChecked = !_settings.ThemeLight;
            // 外观：白天 / 黑夜 + 主题色（菱形色块，点一下即切换）
            _accentHex = string.IsNullOrWhiteSpace(_settings.ThemeAccent) ? ThemeManager.AccentPresets[0].Accent : _settings.ThemeAccent;
            RebuildAccentSwatches();

            if (_settings.Pavise == null) _settings.Pavise = new PaviseSettings();
            PaviseStartWithHubCheck.IsChecked = _settings.Pavise.StartWithHub;

            LoadHotkeys();
            BuildFloatingSection();

            _loadingThemeUi = false;
        }

        private void SelectBehavior(string behavior)
        {
            AskRadio.IsChecked = behavior == CloseBehavior.Ask;
            ExitRadio.IsChecked = behavior == CloseBehavior.Exit;
            TrayRadio.IsChecked = behavior == CloseBehavior.Tray;
        }

        // ---------- 外观（主题） ----------

        private bool _loadingThemeUi;
        private string _accentHex = ThemeManager.AccentPresets[0].Accent;

        /// <summary>按界面当前选择套用主题（立即生效）并写入设置。</summary>
        private void ApplyThemeFromUi()
        {
            if (_loadingThemeUi)
                return;

            bool light = LightRadio.IsChecked == true;
            string accent = string.IsNullOrWhiteSpace(_accentHex) ? ThemeManager.CurrentAccentHex : _accentHex;

            ThemeManager.Apply(light, accent);

            var settings = SettingsStore.Load();
            settings.ThemeLight = light;
            settings.ThemeAccent = ThemeManager.CurrentAccentHex;
            SettingsStore.Save(settings);
            AppLog.Write($"设置：主题已切换 light={light} accent={ThemeManager.CurrentAccentHex}");
        }

        private void ThemeMode_Checked(object sender, RoutedEventArgs e) => ApplyThemeFromUi();

        /// <summary>主题色菱形色块：点一下立即换色（并写盘），选中项带高亮环。</summary>
        private void RebuildAccentSwatches()
        {
            if (AccentSwatchHost == null)
                return;

            try
            {
                AccentSwatchHost.Children.Clear();
                AccentSwatchHost.Children.Add(GameHub.Controls.AccentSwatchRow.Create(_accentHex, hex =>
                {
                    _accentHex = hex;
                    ApplyThemeFromUi();
                    RebuildAccentSwatches();
                }));
            }
            catch (Exception ex)
            {
                AppLog.Write($"设置：主题色块生成失败 {ex.GetType().Name}: {ex.Message}");
            }
        }

        private string GetSelectedBehavior()
        {
            if (ExitRadio.IsChecked == true) return CloseBehavior.Exit;
            if (TrayRadio.IsChecked == true) return CloseBehavior.Tray;
            return CloseBehavior.Ask;
        }

        // ---------- 热键绑定 ----------

        /// <summary>代表「设置界面里的一条热键」：标题、输入框、启用开关。</summary>
        private sealed class HotkeyRow
        {
            public string Label = string.Empty;
            public TextBox Box = null!;
            public CheckBox Toggle = null!;
            public string Default = string.Empty;
        }

        private List<HotkeyRow> BuildRows() => new()
        {
            new HotkeyRow { Label = "输出设备切换", Box = PlaybackHotkeyBox, Toggle = PlaybackHotkeyEnabled, Default = DefaultPlaybackHotkey },
            new HotkeyRow { Label = "输入设备切换", Box = RecordingHotkeyBox, Toggle = RecordingHotkeyEnabled, Default = DefaultRecordingHotkey },
            new HotkeyRow { Label = "蓝牙音频暂停", Box = BluetoothHotkeyBox, Toggle = BluetoothHotkeyEnabled, Default = DefaultBluetoothHotkey },
            new HotkeyRow { Label = "Dronesee 录制", Box = RecordHotkeyBox, Toggle = RecordHotkeyEnabled, Default = DefaultRecordHotkey },
            new HotkeyRow { Label = "Dronesee 即时重放", Box = ReplayHotkeyBox, Toggle = ReplayHotkeyEnabled, Default = DefaultReplayHotkey },
        };

        private IEnumerable<TextBox> AllHotkeyBoxes()
            => new[] { PlaybackHotkeyBox, RecordingHotkeyBox, BluetoothHotkeyBox, RecordHotkeyBox, ReplayHotkeyBox };

        /// <summary>把 settings.json 里的 5 个热键填进界面。</summary>
        private void LoadHotkeys()
        {
            PlaybackHotkeyEnabled.IsChecked = _settings.PlaybackHotkeyEnabled;
            PlaybackHotkeyBox.Text = _settings.PlaybackHotkey;

            RecordingHotkeyEnabled.IsChecked = _settings.RecordingHotkeyEnabled;
            RecordingHotkeyBox.Text = _settings.RecordingHotkey;

            BluetoothHotkeyEnabled.IsChecked = _settings.BluetoothAudioPauseHotkeyEnabled;
            BluetoothHotkeyBox.Text = _settings.BluetoothAudioPauseHotkey;

            RecordHotkeyEnabled.IsChecked = _settings.Dronesee.RecordHotkeyEnabled;
            RecordHotkeyBox.Text = _settings.Dronesee.RecordHotkey;

            ReplayHotkeyEnabled.IsChecked = _settings.Dronesee.ReplayHotkeyEnabled;
            ReplayHotkeyBox.Text = _settings.Dronesee.ReplayHotkey;
        }

        /// <summary>
        /// 点入输入框后按下组合键即完成录制。
        /// 只按修饰键不算完整组合；Esc 视为放弃本次录制。
        /// </summary>
        private void HotkeyBox_PreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (sender is not TextBox box)
                return;

            e.Handled = true;

            var key = e.Key == Key.System ? e.SystemKey : e.Key;

            // Esc：放弃本次录制，保持原值
            if (key == Key.Escape)
                return;

            // 只按下修饰键时，不是一个完整组合
            if (key is Key.LeftCtrl or Key.RightCtrl
                or Key.LeftAlt or Key.RightAlt
                or Key.LeftShift or Key.RightShift
                or Key.LWin or Key.RWin
                or Key.None)
            {
                return;
            }

            string? token = KeyToGestureToken(key);
            if (string.IsNullOrEmpty(token))
                return;

            var parts = new List<string>();
            var modifiers = Keyboard.Modifiers;
            if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(ModifierKeys.Windows)) parts.Add("Win");
            parts.Add(token);

            box.Text = string.Join("+", parts);

            // 用户重新输入后，清掉这条输入的报错样式
            ClearHotkeyError(box);
        }

        /// <summary>把 WPF 的 Key 转成与存储格式一致、能被 HotkeyGesture 解析的片段。</summary>
        private static string? KeyToGestureToken(Key key)
        {
            if (key >= Key.A && key <= Key.Z)
                return key.ToString();

            if (key >= Key.D0 && key <= Key.D9)
                return key.ToString();

            if (key >= Key.NumPad0 && key <= Key.NumPad9)
                return key.ToString();

            if (key >= Key.F1 && key <= Key.F24)
                return key.ToString();

            // 其它可用作热键主键的按键（Delete / Insert / Home / 方向键 / 符号键等）
            return key switch
            {
                Key.None => null,
                _ => key.ToString()
            };
        }

        // ---------- 保存 / 校验 ----------

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            ResetHotkeyErrorStyles();

            var rows = BuildRows();
            var parsed = new List<(HotkeyRow Row, uint Modifiers, uint Vk, string Gesture)>();
            var problems = new List<string>();

            foreach (var row in rows)
            {
                string gesture = (row.Box.Text ?? string.Empty).Trim();
                if (gesture.Length == 0)
                    continue;

                if (!HotkeyGesture.TryParse(gesture, out uint modifiers, out uint vk))
                {
                    MarkHotkeyError(row.Box);
                    problems.Add($"「{row.Label}」的热键格式无效：{gesture}");
                    continue;
                }

                parsed.Add((row, modifiers, vk, gesture));
            }

            // 重复检测：把每个热键解析成 (修饰键, 主键) 后比较，
            // 这样 "Ctrl+Alt+F11" 与 "ALT+CTRL+F11" 也能识别为同一个热键。
            var duplicateGroups = parsed
                .GroupBy(p => (p.Modifiers, p.Vk))
                .Where(g => g.Count() > 1)
                .ToList();

            foreach (var group in duplicateGroups)
            {
                var labels = group.Select(p => $"「{p.Row.Label}」").ToList();
                foreach (var p in group)
                    MarkHotkeyError(p.Row.Box);

                problems.Add($"热键重复：{group.First().Gesture} 同时绑定给了 {string.Join("、", labels)}");
            }

            if (problems.Count > 0)
            {
                string message = "检测到重复或无效的热键绑定，无法保存：\n\n· " + string.Join("\n· ", problems);
                HotkeyErrorText.Text = message;
                HotkeyErrorText.Visibility = Visibility.Visible;

                MessageBox.Show(message, "GameHub · 热键绑定",
                    MessageBoxButton.OK, MessageBoxImage.Error);

                NotificationManager.ShowInfo("设置未保存", "热键绑定存在重复或格式错误，请修正后重试");
                AppLog.Write("热键设置保存被拒绝：" + string.Join("；", problems));
                return;
            }

            // ---- 写入设置 ----
            var behavior = GetSelectedBehavior();
            _settings.CloseAction = behavior;
            _settings.AutoStartMinimizedToTray = AutoStartTrayCheck.IsChecked == true;

            // 外观
            _settings.ThemeLight = LightRadio.IsChecked == true;
            _settings.ThemeAccent = ThemeManager.CurrentAccentHex;

            // Pavise：是否随 GameHub 启动（开=马上拉起，关=马上收掉）
            if (_settings.Pavise == null) _settings.Pavise = new PaviseSettings();
            bool paviseWasOn = _settings.Pavise.StartWithHub;
            _settings.Pavise.StartWithHub = PaviseStartWithHubCheck.IsChecked == true;

            _settings.PlaybackHotkeyEnabled = PlaybackHotkeyEnabled.IsChecked == true;
            _settings.PlaybackHotkey = Fallback(PlaybackHotkeyBox, DefaultPlaybackHotkey);

            _settings.RecordingHotkeyEnabled = RecordingHotkeyEnabled.IsChecked == true;
            _settings.RecordingHotkey = Fallback(RecordingHotkeyBox, DefaultRecordingHotkey);

            _settings.BluetoothAudioPauseHotkeyEnabled = BluetoothHotkeyEnabled.IsChecked == true;
            _settings.BluetoothAudioPauseHotkey = Fallback(BluetoothHotkeyBox, DefaultBluetoothHotkey);

            _settings.Dronesee.RecordHotkeyEnabled = RecordHotkeyEnabled.IsChecked == true;
            _settings.Dronesee.RecordHotkey = Fallback(RecordHotkeyBox, DefaultRecordHotkey);

            _settings.Dronesee.ReplayHotkeyEnabled = ReplayHotkeyEnabled.IsChecked == true;
            _settings.Dronesee.ReplayHotkey = Fallback(ReplayHotkeyBox, DefaultReplayHotkey);

            SettingsStore.Save(_settings);

            // 关键：让主窗口丢掉内存里的旧设置（否则它退出时会用旧值覆盖刚保存的设置），
            // 并按最新热键重新注册，做到"保存即生效"。
            if (Application.Current?.MainWindow is MainWindow main)
            {
                main.ReloadSettings();
                main.ReloadHotkeys();
            }

            // Pavise 的启停跟着刚才的选项立刻生效
            if (paviseWasOn != _settings.Pavise.StartWithHub)
            {
                if (_settings.Pavise.StartWithHub)
                    GameHub.Modules.Pavise.PaviseModule.StartWithHub();
                else
                    GameHub.Modules.Pavise.PaviseModule.StopWithHub();
            }

            AppLog.Write($"设置已保存：关闭方式={behavior}，自启动最小化到托盘={_settings.AutoStartMinimizedToTray}，"
                         + $"热键(输出={_settings.PlaybackHotkey}, 输入={_settings.RecordingHotkey}, "
                         + $"蓝牙={_settings.BluetoothAudioPauseHotkey}, 录制={_settings.Dronesee.RecordHotkey}, "
                         + $"重放={_settings.Dronesee.ReplayHotkey})");

            // 第 7 条：保存反馈改为「保存并应用」按钮上方的主题气泡（2 秒自动消失，点其他处也消失）
            GameHub.Controls.PopBubble.ShowApplied(SaveButton, "已应用");
        }

        private static string Fallback(TextBox box, string fallback)
        {
            string text = (box.Text ?? string.Empty).Trim();
            return text.Length == 0 ? fallback : text;
        }

        private void ResetHotkeyErrorStyles()
        {
            foreach (var box in AllHotkeyBoxes())
                ClearHotkeyError(box);

            HotkeyErrorText.Text = string.Empty;
            HotkeyErrorText.Visibility = Visibility.Collapsed;
        }

        private static void MarkHotkeyError(TextBox box)
        {
            box.BorderBrush = new SolidColorBrush(Color.FromRgb(0xFF, 0x48, 0x58));
            box.BorderThickness = new Thickness(2);
        }

        private static void ClearHotkeyError(TextBox box)
        {
            box.ClearValue(Control.BorderBrushProperty);
            box.ClearValue(Control.BorderThicknessProperty);
        }

        // ---------- 悬浮窗（各模块的悬浮窗设置汇总到这一块） ----------

        /// <summary>
        /// 把各模块的悬浮窗设置集中到 GameHub 设置里：
        ///   · GlassBoard 四个桌面组件（时钟 / 天气 / 音乐 / 备忘录）
        ///   · EasyBluetooth 的设备电量悬浮窗
        /// 与各模块自己的设置页共用同一份配置，改哪边都一样；
        /// GlassBoard 组件每 1.5 秒自检时会重新套用选项，所以改完最多 1.5 秒生效。
        /// </summary>
        private void BuildFloatingSection()
        {
            FloatingHost.Children.Clear();

            _settings.EasyBluetooth ??= new EasyBluetoothSettings();

            var gb = GameHub.Modules.GlassBoard.GlassBoardModule.Settings;

            AddFloatingRow("桌面时钟", "时钟组件。锁定移动后鼠标拖不动；始终置顶会盖在普通窗口之上。",
                () => gb.ClockVisible,
                on => { if (on) GameHub.Modules.GlassBoard.GlassBoardModule.ShowClock(); else GameHub.Modules.GlassBoard.GlassBoardModule.HideClock(); SaveGlassBoard(); },
                () => gb.ClockFloating.Locked, on => { gb.ClockFloating.Locked = on; SaveGlassBoard(); },
                () => gb.ClockFloating.Topmost, on => { gb.ClockFloating.Topmost = on; SaveGlassBoard(); },
                () => gb.ClockFloating.ClickThrough, on => { gb.ClockFloating.ClickThrough = on; SaveGlassBoard(); },
                null,
                () => gb.ClockFloating.Opacity, v => { gb.ClockFloating.Opacity = v; SaveGlassBoard(); });

            AddFloatingRow("桌面天气", "天气组件。",
                () => gb.WeatherVisible,
                on => { if (on) GameHub.Modules.GlassBoard.GlassBoardModule.ShowWeather(); else GameHub.Modules.GlassBoard.GlassBoardModule.HideWeather(); SaveGlassBoard(); },
                () => gb.WeatherFloating.Locked, on => { gb.WeatherFloating.Locked = on; SaveGlassBoard(); },
                () => gb.WeatherFloating.Topmost, on => { gb.WeatherFloating.Topmost = on; SaveGlassBoard(); },
                () => gb.WeatherFloating.ClickThrough, on => { gb.WeatherFloating.ClickThrough = on; SaveGlassBoard(); },
                null,
                () => gb.WeatherFloating.Opacity, v => { gb.WeatherFloating.Opacity = v; SaveGlassBoard(); });

            AddFloatingRow("桌面音乐", "音乐组件。",
                () => gb.MusicVisible,
                on => { if (on) GameHub.Modules.GlassBoard.GlassBoardModule.ShowMusic(); else GameHub.Modules.GlassBoard.GlassBoardModule.HideMusic(); SaveGlassBoard(); },
                () => gb.MusicFloating.Locked, on => { gb.MusicFloating.Locked = on; SaveGlassBoard(); },
                () => gb.MusicFloating.Topmost, on => { gb.MusicFloating.Topmost = on; SaveGlassBoard(); },
                () => gb.MusicFloating.ClickThrough, on => { gb.MusicFloating.ClickThrough = on; SaveGlassBoard(); },
                null,
                () => gb.MusicFloating.Opacity, v => { gb.MusicFloating.Opacity = v; SaveGlassBoard(); });

            AddFloatingRow("桌面备忘录", "备忘录组件。",
                () => gb.NotesVisible,
                on => { if (on) GameHub.Modules.GlassBoard.GlassBoardModule.ShowNotes(); else GameHub.Modules.GlassBoard.GlassBoardModule.HideNotes(); SaveGlassBoard(); },
                () => gb.NotesFloating.Locked, on => { gb.NotesFloating.Locked = on; SaveGlassBoard(); },
                () => gb.NotesFloating.Topmost, on => { gb.NotesFloating.Topmost = on; SaveGlassBoard(); },
                () => gb.NotesFloating.ClickThrough, on => { gb.NotesFloating.ClickThrough = on; SaveGlassBoard(); },
                null,
                () => gb.NotesFloating.Opacity, v => { gb.NotesFloating.Opacity = v; SaveGlassBoard(); });

            AddFloatingRow("蓝牙设备悬浮窗", "第三模块（无线设备电量）的悬浮窗。",
                () => _settings.EasyBluetooth.FloatingEnabled, on => { _settings.EasyBluetooth.FloatingEnabled = on; SaveHubSettings(); },
                () => _settings.EasyBluetooth.FloatingLocked, on => { _settings.EasyBluetooth.FloatingLocked = on; SaveHubSettings(); },
                () => _settings.EasyBluetooth.FloatingTopmost, on => { _settings.EasyBluetooth.FloatingTopmost = on; SaveHubSettings(); },
                () => _settings.EasyBluetooth.FloatingClickThrough, on => { _settings.EasyBluetooth.FloatingClickThrough = on; SaveHubSettings(); },
                ("只显示已连接", () => _settings.EasyBluetooth.FloatingOnlyConnected, on => { _settings.EasyBluetooth.FloatingOnlyConnected = on; SaveHubSettings(); }),
                () => _settings.EasyBluetooth.FloatingOpacity, v => { _settings.EasyBluetooth.FloatingOpacity = v; SaveHubSettings(); });

            AppLog.Write($"设置：悬浮窗汇总已生成 {FloatingHost.Children.Count} 项");
        }

        /// <summary>一行悬浮窗设置：标题 + 4~5 个开关 + 一条不透明度滑块。</summary>
        private void AddFloatingRow(string title, string hint,
            Func<bool> getVisible, Action<bool> setVisible,
            Func<bool> getLocked, Action<bool> setLocked,
            Func<bool> getTopmost, Action<bool> setTopmost,
            Func<bool> getClickThrough, Action<bool> setClickThrough,
            (string Label, Func<bool> Get, Action<bool> Set)? extraSwitch,
            Func<double> getOpacity, Action<double> setOpacity)
        {
            var card = new Border
            {
                Background = new SolidColorBrush(Color.FromRgb(0x17, 0x1B, 0x21)),
                BorderBrush = new SolidColorBrush(Color.FromRgb(0x27, 0x2C, 0x34)),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(10),
                Margin = new Thickness(0, 0, 0, 8)
            };

            var stack = new StackPanel();
            card.Child = stack;

            stack.Children.Add(new TextBlock
            {
                Text = title,
                FontSize = 12.5,
                FontWeight = FontWeights.SemiBold,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF9))
            });

            var row = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };
            row.Children.Add(MakeSwitch("显示", getVisible(), setVisible));
            row.Children.Add(MakeSwitch("锁定移动", getLocked(), setLocked));
            row.Children.Add(MakeSwitch("始终置顶", getTopmost(), setTopmost));
            row.Children.Add(MakeSwitch("鼠标穿透", getClickThrough(), setClickThrough));

            if (extraSwitch is { } extra)
                row.Children.Add(MakeSwitch(extra.Label, extra.Get(), extra.Set));

            stack.Children.Add(row);

            var sliderRow = new StackPanel { Orientation = Orientation.Horizontal, Margin = new Thickness(0, 6, 0, 0) };

            sliderRow.Children.Add(new TextBlock
            {
                Text = "不透明度",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x71))
            });

            double start = System.Math.Clamp(getOpacity(), 0.25, 1.0);
            double last = start;

            var percent = new TextBlock
            {
                Text = $"{start * 100:0}%",
                FontSize = 11,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x71))
            };

            var slider = new Slider
            {
                Minimum = 0.25,
                Maximum = 1.0,
                Value = start,
                Width = 140,
                Margin = new Thickness(10, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            };

            slider.ValueChanged += (_, e) =>
            {
                percent.Text = $"{e.NewValue * 100:0}%";

                if (System.Math.Abs(e.NewValue - last) < 0.01)
                    return;

                last = e.NewValue;
                setOpacity(e.NewValue);
            };

            sliderRow.Children.Add(slider);
            sliderRow.Children.Add(percent);
            stack.Children.Add(sliderRow);

            if (!string.IsNullOrEmpty(hint))
            {
                stack.Children.Add(new TextBlock
                {
                    Text = hint,
                    FontSize = 11,
                    Margin = new Thickness(0, 6, 0, 0),
                    TextWrapping = TextWrapping.Wrap,
                    Foreground = new SolidColorBrush(Color.FromRgb(0x5B, 0x64, 0x71))
                });
            }

            FloatingHost.Children.Add(card);
        }

        /// <summary>一个勾选项：点一下直接写进配置（与各模块设置页行为一致）。</summary>
        private static CheckBox MakeSwitch(string text, bool isChecked, Action<bool> set)
        {
            var check = new CheckBox
            {
                Content = text,
                IsChecked = isChecked,
                FontSize = 12,
                Margin = new Thickness(0, 0, 14, 0),
                VerticalAlignment = VerticalAlignment.Center,
                Foreground = new SolidColorBrush(Color.FromRgb(0xF4, 0xF6, 0xF9))
            };

            check.Click += (_, _) => set(check.IsChecked == true);
            return check;
        }

        private static void SaveGlassBoard()
            => GameHub.Modules.GlassBoard.GlassBoardStore.Save(GameHub.Modules.GlassBoard.GlassBoardModule.Settings);

        /// <summary>第三模块的悬浮窗配置存在 GameHub 主设置里，存完立刻套用一次。</summary>
        private void SaveHubSettings()
        {
            SettingsStore.Save(_settings);

            try
            {
                GameHub.Modules.EasyBluetooth.EasyBluetoothFloatingController.Apply();
            }
            catch (Exception ex)
            {
                AppLog.Write($"设置：套用第三模块悬浮窗失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();
    }
}
