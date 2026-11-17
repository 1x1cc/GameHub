using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using GameHub.Utils;

namespace GameHub.Modules.Dronesee
{
    public partial class DroneseeSettingsWindow : Window
    {
        private DroneseeSettings _settings = new();
        private bool _isBindingHotkey;
        private string _hotkeyTarget = "record"; // "record" or "replay"
        private Button? _hotkeyButton; // 当前正在绑定热键的按钮
        private System.Windows.Threading.DispatcherTimer? _liveTimer;

        /// <summary>LoadSettings 期间屏蔽滑块事件，避免加载时误触发保存。</summary>
        private bool _suppressToggleEvents;

        public DroneseeSettingsWindow()
        {
            InitializeComponent();
            Loaded += OnLoaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            LoadSettings();
            StartLiveTimer();
        }

        /// <summary>
        /// 每秒刷新一次「内存占用 / 可缓存时长」。
        /// 回放缓冲的实际帧大小取决于画面复杂度，只有运行时才能拿到真实值。
        /// </summary>
        private void StartLiveTimer()
        {
            _liveTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _liveTimer.Tick += (_, _) => UpdateMemoryUsageText();
            _liveTimer.Start();
            UpdateMemoryUsageText();
        }

        /// <summary>从 SettingsStore 加载设置到 UI。</summary>
        private void LoadSettings()
        {
            _suppressToggleEvents = true;
            try
            {
                var moduleSettings = SettingsStore.Load() ?? new ModuleSettings();
                // 确保 _settings 不为 null
                if (moduleSettings.Dronesee == null)
                {
                    moduleSettings.Dronesee = new DroneseeSettings();
                }
                _settings = moduleSettings.Dronesee;
                _settings.Normalize();

                // 保存路径
                if (SavePathBox != null)
                    SavePathBox.Text = _settings.SavePath;

                // 录制总开关（滑块）
                if (RecordingEnabled != null)
                    RecordingEnabled.IsChecked = _settings.RecordingEnabled;

                // 系统声音
                if (SystemAudioBox != null)
                    SystemAudioBox.IsChecked = _settings.SystemAudioEnabled;

                // 麦克风
                if (MicrophoneEnabled != null)
                    MicrophoneEnabled.IsChecked = _settings.MicrophoneEnabled;

                // 分辨率
                if (ResolutionBox != null)
                    SetResolution(_settings.Resolution);

                // 帧率
                if (FrameRateBox != null)
                    SetComboBoxValue(FrameRateBox, _settings.FrameRate.ToString());

                // 码率（界面单位 Mbps；非预设值填进右侧自定义框）
                if (BitrateBox != null)
                    SetBitrate(BitrateBox, _settings.Bitrate);

                // 编码器
                if (EncoderBox != null)
                    SetComboBoxValue(EncoderBox, _settings.Encoder);

                // 封装格式
                if (ContainerBox != null)
                    SetComboBoxValue(ContainerBox, _settings.ContainerFormat);

                // 关键帧间隔
                if (KeyframeBox != null)
                    SetComboBoxValue(KeyframeBox, _settings.KeyframeInterval.ToString());

                // 音频参数
                if (AudioSampleRateBox != null)
                    SetComboBoxValue(AudioSampleRateBox, _settings.AudioSampleRate.ToString());
                if (AudioChannelsBox != null)
                    SetComboBoxValue(AudioChannelsBox, _settings.AudioChannels.ToString());
                if (AudioBitrateBox != null)
                    SetComboBoxValue(AudioBitrateBox, _settings.AudioBitrate.ToString());

                // 即时重放
                if (InstantReplayEnabled != null)
                    InstantReplayEnabled.IsChecked = _settings.InstantReplayEnabled;

                // 即时重放音频开关（新增）；未显式设置过（null）时显示为「跟随录制设置」
                if (ReplaySystemAudioBox != null)
                    ReplaySystemAudioBox.IsChecked = _settings.ReplaySystemAudioEnabled ?? _settings.SystemAudioEnabled;
                if (ReplayMicrophoneBox != null)
                    ReplayMicrophoneBox.IsChecked = _settings.ReplayMicrophoneEnabled ?? _settings.MicrophoneEnabled;

                if (MaxReplayDurationSlider != null)
                    MaxReplayDurationSlider.Value = _settings.MaxReplayDurationSeconds;
                UpdateMaxReplayDurationText();
                if (ReplayFrameRateBox != null)
                    SetComboBoxValue(ReplayFrameRateBox, _settings.ReplayFrameRate.ToString());

                // 即时重放分辨率
                if (ReplayResolutionBox != null)
                    SetReplayResolution(_settings.ReplayResolution);

                // 即时重放码率
                if (ReplayBitrateBox != null)
                    SetReplayBitrate(_settings.ReplayBitrate);

                // 热键
                if (RecordHotkeyEnabled != null)
                    RecordHotkeyEnabled.IsChecked = _settings.RecordHotkeyEnabled;
                if (RecordHotkeyBox != null)
                    RecordHotkeyBox.Text = _settings.RecordHotkey;
                if (ReplayHotkeyEnabled != null)
                    ReplayHotkeyEnabled.IsChecked = _settings.ReplayHotkeyEnabled;
                if (ReplayHotkeyBox != null)
                    ReplayHotkeyBox.Text = _settings.ReplayHotkey;

                // 内存使用
                UpdateMemoryUsageText();
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：加载设置失败", ex);
                MessageBox.Show("加载设置失败：" + ex.Message, "Dronesee",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
            finally
            {
                _suppressToggleEvents = false;
            }
        }

        /// <summary>录制滑块切换：实时记住状态（下次启动恢复）。</summary>
        private void RecordingEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressToggleEvents || _settings == null) return;
            _settings.RecordingEnabled = RecordingEnabled.IsChecked == true;
            SaveToggleStates();
        }

