using GameHub.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows.Media.Imaging;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 在线封面来源（按「曲名 + 歌手」查询）。
    ///
    /// 【为什么需要它 —— 本机实测】
    ///   网易云音乐**不向系统注册媒体会话**：SMTC 会话数 = 0，NPSMLib 同样为空；
    ///   它的本地目录里也没有任何图片缓存（只有 .idx / .info / .uc 音频缓存）。
    ///   原版 GlassBoard 的封面只有两条路 —— GSMTC 缩略图 / NPSMLib 缩略图 ——
    ///   都要求播放器先注册会话，所以原版在这台机器上同样没有封面。
    ///
    /// 【实际可用的接口】
    ///   旧的 https://music.163.com/api/search/get/web **已经不返回封面字段**（实测 picUrl 全为空）；
    ///   改用 https://music.163.com/api/cloudsearch/pc（POST）：
    ///     · 返回体里 "al":{...,"picUrl":"http://p1.music.126.net/xxx==/109951163597639903.jpg"}
    ///     · 实测该图可正常下载（152147 字节，Content-Type: image/jpg）。
    ///   请求必须带 User-Agent 与 Referer=https://music.163.com/，否则拿不到结果。
    ///
    /// 【保守策略】
    ///   只在返回体里确实出现我们查的曲名（或歌手）时才采用封面，
    ///   否则宁可不要 —— 贴一张别人的封面比没有封面更糟。
    ///   结果只在内存里缓存；每换一首歌最多一次网络请求，任何失败都静默回退到占位音符。
    /// </summary>
    public static class OnlineCoverSource
    {
        private const string Endpoint = "https://music.163.com/api/cloudsearch/pc";

        private static readonly HttpClient Http = CreateClient();

        private static readonly Dictionary<string, BitmapImage?> Cache = new(StringComparer.Ordinal);

        private static readonly object Gate = new();

        private static HttpClient CreateClient()
        {
            var client = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

            client.DefaultRequestHeaders.TryAddWithoutValidation(
                "User-Agent",
                "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36");

            client.DefaultRequestHeaders.TryAddWithoutValidation("Referer", "https://music.163.com/");

            return client;
        }

        public static void Clear()
        {
            lock (Gate)
                Cache.Clear();
        }

        /// <summary>查封面；查不到返回 null（调用方显示占位音符）。</summary>
        public static async Task<BitmapImage?> FindAsync(string? title, string? artist)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(title))
                    return null;

                string track = title!;
                string who = artist ?? string.Empty;
                string key = (track + "|" + who).Trim();

                lock (Gate)
                {
                    if (Cache.TryGetValue(key, out var hit))
                        return hit;
                }

                string body = await SearchAsync(track, who);

                if (string.IsNullOrWhiteSpace(body))
                    return null;   // 网络没通/没返回：不缓存，下次还能再试

                BitmapImage? image = null;

                if (IsPlausibleMatch(body, track, who))
                    image = await FirstCoverAsync(body, track);

                lock (Gate)
                    Cache[key] = image;

                return image;
            }
            catch (Exception ex)
            {
                AppLog.Write($"GlassBoard 音乐：在线封面查询失败：{ex.GetType().Name}: {ex.Message}");
                return null;
            }
        }

        private static async Task<string> SearchAsync(string title, string artist)
        {
            string query = string.IsNullOrWhiteSpace(artist) ? title : title + " " + artist;

            using var form = new FormUrlEncodedContent(new[]
            {
                new KeyValuePair<string, string>("s", query),
                new KeyValuePair<string, string>("type", "1"),
                new KeyValuePair<string, string>("limit", "5"),
                new KeyValuePair<string, string>("offset", "0"),
            });

            using var response = await Http.PostAsync(Endpoint, form);

            if (!response.IsSuccessStatusCode)
                return string.Empty;

            return await response.Content.ReadAsStringAsync();
        }

        /// <summary>返回体里出现曲名（或歌手）才认为搜到了对的东西。</summary>
        private static bool IsPlausibleMatch(string body, string title, string artist)
        {
            if (body.Contains(title, StringComparison.OrdinalIgnoreCase))
                return true;

            return !string.IsNullOrWhiteSpace(artist)
                   && body.Contains(artist, StringComparison.OrdinalIgnoreCase);
        }

        private static async Task<BitmapImage?> FirstCoverAsync(string body, string title)
        {
            foreach (Match match in Regex.Matches(body, "\"picUrl\"\\s*:\\s*\"([^\"]+)\""))
            {
                string url = Unescape(match.Groups[1].Value);

                if (string.IsNullOrWhiteSpace(url))
                    continue;

                var image = await DownloadAsync(url);

                if (image != null)
                {
                    AppLog.Write($"GlassBoard 音乐：在线封面已获取（{title}）：{url}");
                    return image;
                }
            }

            AppLog.Write($"GlassBoard 音乐：在线封面未取到（{title}）");
            return null;
        }

        private static async Task<BitmapImage?> DownloadAsync(string url)
        {
            try
            {
                using var response = await Http.GetAsync(url);

                if (!response.IsSuccessStatusCode)
                    return null;

                byte[] bytes = await response.Content.ReadAsByteArrayAsync();

                if (bytes.Length == 0)
                    return null;

                using var stream = new MemoryStream(bytes);

                var image = new BitmapImage();
                image.BeginInit();
                image.CacheOption = BitmapCacheOption.OnLoad;
                image.StreamSource = stream;
                image.EndInit();
                image.Freeze();

                return image;
            }
            catch
            {
                return null;
            }
        }

        private static string Unescape(string value)
            => value.Replace("\\/", "/").Replace("\\u0026", "&");
    }
}
