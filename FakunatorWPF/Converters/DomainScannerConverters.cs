using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;
using System.Windows.Data;

namespace Fakunator.Converters;

/// <summary>
/// Percent (0..1) × containerWidth → width in pixels для прогресс-полосок под провайдерами.
/// Bind[0]=Percent, Bind[1]=ActualWidth контейнера.
/// </summary>
public class PercentWidthConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        if (values == null || values.Length < 2) return 0.0;
        double pct = values[0] is double d ? d : 0.0;
        double w = values[1] is double dw ? dw : 0.0;
        if (double.IsNaN(pct) || double.IsInfinity(pct)) pct = 0;
        if (double.IsNaN(w) || double.IsInfinity(w) || w < 0) w = 0;
        return Math.Max(0, Math.Min(w, w * pct));
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>double → bool: true если значение меньше порога.
/// Порог задаётся параметром (double.Parse); по умолчанию 22.
/// Используется чтобы прятать подписи процентов на слишком узких сегментах.</summary>
public class LessThanConverter : IValueConverter
{
    public double Threshold { get; set; } = 22;
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double v = value is double d ? d : 0;
        double th = Threshold;
        if (parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p))
            th = p;
        return v < th;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>"lame_delegation" → "ДА", остальное → "—" (тире как на макете).</summary>
public class LameFlagConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (value as string) == "lame_delegation" ? "ДА" : "—";
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Код статуса → локализованная подпись для live-потока.
/// ok=активен, lame_delegation=брошен, timeout=таймаут, nxdomain=нет домена,
/// no_ns=нет NS, no_a=нет A, error/*=ошибка.
/// IMultiValueConverter (а не IValueConverter): второй bind — на Fakunator.Core.Loc.Instance.Language,
/// нужен только чтобы конвертер переоценивался при смене языка (значение не используется).
/// </summary>
public class StatusRuConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var code = values is { Length: > 0 } ? values[0] as string : null;
        var key = code switch
        {
            "ok" => "domainscanner.status.code.ok",
            "lame_delegation" => "domainscanner.status.code.lame",
            "timeout" => "domainscanner.status.code.timeout",
            "nxdomain" => "domainscanner.status.code.nxdomain",
            "no_ns" => "domainscanner.status.code.noNs",
            "no_a" => "domainscanner.status.code.noA",
            _ => "domainscanner.status.code.error",
        };
        return Fakunator.Core.Loc.T(key);
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Код статуса → цвет надписи (для live-потока).</summary>
public class StatusColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hex = (value as string) switch
        {
            "ok" => "#22c55e",              // зелёный "активен"
            "lame_delegation" => "#ef4444", // красный "брошен"
            "timeout" => "#f59e0b",         // оранжевый "таймаут"
            "nxdomain" => "#71717a",        // серый "нет домена"
            "no_ns" or "no_a" => "#71717a", // серый
            _ => "#ef4444",                 // красный "ошибка"
        };
        return new System.Windows.Media.SolidColorBrush(
            (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!);
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Hex-строка "#22c55e" → System.Windows.Media.Color для биндингов Ellipse.Fill.</summary>
public class HexToColorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var hex = value as string;
        if (string.IsNullOrEmpty(hex)) return System.Windows.Media.Colors.Gray;
        try
        {
            return (System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex)!;
        }
        catch { return System.Windows.Media.Colors.Gray; }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>
/// Если ns_provider пустой или "Unknown" — показываем локализованное "не отвечает" (как на макете),
/// в остальных случаях провайдер как есть (доменное имя — не переводится).
/// IMultiValueConverter: второй bind — на Loc.Instance.Language, только для переоценки при смене языка.
/// </summary>
public class ProviderOrDeadConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object parameter, CultureInfo culture)
    {
        var s = values is { Length: > 0 } ? values[0] as string : null;
        if (string.IsNullOrWhiteSpace(s) || s == "Unknown")
            return Fakunator.Core.Loc.T("domainscanner.liveFeed.noResponse");
        return s;
    }
    public object[] ConvertBack(object value, Type[] targetTypes, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// NS-хосты как JSON-строка → короткая читаемая строка "ns1.foo, ns2.foo, …".
/// </summary>
public class NsHostsShortConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is not string json || string.IsNullOrEmpty(json)) return "";
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(json);
            if (arr == null || arr.Count == 0) return "";
            if (arr.Count <= 2) return string.Join(", ", arr);
            return string.Join(", ", arr.GetRange(0, 2)) + $", +{arr.Count - 2}";
        }
        catch
        {
            return json;
        }
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}

/// <summary>Unix timestamp (long) → "HH:mm:ss" в локальном времени.</summary>
public class UnixToTimeConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is null) return "";
        long ts = value switch
        {
            long l => l,
            int i => i,
            _ => 0,
        };
        if (ts <= 0) return "";
        return DateTimeOffset.FromUnixTimeSeconds(ts).LocalDateTime.ToString("HH:mm:ss");
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => Binding.DoNothing;
}
