using System;
using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Fakunator.ViewModels;

/// <summary>
/// Converts AnalyzeViewModel.AnalyzeState to Visibility. Pass the target state as ConverterParameter.
/// If the current state matches the parameter, returns Visible; otherwise Collapsed.
/// </summary>
public class AnalyzeStateToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        if (value is AnalyzeViewModel.AnalyzeState state && parameter is string targetStr)
        {
            if (Enum.TryParse<AnalyzeViewModel.AnalyzeState>(targetStr, out var target))
            {
                return state == target ? Visibility.Visible : Visibility.Collapsed;
            }
        }
        return Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
    {
        throw new NotImplementedException();
    }
}
