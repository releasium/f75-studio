using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace F75Studio.App.Converters;

/// <summary>Visible when the enum value's name equals the ConverterParameter string.</summary>
public sealed class EnumMatchToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value?.ToString() == parameter as string ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
