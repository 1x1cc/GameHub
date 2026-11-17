using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Threading;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 备忘录仓库（对应原版 NotesRepository）。
    ///
    /// 与说明书一致的行为：
    ///   - 输入停止约 0.65 秒自动保存，底部给出保存状态；
    ///   - 切换笔记、退出程序前都会强制写入待保存内容；
    ///   - 保存失败不假装成功：保持 dirty 并提示“请先导出备忘录”。
    /// </summary>
    public sealed class NotesRepository : IDisposable
    {
        private readonly string _path;
        private readonly DispatcherTimer _timer;
        private bool _dirty;

        public NoteBook Book { get; private set; }

        public string Status { get; private set; } = "已保存";

        /// <summary>状态文字变化时通知界面（底部保存反馈）。</summary>
        public event Action? StatusChanged;

        public NotesRepository()
        {
            _path = Path.Combine(GlassBoardModule.DataFolder, "notes.json");
            Book = NoteFiles.Read(_path);

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(650) };
            _timer.Tick += (_, _) =>
            {
                _timer.Stop();
                Flush();
            };
        }

        private void SetStatus(string status)
        {
            Status = status;
            StatusChanged?.Invoke();
        }

        /// <summary>正文/标题被改动：打脏标记，650ms 后自动保存。</summary>
        public void Changed()
        {
            _dirty = true;
            Book.Selected.Updated = DateTime.UtcNow.Ticks;
            SetStatus("正在保存…");

            _timer.Stop();
            _timer.Start();
        }

        /// <summary>立即写盘。写失败时保持 dirty，避免用户以为已经存好。</summary>
        public bool Flush()
        {
            _timer.Stop();

            if (!_dirty && File.Exists(_path))
                return true;

            try
            {
                NoteFiles.Write(_path, Book);
                _dirty = false;
                SetStatus("已保存 · " + DateTime.Now.ToString("HH:mm"));
                return true;
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 备忘录：保存失败：{ex.GetType().Name}: {ex.Message}");
                SetStatus("保存失败，请先导出备忘录");
                _dirty = true;
                return false;
            }
        }

        public void Add()
        {
            Flush();

            var item = new NoteItem();
            Book.Items.Add(item);
            Book.SelectedId = item.Id;

            Changed();
            Flush();
        }

        public void Remove()
        {
            Book.Items.Remove(Book.Selected);
            Book.Normalize();

            Changed();
            Flush();
        }

        public void Select(string id)
        {
            Flush();

            if (Book.Items.Any(n => n.Id == id))
            {
                Book.SelectedId = id;
                Changed();
                Flush();
            }
        }

        /// <summary>导出当前笔记为 TXT（UTF-8 带 BOM，与说明书“导出 TXT”一致）。</summary>
        public void Export(string destination)
        {
            var selected = Book.Selected;
            File.WriteAllText(
                destination,
                selected.Title + Environment.NewLine + Environment.NewLine + selected.Body,
                new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }

        public void Reload()
        {
            _timer.Stop();
            _dirty = false;
            Book = NoteFiles.Read(_path);
            SetStatus("已恢复");
        }

        public void Dispose()
        {
            _timer.Stop();
            Flush();
        }
    }
}
