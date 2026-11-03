using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using F75Studio.Lighting;

namespace F75Studio.App.Converters;

public sealed class RgbToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var rgb = value is Rgb c ? c : Rgb.Black;
        var brush = new SolidColorBrush(System.Windows.Media.Color.FromRgb(rgb.R, rgb.G, rgb.B));
        brush.Freeze();
        return brush;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
