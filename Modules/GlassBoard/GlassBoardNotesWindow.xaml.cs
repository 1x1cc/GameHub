using System;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using GameHub.Utils;
using Microsoft.Win32;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 桌面备忘录组件（对应原版备忘录组件）。
    ///
    /// 行为对齐说明书第 19 章：
    ///   - 新建入口建笔记，下拉列表切换笔记；
    ///   - 标题最多约 120 个字符；
    ///   - 输入停止约 0.65 秒自动保存，底部显示保存状态；
    ///   - “清单”按钮插入“☐ ”文本标记（不是任务管理系统）；
    ///   - “导出”另存为 TXT；删除需要确认。
    /// </summary>
    public partial class GlassBoardNotesWindow : GlassBoardWidgetWindow
    {
        private readonly NotesRepository _notes = new();
        private bool _loading;

        public GlassBoardNotesWindow()
        {
            InitializeComponent();

            NotePicker.ItemsSource = _notes.Book.Items;

            // 保存状态由仓库推送（“正在保存…”→“已保存 · HH:mm”）
            _notes.StatusChanged += () => StatusText.Text = _notes.Status;

            LoadEditor();
        }

        /// <summary>立即写入待保存内容（面板上的“立即保存”按钮调用）。</summary>
        public bool FlushPending() => _notes.Flush();

        protected override void RestorePlacement()
        {
            var settings = GlassBoardModule.Settings;
            PlaceOnScreen(settings.NotesLeft, settings.NotesTop, settings.NotesPlaced, defaultTop: 420);
        }

        protected override void PersistPlacement()
        {
            var settings = GlassBoardModule.Settings;
            settings.NotesLeft = Left;
            settings.NotesTop = Top;
            settings.NotesPlaced = true;
            GlassBoardStore.Save(settings);
        }

        /// <summary>把当前选中的笔记读进编辑框（期间不触发“已改动”，否则会自己写自己）。</summary>
        private void LoadEditor()
        {
            _loading = true;
            try
            {
                var selected = _notes.Book.Selected;

                NotePicker.SelectedItem = selected;
                TitleBox.Text = selected.Title;
                BodyBox.Text = selected.Body;
                StatusText.Text = _notes.Status;
            }
            finally
            {
                _loading = false;
            }
        }

        private void DragStrip_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
            => DragWidget(sender, e);

        private void NotePicker_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_loading)
                return;

            if (NotePicker.SelectedItem is NoteItem item)
            {
                _notes.Select(item.Id);
                LoadEditor();
            }
        }

        private void TitleBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading)
                return;

            _notes.Book.Selected.Title = TitleBox.Text;
            _notes.Changed();
        }

        private void BodyBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            if (_loading)
                return;

            _notes.Book.Selected.Body = BodyBox.Text;
            _notes.Changed();
        }

        private void Add_Click(object sender, RoutedEventArgs e)
        {
            _notes.Add();
            RefreshList();
            LoadEditor();
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            var target = _notes.Book.Selected;

            var answer = MessageBox.Show(
                this,
                "确定删除备忘录“" + target.DisplayTitle + "”吗？\n"
                + "删除后没有回收站，重要内容建议先导出。",
                "删除备忘录",
                MessageBoxButton.OKCancel,
                MessageBoxImage.Warning);

            if (answer != MessageBoxResult.OK)
                return;

            _notes.Remove();
            RefreshList();
            LoadEditor();
        }

        private void RefreshList()
        {
            // Items 是普通 List，增删后直接重设数据源即可
            NotePicker.ItemsSource = null;
            NotePicker.ItemsSource = _notes.Book.Items;
        }

        /// <summary>
        /// 清单按钮：在光标处插入“☐ ”标记。
        /// 说明书明确它只是文本标记，不是带状态/截止日期的任务系统。
        /// </summary>
        private void Checklist_Click(object sender, RoutedEventArgs e)
        {
            int caret = BodyBox.CaretIndex;

            BodyBox.Text = BodyBox.Text.Insert(caret, "☐ ");
            BodyBox.CaretIndex = caret + 2;
            BodyBox.Focus();
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            var selected = _notes.Book.Selected;

            var dialog = new SaveFileDialog
            {
                Title = "导出备忘录",
                FileName = MakeSafeFileName(selected.DisplayTitle) + ".txt",
                Filter = "文本文件 (*.txt)|*.txt|所有文件 (*.*)|*.*",
                DefaultExt = ".txt"
            };

            if (dialog.ShowDialog(this) != true)
                return;

            try
            {
                _notes.Export(dialog.FileName);
                StatusText.Text = "已导出：" + Path.GetFileName(dialog.FileName);
                AppLog.Write($"GlassBoard 备忘录：已导出到 {dialog.FileName}");
            }
            catch (Exception ex)
            {
                StatusText.Text = "导出失败：" + ex.Message;
                AppLog.Write($"GlassBoard 备忘录：导出失败：{ex.GetType().Name}: {ex.Message}");
            }
        }

        private static string MakeSafeFileName(string name)
        {
            foreach (var bad in Path.GetInvalidFileNameChars())
                name = name.Replace(bad, '_');

            return string.IsNullOrWhiteSpace(name) ? "备忘录" : name;
        }

        protected override void OnClosed(EventArgs e)
        {
            // 关组件前把待保存内容写掉：说明书要求“退出会尝试写入待保存内容”
            _notes.Dispose();

            base.OnClosed(e);
        }
    }
}
