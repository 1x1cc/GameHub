using GameHub.Interfaces;
using GameHub.Utils;
using System;
using System.IO;
using System.Threading.Tasks;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 第 6 个卡片：GlassBoard 桌面美化工具。
    ///
    /// 参考 SENLIN-SAMA/win-.- 的 GlassBoard（该项目只发布了成品，
    /// 本模块按其反编译源码的 Win32 机制在 GameHub 内重写）：
    ///   - 卡片圆形滑块：模块总开关，状态跨次记忆；
    ///   - 面板内单独控制任务栏 / 桌面图标 / 任务栏自动隐藏；
    ///   - 桌面组件（时钟 / 备忘录 / 天气 / 音乐）与玻璃 Dock、应用大面板；
    ///   - 桌面控制球（控制中心）：单击展开面板、拖动换位、右键调样式与大小；
    ///   - 三层兜底恢复，桌面随时能还原回来。
    /// </summary>
    public sealed class GlassBoardModule : IModule, IModuleSwitch
    {
        /// <summary>模块配置（总开关状态、时钟样式与位置）。</summary>
        public static GlassBoardSettings Settings { get; }

        /// <summary>
        /// 卡片总开关状态。
        ///
        /// 已按需求改为“跨次记忆”：状态写进 settings.json，
        /// 下次启动 GameHub 会按上次的状态继续生效。
        /// 同时保留了两条安全线：关闭时立即还原，退出程序时也一定还原。
        /// </summary>
        private static bool _enabled;

        private static GlassBoardClockWindow? _clock;

        private static GlassBoardNotesWindow? _notes;

        private static GlassBoardWeatherWindow? _weather;

        private static GlassBoardMusicWindow? _music;

        private static Dock.GlassBoardDockWindow? _dock;

        private static Dock.GlassBoardBoardWindow? _board;

        /// <summary>桌面控制球（控制中心）窗口。</summary>
        private static GlassBoardControlOrbWindow? _orb;

        static GlassBoardModule()
        {
            Settings = GlassBoardStore.Load();
            _enabled = Settings.ModuleEnabled;
        }

        public string Name => "GlassBoard";

        public string Description => "桌面美化工具";

        public string IconPath => "pack://application:,,,/Assets/Icons/glassboard.png";

        public bool IsAutoStartEnabled => AutoStartManager.IsEnabled();

        /// <summary>模块总开关的当前状态（面板显示用）。</summary>
        public static bool ModuleEnabled => _enabled;

        /// <summary>桌面时钟是否正在显示。</summary>
        public static bool ClockVisible => _clock != null;

        /// <summary>桌面备忘录是否正在显示。</summary>
        public static bool NotesVisible => _notes != null;

        /// <summary>桌面天气是否正在显示。</summary>
        public static bool WeatherVisible => _weather != null;

        /// <summary>桌面音乐是否正在显示。</summary>
        public static bool MusicVisible => _music != null;


        /// <summary>玻璃 Dock 是否正在显示。</summary>
        public static bool DockVisible => _dock != null;

        /// <summary>玻璃 Dock 窗口实例（供设置窗口失焦时恢复焦点）。</summary>
        public static Dock.GlassBoardDockWindow? DockWindow => _dock;

        /// <summary>控制中心小球是否正在显示。</summary>
        public static bool ControlOrbVisible => _orb != null;

        /// <summary>应用大面板是否正在显示。</summary>
        public static bool BoardVisible => _board != null;

        /// <summary>GlassBoard 自己的状态文件目录（配置、恢复记录、自动隐藏记录）。</summary>
        public static string DataFolder => Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "GameHub",
            "GlassBoard");

        public bool IsEnabled => _enabled;

        /// <summary>
        /// 卡片上的圆形滑块调用这里：
        ///   开启 = 隐藏任务栏与桌面图标，让壁纸完整可见；
        ///   关闭 = 立即按记录撤销隐藏，把桌面还原回原样。
        /// 状态会被记住（下次启动继续生效）。
        /// </summary>
        public void SetEnabled(bool enabled)
        {
            if (enabled == _enabled)
                return;

            _enabled = enabled;
            Settings.ModuleEnabled = enabled;
            GlassBoardStore.Save(Settings);

            if (enabled)
            {
                // 开启：先接管桌面（隐藏任务栏/图标），再按记忆把组件原样恢复
                ApplyBeautify();
                RestoreWidgets();
            }
            else
            {
                // 关闭：整个模块停运——时钟 / 备忘录 / Dock / 大面板全部收起。
                // 注意“记忆”必须留着，下次开启才能按原样回来。
                StopWidgets();
                RevertBeautify();
            }
        }

        /// <summary>
        /// 面板点“一键恢复”时调用：只把总开关状态记为关闭并持久化，
        /// 桌面由面板自己负责还原（避免还原两次）。
        /// </summary>
        public static void MarkDisabledByRestore()
        {
            _enabled = false;
            Settings.ModuleEnabled = false;
            GlassBoardStore.Save(Settings);
        }

        /// <summary>
        /// 启动时按上次记录恢复现场：
        ///   1) 总开关上次是开 → 重新隐藏任务栏与桌面图标；
        ///   2) 时钟上次是显示 → 重新显示时钟（回到记录的位置）。
        ///
        /// 调用时机：主窗口已经执行完“兜底还原”之后，
        /// 这样顺序是“先清干净，再按记录重新生效”。
        /// </summary>
        public static void ApplyPersistedState()
        {
            // 首次运行先把开始菜单里的入口导进来，再按记录打开界面，
            // 这样大面板一打开就是满的（对应原版 Preferences.Imported 门闸）。
            EnsureFirstRunImport();

            // 开机自启动 = 跟随上次记忆：上次关着就整个模块不运行，桌面保持原样
            if (!_enabled)
            {
                AppLog.Write("GlassBoard：上次记忆为关闭，本次不启动任何桌面组件");
                return;
            }

            AppLog.Write("GlassBoard：按上次记录恢复桌面美化（总开关=开）");
            ApplyBeautify();

            if (Settings.ClockVisible)
            {
                AppLog.Write("GlassBoard：按上次记录恢复桌面时钟");
                ShowClock();
            }

            if (Settings.NotesVisible)
            {
                AppLog.Write("GlassBoard：按上次记录恢复桌面备忘录");
                ShowNotes();
            }

            if (Settings.WeatherVisible)
            {
                AppLog.Write("GlassBoard：按上次记录恢复桌面天气");
                ShowWeather();
            if (Settings.MusicVisible)
            {
                AppLog.Write("GlassBoard：按上次记录恢复桌面音乐");
                ShowMusic();
            }
            }

            if (Settings.DockVisible)
            {
                AppLog.Write("GlassBoard：按上次记录恢复玻璃 Dock");
                ShowDock();
            }

            if (Settings.BoardVisible)
            {
                AppLog.Write("GlassBoard：按上次记录打开应用大面板");
                ShowBoard();
            }

            if (Settings.ControlOrbVisible)
            {
                AppLog.Write("GlassBoard：按上次记录显示控制中心小球");
                ShowControlOrb();
            }
        }

        // ---------- 桌面时钟 ----------

        public static void ShowClock()
        {
            try
            {
                if (_clock != null)
                {
                    _clock.Activate();
                    return;
                }

                var clock = new GlassBoardClockWindow();
                clock.Closed += (_, _) => _clock = null;
                _clock = clock;
                clock.Show();

                Settings.ClockVisible = true;
                GlassBoardStore.Save(Settings);

                AppLog.Write($"GlassBoard：桌面时钟已显示（位置 {clock.Left:0},{clock.Top:0}）");
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：显示桌面时钟失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void HideClock()
        {
            try
            {
                var clock = _clock;
                _clock = null;
                clock?.Close();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：隐藏桌面时钟失败：{ex.GetType().Name}: {ex.Message}");
            }

            Settings.ClockVisible = false;
            GlassBoardStore.Save(Settings);
        }

        public static void ToggleClock()
        {
            if (ClockVisible)
                HideClock();
            else
                ShowClock();
        }

        /// <summary>时钟被拖动后记录新位置（跨次记忆）。</summary>
        public static void SaveClockPlacement(double left, double top)
        {
            Settings.ClockLeft = left;
            Settings.ClockTop = top;
            Settings.ClockPlaced = true;
            GlassBoardStore.Save(Settings);
        }

        /// <summary>面板改了时钟样式后立刻让时钟按新样式重画。</summary>
        public static void RefreshClockStyle()
        {
            try
            {
                _clock?.ReloadStyle();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：刷新时钟样式失败：{ex.GetType().Name}: {ex.Message}");
            }

            GlassBoardStore.Save(Settings);
        }

        // ---------- 桌面天气 ----------

        public static void ShowWeather()
        {
            try
            {
                if (_weather != null)
                {
                    _weather.Activate();
                    return;
                }

                var weather = new GlassBoardWeatherWindow();
                weather.Closed += (_, _) => _weather = null;
                _weather = weather;
                weather.Show();

                Settings.WeatherVisible = true;
                GlassBoardStore.Save(Settings);

                AppLog.Write($"GlassBoard：桌面天气已显示（位置 {weather.Left:0},{weather.Top:0}）");
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：显示桌面天气失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void HideWeather()
        {
            try
            {
                var weather = _weather;
                _weather = null;
                weather?.Close();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：隐藏桌面天气失败：{ex.GetType().Name}: {ex.Message}");
            }

            Settings.WeatherVisible = false;
            GlassBoardStore.Save(Settings);
        }

        public static void ToggleWeather()
        {
            if (WeatherVisible)
                HideWeather();
            else
                ShowWeather();
        }

        /// <summary>天气组件被拖动后记录新位置（跨次记忆）。</summary>
        public static void SaveWeatherPlacement(double left, double top)
        {
            Settings.WeatherLeft = left;
            Settings.WeatherTop = top;
            Settings.WeatherPlaced = true;
            GlassBoardStore.Save(Settings);
        }

        /// <summary>改了城市或湿度开关后，让天气组件立刻重新取数。</summary>
        public static void RefreshWeather()
        {
            try
            {
                _weather?.ReloadStyle();
                _weather?.QueueRefresh();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：刷新天气失败：{ex.GetType().Name}: {ex.Message}");
            }

            GlassBoardStore.Save(Settings);
        }

        // ---------- 桌面音乐 ----------

        public static void ShowMusic()
        {
            try
            {
                if (_music != null)
                {
                    _music.Activate();
                    return;
                }

                var music = new GlassBoardMusicWindow();
                music.Closed += (_, _) => _music = null;
                _music = music;
                music.Show();

                Settings.MusicVisible = true;
                GlassBoardStore.Save(Settings);

                AppLog.Write($"GlassBoard：桌面音乐已显示（位置 {music.Left:0},{music.Top:0}）");
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：显示桌面音乐失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void HideMusic()
        {
            try
            {
                var music = _music;
                _music = null;
                music?.Close();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：隐藏桌面音乐失败：{ex.GetType().Name}: {ex.Message}");
            }

            Settings.MusicVisible = false;
            GlassBoardStore.Save(Settings);
        }

        public static void ToggleMusic()
        {
            if (MusicVisible)
                HideMusic();
            else
                ShowMusic();
        }

        /// <summary>音乐组件被拖动后记录新位置（跨次记忆）。</summary>
        public static void SaveMusicPlacement(double left, double top)
        {
            Settings.MusicLeft = left;
            Settings.MusicTop = top;
            Settings.MusicPlaced = true;
            GlassBoardStore.Save(Settings);
        }

        /// <summary>改了控件开关或紧凑模式后，让音乐组件立刻重画。</summary>
        public static void RefreshMusic()
        {
            try
            {
                _music?.ReloadStyle();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：刷新音乐样式失败：{ex.GetType().Name}: {ex.Message}");
            }

            GlassBoardStore.Save(Settings);
        }

        /// <summary>设置界面的「播放器状态」：读不到音乐时用来定位是哪个环节。</summary>
        public static string MusicDiagnostics()
            => _music == null ? "音乐组件未打开（先点「音乐」把它显示出来）" : _music.Diagnostics();
        // ---------- 桌面备忘录 ----------


        public static void ShowNotes()
        {
            try
            {
                if (_notes != null)
                {
                    _notes.Activate();
                    return;
                }

                var notes = new GlassBoardNotesWindow();
                notes.Closed += (_, _) => _notes = null;
                _notes = notes;
                notes.Show();

                Settings.NotesVisible = true;
                GlassBoardStore.Save(Settings);

                AppLog.Write($"GlassBoard：桌面备忘录已显示（位置 {notes.Left:0},{notes.Top:0}）");
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：显示桌面备忘录失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void HideNotes()
        {
            try
            {
                var notes = _notes;
                _notes = null;
                notes?.Close();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：隐藏桌面备忘录失败：{ex.GetType().Name}: {ex.Message}");
            }

            Settings.NotesVisible = false;
            GlassBoardStore.Save(Settings);
        }

        public static void ToggleNotes()
        {
            if (NotesVisible)
                HideNotes();
            else
                ShowNotes();
        }

        /// <summary>让正在显示的备忘录立刻写盘（面板“立即保存”用）；没打开时返回 false。</summary>
        public static bool FlushNotes()
        {
            if (_notes == null)
                return false;

            return _notes.FlushPending();
        }

        // ---------- 应用大面板（所有快捷方式）----------

        /// <summary>
        /// 打开应用大面板：Dock 上 ▦ 按钮和面板里的入口都走这里。
        /// 注意与原版一致：大面板是“所有快捷方式”的启动台，不是模块设置界面。
        /// </summary>
        public static void ShowBoard()
        {
            try
            {
                if (_board != null)
                {
                    _board.Activate();
                    return;
                }

                var board = new Dock.GlassBoardBoardWindow();
                board.BoardRequestSettings = OpenSettingsFromDock;
                board.Closed += (_, _) =>
                {
                    _board = null;
                    Settings.BoardVisible = false;
                    GlassBoardStore.Save(Settings);
                };

                _board = board;
                board.Show();

                Settings.BoardVisible = true;
                GlassBoardStore.Save(Settings);

                AppLog.Write("GlassBoard：应用大面板已打开");
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：打开应用大面板失败：{ex.GetType().Name}: {ex.Message}");
                _board = null;
            }
        }

        public static void HideBoard()
        {
            try
            {
                var board = _board;
                _board = null;
                board?.Close();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：关闭应用大面板失败：{ex.GetType().Name}: {ex.Message}");
            }

            Settings.BoardVisible = false;
            GlassBoardStore.Save(Settings);
        }

        public static void ToggleBoard()
        {
            if (BoardVisible)
                HideBoard();
            else
                ShowBoard();
        }

        /// <summary>
        /// Dock 上 ▦ 的处理器：打开“所有快捷方式”的应用大面板（原版行为）。
        /// 同样先丢回消息队列再打开，绝不在点击里同步做阻塞操作。
        /// </summary>
        private static void OpenBoardFromDock()
        {
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null)
                    return;

                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // 作者要求：从 Dock 打开面板/设置时，绝不能把 GameHub 主窗口带到前台

                        // ▦ 是开关：面板开着就收起，没开就打开（原版行为）
                        if (_board != null)
                            HideBoard();
                        else
                            ShowBoard();
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"GlassBoard：Dock 打开大面板失败：{ex.GetType().Name}: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：调度打开大面板失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------- 桌面控制球（控制中心） ----------

        /// <summary>
        /// 显示控制中心小球（对应原版 ControlCenter.Enabled = true）。
        /// 小球是桌面上的控制中心入口：单击展开面板、拖动换位、右键调样式与大小。
        /// 窗口本身 ShowActivated = false，显示时不抢用户当前窗口的焦点。
        /// </summary>
        public static void ShowControlOrb()
        {
            try
            {
                if (_orb != null)
                    return;

                var orb = new GlassBoardControlOrbWindow();
                orb.Closed += (_, _) => _orb = null;
                _orb = orb;
                orb.Show();

                Settings.ControlOrbVisible = true;
                GlassBoardStore.Save(Settings);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：显示控制中心小球失败：{ex.GetType().Name}: {ex.Message}");
                _orb = null;
            }
        }

        public static void HideControlOrb()
        {
            try
            {
                var orb = _orb;
                _orb = null;
                orb?.Close();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：隐藏控制中心小球失败：{ex.GetType().Name}: {ex.Message}");
            }

            Settings.ControlOrbVisible = false;
            GlassBoardStore.Save(Settings);
        }

        public static void ToggleControlOrb()
        {
            if (ControlOrbVisible)
                HideControlOrb();
            else
                ShowControlOrb();
        }

        /// <summary>小球被拖动（或重置）后记录位置（跨次记忆）。</summary>
        public static void SaveControlOrbPlacement(double left, double top, bool placed = true)
        {
            var orb = Settings.ControlOrb;
            orb.Left = left;
            orb.Top = top;
            orb.Placed = placed;

            GlassBoardStore.Save(Settings);
        }

        /// <summary>
        /// 改完小球外观（样式 / 大小 / 图标 / 不透明度 / 锁定 / 总在最前）后调用：
        /// 先落盘，再让已经开着的小球立刻按新配置重画。
        /// </summary>
        public static void RefreshControlOrb()
        {
            Settings.ControlOrb.Normalize();
            GlassBoardStore.Save(Settings);

            try
            {
                _orb?.ReloadStyle();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：刷新控制中心小球失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// “回到默认位置”：忘掉记录，小球（若正显示）立刻回到主屏右下角。
        /// </summary>
        public static void ResetControlOrbPlacement()
        {
            try
            {
                Settings.ControlOrb.Placed = false;
                GlassBoardStore.Save(Settings);

                _orb?.ResetPlacement();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：重置控制中心小球位置失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        // ---------- 首次导入开始菜单 ----------

        /// <summary>
        /// 首次运行导入开始菜单（原版 Preferences.Imported 门闸）：
        /// 导入结果直接落盘，Dock / 大面板下次读盘就能看到。
        /// </summary>
        public static int EnsureFirstRunImport()
        {
            try
            {
                var model = Dock.DockStore.Load();

                if (model.Imported)
                {
                    // 已经导入过：对照表与分类规则会持续补齐（比如这次补的「讲述人 / 屏幕键盘」），
                    // 启动时重新对照一次——旧入口不必清空 dock.json 也能跟着变。
                    int relabeled = Dock.BoardImporter.Relabel(model);

                    if (relabeled > 0)
                    {
                        model.Normalize();
                        Dock.DockStore.Save(model);
                        AppLog.Write($"GlassBoard：启动时重新对照 {relabeled} 个入口的名称/分组");
                    }

                    return 0;
                }

                return ImportInto(model);
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：首次导入失败：{ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        /// <summary>手动重跑导入（装了新软件后用）。导入后如果大面板开着，重开一次才能看到新入口。</summary>
        public static int ImportStartMenuApps()
        {
            try
            {
                int added = ImportInto(Dock.DockStore.Load());

                if (_board != null)
                {
                    var stale = _board;
                    _board = null;
                    stale.Close();
                    ShowBoard();
                }

                return added;
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：导入开始菜单失败：{ex.GetType().Name}: {ex.Message}");
                return 0;
            }
        }

        private static int ImportInto(Dock.DockModel model)
        {
            int added = Dock.BoardImporter.Import(model, includeDesktop: true);
            Dock.DockStore.Save(model);
            return added;
        }

        // ---------- 设置窗口 ----------

        private static GlassBoardSettingsWindow? _settingsWindow;

        /// <summary>打开设置窗口（模态；卡片面板的 ⚙ 走这里）。</summary>
        /// <summary>
        /// 打开设置窗口（**非模态**）。
        /// 用户反馈“从 GameHub 打开设置后 Dock 点不动了”：根因是这里用的 ShowDialog 是模态的，
        /// 模态会禁用同进程的其它窗口（包括 Dock），点一下就像整个界面卡死。
        /// 改用 Show() 后，设置窗口开着也照常能点 Dock。
        /// </summary>
        public static void OpenSettings() => EnsureSettings(modal: false);

        /// <summary>保证只有一个设置窗口实例：已经开着就把它叫到前面。</summary>
        private static void EnsureSettings(bool modal)
        {
            try
            {
                if (_settingsWindow != null)
                {
                    if (_settingsWindow.WindowState == System.Windows.WindowState.Minimized)
                        _settingsWindow.WindowState = System.Windows.WindowState.Normal;

                    _settingsWindow.Activate();
                    return;
                }

                var window = new GlassBoardSettingsWindow();

                // 鎵撳紑璁剧疆绐楀彛鍓嶏紝鍏堣褰曞皬鐞冪姸鎬佸苟闅愯棌瀹冿紙閬垮厤鐢ㄦ埛鏃犳硶鍐嶇偣鍑诲皬鐞冿級
                bool orbWasVisible = ControlOrbVisible;
                if (orbWasVisible)
                    HideControlOrb();

                // 只在主窗口确实可见时才挂属主：
                // WPF 里激活一个子窗口会把它的属主窗口一并带到前台，
                // 主窗口此时是隐藏的，挂上去就会出现"点 Dock 却弹出 GameHub"。

                window.Closed += (_, _) =>
                {
                    _settingsWindow = null;
                    // 璁剧疆绐楀彛鍏抽棴鍚庯紝鎭㈠灏忕悆锛堝鏋滀箣鍓嶆槸鍙鐨勶級
                    if (orbWasVisible)
                        ShowControlOrb();
                };
                _settingsWindow = window;

                if (modal)
                    window.ShowDialog();
                else
                    window.Show();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：打开设置失败：{ex.GetType().Name}: {ex.Message}");
                _settingsWindow = null;
            }
        }

        /// <summary>Dock / 大面板上的 ⚙：先把调用丢回消息队列，再以非模态打开，绝不能挡住 Dock。</summary>
        private static void OpenSettingsFromDock()
        {
            try
            {
                var dispatcher = System.Windows.Application.Current?.Dispatcher;
                if (dispatcher == null)
                    return;

                dispatcher.BeginInvoke(new Action(() =>
                {
                    try
                    {
                        // 作者要求：从 Dock 打开面板/设置时，绝不能把 GameHub 主窗口带到前台

                        // ⚙ 是开关：设置窗口已经开着就关掉，没开才打开（用户要求：再点一次关闭）
                        if (_settingsWindow != null)
                        {
                            _settingsWindow.Close();
                            return;
                        }

                        EnsureSettings(modal: false);
                    }
                    catch (Exception ex)
                    {
                        AppLog.Write($"GlassBoard：设置窗口打开失败：{ex.GetType().Name}: {ex.Message}");
                    }
                }));
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：调度打开设置失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 设置改完后调用：把已经打开的 Dock / 大面板重建一次。
        /// 这两个窗口各自在构造时读盘并缓存模型，不重建就套不上新配置。
        /// </summary>
        public static void ApplyDockAndBoardSettings()
        {
            try
            {
                if (_dock != null)
                {
                    var staleDock = _dock;
                    _dock = null;
                    staleDock.Close();
                    ShowDock();
                }

                if (_board != null)
                {
                    var staleBoard = _board;
                    _board = null;
                    staleBoard.Close();
                    ShowBoard();
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：套用设置失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>重置 Dock 与大面板的布局（位置、尺寸回到默认，按对齐方式重新摆放）。</summary>
        public static void ResetLayout()
        {
            try
            {
                var model = Dock.DockStore.Load();

                model.DockPlaced = false;
                model.DockLeft = 0;
                model.DockTop = 0;
                model.BoardPlaced = false;
                model.BoardLeft = 0;
                model.BoardTop = 0;
                model.BoardWidth = 0;
                model.BoardHeight = 0;

                Dock.DockStore.Save(model);
                ApplyDockAndBoardSettings();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：重置布局失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void NotifySettingsClosed() => _settingsWindow = null;

        // ---------- 玻璃 Dock ----------

        public static void ShowDock()
        {
            try
            {
                if (_dock != null)
                {
                    _dock.Refresh();
                    _dock.Activate();
                    return;
                }

                var dock = new Dock.GlassBoardDockWindow(OpenBoardFromDock, OpenSettingsFromDock, ToggleGameHubFromDock);
                dock.Closed += (_, _) => _dock = null;
                _dock = dock;
                dock.Show();

                Settings.DockVisible = true;
                GlassBoardStore.Save(Settings);

                AppLog.Write($"GlassBoard：玻璃 Dock 已显示（位置 {dock.Left:0},{dock.Top:0}）");
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：显示玻璃 Dock 失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        public static void HideDock()
        {
            try
            {
                var dock = _dock;
                _dock = null;
                dock?.Close();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：隐藏玻璃 Dock 失败：{ex.GetType().Name}: {ex.Message}");
            }

            Settings.DockVisible = false;
            GlassBoardStore.Save(Settings);
        }

        public static void ToggleDock()
        {
            if (DockVisible)
                HideDock();
            else
                ShowDock();
        }

        /// <summary>面板上的“添加软件到 Dock”：让 Dock 弹出文件选择。</summary>
        public static bool AddDockFiles()
        {
            if (_dock == null)
                return false;

            _dock.AddFiles();
            return true;
        }

        // ---------- 桌面净化 ----------

        /// <summary>
        /// 按记忆恢复桌面组件（时钟 / 备忘录 / Dock / 大面板）。
        /// 总开关打开、程序启动都走这里，保证“记忆”只有一处解释。
        /// </summary>
        private static void RestoreWidgets()
        {
            try
            {
                if (Settings.ClockVisible)
                    ShowClock();

                if (Settings.NotesVisible)
                    ShowNotes();

                if (Settings.DockVisible)
                    ShowDock();

                if (Settings.BoardVisible)
                    ShowBoard();

                if (Settings.ControlOrbVisible)
                    ShowControlOrb();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：按记忆恢复组件失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 整体停运：收起全部桌面组件，但**保留**“上次开着哪些组件”的记忆。
        ///
        /// 必须先存后恢复这组标志位：HideXxx() 会把对应记忆位清成 false，
        /// 直接调就会把用户的记忆抹掉，下次打开总开关就什么都回不来了。
        /// </summary>
        private static void StopWidgets()
        {
            bool clock = Settings.ClockVisible;
            bool notes = Settings.NotesVisible;
            bool weather = Settings.WeatherVisible;
            bool music = Settings.MusicVisible;
            bool dock = Settings.DockVisible;
            bool board = Settings.BoardVisible;
            bool orb = Settings.ControlOrbVisible;

            try
            {
                HideClock();
                HideNotes();
                HideWeather();
                HideMusic();
                HideDock();
                HideBoard();
                HideControlOrb();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：停用组件失败：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                Settings.ClockVisible = clock;
                Settings.NotesVisible = notes;
                Settings.WeatherVisible = weather;
                Settings.MusicVisible = music;
                Settings.DockVisible = dock;
                Settings.BoardVisible = board;
                Settings.ControlOrbVisible = orb;
                GlassBoardStore.Save(Settings);
            }
        }

        /// <summary>
        /// Dock 上的「唤出 / 最小化 GameHub」：可见就最小化，最小化或隐藏就唤出到前台。
        /// 只操作自己的主窗口，不碰别人的窗口（与全项目一致）。
        /// </summary>
        private static void ToggleGameHubFromDock()
        {
            try
            {
                var app = System.Windows.Application.Current;

                if (app?.MainWindow is not Views.MainWindow main)
                {
                    AppLog.Write("GlassBoard Dock：没找到 GameHub 主窗口");
                    return;
                }

                if (main.IsVisible && main.WindowState != System.Windows.WindowState.Minimized)
                {
                    main.WindowState = System.Windows.WindowState.Minimized;
                    return;
                }

                if (!main.IsVisible)
                    main.Show();

                if (main.WindowState == System.Windows.WindowState.Minimized)
                    main.WindowState = System.Windows.WindowState.Normal;

                main.Activate();
                main.Topmost = true;
                main.Topmost = false;
                main.Focus();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：唤出 GameHub 失败 {ex.GetType().Name}: {ex.Message}");
            }
        }

        /// <summary>
        /// 桌面美化应用（2026-11-12 用户要求「记住我的设置」）：
        /// 不再无条件隐藏任务栏 / 桌面图标 —— 只按他最后一次的选择重放。
        /// 想隐藏就点控制中心的「隐藏任务栏 / 隐藏桌面图标」，那一次点击会被记住；
        /// 手动把任务栏显示出来后，Maintain() 也会把这次显示记成最新选择。
        /// </summary>
        private static void ApplyBeautify()
        {
            try
            {
                AppLog.Write("GlassBoard：桌面美化按上次的选择应用（不再无条件隐藏）");
                DesktopVisibility.ReplayDesired();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：应用桌面美化失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static void RevertBeautify()
        {
            try
            {
                AppLog.Write("GlassBoard：模块关闭，按记录还原任务栏与桌面图标");
                DesktopVisibility.RestoreOriginal();
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard：关闭时还原桌面失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        public Task<string> GetStatusTextAsync()
        {
            return Task.Run(() =>
            {
                string widgets = string.Empty;

                // 总开关关着 = 整个模块停运：这些“记忆”不是正在运行，别混在一起显示
                if (_enabled)
                {
                    if (Settings.ClockVisible) widgets += " · 时钟开";
                    if (Settings.NotesVisible) widgets += " · 备忘录开";
                    if (Settings.DockVisible) widgets += " · Dock开";
                    if (Settings.BoardVisible) widgets += " · 大面板开";
                    if (Settings.ControlOrbVisible) widgets += " · 控制球开";
                }

                if (!_enabled)
                    return "已关闭 · 桌面保持原样" + widgets;

                bool? taskbar = DesktopVisibility.IsHidden(taskbar: true);
                bool? icons = DesktopVisibility.IsHidden(taskbar: false);

                if (taskbar == null && icons == null)
                    return "未检测到桌面窗口" + widgets;

                if (taskbar == true && icons == true)
                    return "已开启 · 任务栏与桌面图标已隐藏" + widgets;

                if (taskbar == true)
                    return "已开启 · 任务栏已隐藏" + widgets;

                if (icons == true)
                    return "已开启 · 桌面图标已隐藏" + widgets;

                return "已开启 · 桌面已是整洁状态" + widgets;
            });
        }

        /// <summary>
        /// 卡片上的入口。
        ///
        /// GlassBoard 的设置只有一处：Dock 上的 ⚙（GlassBoardSettingsWindow）。
        /// 这里直接转到设置窗口。
        /// </summary>
        public void OpenPanel() => OpenSettings();

    }
}
