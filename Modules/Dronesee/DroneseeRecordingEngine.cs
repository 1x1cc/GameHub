using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows;
using GameHub.Modules.Dronesee.Audio;
using GameHub.Utils;

namespace GameHub.Modules.Dronesee
{
    /// <summary>
    /// Dronesee 录制引擎（OBS Studio 式架构）。
    ///
    /// 核心思路：
    ///   1) 视频用 FFmpeg gdigrab 捕获，硬件编码（AMD AMF / Intel QSV / Nvidia NVENC，
    ///      都不可用时回退 libx264）。
    ///   2) 音频用 WASAPI 环回（系统声音）+ 麦克风，混成 48kHz 立体声 PCM，
    ///      通过 FFmpeg 的 stdin（pipe:0）汇入同一个 FFmpeg 进程 —— 和 OBS 一样，
    ///      音视频在编码阶段就同步，不存在"先录后合"的漂移。
    ///   3) 录制：FFmpeg 直接写 MPEG-TS 到保存目录（TS 是流式容器，进程被杀也不损坏），
    ///      停止时用 -c copy 无损转封装成 mp4/mkv（几秒钟，不重新编码）。
    ///   4) 即时重放（参考 OBS Replay Buffer）：FFmpeg 把编码后的 TS 流写到 stdout（pipe:1），
    ///      引擎把流读进内存环形缓冲，按「最大时长」和「内存缓存」双上限滚动淘汰。
    ///      按热键保存时：把缓冲落成临时 .ts → -c copy 转封装成 mp4 → 存到保存目录并自动播放。
    ///      没有任何 JPEG 抓帧、没有任何二次编码。
    ///
    /// 内存占用：编码后的 H.264 流 ≈ 码率 ÷ 8。
    ///   30 分钟 @ 2500kbps ≈ 577MB；@ 8000kbps ≈ 1.8GB。
    ///   只受「内存缓存」上限和内存条实际容量限制；与硬盘容量、硬盘自带缓存无关。
    /// </summary>
    public class DroneseeRecordingEngine : IDisposable
    {
        // ---------- 事件 ----------

        public event Action<string>? OnStatusChanged;
        public event Action<string>? OnMemoryWarning;
        public event Action<string>? OnRecordingComplete;

        /// <summary>录制真正开始（FFmpeg 已拉起）。</summary>
        public event Action? OnRecordingStarted;

        /// <summary>即时重放缓冲真正开始缓存。</summary>
        public event Action? OnReplayStarted;

        /// <summary>一次回放片段已保存（参数 = 最终文件路径）。</summary>
        public event Action<string>? OnReplayClipSaved;

        /// <summary>音频设备识别结果（设备识别不到会自动关闭该路并告知用户）。</summary>
        public event Action<string>? OnAudioInfo;

        // ---------- 字段 ----------

        // 正式录制
        private Process? _ffmpegProcess;
        private WasapiAudioCapture? _audioCapture;
        private bool _isRecording;
        private double _recordingElapsed;
        private string? _recordingTsPath;        // 录制中的 TS（崩溃安全）
        private string? _currentRecordingPath;   // 最终 mp4/mkv 目标

        /// <summary>
        /// 录像「搭车」句柄：非 null 时，即时重放读取线程会把同一份 TS 字节
        /// 额外写进这个文件 —— 录制与重放共用一路采集/编码/音频会话。
        /// </summary>
        private FileStream? _recordingTap;
        private string? _recordingTapPath;

        // 即时重放（OBS 式滚动缓冲）
        private Process? _replayFfmpeg;
        private WasapiAudioCapture? _replayAudio;
        private bool _isReplayActive;
        private double _replayElapsed;
        private Thread? _replayReader;           // 读 FFmpeg stdout 的线程
        private volatile bool _replayReaderRun;
        private readonly Queue<RingChunk> _ring = new();
        private long _ringBytes;
        private DateTime _ringLast;
        private bool _replayMemoryWarned;

        private double _memoryUsageMB;
        private System.Timers.Timer? _statusTimer;
        private System.Timers.Timer? _memoryTimer;

        private DroneseeSettings _settings;

        // 编码器探测缓存，避免每次都跑 ffmpeg -encoders
        private string? _cachedEncoder;
        private string? _cachedFfmpeg;

        // ---------- 属性 ----------

        public bool IsRecording => _isRecording;
        public bool IsReplayActive => _isReplayActive;
        public double RecordingElapsed => _recordingElapsed;
        public double ReplayElapsed => _replayElapsed;
        public double MemoryUsageMB => _memoryUsageMB;
        public string? CurrentRecordingPath => _currentRecordingPath;

        /// <summary>即时重放缓冲是否正在运行（设置里打开“即时重放”滑块后为 true）。</summary>
        public bool IsReplayBufferRunning => _isReplayActive;

        // ---------- 回放缓冲的实时指标（设置界面显示「内存 ↔ 时长」关系用） ----------

        /// <summary>环形缓冲当前占用内存（MB）。</summary>
        public double BufferUsageMB
        {
            get { lock (_ringLock) { return _ringBytes / (1024.0 * 1024.0); } }
        }

        /// <summary>环形缓冲内存上限（MB）=「内存缓存」设置。</summary>
        public double BufferLimitMB => Math.Max(1, _settings.MemoryCacheMB);

        /// <summary>缓冲里实际可回放的秒数（按到达时间算的滚动窗口跨度）。</summary>
        public double BufferedSeconds
        {
            get
            {
                lock (_ringLock)
                {
                    if (_ring.Count == 0) return 0;
                    return (_ringLast - _ring.Peek()!.Arrival).TotalSeconds;
                }
            }
        }

        /// <summary>实测缓冲码率（Mbps）。缓冲不足 1 秒时返回 0。</summary>
        public double RingMbps
        {
            get
            {
                lock (_ringLock)
                {
                    if (_ring.Count == 0) return 0;
                    double span = (_ringLast - _ring.Peek()!.Arrival).TotalSeconds;
                    if (span < 1.0) return 0;
                    return _ringBytes * 8.0 / span / 1_000_000.0;
                }
            }
        }

        public DroneseeRecordingEngine()
        {
            var moduleSettings = SettingsStore.Load();
            _settings = moduleSettings?.Dronesee ?? new DroneseeSettings();
            _settings.Normalize();
        }

        // ---------- 公开方法 ----------

        public void ToggleRecording()
        {
            if (_isRecording) StopRecordingAndSave();
            else StartRecording();
        }

