using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace GameHub.Utils
{
    /// <summary>
    /// 统一日志写入：%TEMP%\gamehub.log。
    ///
    /// 为什么单独抽出来：
    ///   1) 以前主窗口、托盘、自启动各自用 File.AppendAllText 写同一个文件，
    ///      多线程同时写会互相打断；这里加锁串行化；
    ///   2) 以前时间戳只有 HH:mm:ss，跨天排查"开机时到底有没有启动"根本对不上，
    ///      现在带日期，可以和事件查看器的开机/登录时间直接比对；
    ///   3) 显式写 UTF-8 BOM，避免在不同编辑器/命令行下看到乱码；
    ///   4) 每条日志带线程号与毫秒，排查"切设备后热键没反应"这类问题时，
    ///      能看出到底哪条线（UI 线程 / 后台线程）没有跑到。
    /// </summary>
    public static class AppLog
    {
        private static readonly object Gate = new();

        private static readonly UTF8Encoding Utf8WithBom = new(encoderShouldEmitUTF8Identifier: true);

        /// <summary>内存环形缓冲，供"报错/诊断报告"在不读文件时也能拿到最近现场。</summary>
        private const int RecentCapacity = 600;

        private static readonly LinkedList<string> Recent = new();

        /// <summary>日志文件完整路径。</summary>
        public static string FilePath { get; } = Path.Combine(Path.GetTempPath(), "gamehub.log");

        /// <summary>追加一条日志。任何写失败都静默忽略：日志本身绝不能影响程序运行。</summary>
        public static void Write(string message)
        {
            try
            {
                // 带线程号：排查"是不是跑到了 UI 线程 / 回调根本没执行"
                var line = $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff}] [T{Environment.CurrentManagedThreadId}] {message}";

                lock (Gate)
                {
                    Recent.AddLast(line);
                    while (Recent.Count > RecentCapacity)
                        Recent.RemoveFirst();

                    File.AppendAllText(FilePath, line + Environment.NewLine, Utf8WithBom);
                }
            }
            catch
            {
                // 忽略
            }
        }

        /// <summary>带格式化的追加。</summary>
        public static void Write(string format, params object?[] args)
        {
            try
            {
                Write(string.Format(format, args));
            }
            catch
            {
                // 格式化失败（占位符不匹配等）时退回到原始文本
                Write(format);
            }
        }

        /// <summary>记录一条带上下文的异常（含消息与堆栈）。</summary>
        public static void Write(string context, Exception ex)
        {
            Write($"{context} → {ex.GetType().FullName}: {ex.Message}{Environment.NewLine}{ex.StackTrace}");
        }

        /// <summary>取最近日志行的快照（供诊断报告使用）。</summary>
        public static IReadOnlyList<string> RecentLines()
        {
            lock (Gate)
            {
                var copy = new List<string>(Recent.Count);
                foreach (var item in Recent)
                    copy.Add(item);
                return copy;
            }
        }
    }
}