        /// <summary>即时重放滑块切换：实时记住状态 + 立即生效（开→启动缓冲，关→停止）。</summary>
        private void InstantReplayEnabled_Toggled(object sender, RoutedEventArgs e)
        {
            if (_suppressToggleEvents || _settings == null) return;
            _settings.InstantReplayEnabled = InstantReplayEnabled.IsChecked == true;
            SaveToggleStates();
            DroneseeModule.ApplyReplayBufferState();
        }

        /// <summary>把两个开关状态立即写回 settings.json，实现「记住上次状态」。</summary>
        private void SaveToggleStates()
        {
            try
            {
                var root = SettingsStore.Load();
                root.Dronesee = _settings;
                SettingsStore.Save(root);
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：保存开关状态失败", ex);
            }
        }

        /// <summary>保存设置到 SettingsStore。</summary>
        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                _settings = SettingsStore.Load().Dronesee ?? new DroneseeSettings();

                // 保存路径
                if (SavePathBox != null)
                    _settings.SavePath = SavePathBox.Text.Trim();

                // 录制总开关
                _settings.RecordingEnabled = RecordingEnabled.IsChecked == true;

                // 系统声音
                if (SystemAudioBox != null) _settings.SystemAudioEnabled = SystemAudioBox.IsChecked == true;

                // 麦克风
                if (MicrophoneEnabled != null) _settings.MicrophoneEnabled = MicrophoneEnabled.IsChecked == true;

                // 分辨率
                var resItem = ResolutionBox.SelectedItem as ComboBoxItem;
                if (resItem != null && resItem.Tag?.ToString() == "custom")
                {
                    _settings.Resolution = CustomResolutionBox.Text.Trim() ?? "1920x1080";
                }
                else if (resItem != null)
                {
                    _settings.Resolution = resItem.Tag?.ToString() ?? "1920x1080";
                }

                // 帧率
                var frItem = FrameRateBox.SelectedItem as ComboBoxItem;
                if (frItem != null && int.TryParse(frItem.Tag?.ToString(), out int fr))
                    _settings.FrameRate = fr;

                // 码率
                var brItem = BitrateBox.SelectedItem as ComboBoxItem;
                if (brItem != null && brItem.Tag?.ToString() == "custom")
                {
                    if (TryParseMbps(CustomBitrateBox.Text, out int cb))
                        _settings.Bitrate = cb;                // 界面 Mbps → 存盘 kbps
                }
                else if (brItem != null && int.TryParse(brItem.Tag?.ToString(), out int br))
                {
                    _settings.Bitrate = br;
                }

                // 编码器
                var encItem = EncoderBox.SelectedItem as ComboBoxItem;
                if (encItem != null)
                    _settings.Encoder = encItem.Tag?.ToString() ?? "auto";

                // 封装格式
                var contItem = ContainerBox.SelectedItem as ComboBoxItem;
                if (contItem != null)
                    _settings.ContainerFormat = contItem.Tag?.ToString() ?? "mp4";

