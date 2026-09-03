using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Fakunator.Core.DomainScanner;
using Fakunator.ViewModels;

namespace Fakunator.Views;

public partial class DomainScannerView : UserControl
{
    public DomainScannerView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is DomainScannerViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;
        if (e.NewValue is DomainScannerViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            FeedList.ItemsSource = vm.FreshFeed;
            UpdateAll(vm);
            ApplyFeedExpanded(vm.IsFeedExpanded);
        }
    }

    private void OnFeedToggle(object sender, RoutedEventArgs e)
    {
        if (DataContext is DomainScannerViewModel vm)
        {
            vm.IsFeedExpanded = !vm.IsFeedExpanded;
            ApplyFeedExpanded(vm.IsFeedExpanded);
        }
    }

    private void ApplyFeedExpanded(bool expanded)
    {
        FeedContent.Visibility = expanded ? Visibility.Visible : Visibility.Collapsed;
        TxtFeedChevron.Text = expanded ? "▼" : "▶";
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not DomainScannerViewModel vm) return;
        if (e.PropertyName is nameof(DomainScannerViewModel.Snapshot)
                            or nameof(DomainScannerViewModel.BrowserTotal))
        {
            Dispatcher.Invoke(() => UpdateAll(vm));
        }
    }

    private void UpdateAll(DomainScannerViewModel vm)
    {
        var snap = vm.Snapshot;
        if (snap == null)
        {
            KpiFetched.Text = "0";
            KpiProcessed.Text = "0";
            KpiSaved.Text = "0";
            KpiSpeed.Text = "0";
            KpiErrors.Text = "0";
        }
        else
        {
            KpiFetched.Text = snap.TotalFetched.ToString("N0");
            KpiProcessed.Text = snap.TotalProcessed.ToString("N0");
            int lame = 0;
            snap.ByStatus.TryGetValue("lame_delegation", out lame);
            KpiSaved.Text = lame.ToString("N0");
            KpiSpeed.Text = snap.Speed.ToString("N0");
            KpiErrors.Text = snap.Errors.ToString("N0");
        }
        KpiTotal.Text = (snap?.TotalInDb ?? 0).ToString("N0");

        TxtBrowserCount.Text = $"{vm.BrowserTotal:N0} записей";
        TxtFeedPlaceholder.Visibility = vm.FreshFeed.Count > 0
            ? Visibility.Collapsed : Visibility.Visible;
    }
}