        /// <summary>开始正式录制（TS 落盘，崩溃安全）。</summary>
        public void StartRecording()
        {
            if (_isRecording) return;

            try
            {
                _settings = SettingsStore.Load().Dronesee ?? new DroneseeSettings();
                _settings.Normalize();

                string ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    ShowStatus("录制未能启动：FFmpeg 未找到（需与 GameHub.exe 同目录或加入 PATH）");
                    NotifyFfmpegMissing();
                    return;
                }

                // 【单管线搭车】即时重放缓冲已在跑 → 不再新开一路采集/编码/音频，
                // 而是把它读到的 TS 字节同时落一份到录像文件。
                //
                // 为什么必须这样（本机日志实测的教训）：
                //   同时开两路 ddagrab+QSV 且各自打开音频设备时，
                //     1) 麦克风被重复占用 → 其他软件用不了麦，且两路都拿不到真实采样；
                //     2) 录像里的音轨是静音（因为是静音补齐的）；
                //     3) 反复创建/销毁采集设备 → 整机极卡。
                // 共用一路后：只有一个采集、一块编码、一个音频会话。
                if (_isReplayActive && _replayFfmpeg != null && !_replayFfmpeg.HasExited)
                {
                    string tapTs = GenerateOutputPath("Recording", ".ts");
                    try
                    {
                        _recordingTap = new FileStream(tapTs, FileMode.Create, FileAccess.Write, FileShare.Read);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"Dronesee：录像搭车文件创建失败：{tapTs}", ex);
                        ShowStatus("录制未能启动：无法创建录像文件（详见日志）");
                        return;
                    }

                    _recordingTapPath = tapTs;
                    _recordingTsPath = tapTs;
                    _currentRecordingPath = ChangeExtensionByContainer(tapTs);
                    _isRecording = true;
                    _recordingElapsed = 0;

                    _statusTimer = new System.Timers.Timer(1000);
                    _statusTimer.Elapsed += OnRecordingStatusTimerElapsed;
                    _statusTimer.AutoReset = true;
                    _statusTimer.Start();

                    AppLog.Write($"Dronesee：录制已启动（共用即时重放单管线）→ {tapTs}");
                    ShowStatus($"录制已启动（共用即时重放管线）：{_currentRecordingPath}");
                    OnRecordingStarted?.Invoke();
                    return;
                }

                // 1) 先打开音频设备（只打开不启动，stdin 得等 FFmpeg 拉起后才有）
                WasapiAudioCapture? capture = OpenAudioCapture(forReplay: false);
                bool hasAudio = capture?.HasAnySource == true;

                // 2) 启动 FFmpeg（TS 是流式容器：进程被杀文件也不损坏）
                string encoder = DetectEncoder(ffmpegPath, out string encoderNote);
                string tsPath = GenerateOutputPath("Recording", ".ts");
                string args = BuildFFmpegArgs(
                    ffmpegOut: tsPath,
                    toStdout: false,
                    ffmpegPath: ffmpegPath,
                    encoder: encoder,
                    videoFps: _settings.FrameRate,
                    videoBitrateKbps: _settings.Bitrate,
                    hasAudio: hasAudio);

                ShowStatus($"正在启动录制：{encoder} → {tsPath}");
                AppLog.Write($"Dronesee：启动录制 encoder={encoder} note={encoderNote} args={args}");

                var proc = StartFFmpeg(ffmpegPath, args, redirectStdout: false, hasAudio);
                if (proc == null)
                {
                    capture?.Dispose();
                    ShowStatus("录制未能启动：FFmpeg 进程启动失败（详见日志）");
                    return;
                }

                _ffmpegProcess = proc;
                _ffmpegProcess.Exited += OnRecordingFFmpegExited;
                _recordingTsPath = tsPath;
                _currentRecordingPath = ChangeExtensionByContainer(tsPath);
                _audioCapture = capture;

                // 3) 音频泵启动，PCM 写进 FFmpeg stdin
                if (hasAudio && _audioCapture != null)
                    _audioCapture.Start(proc.StandardInput.BaseStream);

                _isRecording = true;
                _recordingElapsed = 0;

                _statusTimer = new System.Timers.Timer(1000);
                _statusTimer.Elapsed += OnRecordingStatusTimerElapsed;
                _statusTimer.AutoReset = true;
                _statusTimer.Start();

                StartMemoryMonitoring();