                // 关键帧间隔
                var kfItem = KeyframeBox.SelectedItem as ComboBoxItem;
                if (kfItem != null && int.TryParse(kfItem.Tag?.ToString(), out int kf))
                    _settings.KeyframeInterval = kf;

                // 音频参数
                var asrItem = AudioSampleRateBox.SelectedItem as ComboBoxItem;
                if (asrItem != null && int.TryParse(asrItem.Tag?.ToString(), out int asr))
                    _settings.AudioSampleRate = asr;

                var achItem = AudioChannelsBox.SelectedItem as ComboBoxItem;
                if (achItem != null && int.TryParse(achItem.Tag?.ToString(), out int ach))
                    _settings.AudioChannels = ach;

                var abrItem = AudioBitrateBox.SelectedItem as ComboBoxItem;
                if (abrItem != null && int.TryParse(abrItem.Tag?.ToString(), out int abr))
                    _settings.AudioBitrate = abr;

                // 即时重放
                _settings.InstantReplayEnabled = InstantReplayEnabled.IsChecked == true;

                // 即时重放音频开关（新增）
                if (ReplaySystemAudioBox != null)
                    _settings.ReplaySystemAudioEnabled = ReplaySystemAudioBox.IsChecked == true;
                if (ReplayMicrophoneBox != null)
                    _settings.ReplayMicrophoneEnabled = ReplayMicrophoneBox.IsChecked == true;

                if (MaxReplayDurationSlider != null) _settings.MaxReplayDurationSeconds = (int)MaxReplayDurationSlider.Value;
                var replayFrameItem = ReplayFrameRateBox.SelectedItem as ComboBoxItem;
                if (replayFrameItem != null && int.TryParse(replayFrameItem.Tag?.ToString(), out int rfr))
                    _settings.ReplayFrameRate = rfr;

                // 已取消内存上限（2026-11-12 用户要求）：不再推算、不再写回任何上限值。
                _settings.MemoryCacheMB = 0;

                // 即时重放分辨率
                var replayResItem = ReplayResolutionBox.SelectedItem as ComboBoxItem;
                if (replayResItem != null && replayResItem.Tag?.ToString() == "custom")
                {
                    _settings.ReplayResolution = CustomReplayResolutionBox.Text.Trim() ?? "1920x1080";
                }
                else if (replayResItem != null)
                {
                    _settings.ReplayResolution = replayResItem.Tag?.ToString() ?? "1920x1080";
                }

                // 即时重放码率
                var replayBrItem = ReplayBitrateBox.SelectedItem as ComboBoxItem;
                if (replayBrItem != null && replayBrItem.Tag?.ToString() == "custom")
                {
                    if (TryParseMbps(CustomReplayBitrateBox.Text, out int rcb))
                        _settings.ReplayBitrate = rcb;         // 界面 Mbps → 存盘 kbps
                }
                else if (replayBrItem != null && int.TryParse(replayBrItem.Tag?.ToString(), out int rbr))
                {
                    _settings.ReplayBitrate = rbr;
                }

                // 热键
                _settings.RecordHotkeyEnabled = RecordHotkeyEnabled.IsChecked == true;
                if (RecordHotkeyBox != null) _settings.RecordHotkey = RecordHotkeyBox.Text;

                _settings.ReplayHotkeyEnabled = ReplayHotkeyEnabled.IsChecked == true;
                if (ReplayHotkeyBox != null) _settings.ReplayHotkey = ReplayHotkeyBox.Text;

                // 归一化
                _settings.Normalize();

                // 保存（需要包装成 ModuleSettings）
                var moduleSettings = SettingsStore.Load();
                moduleSettings.Dronesee = _settings;
                SettingsStore.Save(moduleSettings);

                // 通知模块更新设置
                DroneseeModule.NotifySettingsChanged();

                // 让「即时重放」滑块立即生效（开启→开始后台缓冲；关闭→停止缓冲）
                DroneseeModule.ApplyReplayBufferState();

