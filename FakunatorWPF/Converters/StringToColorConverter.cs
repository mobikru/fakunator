using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;

namespace Fakunator.Converters;

/// <summary>
/// Строка "#RRGGBB" или "#AARRGGBB" → Color (не Brush) для XAML `SolidColorBrush.Color="{Binding ..., Converter=StrToColor}"`.
/// Возвращает Colors.Gray при пустой/невалидной строке.
/// </summary>
public class StringToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is string s && !string.IsNullOrWhiteSpace(s))
        {
            try { return (Color)ColorConverter.ConvertFromString(s); }
            catch { }
        }
        return Colors.Gray;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
