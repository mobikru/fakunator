using System;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using Fakunator.Core;

namespace Fakunator.Converters;

/// <summary>
/// One-way value converter: ISO 3166-1 alpha-2 code -> "RU · Россия" (text only, no emoji).
/// Returns "?" for null/empty/"?" input.
/// </summary>
public class IsoToFlagConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var iso = value as string;
        if (string.IsNullOrWhiteSpace(iso) || iso == "?") return "?";
        var name = CountryFlags.GetName(iso);
        return iso == name ? iso : $"{iso} · {name}";
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// ISO -> SolidColorBrush of country flag color (for colored badges).
/// </summary>
public class IsoToColorConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var iso = value as string;
        var color = (Color)ColorConverter.ConvertFromString(CountryFlags.GetColor(iso));
        return new SolidColorBrush(color);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