                // 第 7 条：改为保存按钮上方的主题气泡，不再弹居中对话框
                GameHub.Controls.PopBubble.ShowApplied(SaveButton, "已保存");
            }
            catch (Exception ex)
            {
                MessageBox.Show("保存设置失败：" + ex.Message, "Dronesee",
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }

        /// <summary>关闭窗口。</summary>
        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        /// <summary>打开保存路径文件夹。</summary>
        private void OpenFolder_Click(object sender, RoutedEventArgs e)
        {
            string path = SavePathBox.Text.Trim();
            if (string.IsNullOrEmpty(path))
            {
                // 使用默认路径
                path = _settings.SavePath;
            }

            try
            {
                // 确保路径存在
                if (!Directory.Exists(path))
                {
                    Directory.CreateDirectory(path);
                }

                // 打开资源管理器并定位到该路径
                Process.Start(new ProcessStartInfo
                {
                    FileName = path,
                    UseShellExecute = true
                });
            }
            catch (Exception ex)
            {
                MessageBox.Show("打开文件夹失败：" + ex.Message, "Dronesee",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }

        private System.Windows.Controls.TextBox _hotkeyBox;

        /// <summary>点热键框即开始绑定（与其它模块卡片保持一致的交互）。</summary>
        private void HotkeyBox_PreviewMouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (sender is not System.Windows.Controls.TextBox box)
                return;

            StartHotkeyBinding(ReferenceEquals(box, ReplayHotkeyBox) ? "replay" : "record", box);
        }

        /// <summary>开始热键绑定（等待用户按下组合键）。</summary>
        private void StartHotkeyBinding(string target, object senderObj)
        {
            _isBindingHotkey = true;
            _hotkeyTarget = target;

            // 记录正在录制的热键框（点框开始绑定，不再用独立按钮）
            _hotkeyBox = senderObj as System.Windows.Controls.TextBox;
            if (_hotkeyBox != null)
                _hotkeyBox.Text = "按下组合键...";

            // 添加键盘监听
            PreviewKeyDown += OnHotkeyPreviewKeyDown;
        }

        private void OnHotkeyPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isBindingHotkey) return;

            // 构建热键字符串
            string hotkey = BuildHotkeyString(e);
            if (string.IsNullOrEmpty(hotkey)) return;

            // 更新文本（统一用只读热键框显示）
            if (_hotkeyTarget == "record")
            {
                if (RecordHotkeyBox != null) RecordHotkeyBox.Text = hotkey;
                _settings.RecordHotkey = hotkey;
            }
            else
            {
                if (ReplayHotkeyBox != null) ReplayHotkeyBox.Text = hotkey;
                _settings.ReplayHotkey = hotkey;
            }

            // 录制完成，释放热键框引用
            _hotkeyBox = null;

            // 移除键盘监听
            PreviewKeyDown -= OnHotkeyPreviewKeyDown;
            _isBindingHotkey = false;
        }

        /// <summary>从 KeyEventArgs 构建热键字符串。</summary>
        private static string BuildHotkeyString(KeyEventArgs e)
        {
            var parts = new System.Collections.Generic.List<string>();

            if (e.Key == Key.Delete || e.Key == Key.Pause) return e.Key.ToString();
            if (e.Key == Key.Escape) return string.Empty;

            // Keyboard.Modifiers 返回 ModifierKeys 枚举，转换为 int 后使用数值检查
            // Control=0x0002, Alt=0x0001, Shift=0x0004, Windows=0x0008
            int mod = (int)Keyboard.Modifiers;
            if ((mod & 0x0002) != 0) parts.Add("Ctrl");
            if ((mod & 0x0001) != 0) parts.Add("Alt");
            if ((mod & 0x0004) != 0) parts.Add("Shift");
            if ((mod & 0x0008) != 0) parts.Add("Win");

            // 处理功能键
            string keyName = e.Key.ToString();
            if (Enum.TryParse<Key>(keyName, out var key) && key >= Key.F1 && key <= Key.F12)
            {
                parts.Add(keyName);
            }
            else if (key >= Key.A && key <= Key.Z)
            {
                parts.Add(keyName.ToUpperInvariant());
            }
            else if (key >= Key.D0 && key <= Key.D9)
            {
                parts.Add(keyName);
            }
            else
            {
                return string.Empty;
            }

            return string.Join("+", parts);
        }

        /// <summary>设置 ComboBox 选中项（按值）。</summary>
        private static void SetComboBoxValue(ComboBox comboBox, string value)
        {
            foreach (ComboBoxItem item in comboBox.Items)
            {
                if (item.Tag?.ToString() == value)
                {
                    comboBox.SelectedItem = item;
                    return;
                }
            }
            // 如果没找到，尝试直接设置文本
            comboBox.Text = value;
        }

