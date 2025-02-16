using Avalonia.Data.Converters;
using System;
using System.Globalization;

namespace AvaloniaApplication2.Converters
{
    public class BoolToDoubleConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        {
            if (value is bool boolValue && parameter is string param)
            {
                var parts = param.Split(':');
                if (parts.Length == 2 &&
                    double.TryParse(parts[0], out double falseValue) &&
                    double.TryParse(parts[1], out double trueValue))
                {
                    return boolValue ? trueValue : falseValue;
                }
            }

            if (value is bool b)
            {
                return b ? 1.0 : 0.0;
            }

            return 0.0;
        }

        public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        {
            throw new NotImplementedException();
        }
    }
}