                ShowStatus($"录制已启动：{_currentRecordingPath}");
                OnRecordingStarted?.Invoke();
            }
            catch (Exception ex)
            {
                StopAudioCapture(forReplay: false);
                ShowStatus($"录制启动异常：{ex.Message}");
                AppLog.Write("Dronesee：录制启动异常", ex);
            }
        }

        /// <summary>停止录制：杀 FFmpeg（TS 安全）→ 无损转封装成 mp4/mkv。</summary>
        public void StopRecordingAndSave()
        {
            if (!_isRecording) return;

            string? finalPath = _currentRecordingPath;
            double elapsed = _recordingElapsed;

            try
            {
                _isRecording = false;

                _statusTimer?.Stop();
                _statusTimer?.Dispose();
                _statusTimer = null;

                // ---------- 搭车模式：只关文件 + 转封装，绝不碰即时重放管线 ----------
                if (_recordingTap != null)
                {
                    try { _recordingTap.Flush(); _recordingTap.Dispose(); } catch { }
                    _recordingTap = null;

                    string? tapDone = FinalizeRecordingFile(elapsed);
                    if (!string.IsNullOrEmpty(tapDone) && File.Exists(tapDone))
                    {
                        ShowStatus($"录制已完成：{FormatTime(elapsed)} → {tapDone}");
                        OnRecordingComplete?.Invoke(tapDone);
                    }
                    else
                    {
                        ShowStatus("录制已停止（未生成有效文件，请检查日志）");
                    }
                    return;
                }

                // 停音频泵 → 关 PCM 管道让 FFmpeg 收到音频 EOF，自己把 TS 收尾后退出；
                // 直接强杀会导致最后一帧不完整（转封装后 ffprobe 会报 decoding MB 错误）。
                StopAudioCapture(forReplay: false);
                TryCloseFfmpegStdin(_ffmpegProcess);

                // 只等一小会儿：关掉音频 stdin 只会让【音频输入】EOF，
                // 视频输入（gdigrab -i desktop）还在跑，ffmpeg 不会自己退出，
                // 等满 2.5s 纯属浪费（每次停止都卡 2.5 秒）。
                // 配合 -flush_packets 1，强杀前数据已基本落盘。
                var recProc = _ffmpegProcess;
                if (recProc != null && !recProc.HasExited)
                {
                    try { recProc.WaitForExit(400); } catch { }
                }

                KillFfmpeg(ref _ffmpegProcess);

                _memoryTimer?.Stop();
                _memoryTimer?.Dispose();
                _memoryTimer = null;
                _memoryUsageMB = 0;

                string? produced = FinalizeRecordingFile(elapsed);

                if (!string.IsNullOrEmpty(produced) && File.Exists(produced))
                {
                    ShowStatus($"录制已完成：{FormatTime(elapsed)} → {produced}");
                    OnRecordingComplete?.Invoke(produced);
                }
                else
                {
                    ShowStatus("录制已停止（未生成有效文件，请检查日志）");
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：停止录制异常", ex);
                OnRecordingCompleteSafeFallback();
            }
        }

        /// <summary>启动即时重放滚动缓冲（OBS Replay Buffer）。</summary>
        public void StartReplayBuffer()
        {
            if (_isReplayActive) return;

            try
            {
                _settings = SettingsStore.Load().Dronesee ?? new DroneseeSettings();
                _settings.Normalize();

                string ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    ShowStatus("即时重放未能开启：FFmpeg 未找到");
                    NotifyFfmpegMissing();
                    return;
                }

                lock (_ringLock)
                {
                    _ring.Clear();
                    _ringBytes = 0;
                }
                _replayMemoryWarned = false;

                WasapiAudioCapture? capture = OpenAudioCapture(forReplay: true);
                bool hasAudio = capture?.HasAnySource == true;

                string encoder = DetectEncoder(ffmpegPath, out string encoderNote);
                string args = BuildFFmpegArgs(
                    ffmpegOut: "pipe:1",
                    toStdout: true,
                    ffmpegPath: ffmpegPath,
                    encoder: encoder,
                    videoFps: _settings.ReplayFrameRate,
                    videoBitrateKbps: _settings.ReplayBitrate,
                    hasAudio: hasAudio);

                AppLog.Write($"Dronesee：启动重放缓冲 encoder={encoder} note={encoderNote} args={args}");

                var proc = StartFFmpeg(ffmpegPath, args, redirectStdout: true, hasAudio);
                if (proc == null)
                {
                    capture?.Dispose();
                    ShowStatus("即时重放未能开启：FFmpeg 进程启动失败（详见日志）");
                    return;
                }

                _replayFfmpeg = proc;
                _replayFfmpeg.Exited += OnReplayFFmpegExited;
                _replayAudio = capture;

                if (hasAudio && _replayAudio != null)
                    _replayAudio.Start(proc.StandardInput.BaseStream);

                // 读 stdout → 内存环形缓冲
                _replayReaderRun = true;
                _replayReader = new Thread(ReplayReaderLoop)
                {
                    IsBackground = true,
                    Name = "GameHub.Dronesee.ReplayRing",
                    Priority = ThreadPriority.AboveNormal
                };
                _replayReader.Start();

                _isReplayActive = true;
                _replayElapsed = 0;

                _statusTimer = new System.Timers.Timer(1000);
                _statusTimer.Elapsed += OnReplayStatusTimerElapsed;
                _statusTimer.AutoReset = true;
                _statusTimer.Start();

                ShowStatus("即时重放缓冲已启动（按热键保存片段）");
                OnReplayStarted?.Invoke();
            }
            catch (Exception ex)
            {
                StopReplayBuffer();
                ShowStatus($"即时重放启动异常：{ex.Message}");
                AppLog.Write("Dronesee：即时重放启动异常", ex);
            }
        }

        /// <summary>停止即时重放缓冲。</summary>
        public void StopReplayBuffer()
        {
            if (!_isReplayActive && _replayFfmpeg == null) return;

            // 若正在搭车录制，管线一停录像就断了：先把录像收尾
            if (_recordingTap != null)
                StopRecordingAndSave();

            _isReplayActive = false;
            _replayReaderRun = false;

            _statusTimer?.Stop();
            _statusTimer?.Dispose();
            _statusTimer = null;

            StopAudioCapture(forReplay: true);
            KillFfmpeg(ref _replayFfmpeg);

            try { _replayReader?.Join(2000); } catch { }
            _replayReader = null;

            lock (_ringLock)
            {
                _ring.Clear();
                _ringBytes = 0;
            }

            ShowStatus("即时重放缓冲已停止");
        }

        /// <summary>
        /// 保存一次回放片段（热键触发）。
        /// dump 环形缓冲 → 临时 .ts → -c copy 无损转封装 → 保存目录。
        ///
        /// 【重要】这里只存文件，绝不弹任何窗口、不定屏幕显示的内容：
        /// 热键按下后后台默默写盘，只弹一条右下角小提示告知文件位置。
        /// 返回是否成功保存了文件。
        /// </summary>
        public bool SaveReplayClip()
        {
            if (!_isReplayActive)
            {
                ShowStatus("没有可保存的内容：即时重放缓冲未运行");
                return false;
            }

            byte[] snapshot;
            double span;
            lock (_ringLock)
            {
                if (_ringBytes == 0 || _ring.Count == 0)
                {
                    ShowStatus("没有可保存的内容：缓冲还是空的");
                    return false;
                }

                span = (_ringLast - _ring.Peek()!.Arrival).TotalSeconds;
                using var ms = new MemoryStream((int)_ringBytes);
                foreach (var chunk in _ring)
                    ms.Write(chunk.Data, 0, chunk.Data.Length);
                snapshot = ms.ToArray();
            }

            try
            {
                EnsureTempDir();

                string tempTs = Path.Combine(TempDir, $"replay_{Guid.NewGuid():N}.ts");
                File.WriteAllBytes(tempTs, snapshot);

                string finalMp4 = GenerateOutputPath("Replay", null);
                string? done = RemuxToContainer(tempTs, finalMp4);

                if (!string.IsNullOrEmpty(done) && File.Exists(done))
                {
                    TryDelete(tempTs);
                    ShowStatus($"回放录像已保存：{FormatTime(span)} → {done}");
                    OnReplayClipSaved?.Invoke(done);
                    return true;
                }

                // 转封装失败也绝不丢用户的片段：把 TS 直接放进保存目录
                string fallbackTs = ChangeExtension(finalMp4, ".ts");
                try
                {
                    File.Copy(tempTs, fallbackTs, overwrite: true);
                    TryDelete(tempTs);
                    ShowStatus($"回放已保存为 TS（mp4 封装失败，可用播放器直接打开）：{fallbackTs}");
                    OnReplayClipSaved?.Invoke(fallbackTs);
                    return true;
                }
                catch (Exception ex)
                {
                    AppLog.Write("Dronesee：回放片段保存失败", ex);
                    ShowStatus("回放保存失败（详见日志）");
                    return false;
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：保存回放片段异常", ex);
                ShowStatus($"回放保存异常：{ex.Message}");
                return false;
            }
        }

        public void UpdateSettings()
        {
            _settings = SettingsStore.Load().Dronesee ?? new DroneseeSettings();
            _settings.Normalize();
        }

        public void Dispose()
        {
            StopRecordingAndSave();
            StopReplayBuffer();

            try { _recordingTap?.Flush(); _recordingTap?.Dispose(); } catch { }
            _recordingTap = null;

            _statusTimer?.Dispose();
            _memoryTimer?.Dispose();

            lock (_ringLock)
            {
                _ring.Clear();
                _ringBytes = 0;
            }
        }

        // ---------- FFmpeg 进程 ----------

        /// <summary>启动 FFmpeg；hasAudio 时重定向 stdin 供 PCM 写入，toStdout 时重定向 stdout 供环形缓冲读取。</summary>
        private static Process? StartFFmpeg(string ffmpegPath, string args, bool redirectStdout, bool hasAudio)
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    RedirectStandardInput = hasAudio,
                    RedirectStandardOutput = redirectStdout,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
                if (!proc.Start())
                {
                    proc.Dispose();
                    return null;
                }

                try { proc.BeginErrorReadLine(); } catch { }
                proc.ErrorDataReceived += (_, e) =>
                {
                    if (!string.IsNullOrWhiteSpace(e.Data))
                        AppLog.Write($"Dronesee ffmpeg: {e.Data}");
                };

                return proc;
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：启动 FFmpeg 进程失败", ex);
                return null;
            }
        }

        /// <summary>
        /// 统一构建 FFmpeg 参数（录制与重放共用，OBS 式）。
        /// 抓屏方案由 SelectCapturePipeline 探测决定：
        ///   ddagrab(GPU 桌面复制) + 硬件编码 全程显存 —— 不用 GDI BitBlt，
        ///   不干扰游戏渲染，也不会像 gdigrab 默认抓鼠标那样导致光标回闪。
        /// </summary>
        private string BuildFFmpegArgs(string ffmpegOut, bool toStdout, string ffmpegPath,
                                        string encoder, int videoFps, int videoBitrateKbps, bool hasAudio)
        {
            var pipe = SelectCapturePipeline(ffmpegPath, encoder, videoFps);
            var sb = new StringBuilder();

            sb.Append("-hide_banner -y -loglevel warning ");

            // ---- 输入阶段（全部 -i 先出现；输出选项一律在后面） ----
            sb.Append(pipe.InputArgs);

            // 音频：WASAPI 混音 PCM 从 stdin 进来（和 OBS 一样，编码期就同步）
            if (hasAudio)
            {
                sb.Append($"-thread_queue_size 512 -f s16le -ar {WasapiAudioCapture.TargetSampleRate} " +
                          $"-ac {WasapiAudioCapture.TargetChannels} -i pipe:0 ");
            }

            // ---- 输出阶段 ----
            // 分辨率自适应：GPU 链的 scale_qsv 已含在输入图里；CPU 链用 -vf scale
            sb.Append(pipe.FilterArgs);

            // 恒定帧率：防止高负载下重复时间戳刷 Non-monotonic DTS
            sb.Append("-fps_mode cfr ");

            // 编码
            sb.Append($"-c:v {pipe.Encoder} ");
            if (pipe.Encoder == "libx264") sb.Append("-preset veryfast ");
            sb.Append($"-b:v {videoBitrateKbps}k ");
            sb.Append($"-g {Math.Max(1, 2 * videoFps)} ");
            if (!pipe.GpuPipeline) sb.Append("-pix_fmt yuv420p ");

            if (hasAudio)
            {
                sb.Append($"-c:a aac -b:a {_settings.AudioBitrate}k " +
                          $"-ar {_settings.AudioSampleRate} -ac {_settings.AudioChannels} ");
            }
            else
            {
                sb.Append("-an ");
            }

            // 每写一个包就 flush：
            //  1) 停止录制是「强杀」进程（stdin 被 PCM 占用，没法发 'q'），
            //     不 flush 会把最后几秒丢在 ffmpeg 缓冲区里（本机实测：丢 2.95s → 加此参数后 1.05s）；
            //  2) 即时重放走 stdout 管道，不 flush 会让环形缓冲迟迟收不到数据。
            sb.Append("-flush_packets 1 ");

            // 输出：TS 是流式容器，被强杀也不损坏（这是本方案崩溃安全的关键）
            sb.Append(toStdout ? "-f mpegts pipe:1 " : $"-f mpegts \"{ffmpegOut}\"");

            return sb.ToString();
        }

        // ---------- 抓屏方案（一次探测，缓存复用） ----------

        /// <summary>一条完整的“抓屏→缩放→编码器”管线描述。</summary>
        private sealed class CapturePipeline
        {
            public string InputArgs = "";
            public string FilterArgs = "";
            public bool GpuPipeline;   // GPU 链：不能再加 -pix_fmt（帧已是 qsv 硬件格式）
            public string Encoder = "";
            public string Name = "";
        }

        private string? _cachedPipeKey;
        private CapturePipeline? _cachedPipe;

        /// <summary>
        /// 选择抓屏→缩放→编码管线（对每个候选真实跑 0.5 秒编码验证，之后缓存）。
        ///
        /// 候选顺序（全部 draw_mouse=0 —— gdigrab/ddagrab 默认捕捉鼠标会把光标画进帧里，
        /// 游戏中会导致系统光标回闪）：
        ///   1) ddagrab + QSV 全显存：抓屏/缩放/编码全在 GPU，CPU 最低
        ///      （本机实测 16.6% 单核 vs gdigrab 23.6%），且没有 GDI BitBlt 干扰游戏渲染；
        ///      还顺带修复 gdigrab 在高帧率下时间戳异常导致 QSV 报
        ///      "Current frame rate is unsupported" 的问题（120fps 实测可用）。
        ///   2) ddagrab + CPU 缩放：兼容 libx264 / AMF / NVENC 等不能直接吃 d3d11 帧的编码器；
        ///   3) gdigrab 兜底：任何机器都能跑，但 BitBlt 最重。
        /// 全部失败时回退 gdigrab + libx264（纯软件，必然可用）。
        /// </summary>
        private CapturePipeline SelectCapturePipeline(string ffmpegPath, string encoder, int fps)
        {
            var (w, h) = ResolveOutputSize();
            string key = $"{ffmpegPath}|{encoder}|{fps}|{w}x{h}";
            if (_cachedPipe != null && _cachedPipeKey == key)
                return _cachedPipe;

            var candidates = new List<CapturePipeline>();

            if (encoder == "h264_qsv")
            {
                candidates.Add(new CapturePipeline
                {
                    InputArgs = $"-f lavfi -i \"ddagrab=output_idx=0:framerate={fps}:draw_mouse=0," +
                                $"hwmap=derive_device=qsv,format=qsv,scale_qsv=w={w}:h={h}\" ",
                    FilterArgs = "",
                    GpuPipeline = true,
                    Encoder = encoder,
                    Name = "ddagrab+QSV 全显存"
                });
            }

            candidates.Add(new CapturePipeline
            {
                InputArgs = $"-f lavfi -i \"ddagrab=output_idx=0:framerate={fps}:draw_mouse=0,hwdownload,format=bgra\" ",
                FilterArgs = $"-vf scale={w}:{h}:flags=bilinear ",
                Encoder = encoder,
                Name = "ddagrab+CPU缩放"
            });

            candidates.Add(new CapturePipeline
            {
                InputArgs = $"-thread_queue_size 512 -f gdigrab -framerate {fps} -draw_mouse 0 " +
                            $"-offset_x 0 -offset_y 0 -i desktop ",
                FilterArgs = $"-vf scale={w}:{h}:flags=bilinear ",
                Encoder = encoder,
                Name = "gdigrab兜底"
            });

            foreach (var c in candidates)
            {
                if (ProbeCapture(ffmpegPath, c, fps))
                {
                    _cachedPipe = c;
                    _cachedPipeKey = key;
                    AppLog.Write($"Dronesee：抓屏方案 = {c.Name}（{w}x{h}@{fps}）");
                    return c;
                }
                AppLog.Write($"Dronesee：抓屏方案探测不可用：{c.Name}");
            }

            // 终极兜底：gdigrab + libx264（纯软件，任何机器都能跑）
            var fallback = new CapturePipeline
            {
                InputArgs = $"-thread_queue_size 512 -f gdigrab -framerate {fps} -draw_mouse 0 " +
                            $"-offset_x 0 -offset_y 0 -i desktop ",
                FilterArgs = $"-vf scale={w}:{h}:flags=bilinear ",
                Encoder = "libx264",
                Name = "gdigrab+libx264终极兜底"
            };
            _cachedPipe = fallback;
            _cachedPipeKey = key;
            AppLog.Write("Dronesee：所有抓屏方案探测失败，回退 gdigrab+libx264（纯 CPU 编码，游戏时可能掉帧）");
            return fallback;
        }

        /// <summary>用候选管线真实跑 0.5 秒编码验证可用性（不是只看 -encoders 列表）。</summary>
        private static bool ProbeCapture(string ffmpegPath, CapturePipeline pipe, int fps)
        {
            try
            {
                string args = $"-hide_banner -y -loglevel error {pipe.InputArgs}{pipe.FilterArgs}" +
                              $"-fps_mode cfr -c:v {pipe.Encoder} -b:v 1000k " +
                              $"-g {Math.Max(1, 2 * fps)} " +
                              (pipe.Encoder == "libx264" ? "-preset veryfast " : "") +
                              (pipe.GpuPipeline ? "" : "-pix_fmt yuv420p ") +
                              "-an -t 0.5 -f null -";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return false;

                string err = proc.StandardError.ReadToEnd();
                if (!proc.WaitForExit(8000))
                {
                    try { proc.Kill(); } catch { }
                    return false;
                }

                if (proc.ExitCode != 0)
                {
                    var errLines = err.Trim().Split('\n');
                    string firstErr = errLines.Length > 0 ? errLines[0] : "";
                    AppLog.Write($"Dronesee：管线探测失败（{pipe.Name}）：{firstErr}");
                }

                return proc.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>TS → mp4/mkv：纯转封装，不重新编码（-c copy）。</summary>
        private string? RemuxToContainer(string tsPath, string finalPath)
        {
            string ffmpeg = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpeg)) return null;

            try
            {
                bool mkv = string.Equals(_settings.ContainerFormat, "mkv", StringComparison.OrdinalIgnoreCase);
                string args = mkv
                    ? $"-hide_banner -y -loglevel error -fflags +genpts -i \"{tsPath}\" -c copy -f matroska \"{finalPath}\""
                    : $"-hide_banner -y -loglevel error -fflags +genpts -i \"{tsPath}\" -c copy -movflags +faststart \"{finalPath}\"";

                AppLog.Write($"Dronesee：转封装 args={args}");

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpeg,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return null;

                string stderr = proc.StandardError.ReadToEnd();
                proc.WaitForExit(60_000);

                if (proc.ExitCode == 0 && File.Exists(finalPath) && new FileInfo(finalPath).Length > 0)
                    return finalPath;

                AppLog.Write($"Dronesee：转封装失败 exit={proc.ExitCode} stderr={stderr}");
                return null;
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：转封装异常", ex);
                return null;
            }
        }

        private void OnRecordingFFmpegExited(object? sender, EventArgs e)
        {
            if (!_isRecording) return;

            // FFmpeg 意外退出（多为参数/编码器问题）：TS 已在磁盘上，尝试收尾
            _isRecording = false;
            AppLog.Write("Dronesee：录制 FFmpeg 意外退出，尝试收尾保存 TS");

            _statusTimer?.Stop();
            _statusTimer?.Dispose();
            _statusTimer = null;

            StopAudioCapture(forReplay: false);
            _memoryTimer?.Stop();
            _memoryTimer?.Dispose();
            _memoryTimer = null;

            double elapsed = _recordingElapsed;
            string? produced = FinalizeRecordingFile(elapsed);
            if (!string.IsNullOrEmpty(produced) && File.Exists(produced))
            {
                ShowStatus($"录制已收尾：{FormatTime(elapsed)} → {produced}");
                OnRecordingComplete?.Invoke(produced);
            }
            else
            {
                ShowStatus("录制中断（FFmpeg 意外退出，详见日志）");
            }
        }

        private void OnReplayFFmpegExited(object? sender, EventArgs e)
        {
            if (!_isReplayActive) return;

            _isReplayActive = false;
            _replayReaderRun = false;
            _statusTimer?.Stop();
            _statusTimer?.Dispose();
            _statusTimer = null;

            ShowStatus("即时重放缓冲中断（FFmpeg 意外退出，详见日志）");
            AppLog.Write("Dronesee：重放缓冲 FFmpeg 意外退出");
        }

        /// <summary>把录制 TS 转封装成最终容器；成功则删除 TS。</summary>
        private string? FinalizeRecordingFile(double elapsedSeconds)
        {
            string? ts = _recordingTsPath;
            string? final = _currentRecordingPath;
            _recordingTsPath = null;

            if (string.IsNullOrEmpty(ts) || !File.Exists(ts))
            {
                AppLog.Write($"Dronesee：收尾失败——TS 文件不存在（path={ts ?? "(null)"}）");
                return null;
            }

            long tsLen = new FileInfo(ts).Length;
            if (tsLen == 0)
            {
                // 不删：空 TS 说明 FFmpeg 一个数据包都没写出来，留着便于诊断
                AppLog.Write($"Dronesee：收尾失败——TS 长度为 0（{ts}）。" +
                             "说明 FFmpeg 没有写出任何数据（编码器不可用 / 输入被阻塞 / 进程被立即杀掉）。");
                return null;
            }

            AppLog.Write($"Dronesee：收尾开始——TS {tsLen / 1024.0:F0} KB，目标 {final}");

            if (string.IsNullOrEmpty(final))
                return ts;   // 没有目标容器名就把 TS 交出去

            string? done = RemuxToContainer(ts, final);
            if (!string.IsNullOrEmpty(done) && File.Exists(done))
            {
                TryDelete(ts);
                return done;
            }

            // 转封装失败：保留 TS，至少不丢内容
            return File.Exists(ts) ? ts : done;
        }

        /// <summary>异常兜底：尽量把已落盘的 TS 收尾出来。</summary>
        private void OnRecordingCompleteSafeFallback()
        {
            try
            {
                string? produced = FinalizeRecordingFile(_recordingElapsed);
                if (!string.IsNullOrEmpty(produced) && File.Exists(produced))
                    OnRecordingComplete?.Invoke(produced);
            }
            catch { }
        }

        /// <summary>关闭 FFmpeg 的 stdin（PCM 管道），让它收到音频 EOF 后自行收尾。</summary>
        private static void TryCloseFfmpegStdin(Process? proc)
        {
            if (proc == null) return;
            try { proc.StandardInput?.Close(); } catch { /* 未重定向 stdin 时忽略 */ }
        }

        private static void KillFfmpeg(ref Process? proc)
        {
            var p = proc;
            proc = null;
            if (p == null) return;

            try
            {
                if (!p.HasExited)
                {
                    p.Kill();
                    p.WaitForExit(3000);
                }
            }
            catch { }
            try { p.Dispose(); } catch { }
        }

        // ---------- 环形缓冲 ----------

        private sealed class RingChunk
        {
            public byte[] Data = Array.Empty<byte>();
            public DateTime Arrival;
        }

        private readonly object _ringLock = new();

        /// <summary>持续读 FFmpeg stdout 的 TS 流并塞进环形缓冲。</summary>
        private void ReplayReaderLoop()
        {
            var proc = _replayFfmpeg;
            if (proc == null) return;

            var buffer = new byte[64 * 1024];

            try
            {
                var stream = proc.StandardOutput.BaseStream;

                while (_replayReaderRun && !proc.HasExited)
                {
                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    var chunk = new RingChunk
                    {
                        Data = new byte[read],
                        Arrival = DateTime.UtcNow
                    };
                    System.Buffer.BlockCopy(buffer, 0, chunk.Data, 0, read);

                    lock (_ringLock)
                    {
                        _ring.Enqueue(chunk);
                        _ringBytes += read;
                        _ringLast = chunk.Arrival;
                        TrimRingLocked();
                    }

                    // 搭车录制：同一份 TS 字节写进录像文件（不额外采集/编码）
                    var tap = _recordingTap;
                    if (tap != null)
                    {
                        try
                        {
                            tap.Write(chunk.Data, 0, chunk.Data.Length);
                        }
                        catch (Exception ex)
                        {
                            AppLog.Write("Dronesee：录像搭车写入失败", ex);
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：重放缓冲读取线程退出", ex);
            }
        }

        /// <summary>
        /// 滚动淘汰（锁内调用）：双上限 ——「最大时长」与「内存缓存」字节数，
        /// 谁先到就裁谁。这是 OBS Replay Buffer 的核心逻辑。
        /// </summary>
        private void TrimRingLocked()
        {
            double maxSeconds = _settings.MaxReplayDurationSeconds;
            long maxBytes = (long)_settings.MemoryCacheMB * 1024 * 1024;

            bool trimmedByBytes = false;
            double spanWhenTrimmed = 0;

            while (_ring.Count > 0)
            {
                double span = (_ringLast - _ring.Peek()!.Arrival).TotalSeconds;
                bool overTime = span > maxSeconds;
                bool overBytes = _ringBytes > maxBytes;

                if (!overTime && !overBytes) break;

                if (overBytes && !overTime)
                {
                    trimmedByBytes = true;
                    spanWhenTrimmed = span;
                }

                var oldest = _ring.Dequeue();
                _ringBytes -= oldest.Data.Length;
            }

            // 「内存缓存」不够存满设定的最大时长 → 提醒一次需要多少
            if (trimmedByBytes && !_replayMemoryWarned && spanWhenTrimmed < maxSeconds - 1)
            {
                double mbps = _ringBytes * 8.0 / Math.Max(1, spanWhenTrimmed) / 1_000_000.0;
                double needMB = mbps * 1000_000.0 * maxSeconds / 8.0 / (1024.0 * 1024.0);
                _replayMemoryWarned = true;
                OnMemoryWarning?.Invoke(
                    $"「内存缓存 {_settings.MemoryCacheMB}MB」不够存满 {maxSeconds:F0} 秒，" +
                    $"当前实测只能缓冲约 {spanWhenTrimmed:F0} 秒。" +
                    $"把「内存缓存」调到约 {needMB:F0}MB，或降低「重放码率」/「最大时长」。");
            }
        }

        // ---------- 音频 ----------

        /// <summary>
        /// 打开 WASAPI 音频（环回 + 麦克风），识别不到的自动关闭并通知用户。
        /// 只打开设备，不启动泵——泵要等 FFmpeg stdin 就绪后再 Start。
        /// </summary>
        private WasapiAudioCapture? OpenAudioCapture(bool forReplay)
        {
            bool wantSystem = _settings.SystemAudioEnabled;
            bool wantMic = _settings.MicrophoneEnabled;

            if (!wantSystem && !wantMic)
            {
                AppLog.Write("Dronesee：系统声音/麦克风均关闭，本次为纯视频");
                return null;
            }

            try
            {
                var capture = new WasapiAudioCapture();
                bool ok = capture.Open(wantSystem, wantMic);

                var parts = new List<string>();
                if (wantSystem)
                    parts.Add(capture.SystemAudioActive
                        ? $"系统声音：{capture.SystemAudioDeviceName}"
                        : $"系统声音：已关闭（{capture.SystemAudioError ?? "未检测到设备"}）");
                if (wantMic)
                    parts.Add(capture.MicrophoneActive
                        ? $"麦克风：{capture.MicrophoneDeviceName}"
                        : $"麦克风：已关闭（{capture.MicrophoneError ?? "未检测到设备"}）");

                OnAudioInfo?.Invoke(string.Join("\n", parts));

                if (!ok)
                {
                    capture.Dispose();
                    AppLog.Write("Dronesee：无可用音频设备，本次仅录制画面");
                    return null;
                }

                return capture;
            }
            catch (Exception ex)
            {
                AppLog.Write("Dronesee：音频捕捉打开失败（本次仅录画面）", ex);
                return null;
            }
        }

        private void StopAudioCapture(bool forReplay)
        {
            if (forReplay)
            {
                try { _replayAudio?.Stop(); } catch { }
                try { _replayAudio?.Dispose(); } catch { }
                _replayAudio = null;
            }
            else
            {
                try { _audioCapture?.Stop(); } catch { }
                try { _audioCapture?.Dispose(); } catch { }
                _audioCapture = null;
            }
        }

        // ---------- 回放窗口 ----------
        //
        // 已移除：旧实现会在保存后弹一个全屏窗口自动播放片段，
        // 会盖住用户当前屏幕（而且 MediaElement 渲染失败时就是一片黑屏）。
        // 按需求：热键只负责把回放保存到磁盘，不对屏幕显示做任何干预。

        // ---------- 工具 ----------

        /// <summary>已找到的 ffmpeg 路径缓存（只缓存成功结果；失败不缓存，便于用户补放后立刻生效）。</summary>
        private static string? _ffmpegCached;

        /// <summary>
        /// 查找 ffmpeg.exe。
        ///
        /// 为什么要搜这么多地方：用户会把整个程序目录拷成多份
        /// （publish\v15、v15_final、bin\Release…），拷贝时经常漏掉 105MB 的 ffmpeg.exe。
        /// 一旦找不到，即时重放缓冲根本不会启动，症状就是「按热键总是提示没有可保存的内容」。
        /// 所以除了同目录，这里还会向上找最多 4 层父目录、以及每层父目录下的一级子目录。
        /// </summary>
        private static string FindFFmpeg()
        {
            if (!string.IsNullOrEmpty(_ffmpegCached) && File.Exists(_ffmpegCached))
                return _ffmpegCached;

            string baseDir = AppDomain.CurrentDomain.BaseDirectory;

            // 1) 同目录 / 同目录下的 publish
            string p = Path.Combine(baseDir, "ffmpeg.exe");
            if (File.Exists(p)) { _ffmpegCached = p; return p; }

            p = Path.Combine(baseDir, "publish", "ffmpeg.exe");
            if (File.Exists(p)) { _ffmpegCached = p; return p; }

            // 2) 向上最多 4 层：每层先看自己，再看它的一级子目录
            try
            {
                var dir = new DirectoryInfo(baseDir);
                for (int level = 0; level < 4; level++)
                {
                    dir = dir?.Parent;
                    if (dir == null) break;

                    p = Path.Combine(dir.FullName, "ffmpeg.exe");
                    if (File.Exists(p)) { _ffmpegCached = p; return p; }

                    DirectoryInfo[] subs;
                    try { subs = dir.GetDirectories(); } catch { continue; }

                    foreach (var sub in subs)
                    {
                        try
                        {
                            p = Path.Combine(sub.FullName, "ffmpeg.exe");
                            if (File.Exists(p)) { _ffmpegCached = p; return p; }
                        }
                        catch { }
                    }
                }
            }
            catch { }

            // 3) 常见位置
            string[] common =
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "GameHub"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "GameHub"),
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            };

            foreach (var dir in common)
            {
                try
                {
                    if (string.IsNullOrEmpty(dir)) continue;
                    p = Path.Combine(dir, "ffmpeg.exe");
                    if (File.Exists(p)) { _ffmpegCached = p; return p; }
                }
                catch { }
            }

            // 4) PATH
            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var dir in pathEnv.Split(Path.PathSeparator))
                {
                    try
                    {
                        p = Path.Combine(dir.Trim(), "ffmpeg.exe");
                        if (File.Exists(p)) { _ffmpegCached = p; return p; }
                    }
                    catch { }
                }
            }

            return string.Empty;
        }

        /// <summary>
        /// FFmpeg 找不到时给一个显眼、可操作的提示。
        /// 旧实现只写状态文本与日志，用户看不到，只会反复按热键、反复看到
        /// 「没有可保存的内容」，完全不知道真实原因是缺 ffmpeg.exe
        /// （日志里只会有「即时重放未能开启：FFmpeg 未找到」）。
        /// </summary>
        private static void NotifyFfmpegMissing()
        {
            try
            {
                string expected = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "ffmpeg.exe");
                NotificationManager.ShowInfo("Dronesee 缺少 FFmpeg",
                    $"录制/即时重放都无法工作。\n把 ffmpeg.exe 放到：\n{expected}\n（与 GameHub.exe 同一目录）");
            }
            catch { }
        }

        private string DetectEncoder(string ffmpegPath, out string note)
        {
            note = string.Empty;

            if (_cachedEncoder != null && _cachedFfmpeg == ffmpegPath)
            {
                note = "使用缓存结果";
                return _cachedEncoder;
            }

            string requested = (_settings.Encoder ?? "auto").ToLowerInvariant().Trim();

            // 用户手动指定且实测可用 → 直接用
            if (requested != "auto" && !string.IsNullOrEmpty(requested))
            {
                if (TestEncoder(ffmpegPath, requested))
                {
                    note = "用户指定";
                    _cachedEncoder = requested;
                    _cachedFfmpeg = ffmpegPath;
                    return requested;
                }
                AppLog.Write($"Dronesee：指定编码器 {requested} 实测不可用，改自动检测");
                note = $"指定 {requested} 实测不可用，改自动";
                // 继续自动
            }

            // 自动：实测每个硬件编码器（N→I→A）。
            // 旧实现用 `ffmpeg -encoders` 只查编译支持，不查运行时驱动，
            // 导致 h264_nvenc 在无 NVIDIA 驱动的机器上被选中，运行时 Cannot load nvcuda.dll。
            string[] candidates = { "h264_nvenc", "h264_qsv", "h264_amf" };
            foreach (var c in candidates)
            {
                if (TestEncoder(ffmpegPath, c))
                {
                    note = $"自动检测 → {c}";
                    _cachedEncoder = c;
                    _cachedFfmpeg = ffmpegPath;
                    return c;
                }
            }

            // 兜底软件编码（纯 CPU，一定可用）
            note = "硬件编码器均不可用，回退 libx264（纯 CPU）";
            _cachedEncoder = "libx264";
            _cachedFfmpeg = ffmpegPath;
            return "libx264";
        }

        /// <summary>
        /// 实测编码器是否真的能用：跑一段极短的编码测试。
        /// 旧实现用 `ffmpeg -encoders` 只查「编译支持」，不查「运行时驱动」，
        /// 导致 h264_nvenc 在没装 NVIDIA 驱动的机器上被选中，运行时 Cannot load nvcuda.dll。
        /// </summary>
        private static bool TestEncoder(string ffmpegPath, string encoderName)
        {
            try
            {
                string args =
                    $"-hide_banner -loglevel error -f lavfi -i color=c=black:s=128x128:r=30:d=0.3 " +
                    $"-c:v {encoderName} -pix_fmt yuv420p -f null -";

                var psi = new ProcessStartInfo
                {
                    FileName = string.IsNullOrEmpty(ffmpegPath) ? "ffmpeg" : ffmpegPath,
                    Arguments = args,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var process = Process.Start(psi);
                if (process == null) return false;

                // 读完输出避免管道阻塞
                try { process.StandardOutput.ReadToEnd(); } catch { }
                try { process.StandardError.ReadToEnd(); } catch { }
                process.WaitForExit(8000);

                return process.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }

        private const int SM_CXSCREEN = 0;
        private const int SM_CYSCREEN = 1;

        [DllImport("user32.dll")]
        private static extern int GetSystemMetrics(int nIndex);

        /// <summary>
        /// 解析【输出】分辨率（scale 滤镜的目标）：
        ///   1) 取设置里的「分辨率」，宽高都取偶数（H.264 要求）；
        ///   2) 若设置值比真实桌面还大（例如 1080p 屏设 4K），直接用桌面原生尺寸，
        ///      避免无意义的放大与画质损失；
        ///   3) 抓屏本身始终是原生桌面（gdigrab 不加 -video_size），
        ///      所以这里不需要受桌面尺寸限制，它只是缩放目标。
        /// </summary>
        private (int width, int height) ResolveOutputSize()
        {
            int desktopW = GetSystemMetrics(SM_CXSCREEN);
            int desktopH = GetSystemMetrics(SM_CYSCREEN);
            if (desktopW < 2) desktopW = 1920;
            if (desktopH < 2) desktopH = 1080;

            desktopW -= desktopW % 2;
            desktopH -= desktopH % 2;

            if (!TryParseResolution(_settings.Resolution, out int w, out int h) || w < 2 || h < 2)
                return (desktopW, desktopH);

            // 比桌面还大就没必要放大
            if (w > desktopW || h > desktopH)
            {
                AppLog.Write($"Dronesee：设置的分辨率 {w}x{h} 大于桌面 {desktopW}x{desktopH}，" +
                             "改用原生桌面尺寸（不放大）。");
                return (desktopW, desktopH);
            }

            w -= w % 2;
            h -= h % 2;
            if (w < 2 || h < 2) return (desktopW, desktopH);

            return (w, h);
        }

        private static bool TryParseResolution(string? resolution, out int width, out int height)
        {
            width = 0;
            height = 0;
            if (string.IsNullOrWhiteSpace(resolution)) return false;

            var parts = resolution.Split('x', 'X', '×');
            if (parts.Length != 2) return false;

            return int.TryParse(parts[0].Trim(), out width) && int.TryParse(parts[1].Trim(), out height)
                   && width > 0 && height > 0;
        }

        // ---------- 路径 ----------

        private static string TempDir =>
            Path.Combine(Path.GetTempPath(), "GameHub", "Dronesee");

        private static void EnsureTempDir()
        {
            try { Directory.CreateDirectory(TempDir); } catch { }
        }

        private static void TryDelete(string? path)
        {
            if (string.IsNullOrEmpty(path)) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        /// <summary>生成输出文件路径（默认 = GameHub 程序目录下 GameHub Recordings 子文件夹）。</summary>
        private string GenerateOutputPath(string prefix, string? forcedExtension)
        {
            string dir = ResolveSaveDir();

            string ext = forcedExtension ?? (string.Equals(_settings.ContainerFormat, "mkv",
                                  StringComparison.OrdinalIgnoreCase) ? ".mkv" : ".mp4");

            string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            return Path.Combine(dir, $"{prefix}_{timestamp}{ext}");
        }

        private string ChangeExtensionByContainer(string tsPath)
            => ChangeExtension(tsPath,
                string.Equals(_settings.ContainerFormat, "mkv", StringComparison.OrdinalIgnoreCase)
                    ? ".mkv" : ".mp4");

        private static string ChangeExtension(string path, string ext)
        {
            try { return Path.ChangeExtension(path, ext); }
            catch { return path + ext; }
        }

        /// <summary>
        /// 解析保存目录：无效（空白/根盘符/不可创建）一律回退默认目录。
        /// 默认目录 = GameHub.exe 所在文件夹\GameHub Recordings；
        /// 程序目录不可写（如装在 Program Files）时回退 %LOCALAPPDATA%\GameHub\GameHub Recordings。
        /// </summary>
        private string ResolveSaveDir()
        {
            string dir = _settings?.SavePath ?? string.Empty;

            bool pathIsRootDrive = dir.Length == 3
                                   && char.IsLetter(dir[0])
                                   && dir[1] == ':'
                                   && dir[2] == '\\';
            if (!pathIsRootDrive && dir.EndsWith(":")) pathIsRootDrive = true;

            bool invalid = string.IsNullOrWhiteSpace(dir) || pathIsRootDrive;
            if (!invalid)
            {
                try { if (!Directory.Exists(dir)) invalid = true; }
                catch { invalid = true; }
            }

            if (invalid)
            {
                AppLog.Write($"Dronesee：保存路径无效（'{dir}'），回退默认目录");
                dir = DroneseeSettings.DefaultSaveFolder();
            }

            try { Directory.CreateDirectory(dir); }
            catch (Exception ex)
            {
                AppLog.Write($"Dronesee：创建保存目录失败（{dir}），回退 LocalData 目录", ex);
                dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "GameHub", "GameHub Recordings");
                try { Directory.CreateDirectory(dir); } catch { }
            }

            return dir;
        }

        // ---------- 状态 / 计时器 ----------

        private void ShowStatus(string status)
        {
            AppLog.Write($"Dronesee：{status}");

            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher == null) return;

            if (!dispatcher.CheckAccess())
            {
                dispatcher.BeginInvoke(new Action(() => OnStatusChanged?.Invoke(status)));
                return;
            }

            OnStatusChanged?.Invoke(status);
        }

        private void OnRecordingStatusTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            _recordingElapsed += 1.0;
            ShowStatus($"录制中 {FormatTime(_recordingElapsed)}");
        }

        private void OnReplayStatusTimerElapsed(object? sender, System.Timers.ElapsedEventArgs e)
        {
            if (_replayElapsed < _settings.MaxReplayDurationSeconds)
                _replayElapsed += 1.0;

            double mb;
            double span;
            lock (_ringLock)
            {
                mb = _ringBytes / (1024.0 * 1024.0);
                span = _ring.Count == 0 ? 0 : (_ringLast - _ring.Peek()!.Arrival).TotalSeconds;
            }

            ShowStatus($"即时重放缓冲中 {FormatTime(_replayElapsed)} | 已缓存 {FormatTime(span)} / {_settings.MemoryCacheMB}MB({mb:F0})");
        }

        private void StartMemoryMonitoring()
        {
            _memoryTimer = new System.Timers.Timer(5000);
            _memoryTimer.Elapsed += (_, _) =>
            {
                try
                {
                    var process = Process.GetCurrentProcess();
                    _memoryUsageMB = process.WorkingSet64 / (1024.0 * 1024.0);
                }
                catch { }
            };
            _memoryTimer.AutoReset = true;
            _memoryTimer.Start();
        }

        private static string FormatTime(double totalSeconds)
        {
            int totalSec = (int)totalSeconds;
            int h = totalSec / 3600;
            int m = (totalSec % 3600) / 60;
            int s = totalSec % 60;
            if (h > 0) return $"{h:D2}:{m:D2}:{s:D2}";
            return $"{m:D2}:{s:D2}";
        }
    }
}
