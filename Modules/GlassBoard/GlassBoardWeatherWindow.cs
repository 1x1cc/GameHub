using GameHub.Utils;
using System;
using System.Globalization;
using System.Net.Http;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Threading;

namespace GameHub.Modules.GlassBoard
{
    /// <summary>
    /// 桌面天气组件（对应原版 GlassBoard 的 Widgets.Weather）。
    ///
    /// 数据源用 Open-Meteo —— 免注册、无密钥，而且支持中文城市名反查坐标：
    ///   1) 城市 -> 坐标：geocoding-api.open-meteo.com/v1/search?name=城市&count=1&language=zh
    ///   2) 坐标 -> 天气：api.open-meteo.com/v1/forecast
    ///        ?current=temperature_2m,weather_code,relative_humidity_2m
    ///        &daily=temperature_2m_max,temperature_2m_min&timezone=auto&forecast_days=1
    /// 上面两组字段名都用真实请求逐项核对过，不是照着文档猜的。
    ///
    /// 行为对齐原版：拖动换位并跨次记忆、右键菜单（刷新 / 湿度 / 隐藏）、
    /// 每 15 分钟自动刷新（不做常连）；取数失败只在卡片上写一行提示，不弹框。
    /// </summary>
    public sealed class GlassBoardWeatherWindow : GlassBoardWidgetWindow
    {
        private const double Pad = 14.0;

        private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(12) };

        private readonly Grid _root = new();
        private readonly Border _card = new();
        private readonly TextBlock _city = new();
        private readonly TextBlock _temp = new();
        private readonly TextBlock _range = new();
        private readonly TextBlock _humid = new();
        private readonly TextBlock _status = new();

        private readonly DispatcherTimer _timer;
        private bool _busy;

        public GlassBoardWeatherWindow()
        {
            Width = 236;
            Height = 172;
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");

            _card.CornerRadius = new CornerRadius(18);
            _card.Padding = new Thickness(16, 14, 16, 14);
            _card.Margin = new Thickness(Pad);
            _card.BorderThickness = new Thickness(1);
            _card.Effect = new DropShadowEffect { BlurRadius = 20, ShadowDepth = 3, Opacity = 0.35, Color = Colors.Black };

            _city.FontSize = 13.0;
            _city.FontWeight = FontWeights.SemiBold;
            _city.TextTrimming = TextTrimming.CharacterEllipsis;
            _city.MaxWidth = 190;

            _temp.FontSize = 40.0;
            _temp.FontWeight = FontWeights.SemiBold;
            _temp.Margin = new Thickness(0, 4, 0, 0);

            _range.FontSize = 12.0;

            _humid.FontSize = 12.0;
            _humid.Margin = new Thickness(0, 2, 0, 0);

            _status.FontSize = 11.0;
            _status.Margin = new Thickness(0, 6, 0, 0);
            _status.TextWrapping = TextWrapping.Wrap;

            var stack = new StackPanel();
            stack.Children.Add(_city);
            stack.Children.Add(_temp);
            stack.Children.Add(_range);
            stack.Children.Add(_humid);
            stack.Children.Add(_status);
            _card.Child = stack;

            _root.Children.Add(_card);
            Content = _root;

            _card.MouseLeftButtonDown += DragWidget;
            _root.MouseRightButtonUp += ShowMenu;

            _timer = new DispatcherTimer { Interval = TimeSpan.FromMinutes(15) };
            _timer.Tick += (_, _) => QueueRefresh();

            Loaded += (_, _) =>
            {
                ReloadStyle();
                _timer.Start();
                QueueRefresh();
            };

            Closed += (_, _) => _timer.Stop();

            ReloadStyle();
        }

        protected override void RestorePlacement()
        {
            var s = GlassBoardModule.Settings;
            PlaceOnScreen(s.WeatherLeft, s.WeatherTop, s.WeatherPlaced, 220);
        }

        protected override void PersistPlacement()
            => GlassBoardModule.SaveWeatherPlacement(Left, Top);

        /// <summary>按主题重画外观（模块改了设置后调用）。</summary>
        public void ReloadStyle()
        {
            _card.Background = new SolidColorBrush(Color.FromArgb(0xB3, 0x18, 0x1A, 0x20));
            _card.BorderBrush = GlassBoardTheme.Line;

            _city.Foreground = GlassBoardTheme.Ink;
            _temp.Foreground = GlassBoardTheme.Ink;
            _range.Foreground = GlassBoardTheme.Muted;
            _humid.Foreground = GlassBoardTheme.Muted;
            _status.Foreground = GlassBoardTheme.Accent;

            _humid.Visibility = GlassBoardModule.Settings.WeatherShowHumidity
                ? Visibility.Visible
                : Visibility.Collapsed;

            if (string.IsNullOrWhiteSpace(_status.Text))
                _status.Text = "正在获取天气…";
        }

        public void QueueRefresh() => _ = RefreshAsync();

