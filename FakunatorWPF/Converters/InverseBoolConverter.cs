using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Fakunator.Converters;

/// <summary>
/// bool → !bool. С параметром "vis" — !bool → Visibility.Visible / Collapsed.
/// </summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        bool inv = value is bool b ? !b : true;
        var p = parameter as string;
        if (!string.IsNullOrEmpty(p) && string.Equals(p, "vis", StringComparison.OrdinalIgnoreCase))
            return inv ? Visibility.Visible : Visibility.Collapsed;
        return inv;
    }
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
