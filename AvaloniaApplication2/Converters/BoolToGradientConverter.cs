using System;
using System.Collections.Generic;
using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Immutable;
using Avalonia.Data.Converters;
using Avalonia;

namespace AvaloniaApplication2.Converters
{
    public class BoolToGradientConverter : IMultiValueConverter
    {
        private static readonly LinearGradientBrush GreenGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#22c55e"), 0),
                new GradientStop(Color.Parse("#16a34a"), 1)
            }
        };

        private static readonly LinearGradientBrush GreenHoverGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#15803d"), 0),
                new GradientStop(Color.Parse("#166534"), 1)
            }
        };

        private static readonly LinearGradientBrush RedGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#ef4444"), 0),
                new GradientStop(Color.Parse("#dc2626"), 1)
            }
        };

        private static readonly LinearGradientBrush RedHoverGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#dc2626"), 0),
                new GradientStop(Color.Parse("#b91c1c"), 1)
            }
        };

        private static readonly LinearGradientBrush RedPressedGradient = new LinearGradientBrush
        {
            StartPoint = new RelativePoint(0, 0, RelativeUnit.Relative),
            EndPoint = new RelativePoint(1, 1, RelativeUnit.Relative),
            GradientStops =
            {
                new GradientStop(Color.Parse("#b91c1c"), 0),
                new GradientStop(Color.Parse("#991b1b"), 1)
            }
        };

        public object Convert(IList<object> values, Type targetType, object parameter, CultureInfo culture)
        {
            if (values.Count < 1 || values[0] is not bool isLocked)
                return GreenGradient;

            string state = values.Count > 1 ? values[1] as string ?? "" : "";

            if (isLocked)
            {
                return state switch
                {
                    "hover" => RedHoverGradient,
                    "pressed" => RedPressedGradient,
                    _ => RedGradient
                };
            }
            else
            {
                return state switch
                {
                    "hover" => GreenHoverGradient,
                    "pressed" => GreenHoverGradient,
                    _ => GreenGradient
                };
            }
        }
    }
}