using System;
using System.Globalization;
using System.Windows.Data;

namespace Fakunator.Converters;

/// <summary>bool → !bool. Например для IsEnabled когда VM держит Busy=true.</summary>
public class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : true;
    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is bool b ? !b : false;
}
