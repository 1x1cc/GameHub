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
        private DroneseeSettings _settings;
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
                var moduleSettings = SettingsStore.Load();
                // 确保 _settings 不为 null
                if (moduleSettings?.Dronesee == null)
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

                // 码率
                if (BitrateBox != null)
                    SetComboBoxValue(BitrateBox, _settings.Bitrate.ToString());

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
                if (MaxReplayDurationSlider != null)
                    MaxReplayDurationSlider.Value = _settings.MaxReplayDurationSeconds;
                UpdateMaxReplayDurationText();
                if (MemoryCacheBox != null)
                    SetComboBoxValue(MemoryCacheBox, _settings.MemoryCacheMB.ToString());
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
                if (RecordHotkeyText != null)
                    RecordHotkeyText.Text = _settings.RecordHotkey;
                if (ReplayHotkeyEnabled != null)
                    ReplayHotkeyEnabled.IsChecked = _settings.ReplayHotkeyEnabled;
                if (ReplayHotkeyText != null)
                    ReplayHotkeyText.Text = _settings.ReplayHotkey;

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
                    if (int.TryParse(CustomBitrateBox.Text.Trim(), out int cb))
                        _settings.Bitrate = cb;
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
                if (MaxReplayDurationSlider != null) _settings.MaxReplayDurationSeconds = (int)MaxReplayDurationSlider.Value;
                var replayFrameItem = ReplayFrameRateBox.SelectedItem as ComboBoxItem;
                if (replayFrameItem != null && int.TryParse(replayFrameItem.Tag?.ToString(), out int rfr))
                    _settings.ReplayFrameRate = rfr;

                var memItem = MemoryCacheBox.SelectedItem as ComboBoxItem;
                if (memItem != null && int.TryParse(memItem.Tag?.ToString(), out int mem))
                    _settings.MemoryCacheMB = mem;

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
                    if (int.TryParse(CustomReplayBitrateBox.Text.Trim(), out int rcb))
                        _settings.ReplayBitrate = rcb;
                }
                else if (replayBrItem != null && int.TryParse(replayBrItem.Tag?.ToString(), out int rbr))
                {
                    _settings.ReplayBitrate = rbr;
                }

                // 热键
                _settings.RecordHotkeyEnabled = RecordHotkeyEnabled.IsChecked == true;
                if (RecordHotkeyText != null) _settings.RecordHotkey = RecordHotkeyText.Text;

                _settings.ReplayHotkeyEnabled = ReplayHotkeyEnabled.IsChecked == true;
                if (ReplayHotkeyText != null) _settings.ReplayHotkey = ReplayHotkeyText.Text;

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

                MessageBox.Show("设置已保存", "Dronesee", MessageBoxButton.OK, MessageBoxImage.Information);
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

        /// <summary>绑定录制热键。</summary>
        private void BindRecordHotkey_Click(object sender, RoutedEventArgs e)
        {
            StartHotkeyBinding("record", sender);
        }

        /// <summary>绑定即时重放热键。</summary>
        private void BindReplayHotkey_Click(object sender, RoutedEventArgs e)
        {
            StartHotkeyBinding("replay", sender);
        }

        /// <summary>开始热键绑定（等待用户按下组合键）。</summary>
        private void StartHotkeyBinding(string target, object senderObj)
        {
            _isBindingHotkey = true;
            _hotkeyTarget = target;

            // 保存按钮引用
            _hotkeyButton = senderObj as Button;
            if (_hotkeyButton != null)
            {
                _hotkeyButton.Content = "按下组合键...";
                _hotkeyButton.IsEnabled = false;
            }

            // 添加键盘监听
            PreviewKeyDown += OnHotkeyPreviewKeyDown;
        }

        private void OnHotkeyPreviewKeyDown(object sender, KeyEventArgs e)
        {
            if (!_isBindingHotkey) return;

            // 构建热键字符串
            string hotkey = BuildHotkeyString(e);
            if (string.IsNullOrEmpty(hotkey)) return;

            // 更新文本
            if (_hotkeyTarget == "record")
            {
                RecordHotkeyText.Text = hotkey;
                _settings.RecordHotkey = hotkey;
            }
            else
            {
                ReplayHotkeyText.Text = hotkey;
                _settings.ReplayHotkey = hotkey;
            }

            // 恢复按钮状态
            if (_hotkeyButton != null)
            {
                _hotkeyButton.Content = "绑定热键";
                _hotkeyButton.IsEnabled = true;
                _hotkeyButton = null;
            }

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
                // 自定义码率
                CustomReplayBitrateBox.Text = bitrate.ToString();
                CustomReplayBitrateBox.Visibility = System.Windows.Visibility.Visible;
            }
        }

        /// <summary>更新最大重放时长文本。</summary>
        private void UpdateMaxReplayDurationText()
        {
            // XAML 加载时 Slider 的 Value=30 会先于 MaxReplayDurationText 初始化触发 ValueChanged，
            // 此时控件还是 null，直接判空跳过，避免空引用刷屏。
            if (MaxReplayDurationSlider == null || MaxReplayDurationText == null) return;

            int seconds = (int)MaxReplayDurationSlider.Value;
            if (seconds >= 60)
            {
                int mins = seconds / 60;
                MaxReplayDurationText.Text = $"{mins} 分钟";
            }
            else
            {
                MaxReplayDurationText.Text = $"{seconds} 秒";
            }

            // 实时更新内存显示
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
        private void UpdateMemoryUsageText()
        {
            try
            {
                double cacheMB = Math.Max(1, _settings?.MemoryCacheMB ?? 512);
                double durationCap = Math.Max(1, _settings?.MaxReplayDurationSeconds ?? 30);
                double configuredKbps = Math.Max(100, _settings?.ReplayBitrate ?? 2500);
                double audioKbps = Math.Max(32, _settings?.AudioBitrate ?? 128);

                var engine = DroneseeModule.GetOrCreateEngine();
                double usedMB = engine?.BufferUsageMB ?? 0;
                double bufferedSec = engine?.BufferedSeconds ?? 0;
                double measuredMbps = engine?.RingMbps ?? 0;
                bool running = engine?.IsReplayBufferRunning ?? false;

                if (MemoryUsageText != null)
                    MemoryUsageText.Text = $"{usedMB:F0} MB / {cacheMB:F0} MB";

                if (MemoryUsageDetailText != null)
                {
                    MemoryUsageDetailText.Text = running
                        ? $"当前占用：{usedMB:F0} MB / 上限 {cacheMB:F0} MB（已缓存 {bufferedSec:F0} 秒 · 实测 {measuredMbps:F1} Mbps）"
                        : $"当前占用：0 MB / 上限 {cacheMB:F0} MB（缓冲未运行，按重放码率估算）";
                }

                if (BufferCapacityText == null) return;

                // 码率（Mbps）：优先用实测，未运行时用设置值估算（视频 + 音频）
                double mbps = measuredMbps > 0
                    ? measuredMbps
                    : (configuredKbps + audioKbps) / 1000.0;

                // 字节预算能存多久：MB * 1024*1024 * 8 / (Mbps * 1e6) = MB * 8.388608 / Mbps
                double budgetSeconds = cacheMB * 8.388608 / mbps;
                double effective = Math.Min(budgetSeconds, durationCap);

                string line = $"可缓存时长：约 {effective:F0} 秒（{mbps:F1} Mbps，存满 {durationCap:F0} 秒上限需 {mbps * durationCap / 8.388608:F0} MB）";

                if (budgetSeconds < durationCap)
                {
                    double needMB = mbps * durationCap / 8.388608;
                    line += $"\n当前受「内存缓存 {cacheMB:F0} MB」限制；想存满 {durationCap:F0} 秒，" +
                            $"需把《内存缓存》调到约 {needMB:F0} MB，或降低《重放码率》。";
                }
                else
                {
                    line += $"\n当前受「最大时长 {durationCap:F0} 秒」限制；再加大内存缓存也不会更长。";
                }

                BufferCapacityText.Text = line;
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
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：Slider 值变化处理失败", ex);
            }
        }

        private void MemoryCacheBox_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            UpdateMemoryUsageText();
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
