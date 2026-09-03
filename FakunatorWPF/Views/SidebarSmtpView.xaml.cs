using System.Text.RegularExpressions;
using System.Windows.Controls;
using System.Windows.Input;

namespace Fakunator.Views;

public partial class SidebarSmtpView : UserControl
{
    public SidebarSmtpView()
    {
        InitializeComponent();
    }

    private void OnNumericInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, @"^\d+$");
    }
}
