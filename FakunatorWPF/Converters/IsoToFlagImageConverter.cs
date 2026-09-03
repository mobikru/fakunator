using System;
using System.Collections.Generic;
using System.Globalization;
using System.Windows.Data;
using System.Windows.Media.Imaging;

namespace Fakunator.Converters;

/// <summary>
/// ISO 3166-1 alpha-2 code -> BitmapImage of the country flag PNG from Assets/Flags/.
/// Caches loaded bitmaps. Returns null if flag not bundled (UI shows nothing).
/// </summary>
public class IsoToFlagImageConverter : IValueConverter
{
    private static readonly Dictionary<string, BitmapImage?> _cache = new();

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var iso = (value as string)?.ToUpperInvariant();
        if (string.IsNullOrEmpty(iso) || iso == "?") return null;

        if (_cache.TryGetValue(iso, out var cached)) return cached;

        try
        {
            var uri = new Uri($"pack://application:,,,/Assets/Flags/{iso}.png", UriKind.Absolute);
            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad;
            bmp.EndInit();
            bmp.Freeze();
            _cache[iso] = bmp;
            return bmp;
        }
        catch
        {
            _cache[iso] = null;
            return null;
        }
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