        /// <summary>取一次天气：城市 -> 坐标 -> 当前天气 + 今日高低温。</summary>
        public async Task RefreshAsync()
        {
            if (_busy)
                return;

            _busy = true;

            try
            {
                string city = (GlassBoardModule.Settings.WeatherCity ?? string.Empty).Trim();
                if (city.Length == 0)
                    city = "北京";

                var (name, lat, lon) = await GeocodeAsync(city);
                var (temp, code, humidity, max, min) = await ForecastAsync(lat, lon);

                _city.Text = name + " · " + Wmo(code);
                _temp.Text = temp.ToString("0.#", CultureInfo.InvariantCulture) + "°";
                _range.Text = string.Format(CultureInfo.InvariantCulture, "最高 {0:0.#}° / 最低 {1:0.#}°", max, min);
                _humid.Text = "湿度 " + humidity + "%";
                _status.Text = "更新于 " + DateTime.Now.ToString("HH:mm", CultureInfo.InvariantCulture);
            }
            catch (Exception ex)
            {
                _status.Text = "天气获取失败：" + ex.Message;
                AppLog.Write($"GlassBoard 天气：获取失败：{ex.GetType().Name}: {ex.Message}");
            }
            finally
            {
                _busy = false;
            }
        }

        private static async Task<(string Name, double Lat, double Lon)> GeocodeAsync(string city)
        {
            string url = "https://geocoding-api.open-meteo.com/v1/search?count=1&language=zh&format=json&name="
                + Uri.EscapeDataString(city);

            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));

            if (!doc.RootElement.TryGetProperty("results", out var results) || results.GetArrayLength() == 0)
                throw new InvalidOperationException("找不到城市「" + city + "」");

            var first = results[0];
            string name = first.TryGetProperty("name", out var n) ? (n.GetString() ?? city) : city;

            return (name, first.GetProperty("latitude").GetDouble(), first.GetProperty("longitude").GetDouble());
        }

        private static async Task<(double Temp, int Code, int Humidity, double Max, double Min)> ForecastAsync(double lat, double lon)
        {
            string url = "https://api.open-meteo.com/v1/forecast?timezone=auto&forecast_days=1"
                + "&current=temperature_2m,weather_code,relative_humidity_2m"
                + "&daily=temperature_2m_max,temperature_2m_min"
                + "&latitude=" + lat.ToString("0.####", CultureInfo.InvariantCulture)
                + "&longitude=" + lon.ToString("0.####", CultureInfo.InvariantCulture);

            using var doc = JsonDocument.Parse(await Http.GetStringAsync(url));

            var rootEl = doc.RootElement;
            var cur = rootEl.GetProperty("current");
            var daily = rootEl.GetProperty("daily");

            int humidity = cur.TryGetProperty("relative_humidity_2m", out var h) ? h.GetInt32() : 0;

            return (
                cur.GetProperty("temperature_2m").GetDouble(),
                cur.GetProperty("weather_code").GetInt32(),
                humidity,
                daily.GetProperty("temperature_2m_max")[0].GetDouble(),
                daily.GetProperty("temperature_2m_min")[0].GetDouble());
        }

        /// <summary>WMO 天气代码转中文（对应原版天气组件的中文描述）。</summary>
        private static string Wmo(int code) => code switch
        {
            0 => "晴",
            1 => "晴间多云",
            2 => "局部多云",
            3 => "阴",
            45 or 48 => "雾",
            51 or 53 or 55 => "毛毛雨",
            56 or 57 => "冻毛毛雨",
            61 => "小雨",
            63 => "中雨",
            65 => "大雨",
            66 or 67 => "冻雨",
            71 => "小雪",
            73 => "中雪",
            75 => "大雪",
            77 => "米雪",
            80 => "阵雨",
            81 => "中阵雨",
            82 => "强阵雨",
            85 or 86 => "阵雪",
            95 => "雷阵雨",
            96 or 99 => "雷暴伴冰雹",
            _ => "未知",
        };

        private void ShowMenu(object sender, MouseButtonEventArgs e)
        {
            var menu = new ContextMenu();
            Dock.BoardMenuTheme.Apply(menu, 82.0);

            var refresh = new MenuItem { Header = "立即刷新" };
            refresh.Click += (_, _) => QueueRefresh();

            var humidity = new MenuItem
            {
                Header = "显示湿度",
                IsCheckable = true,
                IsChecked = GlassBoardModule.Settings.WeatherShowHumidity,
            };
            humidity.Click += (_, _) =>
            {
                GlassBoardModule.Settings.WeatherShowHumidity = humidity.IsChecked;
                GlassBoardModule.RefreshWeather();
            };

            var hide = new MenuItem { Header = "隐藏天气" };
            hide.Click += (_, _) => GlassBoardModule.HideWeather();

            menu.Items.Add(refresh);
            menu.Items.Add(humidity);
            menu.Items.Add(new Separator());
            menu.Items.Add(hide);

            menu.PlacementTarget = _card;
            menu.IsOpen = true;
        }
    }
}
