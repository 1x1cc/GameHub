using System;
using System.Globalization;
using System.Windows.Data;

namespace GameHub.Converters
{
    /// <summary>
    /// 自启动状态文本转换器
    /// </summary>
    public class AutoStartTextConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool isEnabled)
            {
                return isEnabled ? "开机自启动 on" : "开机自启动 off";
            }
            return "开机自启动 off";
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}
