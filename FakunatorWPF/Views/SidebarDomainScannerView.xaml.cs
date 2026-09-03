using System.Windows;
using System.Windows.Controls;
using Fakunator.ViewModels;

namespace Fakunator.Views;

public partial class SidebarDomainScannerView : UserControl
{
    public SidebarDomainScannerView()
    {
        InitializeComponent();
    }

    private void OnTopFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button b) return;
        if (DataContext is DomainScannerViewModel vm)
            vm.TopFilterZone = (b.Tag as string) ?? "";

        // Переключаем стиль активной pill'ы (только одна одновременно).
        var active = (Style)FindResource("TopPillActive");
        var idle = (Style)FindResource("TopPillIdle");
        PillAll.Style = b == PillAll ? active : idle;
        PillRu.Style = b == PillRu ? active : idle;
        PillCom.Style = b == PillCom ? active : idle;
    }
}
