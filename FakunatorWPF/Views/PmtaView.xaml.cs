using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using Fakunator.ViewModels;

namespace Fakunator.Views;

public partial class PmtaView : UserControl
{
    public PmtaView()
    {
        InitializeComponent();
    }

    private void OnOpenPanelUrlClick(object sender, RoutedEventArgs e)
    {
        if (((FrameworkElement)sender).DataContext is PmtaPanelVm vm)
        {
            try { Process.Start(new ProcessStartInfo(vm.EndpointText) { UseShellExecute = true }); }
            catch { /* нет браузера по умолчанию или невалидный URL — не критично */ }
        }
    }
}
