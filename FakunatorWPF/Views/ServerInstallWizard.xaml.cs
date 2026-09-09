using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace Fakunator.Views;

public partial class ServerInstallWizard : Window
{
    private int _step = 1;

    public ServerInstallWizard()
    {
        InitializeComponent();
    }

    private void OnTitleBarDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.Left)
            DragMove();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void OnNextClick(object sender, RoutedEventArgs e)
    {
        if (_step >= 6) { Close(); return; }
        _step++;
        UpdateStepUi();
    }

    private void OnBackClick(object sender, RoutedEventArgs e)
    {
        if (_step <= 1) return;
        _step--;
        UpdateStepUi();
    }

    private void UpdateStepUi()
    {
        Step1.Visibility = _step == 1 ? Visibility.Visible : Visibility.Collapsed;
        Step2.Visibility = _step == 2 ? Visibility.Visible : Visibility.Collapsed;
        Step3.Visibility = _step == 3 ? Visibility.Visible : Visibility.Collapsed;
        Step4.Visibility = _step == 4 ? Visibility.Visible : Visibility.Collapsed;
        Step5.Visibility = _step == 5 ? Visibility.Visible : Visibility.Collapsed;
        Step6.Visibility = _step == 6 ? Visibility.Visible : Visibility.Collapsed;

        HighlightPill(StepPill1, _step == 1);
        HighlightPill(StepPill2, _step == 2);
        HighlightPill(StepPill3, _step == 3);
        HighlightPill(StepPill4, _step == 4);
        HighlightPill(StepPill5, _step == 5);
        HighlightPill(StepPill6, _step == 6);

        BtnBack.IsEnabled = _step > 1;
        BtnNext.Content = _step switch
        {
            4 => "Начать установку →",
            5 => "Пропустить лог",
            6 => "Готово",
            _ => "Далее →"
        };
    }

    private void HighlightPill(Border pill, bool active)
    {
        pill.Background = active
            ? (System.Windows.Media.Brush)FindResource("AccentSoftBrush")
            : System.Windows.Media.Brushes.Transparent;
        if (pill.Child is StackPanel sp && sp.Children.Count >= 2)
        {
            if (sp.Children[0] is Border numBd && numBd.Child is TextBlock numTb)
            {
                numBd.Background = active
                    ? (System.Windows.Media.Brush)FindResource("Accent2Brush")
                    : (System.Windows.Media.Brush)FindResource("Bg3Brush");
                numTb.Foreground = active
                    ? System.Windows.Media.Brushes.White
                    : (System.Windows.Media.Brush)FindResource("Fg3Brush");
            }
            if (sp.Children[1] is TextBlock titleTb)
            {
                titleTb.Foreground = active
                    ? (System.Windows.Media.Brush)FindResource("Accent2Brush")
                    : (System.Windows.Media.Brush)FindResource("Fg2Brush");
                titleTb.FontWeight = active
                    ? System.Windows.FontWeights.SemiBold
                    : System.Windows.FontWeights.Normal;
            }
        }
    }
}
