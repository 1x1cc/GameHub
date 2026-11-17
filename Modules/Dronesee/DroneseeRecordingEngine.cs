using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
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
    ///      引擎把流读进内存环形缓冲，只按「最大时长」滚动淘汰
    ///      （2026-11-12 起已取消内存字节数上限检测）。
    ///      按热键保存时：把缓冲落成临时 .ts → -c copy 转封装成 mp4 → 存到保存目录并自动播放。
    ///      没有任何 JPEG 抓帧、没有任何二次编码。
    ///
    /// 内存占用：编码后的 H.264 流 ≈ 码率 ÷ 8。
    ///   30 分钟 @ 2500kbps ≈ 577MB；@ 8000kbps ≈ 1.8GB。
    ///   只受「最大时长」和内存条实际容量限制；与硬盘容量、硬盘自带缓存无关。
    /// </summary>
    public class DroneseeRecordingEngine : IDisposable
    {
        // ---------- 事件 ----------

        public event Action<string>? OnStatusChanged;
        public event Action<string>? OnRecordingComplete;

        /// <summary>
        /// 采集异常（FFmpeg 意外退出 / 长时间没有新数据）→ 通知用户。
        /// 出现这种情况时：环形缓冲不再更新，
        /// 界面上看起来就是“缓存清不掉、保存出来的总是旧内容”，必须让用户知道。
        /// </summary>
        public event Action<string>? OnCaptureWarning;

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

        private double _memoryUsageMB;
        private System.Timers.Timer? _statusTimer;
        private System.Timers.Timer? _memoryTimer;

        private DroneseeSettings _settings;

        // 编码器探测缓存，避免每次都跑 ffmpeg -encoders
        private string? _cachedEncoder;
        private string? _cachedFfmpeg;

        /// <summary>
        /// 重放管线固定 2 秒一个关键帧：
        /// 保存片段时会从淘汰点向后对齐到最近的关键帧，GOP 越短“多带”的头部越短、
        /// 保存越快。录制管线用设置里的「关键帧」值。
        /// </summary>
        private const int ReplayKeyframeSeconds = 2;

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

        /// <summary>环形缓冲内存上限（MB）：已取消上限，恒为 0（属性保留供旧调用点读取）。</summary>
        public double BufferLimitMB => 0;

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
        public void StartRecording() => StartRecording(null);

        /// <summary>
        /// 开录。profile 不为空时用它当录制参数 —— 「随游戏自动录像」用即时重放那一组画质参数，
        /// 这样**即使即时重放关着**，录出来的画质也和重放设置一致（2026-11-12 用户要求两个功能互不依赖）。
        /// </summary>
        public void StartRecording(DroneseeSettings? profile)
        {
            if (_isRecording) return;

            try
            {
                _settings = profile ?? SettingsStore.Load().Dronesee ?? new DroneseeSettings();
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
                    hasAudio: hasAudio,
                    keyframeSeconds: _settings.KeyframeInterval,
                    resolution: _settings.Resolution);

                WarnIfBitrateLow(_settings.Resolution, _settings.FrameRate, _settings.Bitrate, "正式录制");

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

            // 协调（2026-11-12）：正在搭车录整局时不许重启缓冲 ——
            // 缓冲一换管线，正在写的录像文件就悬在半路（tap 属于旧 stdout 泵），
            // 而且会多出一个音频会话（麦克风被重复占用）。宁可明确拒掉并留痕，
            // 也绝不允许出现“两路采集 + 两个音频会话”。
            if (_recordingTap != null || _isRecording)
            {
                AppLog.Write("Dronesee：正在录整局，暂不重启即时重放缓冲（先让这一局的录像收尾）");
                ShowStatus("正在录整局录像，暂不重启即时重放缓冲");
                return;
            }

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
                _tsScanner.Reset();
                _replayStreamOffset = 0;
                _lastReplayDataUtc = DateTime.UtcNow;

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
                    hasAudio: hasAudio,
                    keyframeSeconds: ReplayKeyframeSeconds,
                    resolution: _settings.ReplayResolution);

                WarnIfBitrateLow(_settings.ReplayResolution, _settings.ReplayFrameRate,
                                 _settings.ReplayBitrate, "即时重放");

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
                int generation = Interlocked.Increment(ref _replayGeneration);   // 会话代次：重启后旧线程立刻退出
                _replayReaderRun = true;
                _replayReader = new Thread(() => ReplayReaderLoop(generation))
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
            Interlocked.Increment(ref _replayGeneration);   // 让可能还在读旧管道的线程立刻退出

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

            List<RingChunk> chunks;
            double span;
            double dataAgeSeconds = 0;
            long headOffset;
            long clipStartOffset;
            byte[]? patPacket;
            byte[]? pmtPacket;

            lock (_ringLock)
            {
                if (_ringBytes == 0 || _ring.Count == 0)
                {
                    ShowStatus("没有可保存的内容：缓冲还是空的");
                    return false;
                }

                span = (_ringLast - _ring.Peek()!.Arrival).TotalSeconds;

                // 缓冲里最新数据离现在多久：很大说明采集已经停了，本次保存的只是旧内容
                dataAgeSeconds = (DateTime.UtcNow - _ringLast).TotalSeconds;

                // 只把块的引用取出来：块内容入队后不再改动，出了锁再写盘也不会被淘汰影响，
                // 同时避免在锁里做几十 MB 的磁盘写入（会堵住读取线程、丢掉采集数据）。
                chunks = new List<RingChunk>(_ring);
                headOffset = chunks[0].StartOffset;
                clipStartOffset = FindClipStartLocked(headOffset);
                patPacket = _tsScanner.LastPat;
                pmtPacket = _tsScanner.LastPmt;
            }

            try
            {
                EnsureTempDir();

                string tempTs = Path.Combine(TempDir, $"replay_{Guid.NewGuid():N}.ts");
                WriteClipFile(tempTs, chunks, clipStartOffset, patPacket, pmtPacket);

                AppLog.Write($"Dronesee：保存片段——窗口头={headOffset}，落盘起点={clipStartOffset}" +
                             $"（已对齐关键帧={clipStartOffset > headOffset}，前移 {clipStartOffset - headOffset} 字节），" +
                             $"缓冲跨度={FormatTime(span)}，块数={chunks.Count}，最新数据龄={dataAgeSeconds:F1}s，" +
                             $"PAT/PMT={(patPacket != null && pmtPacket != null)}，" +
                             $"搭车录制={(_recordingTap != null ? "是（整局录像正在写同一路 TS，两者共用一次采集与编码）" : "否")}");

                if (dataAgeSeconds > 5)
                {
                    OnCaptureWarning?.Invoke($"缓冲里最新画面已是 {dataAgeSeconds:F0} 秒前的（采集可能已中断），" +
                                             "本次保存的是旧内容。请重新打开「即时重放」以恢复采集。");
                }

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
        ///
        /// 【resolution 参数是必需的】以前这里没有分辨率参数，
        /// SelectCapturePipeline → ResolveOutputSize() 硬读 _settings.Resolution，
        /// 于是「重放分辨率」成了死配置：界面里选 1440p / 4K 都白选，
        /// 本机实测回放文件一律是 1920x1080（码率却老老实实按 ReplayBitrate 走，
        /// 说明只有分辨率这一项没接上）。现在录制传「录制分辨率」、
        /// 即时重放传「重放分辨率」，各自名副其实。
        /// </summary>
        private string BuildFFmpegArgs(string ffmpegOut, bool toStdout, string ffmpegPath,
                                        string encoder, int videoFps, int videoBitrateKbps, bool hasAudio,
                                        int keyframeSeconds, string? resolution)
        {
            var pipe = SelectCapturePipeline(ffmpegPath, encoder, videoFps, resolution);
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
            // 分辨率各走各的（录制用「分辨率」、重放用「重放分辨率」）；
            // GPU 链的 scale_qsv 已含在输入图里，CPU 链用 -vf scale（lanczos）。
            sb.Append(pipe.FilterArgs);

            // 恒定帧率：防止高负载下重复时间戳刷 Non-monotonic DTS
            sb.Append("-fps_mode cfr ");

            // 编码
            sb.Append($"-c:v {pipe.Encoder} ");
            if (pipe.Encoder == "libx264") sb.Append("-preset veryfast ");
            sb.Append($"-b:v {videoBitrateKbps}k ");
            // 关键帧间隔：GOP 越短，片段从淘汰点对齐到关键帧后“多带”的头就越短。
            // 旧实现把设置里的「关键帧」完全忽略了（写死 2 秒）——设置改了不生效。
            int keyframeFrames = Math.Max(1, Math.Max(1, keyframeSeconds) * videoFps);
            sb.Append($"-g {keyframeFrames} ");
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
        private CapturePipeline SelectCapturePipeline(string ffmpegPath, string encoder, int fps, string? resolution)
        {
            var (w, h) = ResolveOutputSize(resolution);
            // key 里带着 w x h：录制与重放用不同分辨率时不会互相串用探测缓存。
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
                FilterArgs = $"-vf scale={w}:{h}:flags=lanczos ",
                Encoder = encoder,
                Name = "ddagrab+CPU缩放"
            });

            candidates.Add(new CapturePipeline
            {
                InputArgs = $"-thread_queue_size 512 -f gdigrab -framerate {fps} -draw_mouse 0 " +
                            $"-offset_x 0 -offset_y 0 -i desktop ",
                FilterArgs = $"-vf scale={w}:{h}:flags=lanczos ",
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
                FilterArgs = $"-vf scale={w}:{h}:flags=lanczos ",
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
            Interlocked.Increment(ref _replayGeneration);
            _statusTimer?.Stop();
            _statusTimer?.Dispose();
            _statusTimer = null;

            ShowStatus("即时重放缓冲中断（FFmpeg 意外退出，详见日志）");
            AppLog.Write("Dronesee：重放缓冲 FFmpeg 意外退出");

            // 采集挂了之后环形缓冲就不再刷新（界面上看着就是“缓存清不掉”），
            // 这里有限次数地自动拉回来；实在拉不回来会在状态栏明确告知。
            Task.Run(() =>
            {
                try { TryAutoRestartReplayBuffer("FFmpeg 意外退出"); } catch { }
            });
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

            /// <summary>本块第一个字节在整个 TS 流中的绝对偏移。</summary>
            public long StartOffset;

            /// <summary>本块内出现的「关键帧 AU 起始」绝对偏移（按 188 字节包边界）。</summary>
            public List<long> KeyframeOffsets = new();
        }

        private readonly object _ringLock = new();

        /// <summary>边读边解析 TS：记录关键帧位置与 PAT/PMT，供保存片段时对齐。</summary>
        private readonly TsKeyframeScanner _tsScanner = new();

        /// <summary>已从 FFmpeg 读入的 TS 字节数（= 下一块的绝对偏移）。</summary>
        private long _replayStreamOffset;

        /// <summary>回放会话代次：重启后旧读取线程据此自行退出，不会污染新缓冲。</summary>
        private int _replayGeneration;

        /// <summary>最近一次收到缓冲数据的时刻（采集看门狗用）。</summary>
        private DateTime _lastReplayDataUtc = DateTime.MinValue;

        // 采集异常自动重启（有上限，避免反复重启）
        private int _replayRestartAttempts;
        private DateTime _replayRestartCooldownUntil = DateTime.MinValue;
        private const int MaxReplayAutoRestarts = 3;
        private const double ReplayStallSeconds = 15.0;

        /// <summary>持续读 FFmpeg stdout 的 TS 流并塞进环形缓冲。</summary>
        private void ReplayReaderLoop(int generation)
        {
            var proc = _replayFfmpeg;
            if (proc == null) return;

            var buffer = new byte[64 * 1024];

            try
            {
                var stream = proc.StandardOutput.BaseStream;

                while (_replayReaderRun && !proc.HasExited)
                {
                    if (generation != Volatile.Read(ref _replayGeneration)) break;   // 已被新一轮会话取代

                    int read = stream.Read(buffer, 0, buffer.Length);
                    if (read <= 0) break;

                    var chunk = new RingChunk
                    {
                        Data = new byte[read],
                        Arrival = DateTime.UtcNow,
                        StartOffset = _replayStreamOffset
                    };
                    System.Buffer.BlockCopy(buffer, 0, chunk.Data, 0, read);

                    // 顺带解析这段 TS：记下关键帧 AU 起始位置与最近的 PAT/PMT。
                    // 保存片段时要靠它对齐，否则片段第一帧不是关键帧，播放器可能黑屏。
                    // 解析失败绝不影响缓存本身。
                    try
                    {
                        // 双重检查：只有当前代次的线程才允许喂解析器（重启那一瞬间可能撞上）
                        if (generation == Volatile.Read(ref _replayGeneration))
                        {
                            _tsScanner.Push(chunk.Data, read);
                            chunk.KeyframeOffsets = _tsScanner.TakeNewKeyframes();
                        }
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write("Dronesee：TS 解析异常（只影响片段对齐，不影响缓存）", ex);
                    }
                    _replayStreamOffset += read;
                    _lastReplayDataUtc = chunk.Arrival;

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
        /// 滚动淘汰（锁内调用）：只按「最大时长」滚动。
        /// 已取消内存字节数上限检测（2026-11-12 用户要求）：
        /// 缓冲大小 = 最大时长 × 实测码率，不再有“达到上限”的提示或报错。
        /// </summary>
        private void TrimRingLocked()
        {
            double maxSeconds = _settings.MaxReplayDurationSeconds;
            
            while (_ring.Count > 0)
            {
                double span = (_ringLast - _ring.Peek()!.Arrival).TotalSeconds;
                if (span <= maxSeconds) break;

                var oldest = _ring.Dequeue();
                _ringBytes -= oldest.Data.Length;
            }
        }

        /// <summary>
        /// 在环形缓冲窗口内找「第一个可以安全作为片段首帧的关键帧位置」（必须锁内调用）。
        ///
        /// 为什么必须对齐：
        ///   窗口是按字节/时长淘汰出来的，淘汰点通常落在 GOP 中间。
        ///   直接从这个字节开始存盘，片段的第一帧就不是关键帧（PNG/播放器可能整段黑屏，
        ///   至少开头会黑到下一个关键帧）。对齐到关键帧后，片段开头就是
        ///   PAT/PMT + SPS/PPS + IDR，和正常开头的录像完全一致。
        /// 找不到关键帧标记（极老的缓冲/解析失败）时返回窗口头，保持旧行为不丢内容。
        /// </summary>
        private long FindClipStartLocked(long headOffset)
        {
            foreach (var chunk in _ring)
            {
                if (chunk.KeyframeOffsets.Count == 0) continue;
                foreach (var kf in chunk.KeyframeOffsets)
                {
                    if (kf >= headOffset)
                        return kf;
                }
            }
            return headOffset;
        }

        /// <summary>
        /// 把窗口内容写成临时 .ts：先垫 PAT/PMT（片段自带节目表），
        /// 再从 <paramref name="clipStartOffset"/>（关键帧位置）开始写。
        /// 分块写盘，不再拼大数组 —— 旧实现的内存副本在缓存接近上限时会翻倍占用甚至溢出。
        /// </summary>
        private static void WriteClipFile(string path, List<RingChunk> chunks, long clipStartOffset,
                                         byte[]? patPacket, byte[]? pmtPacket)
        {
            using var fs = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read, 1 << 20);

            if (patPacket != null) fs.Write(patPacket, 0, patPacket.Length);
            if (pmtPacket != null) fs.Write(pmtPacket, 0, pmtPacket.Length);

            foreach (var chunk in chunks)
            {
                long chunkEnd = chunk.StartOffset + chunk.Data.Length;
                if (chunkEnd <= clipStartOffset) continue;

                int skip = chunk.StartOffset >= clipStartOffset
                    ? 0
                    : (int)(clipStartOffset - chunk.StartOffset);
                if (skip >= chunk.Data.Length) continue;

                fs.Write(chunk.Data, skip, chunk.Data.Length - skip);
            }

            fs.Flush();
        }

        // ---------- 音频 ----------

        /// <summary>
        /// 打开 WASAPI 音频（环回 + 麦克风），识别不到的自动关闭并通知用户。
        /// 只打开设备，不启动泵——泵要等 FFmpeg stdin 就绪后再 Start。
        /// 支持独立于录制的即时重放音频配置。
        /// </summary>
        private WasapiAudioCapture? OpenAudioCapture(bool forReplay)
        {
            // 即时重放和正式录制使用独立的音频开关配置；
            // 即时重放那侧为 null（旧配置从未写过这一项）时跟随录制的设置，行为与旧版一致。
            bool wantSystem = _settings.SystemAudioEnabled;
            bool wantMic = _settings.MicrophoneEnabled;
            if (forReplay)
            {
                wantSystem = _settings.ReplaySystemAudioEnabled ?? wantSystem;
                wantMic = _settings.ReplayMicrophoneEnabled ?? wantMic;
            }

            if (!wantSystem && !wantMic)
            {
                string mode = forReplay ? "即时重放" : "录制";
                AppLog.Write($"Dronesee：{mode}的系统声音/麦克风均关闭，本次为纯视频");
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
                    string mode = forReplay ? "即时重放缓冲" : "正式录制";
                    AppLog.Write($"Dronesee：{mode}无可用音频设备，仅录制画面");
                    return null;
                }

                return capture;
            }
            catch (Exception ex)
            {
                string mode = forReplay ? "即时重放缓冲" : "正式录制";
                AppLog.Write($"Dronesee：{mode}音频捕捉打开失败（仅录画面）", ex);
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
        ///   1) 用调用方传入的那一项：正式录制传「分辨率」，即时重放传「重放分辨率」
        ///      （以前这里硬读 _settings.Resolution，所以「重放分辨率」设了等于没设）；
        ///      宽高都取偶数（H.264 要求）；
        ///   2) 若设置值比真实桌面还大（例如 1440p 屏设 4K），直接用桌面原生尺寸，
        ///      避免无意义的放大与画质损失 —— 抓屏是原生桌面，放大不会增加任何细节；
        ///   3) 抓屏本身始终是原生桌面（gdigrab / ddagrab 不加 -video_size），
        ///      所以这里不需要受桌面尺寸限制，它只是缩放目标。
        /// </summary>
        private (int width, int height) ResolveOutputSize(string? resolution)
        {
            int desktopW = GetSystemMetrics(SM_CXSCREEN);
            int desktopH = GetSystemMetrics(SM_CYSCREEN);
            if (desktopW < 2) desktopW = 1920;
            if (desktopH < 2) desktopH = 1080;

            desktopW -= desktopW % 2;
            desktopH -= desktopH % 2;

            if (!TryParseResolution(resolution, out int w, out int h) || w < 2 || h < 2)
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

        /// <summary>
        /// 码率体检（只写日志，不骚扰用户）：
        /// 分辨率 / 帧率提上来而码率不跟涨，画面会明显发糊 —— 这是
        /// 「分辨率明明调高了却看着还是不清楚」最常见的原因。
        /// 经验阈值：低于 0.08 bit/像素/帧就要提示（1440p120 大约需要 40Mbps）。
        /// </summary>
        private static void WarnIfBitrateLow(string? resolution, int fps, int bitrateKbps, string label)
        {
            try
            {
                if (!TryParseResolution(resolution, out int w, out int h) || w < 2 || h < 2 || fps <= 0)
                    return;

                // 按 2026-11-12 定的档位判断：低于这个分辨率的「低码率推荐」才提醒
                (int lowMbps, int highMbps) = BitrateTier(w, h);

                if (bitrateKbps >= lowMbps * 1000)
                    return;

                double perPixel = bitrateKbps * 1000.0 / (w * (double)h * fps);
                double perHourGb = lowMbps * 3600 / 8.0 / 1024.0;
                double perMatchGb = lowMbps * 1080 / 8.0 / 1024.0;   // 一局按 18 分钟算

                AppLog.Write($"Dronesee：{label}码率偏低 —— {w}x{h}@{fps} 时只有 {bitrateKbps / 1000.0:F1} Mbps"
                    + $"（约 {perPixel:F3} bit/像素/帧，画面会发糊）；这一档推荐"
                    + $"低码率 {lowMbps} Mbps（约 {perHourGb:F1} GB/小时、一局 18 分钟约 {perMatchGb:F1} GB）、高码率 {highMbps} Mbps。");
            }
            catch
            {
                // 体检本身绝不影响录制
            }
        }

        /// <summary>
        /// 码率分档（2026-11-12 用户给的推荐值，按分辨率）：
        ///   1080P：低 15 / 高 25 Mbps；2K：低 25 / 高 60 Mbps；4K：低 50 / 高 100 Mbps。
        /// </summary>
        private static (int Low, int High) BitrateTier(int w, int h)
        {
            if (w >= 3840 || h >= 2160) return (50, 100);
            if (w >= 2560 || h >= 1440) return (25, 60);
            return (15, 25);
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

            // 采集看门狗：缓冲长时间收不到新数据，说明“缓存已经不再刷新”，
            // 此时保存出来的必然全是旧内容。先提示，再有限次数地自动重启。
            if ((DateTime.UtcNow - _lastReplayDataUtc).TotalSeconds > ReplayStallSeconds)
                TryAutoRestartReplayBuffer($"已 {ReplayStallSeconds:F0} 秒没有采集到新数据");

            ShowStatus($"即时重放缓冲中 {_replayElapsed:F0} 秒 | 已缓存 {span:F0} 秒 / {_settings.MaxReplayDurationSeconds} 秒（{mb:F0} MB，不设内存上限）");
        }

        /// <summary>
        /// 采集异常时的自动恢复（有上限，避免反复重启刷屏）。
        /// 注意：正在“搭车录制”时不能重启——重启会直接带着录像一起断。
        /// </summary>
        private void TryAutoRestartReplayBuffer(string reason)
        {
            if (_recordingTap != null)
                return;                                  // 录像进行中：只记日志，不动管线

            // 需要重放缓冲的场景有两类，两类都要认：
            //   1) 用户把即时重放设成“常开”（InstantReplayEnabled）；
            //   2) 联动模式：Dronesee 开着“随游戏自动开缓冲”，且游戏正在运行
            //      （此时 InstantReplayEnabled 是关的）。
            // 旧实现只认第 1 类，于是第 2 类下采集被 Defender 之类的杀软结束后
            // 永远不会自愈——录制功能静默失效，连带“一局结束存片段”也存不下。
            var mainSettings = SettingsStore.Load();
            bool alwaysOn = mainSettings.Dronesee?.InstantReplayEnabled == true;

            bool gameRunning = false;
            if (mainSettings.Dronesee?.WatchGameProcess == true)
            {
                string[] names = { "r5apex", "r5apex_dx12" };
                if (mainSettings.Dronesee.GameProcessNames is { Count: > 0 } configured)
                {
                    names = new string[configured.Count];
                    for (int i = 0; i < configured.Count; i++)
                        names[i] = configured[i];
                }

                foreach (string n in names)
                {
                    try
                    {
                        var found = System.Diagnostics.Process.GetProcessesByName(n);
                        try
                        {
                            if (found.Length > 0)
                            {
                                gameRunning = true;
                                break;
                            }
                        }
                        finally
                        {
                            foreach (var p in found)
                                p.Dispose();
                        }
                    }
                    catch
                    {
                        // 枚举失败不影响其它判断
                    }
                }
            }

            bool followGame = mainSettings.Dronesee?.AutoReplayOnGameLaunch == true && gameRunning;

            if (!alwaysOn && !followGame)
                return;                                  // 既没开常开、也没有联动中的游戏：不自动拉回来                                  // 用户已经关掉了这个功能

            if (DateTime.UtcNow < _replayRestartCooldownUntil)
                return;

            if (_replayRestartAttempts >= MaxReplayAutoRestarts)
            {
                // 状态栏用户很可能看不到：这里补一条桌面通知，避免“静默失效”
                try { OnCaptureWarning?.Invoke($"即时重放采集已中断（{reason}），自动重启已达上限，请在设置里重新关闭再打开「即时重放」"); } catch { }
                ShowStatus($"即时重放采集已中断（{reason}），自动重启已达上限，" +
                           "请在设置里重新关闭再打开「即时重放」");
                return;
            }

            _replayRestartAttempts++;
            _replayRestartCooldownUntil = DateTime.UtcNow.AddSeconds(90);

            AppLog.Write($"Dronesee：即时重放采集异常（{reason}），第 {_replayRestartAttempts} 次自动重启");
            OnCaptureWarning?.Invoke($"即时重放采集异常（{reason}），已自动重启（第 {_replayRestartAttempts} 次）");

            try { StopReplayBuffer(); } catch { }
            try { StartReplayBuffer(); } catch { }
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
