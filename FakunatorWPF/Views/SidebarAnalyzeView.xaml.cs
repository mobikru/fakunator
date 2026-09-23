using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fakunator.Core;
using Fakunator.ViewModels;

namespace Fakunator.Views;

public partial class SidebarAnalyzeView : UserControl
{
    private bool _syncing;
    private AnalyzeViewModel? _vm;

    public SidebarAnalyzeView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            if (_vm != null) SyncFromVm(_vm);
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is AnalyzeViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;

        if (e.NewValue is AnalyzeViewModel vm)
        {
            _vm = vm;
            vm.PropertyChanged += OnVmPropertyChanged;
            SyncFromVm(vm);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AnalyzeViewModel vm) return;

        // Whenever the VM total/preview changes (e.g. file loaded), refresh estimate.
        if (e.PropertyName is nameof(AnalyzeViewModel.TotalLines)
                          or nameof(AnalyzeViewModel.PreviewLines))
        {
            UpdateEstimate(vm);
        }
    }

    private void SyncFromVm(AnalyzeViewModel vm)
    {
        _syncing = true;
        try
        {
            // Provider
            var providerLower = (vm.Provider ?? "openai").ToLowerInvariant();
            for (int i = 0; i < CmbProvider.Items.Count; i++)
            {
                if (CmbProvider.Items[i] is ComboBoxItem item
                    && string.Equals(item.Content?.ToString(), providerLower, System.StringComparison.OrdinalIgnoreCase))
                {
                    CmbProvider.SelectedIndex = i;
                    break;
                }
            }

            // Update model card labels for this provider
            if (ProviderModels.TryGetValue(providerLower, out var models))
            {
                TxtMiniName.Text = models.mini;
                TxtMiniPrice.Text = FormatPrice(models.miniPriceIn, models.miniPriceOut);
                TxtFullName.Text = models.full;
                TxtFullPrice.Text = FormatPrice(models.fullPriceIn, models.fullPriceOut);

                // Choose which radio is checked from vm.Model.
                // If vm.Model matches neither card (stale value from older versions),
                // snap it back to the mini variant so we don't send a bad name to the API.
                bool isFull = string.Equals(vm.Model, models.full, System.StringComparison.OrdinalIgnoreCase);
                bool isMini = string.Equals(vm.Model, models.mini, System.StringComparison.OrdinalIgnoreCase);
                if (!isFull && !isMini)
                {
                    _syncing = false;
                    vm.Model = models.mini;
                    _syncing = true;
                }
                RbFull.IsChecked = isFull;
                RbMini.IsChecked = !isFull;
                ApplyModelCardVisuals(isFull);
            }

            // Checkboxes
            ChkUseAi.IsChecked = vm.UseAi;
            ChkTwoPass.IsChecked = vm.TwoPass;
            ChkSkipJunk.IsChecked = vm.SkipJunk;

            // Spend cap
            TxtSpendCap.Text = vm.SpendCap.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);

            // Batch size combo
            SelectComboByContent(CmbBatchSize, vm.AiBatchSize.ToString());

            // Concurrent combo
            SelectComboByContent(CmbConcurrent, vm.MaxConcurrent.ToString());

            UpdateEstimate(vm);
        }
        finally
        {
            _syncing = false;
        }
    }

    private static void SelectComboByContent(ComboBox combo, string content)
    {
        for (int i = 0; i < combo.Items.Count; i++)
        {
            if (combo.Items[i] is ComboBoxItem item
                && string.Equals(item.Content?.ToString(), content, System.StringComparison.Ordinal))
            {
                combo.SelectedIndex = i;
                return;
            }
        }
    }

    private void UpdateEstimate(AnalyzeViewModel vm)
    {
        var (cost, tokIn, tokOut, capped) = vm.EstimateBudget();
        TxtEstCost.Text = string.Format(Loc.T(capped ? "analyze.sidebar.estCostCapped" : "analyze.sidebar.estCostNormal"), cost);
        var capNote = capped ? Loc.T("analyze.sidebar.estCappedNote") : "";
        TxtEstTokens.Text = string.Format(Loc.T("analyze.sidebar.estTokens"), tokIn, tokOut, capNote);
    }

    // ── Settings handlers ───────────────────────────────────────────

    private static readonly Dictionary<string, (string mini, string miniPriceIn, string miniPriceOut, string full, string fullPriceIn, string fullPriceOut)> ProviderModels = new()
    {
        ["openai"] = ("gpt-4o-mini", "$0.15", "$0.60", "gpt-4o", "$2.50", "$10.0"),
        ["anthropic"] = ("claude-haiku-4-5", "$0.80", "$4.00", "claude-sonnet-4-5", "$3.00", "$15.0"),
    };

    private static string FormatPrice(string priceIn, string priceOut) =>
        string.Format(Loc.T("analyze.sidebar.priceFormat"), priceIn, priceOut);

    private void OnProviderChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (DataContext is not AnalyzeViewModel vm) return;
        if (CmbProvider.SelectedItem is not ComboBoxItem item) return;
        var provider = item.Content?.ToString()?.ToLowerInvariant() ?? "openai";
        vm.Provider = provider;

        // Update model cards
        if (ProviderModels.TryGetValue(provider, out var models))
        {
            TxtMiniName.Text = models.mini;
            TxtMiniPrice.Text = FormatPrice(models.miniPriceIn, models.miniPriceOut);
            TxtFullName.Text = models.full;
            TxtFullPrice.Text = FormatPrice(models.fullPriceIn, models.fullPriceOut);
            RbMini.IsChecked = true;
            // ApplyModelCardVisuals will run via OnModelChanged
        }

        UpdateEstimate(vm);
    }

    private void ApplyModelCardVisuals(bool fullSelected)
    {
        if (fullSelected)
        {
            CardFull.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
            CardFull.Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush");
            CardMini.BorderBrush = (System.Windows.Media.Brush)FindResource("Border1Brush");
            CardMini.Background = System.Windows.Media.Brushes.Transparent;
            BadgeFull.Visibility = Visibility.Visible;
            BadgeMini.Visibility = Visibility.Collapsed;
        }
        else
        {
            CardMini.BorderBrush = (System.Windows.Media.Brush)FindResource("AccentBrush");
            CardMini.Background = (System.Windows.Media.Brush)FindResource("AccentSoftBrush");
            CardFull.BorderBrush = (System.Windows.Media.Brush)FindResource("Border1Brush");
            CardFull.Background = System.Windows.Media.Brushes.Transparent;
            BadgeMini.Visibility = Visibility.Visible;
            BadgeFull.Visibility = Visibility.Collapsed;
        }
    }

    private void OnModelChanged(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AnalyzeViewModel vm) return;

        var provider = vm.Provider?.ToLowerInvariant() ?? "openai";
        ProviderModels.TryGetValue(provider, out var models);

        bool fullSelected = RbFull.IsChecked == true;
        ApplyModelCardVisuals(fullSelected);

        if (_syncing) return;

        if (fullSelected)
            vm.Model = models.full ?? "gpt-4o";
        else
            vm.Model = models.mini ?? "gpt-4o-mini";

        UpdateEstimate(vm);
    }

    private void OnModelMiniClick(object sender, MouseButtonEventArgs e)
    {
        RbMini.IsChecked = true;
    }

    private void OnModelFullClick(object sender, MouseButtonEventArgs e)
    {
        RbFull.IsChecked = true;
    }

    private void OnSpendCapChanged(object sender, TextChangedEventArgs e)
    {
        if (_syncing) return;
        if (DataContext is AnalyzeViewModel vm && double.TryParse(
                TxtSpendCap.Text.Replace(",", "."),
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture,
                out var v) && v >= 0)
        {
            vm.SpendCap = v;
            UpdateEstimate(vm);
        }
    }

    private void OnBatchSizeChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (DataContext is not AnalyzeViewModel vm) return;
        if (CmbBatchSize.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content?.ToString(), out var v))
        {
            vm.AiBatchSize = v;
            UpdateEstimate(vm);
        }
    }

    private void OnConcurrentChanged(object sender, SelectionChangedEventArgs e)
    {
        if (_syncing) return;
        if (DataContext is not AnalyzeViewModel vm) return;
        if (CmbConcurrent.SelectedItem is ComboBoxItem item &&
            int.TryParse(item.Content?.ToString(), out var v))
        {
            vm.MaxConcurrent = v;
        }
    }

    private void OnSettingChanged(object sender, RoutedEventArgs e)
    {
        if (_syncing) return;
        if (DataContext is not AnalyzeViewModel vm) return;
        vm.UseAi = ChkUseAi.IsChecked == true;
        vm.TwoPass = ChkTwoPass.IsChecked == true;
        vm.SkipJunk = ChkSkipJunk.IsChecked == true;
        UpdateEstimate(vm);
    }
}
