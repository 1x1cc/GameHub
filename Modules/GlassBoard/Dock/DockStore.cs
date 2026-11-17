using System;
using System.IO;
using System.Text.Json;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard.Dock
{
    /// <summary>
    /// Dock 配置读写（%LOCALAPPDATA%\GameHub\GlassBoard\dock.json）。
    /// 与模块设置分开存：Dock 的入口列表会随着日常使用不断变，单独一份更好维护，
    /// 也不容易因为一次写坏而影响总开关等关键设置。
    ///
    /// 【进程内共享同一份 DockModel】—— 这是修“Dock 上的图标没办法记忆”的关键。
    ///
    /// 以前 Load() 每次都给一份**新的** DockModel：Dock 窗口、大面板、设置窗口各拿一份。
    /// 谁都能把整份模型写回磁盘，于是：
    ///   1) 在大面板里把图标拖到 Dock 上固定（写盘 Pinned=true）——
    ///      日志里能看到「从大面板拖入固定「UU加速器」（新增 1 个）」；
    ///   2) 之后大面板（它那份模型还是固定之前的旧快照）因为保存位置、调透明度、
    ///      重排等原因再次写盘，就把刚固定的图标整份覆盖回 Pinned=false。
    /// 用户看到的现象就是“固定好的图标下次就没了”。
    ///
    /// 现在所有窗口拿到的都是同一个实例，改的就是同一份数据，谁写盘都不会覆盖别人。
    /// 需要强制刷新（比如外部动过文件）时用 Reload()。
    /// </summary>
    public static class DockStore
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        private static readonly object Gate = new();

        /// <summary>进程内唯一的一份 Dock 数据。</summary>
        private static DockModel? _shared;

        private static string FilePath => Path.Combine(GlassBoardModule.DataFolder, "dock.json");

        /// <summary>读取：首次调用读盘，之后一律返回同一个实例。</summary>
        public static DockModel Load()
        {
            lock (Gate)
            {
                return _shared ??= ReadFromDisk();
            }
        }

        /// <summary>强制重新读盘，并替换共享实例。</summary>
        public static DockModel Reload()
        {
            lock (Gate)
            {
                _shared = ReadFromDisk();
                return _shared;
            }
        }

        private static DockModel ReadFromDisk()
        {
            try
            {
                if (!File.Exists(FilePath))
                    return new DockModel();

                var model = JsonSerializer.Deserialize<DockModel>(File.ReadAllText(FilePath));
                model ??= new DockModel();
                model.Normalize();
                return model;
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：读取配置失败，改用默认值：{ex.GetType().Name}: {ex.Message}");
                return new DockModel();
            }
        }

        public static void Save(DockModel model)
        {
            try
            {
                if (model == null)
                    return;

                lock (Gate)
                {
                    // 正常情况下传进来的就是共享实例；万一有人传了别的实例，
                    // 就以它为准并接管为共享实例，避免又出现“两份数据”。
                    if (!ReferenceEquals(model, _shared))
                        _shared = model;

                    model.Normalize();
                    Directory.CreateDirectory(GlassBoardModule.DataFolder);
                    File.WriteAllText(FilePath, JsonSerializer.Serialize(model, Options));
                }
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard Dock：保存配置失败：{ex.GetType().Name}: {ex.Message}");
            }
        }
    }
}