        /// <summary>设置分辨率。</summary>
        private void SetResolution(string resolution)
        {
            var resItem = ResolutionBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == resolution);

            if (resItem != null)
            {
                ResolutionBox.SelectedItem = resItem;
                CustomResolutionBox.Visibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                // 自定义分辨率
                CustomResolutionBox.Text = resolution;
                CustomResolutionBox.Visibility = System.Windows.Visibility.Visible;
            }
        }

        /// <summary>设置即时重放分辨率。</summary>
        private void SetReplayResolution(string resolution)
        {
            var resItem = ReplayResolutionBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == resolution);

            if (resItem != null)
            {
                ReplayResolutionBox.SelectedItem = resItem;
                CustomReplayResolutionBox.Visibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                // 自定义分辨率
                CustomReplayResolutionBox.Text = resolution;
                CustomReplayResolutionBox.Visibility = System.Windows.Visibility.Visible;
            }
        }

        /// <summary>设置录制码率：命中预设就选中，否则选中「自定义…」并把值填进输入框（界面单位 Mbps）。</summary>
        private void SetBitrate(ComboBox comboBox, int bitrateKbps)
        {
            var item = comboBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == bitrateKbps.ToString());

            if (item != null)
            {
                comboBox.SelectedItem = item;
                if (CustomBitrateBox != null)
                    CustomBitrateBox.Visibility = System.Windows.Visibility.Collapsed;
                return;
            }

            var custom = comboBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == "custom");
            if (custom != null) comboBox.SelectedItem = custom;

            if (CustomBitrateBox != null)
            {
                CustomBitrateBox.Text = FormatMbps(bitrateKbps);
                CustomBitrateBox.Visibility = System.Windows.Visibility.Visible;
            }
        }

        /// <summary>设置即时重放码率。</summary>
        private void SetReplayBitrate(int bitrate)
        {
            var brItem = ReplayBitrateBox.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(i => i.Tag?.ToString() == bitrate.ToString());

            if (brItem != null)
            {
                ReplayBitrateBox.SelectedItem = brItem;
                CustomReplayBitrateBox.Visibility = System.Windows.Visibility.Collapsed;
            }
            else
            {
                // 自定义码率（界面单位 Mbps）
                CustomReplayBitrateBox.Text = FormatMbps(bitrate);
                CustomReplayBitrateBox.Visibility = System.Windows.Visibility.Visible;
            }
        }

        /// <summary>更新最大重放时长文本。</summary>
        private void UpdateMaxReplayDurationText()
        {
            // XAML 加载时 Slider 的 Value=30 会先于 MaxReplayDurationText 初始化触发 ValueChanged，
            // 此时控件还是 null，直接判空跳过，避免空引用刷屏。
            if (MaxReplayDurationSlider == null || MaxReplayDurationText == null) return;

            int seconds = (int)Math.Round(MaxReplayDurationSlider.Value);
            // 单位统一为秒（2026-11-12 用户要求）：不再出现「分钟」
            MaxReplayDurationText.Text = $"{seconds} 秒";

            // 同步右侧秒数输入框（正在输入时不打断用户）
            if (MaxReplayDurationBox != null && !MaxReplayDurationBox.IsKeyboardFocused)
                MaxReplayDurationBox.Text = seconds.ToString();

            // 实时更新内存显示：需要多少缓存 + 推荐时长
            UpdateMemoryUsageText();
        }

        /// <summary>
        /// 刷新「内存占用 / 码率 / 可缓存时长」三者的关系（OBS 式编码流缓冲）。
        ///
        /// 缓冲存的是硬件编码后的 H.264 流（不是抓帧），所以：
        ///   可缓存时长 ≈ 内存缓存字节上限 ÷ 实测码率（缓冲运行中）
        ///            或 ≈ 内存缓存字节上限 ÷ 重放码率设置（未运行时估算）
        /// 同时还要受「最大时长」设置封顶。
        /// 只受内存条限制；与硬盘容量、硬盘自带缓存无关。
        /// </summary>
        // ---------- 「最大时长」秒数输入框（2026-11-12 用户要求） ----------

        /// <summary>输入框回车确认：输入 400 → 左侧滑块同步滑到 400 秒。</summary>
        private void MaxReplayDurationBox_KeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key != Key.Enter) return;
            ApplyMaxReplayDurationFromBox();
            e.Handled = true;
        }

        /// <summary>输入框失焦时也应用一次，避免用户输完直接去点别处。</summary>
        private void MaxReplayDurationBox_LostFocus(object sender, RoutedEventArgs e)
            => ApplyMaxReplayDurationFromBox();

        /// <summary>
        /// 把输入框里的秒数应用到滑块。超出滑块范围（5–1800 秒）自动夹住；
        /// 输入非法（空/非数字）时还原成当前值 —— 只做纠正，绝不弹窗报错。
        /// </summary>
        private void ApplyMaxReplayDurationFromBox()
        {
            try
            {
                if (MaxReplayDurationSlider == null || MaxReplayDurationBox == null) return;

                int min = (int)MaxReplayDurationSlider.Minimum;
                int max = (int)MaxReplayDurationSlider.Maximum;

                if (!int.TryParse(MaxReplayDurationBox.Text.Trim(), out int seconds))
                {
                    MaxReplayDurationBox.Text = ((int)Math.Round(MaxReplayDurationSlider.Value)).ToString();
                    return;
                }

                seconds = Math.Clamp(seconds, min, max);
                MaxReplayDurationSlider.Value = seconds;          // 滑块同步滑到该位置
                MaxReplayDurationBox.Text = seconds.ToString();
                UpdateMaxReplayDurationText();
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：应用最大时长输入失败", ex);
            }
        }

        // ---------- 单位换算（界面统一用 Mbps / 秒，存盘仍用 kbps / 秒） ----------

        /// <summary>把界面上的 Mbps 输入解析成存盘用的 kbps（支持 62.5 这样的小数）。</summary>
        private static bool TryParseMbps(string? text, out int kbps)
        {
            kbps = 0;
            if (string.IsNullOrWhiteSpace(text)) return false;
            if (!double.TryParse(text.Trim(), out double mbps)) return false;

            kbps = (int)Math.Clamp(Math.Round(mbps * 1000.0), 100, 100000);   // 与 NormalizeBitrate 的范围一致
            return true;
        }

        /// <summary>kbps → 界面显示的 Mbps 文本（整数不带小数，如 60；62.5 保留一位）。</summary>
        private static string FormatMbps(int kbps)
        {
            double mbps = kbps / 1000.0;
            return Math.Abs(mbps - Math.Round(mbps)) < 0.05
                ? Math.Round(mbps).ToString("0")
                : mbps.ToString("0.#");
        }

        /// <summary>
        /// 自动检测本机可用内存，给出「不影响游戏帧率」的建议缓冲预算
        /// （只用于推荐时长的计算与备注提示，不再当上限使用）：
        /// 物理内存的 25%，最低 512MB，不设硬上限（内存越大给得越多，适配任意机器）。
        /// 8 GB → 2048MB；16 GB → 4096MB；32 GB → 8192MB。
        /// </summary>
        private static int SafeCacheMb()
        {
            try
            {
                double gb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0;
                if (gb < 1) return 2048;

                return (int)Math.Max(512, Math.Round(gb * 0.25) * 1024);
            }
            catch
            {
                return 2048;
            }
        }

        private void UpdateMemoryUsageText()
        {
            try
            {
                double durationCap = Math.Max(1, _settings?.MaxReplayDurationSeconds ?? 30);
                double configuredKbps = Math.Max(100, _settings?.ReplayBitrate ?? 2500);
                double audioKbps = Math.Max(32, _settings?.AudioBitrate ?? 128);

                var engine = DroneseeModule.GetOrCreateEngine();
                double usedMB = engine?.BufferUsageMB ?? 0;
                double bufferedSec = engine?.BufferedSeconds ?? 0;
                double measuredMbps = engine?.RingMbps ?? 0;
                bool running = engine?.IsReplayBufferRunning ?? false;

                // 2026-11-12 用户要求：即时重放开着时锁住这一组参数 ——
                // 引擎只在 StartReplayBuffer 那一刻读一次设置，运行中改了不生效；
                // 总开关留在块外，随时能关。
                if (ReplayParamsPanel != null)
                    ReplayParamsPanel.IsEnabled = !running;

                if (ReplayLockNote != null)
                    ReplayLockNote.Visibility = running ? Visibility.Visible : Visibility.Collapsed;

                if (MemoryUsageText != null)
                    MemoryUsageText.Text = $"{usedMB:F0} MB（无上限）";

                if (MemoryUsageDetailText != null)
                {
                    MemoryUsageDetailText.Text = running
                        ? $"当前占用：{usedMB:F0} MB（不设上限，已缓存 {bufferedSec:F0} 秒 · 实测 {measuredMbps:F1} Mbps）"
                        : "当前占用：0 MB（不设上限，缓冲未运行）";
                }

                if (BufferCapacityText == null) return;

                // 码率（Mbps）：优先用实测，未运行时用设置值估算（视频 + 音频）
                double mbps = measuredMbps > 0
                    ? measuredMbps
                    : (configuredKbps + audioKbps) / 1000.0;

                // 2026-11-12：只做备注说明，不再有任何“上限 / 受限”判定，也不报错
                double needMb = mbps * durationCap / 8.388608;
                int safeMb = SafeCacheMb();
                double ramGb = Math.Round(GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / 1024.0 / 1024.0 / 1024.0, 1);
                double safeSeconds = safeMb * 8.388608 / mbps;

                // ① 不设上限，但实时算出「完成当前时长需要占多少缓存」
                BufferCapacityText.Text =
                    $"需要缓存：{durationCap:F0} 秒 ≈ {needMb:F0} MB（按 {mbps:F1} Mbps 估算）" +
                    (running ? $"\n已缓存 {bufferedSec:F0} 秒 · 当前占用 {usedMB:F0} MB" : "");

                // ② 自动检测本机内存 → 给出「不影响游戏帧率」的推荐时长
                if (MemoryAdviceText != null)
                {
                    MemoryAdviceText.Text =
                        $"推荐时长：约 {safeSeconds:F0} 秒" +
                        $"（自动检测本机内存 {ramGb:F0} GB → 建议缓冲不超过 {safeMb} MB，此范围内不影响游戏帧率）" +
                        (needMb > safeMb
                            ? $"\n当前 {durationCap:F0} 秒约需 {needMb:F0} MB，超出建议值，可能影响游戏帧率。"
                            : $"\n当前 {durationCap:F0} 秒在建议范围内。");
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：更新内存显示失败", ex);
            }
        }

        // ---------- 事件处理 ----------

        private void ResolutionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = ResolutionBox.SelectedItem as ComboBoxItem;
            if (item != null && item.Tag?.ToString() == "custom")
            {
                CustomResolutionBox.Visibility = System.Windows.Visibility.Visible;
                CustomResolutionBox.Focus();
            }
            else
            {
                CustomResolutionBox.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void BitrateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = BitrateBox.SelectedItem as ComboBoxItem;
            if (item != null && item.Tag?.ToString() == "custom")
            {
                CustomBitrateBox.Visibility = System.Windows.Visibility.Visible;
                CustomBitrateBox.Focus();
            }
            else
            {
                CustomBitrateBox.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void MaxReplayDurationSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
        {
            try
            {
                UpdateMaxReplayDurationText();

                // 时长变了 → 只刷新备注里的估算显示；不再推算/写回任何内存上限
                if (_settings != null)
                    _settings.MaxReplayDurationSeconds = (int)MaxReplayDurationSlider.Value;

                UpdateMemoryUsageText();
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：Slider 值变化处理失败", ex);
            }
        }

        private void ReplayResolutionBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = ReplayResolutionBox.SelectedItem as ComboBoxItem;
            if (item != null && item.Tag?.ToString() == "custom")
            {
                CustomReplayResolutionBox.Visibility = System.Windows.Visibility.Visible;
                CustomReplayResolutionBox.Focus();
            }
            else
            {
                CustomReplayResolutionBox.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void ReplayBitrateBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var item = ReplayBitrateBox.SelectedItem as ComboBoxItem;
            if (item != null && item.Tag?.ToString() == "custom")
            {
                CustomReplayBitrateBox.Visibility = System.Windows.Visibility.Visible;
                CustomReplayBitrateBox.Focus();
            }
            else
            {
                CustomReplayBitrateBox.Visibility = System.Windows.Visibility.Collapsed;
            }
        }

        private void Window_Closing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            // 退出时取消热键绑定监听、停掉实时刷新
            PreviewKeyDown -= OnHotkeyPreviewKeyDown;

            if (_liveTimer != null)
            {
                _liveTimer.Stop();
                _liveTimer = null;
            }
        }
    }
}
