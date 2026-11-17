using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;
using GameHub.Utils;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 单条备忘（对应原版 NoteItem）。
    /// 标题上限 120 字，与说明书“标题最多约 120 个字符”一致。
    /// </summary>
    public sealed class NoteItem : INotifyPropertyChanged
    {
        public const int MaxTitleLength = 120;

        private string _title = "新备忘录";

        public string Id { get; set; } = Guid.NewGuid().ToString("N");

        public string Body { get; set; } = string.Empty;

        public long Updated { get; set; } = DateTime.UtcNow.Ticks;

        public string Title
        {
            get => _title;
            set
            {
                if (_title == value)
                    return;

                _title = value;
                OnPropertyChanged(nameof(Title));
                OnPropertyChanged(nameof(DisplayTitle));
            }
        }

        /// <summary>列表显示用：标题为空时回落为“未命名备忘录”。</summary>
        [JsonIgnore]
        public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "未命名备忘录" : Title;

        public event PropertyChangedEventHandler? PropertyChanged;

        private void OnPropertyChanged(string name)
            => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

        public void Normalize()
        {
            if (string.IsNullOrWhiteSpace(Id))
                Id = Guid.NewGuid().ToString("N");

            Title = string.IsNullOrWhiteSpace(Title) ? "未命名备忘录" : Title.Trim();
            if (Title.Length > MaxTitleLength)
                Title = Title[..MaxTitleLength];

            Body ??= string.Empty;
        }

        public override string ToString() => DisplayTitle;
    }

    /// <summary>整本备忘（对应原版 NoteBook）。</summary>
    public sealed class NoteBook
    {
        public List<NoteItem> Items { get; set; } = new();

        public string SelectedId { get; set; } = string.Empty;

        [JsonIgnore]
        public NoteItem Selected
        {
            get
            {
                var found = Items.FirstOrDefault(n => n.Id == SelectedId);
                if (found != null)
                    return found;

                // 原版这里直接 First()，数据不干净时会抛异常；这里退一步，保证界面不炸。
                if (Items.Count == 0)
                    Items.Add(new NoteItem { Title = "随手记" });

                SelectedId = Items[0].Id;
                return Items[0];
            }
        }

        public void Normalize()
        {
            Items = (Items ?? new List<NoteItem>()).Where(n => n != null).ToList();
            foreach (var item in Items)
                item.Normalize();

            Items = Items.GroupBy(n => n.Id).Select(g => g.First()).ToList();

            // 原版行为：整本为空时自动建一条“随手记”
            if (Items.Count == 0)
                Items.Add(new NoteItem { Title = "随手记" });

            if (!Items.Any(n => n.Id == SelectedId))
                SelectedId = Items[0].Id;
        }
    }

    /// <summary>
    /// 备忘录文件读写（对应原版 NoteFiles）。
    /// 写入先落 .tmp 再替换，并保留 .bak；读取失败时把损坏文件另存一份留证。
    /// </summary>
    public static class NoteFiles
    {
        private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

        public static NoteBook Read(string path)
        {
            foreach (var candidate in new[] { path, path + ".bak" })
            {
                try
                {
                    if (!File.Exists(candidate))
                        continue;

                    var book = JsonSerializer.Deserialize<NoteBook>(File.ReadAllText(candidate));
                    if (book == null)
                        throw new InvalidDataException("备忘录文件为空");

                    book.Normalize();
                    return book;
                }
                catch (Exception ex)
                {
                    AppLog.Write($"GlassBoard 备忘录：读取 {Path.GetFileName(candidate)} 失败：{ex.GetType().Name}: {ex.Message}");

                    try
                    {
                        File.Copy(candidate, candidate + ".damaged-" + DateTime.Now.ToString("yyyyMMddHHmmssfff"));
                    }
                    catch
                    {
                        // 留证失败不影响主流程
                    }
                }
            }

            var empty = new NoteBook();
            empty.Normalize();
            return empty;
        }

        public static void Write(string path, NoteBook book)
        {
            book.Normalize();

            var folder = Path.GetDirectoryName(Path.GetFullPath(path));
            if (!string.IsNullOrEmpty(folder))
                Directory.CreateDirectory(folder);

            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(book, Options));

            if (File.Exists(path))
                File.Replace(temp, path, path + ".bak");
            else
                File.Move(temp, path);
        }
    }
}
