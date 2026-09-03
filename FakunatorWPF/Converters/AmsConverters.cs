using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Fakunator.Converters;

/// <summary>true → Visible, false → Collapsed. Не встроенный BooleanToVisibilityConverter
/// потому что нам удобно свой в родном namespace.</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => (value is bool b && b) ? Visibility.Visible : Visibility.Collapsed;

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => value is Visibility v && v == Visibility.Visible;
}

/// <summary>Процент (0..100) × parameter → пиксели. Используется для ширины полосы
/// прогресса рассылки, где total-width задаётся ConverterParameter (напр. 520).</summary>
public class PercentToWidthConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not int p) return 0.0;
        if (parameter is not string s || !double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var total))
            total = 200;
        var pct = Math.Clamp(p, 0, 100);
        return total * (pct / 100.0);
    }

    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

/// <summary>int &gt; 0 → Visible, иначе Collapsed. Для скрытия "›" префикса у корневых списков.</summary>
public class IntGtZeroToVisConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object? parameter, CultureInfo culture)
        => (value is int i && i > 0) ? Visibility.Visible : Visibility.Collapsed;
    public object ConvertBack(object value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotImplementedException();
}
