using System;
using System.Windows;
using GameHub.Utils;

namespace GameHub.Views
{
    /// <summary>
    /// 诊断报告 / 报错窗口：生成一份包含系统信息、音频设备、热键设置和
    /// 最近日志的报告，支持复制、导出并打开文件所在目录。
    /// </summary>
    public partial class CrashReportWindow : Window
    {
        public CrashReportWindow()
        {
            InitializeComponent();
            RefreshReport();
        }

        private void RefreshReport()
        {
            try
            {
                ReportBox.Text = DiagnosticsReport.BuildReport();
                ReportBox.ScrollToEnd();
            }
            catch (Exception ex)
            {
                ReportBox.Text = $"生成报告失败：{ex.Message}";
            }
        }

        private void Refresh_Click(object sender, RoutedEventArgs e) => RefreshReport();

        private void Copy_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                Clipboard.SetText(ReportBox.Text);
                ShowInfo("已复制到剪贴板，可直接粘贴发给开发者。");
            }
            catch (Exception ex)
            {
                ShowInfo("复制失败：" + ex.Message);
            }
        }

        private void Export_Click(object sender, RoutedEventArgs e)
        {
            string path = DiagnosticsReport.SaveReport();
            ShowInfo("报告已导出：\n" + path);
            OpenContainingFolder(path);
        }

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        private void ShowInfo(string message) =>
            MessageBox.Show(message, "GameHub", MessageBoxButton.OK, MessageBoxImage.Information);

        private static void OpenContainingFolder(string path)
        {
            try
            {
                System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "explorer.exe",
                    Arguments = $"/select,\"{path}\"",
                    UseShellExecute = true
                });
            }
            catch
            {
                // 打不开资源管理器也不影响使用
            }
        }
    }
}