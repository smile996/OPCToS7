using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace OPCToS7.App.Converter;

public class StatusColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            string status = value as string ?? "";

            return status switch
            {
                "正常" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#00C853")), // 亮绿色
                "未连接" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9E9E9E")), // 灰色
                "变量错误" => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#F44336")), // 红色
                _ => new SolidColorBrush((Color)ColorConverter.ConvertFromString("#2196F3")) // 默认蓝色
            };
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
