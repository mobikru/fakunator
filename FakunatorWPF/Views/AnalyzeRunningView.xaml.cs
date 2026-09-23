using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Fakunator.Core;
using Fakunator.ViewModels;

namespace Fakunator.Views;

public partial class AnalyzeRunningView : UserControl
{
    private AnalyzeViewModel? _vm;

    public AnalyzeRunningView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            if (_vm != null) UpdateFromSnapshot(_vm.CurrentSnapshot);
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
            CountryBarsPanel.Children.Clear();
            UpdateFromSnapshot(vm.CurrentSnapshot);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AnalyzeViewModel vm) return;
        if (e.PropertyName == nameof(AnalyzeViewModel.CurrentSnapshot))
            UpdateFromSnapshot(vm.CurrentSnapshot);
    }

    private void UpdateFromSnapshot(AnalyzeSnapshot? snap)
    {
        if (snap is null)
        {
            TxtPercent.Text = "0.0%";
            TxtPhase.Text = Loc.T("analyze.running.phaseWaiting");
            TxtProcessed.Text = string.Format(Loc.T("analyze.running.processed"), 0, 0);
            KpiProcessed.Text = "0";
            KpiDb.Text = "0";
            KpiAi.Text = "0";
            KpiJunk.Text = "0";
            KpiAiQueue.Text = "0";
            KpiSpeed.Text = "0/с";
            TxtInTokens.Text = "0";
            TxtOutTokens.Text = "0";
            TxtCostUsd.Text = "$ 0.00";
            ProgressFill.Width = 0;
            Pass2Panel.Visibility = System.Windows.Visibility.Collapsed;
            Pass2Fill.Width = 0;
            TxtPass2Count.Text = "0 / 0";
            CountryBarsPanel.Children.Clear();
            return;
        }

        double pct = snap.Total > 0 ? (double)snap.Processed / snap.Total * 100 : 0;
        TxtPercent.Text = $"{pct:F1}%";

        var phaseName = snap.Phase switch
        {
            "init" => Loc.T("analyze.running.phaseInit"),
            "db" => Loc.T("analyze.running.phaseDb"),
            "ai1" => Loc.T("analyze.running.phaseAi1"),
            "ai2" => Loc.T("analyze.running.phaseAi2"),
            "done" => Loc.T("analyze.running.phaseDone"),
            _ => snap.Phase,
        };
        TxtPhase.Text = phaseName;
        TxtProcessed.Text = string.Format(Loc.T("analyze.running.processed"), snap.Processed, snap.Total);

        // Progress bar
        var trackBorder = ProgressFill.Parent as FrameworkElement;
        double trackWidth = trackBorder?.ActualWidth ?? 0;
        if (trackWidth > 0)
            ProgressFill.Width = Math.Max(0, Math.Min(trackWidth, trackWidth * pct / 100.0));
        else
            ProgressFill.Width = 0;

        // KPIs
        KpiProcessed.Text = snap.Processed.ToString("N0");

        int dbCount = GetCount(snap, "db") + GetCount(snap, "db+morph");
        int aiCount = GetCount(snap, "ai") + GetCount(snap, "ai+ai2");
        int unkCount = GetCount(snap, "unknown");

        KpiDb.Text = dbCount.ToString("N0");
        KpiAi.Text = aiCount.ToString("N0");
        KpiJunk.Text = unkCount.ToString("N0");
        int aiQueue = snap.Total - snap.Processed;
        KpiAiQueue.Text = aiQueue > 0 ? aiQueue.ToString("N0") : "0";
        KpiSpeed.Text = $"{snap.Speed:N1}/с";

        // Tokens
        TxtInTokens.Text = snap.InputTokens.ToString("N0");
        TxtOutTokens.Text = snap.OutputTokens.ToString("N0");
        TxtCostUsd.Text = $"$ {snap.CostUsd:F4}";

        // Pass2 progress — виден только когда Pass2 запустился (Pass2Total > 0)
        if (snap.Pass2Total > 0)
        {
            Pass2Panel.Visibility = System.Windows.Visibility.Visible;
            double pass2Pct = (double)snap.Pass2Processed / snap.Pass2Total * 100;
            TxtPass2Count.Text = string.Format(Loc.T("analyze.running.pass2Count"), snap.Pass2Processed, snap.Pass2Total, pass2Pct);
            var pass2Track = Pass2Fill.Parent as FrameworkElement;
            double pass2Width = pass2Track?.ActualWidth ?? 0;
            if (pass2Width > 0)
                Pass2Fill.Width = Math.Max(0, Math.Min(pass2Width, pass2Width * pass2Pct / 100.0));
        }
        else
        {
            Pass2Panel.Visibility = System.Windows.Visibility.Collapsed;
        }

        // Country bars (debounced via snapshot tick rate, ~5 Hz)
        UpdateCountryBars(snap.CountryStats);

        // Auto-scroll DataGrid to the latest live result
        if (DataContext is AnalyzeViewModel vm && vm.LiveResults.Count > 0)
        {
            try
            {
                LiveGrid.ScrollIntoView(vm.LiveResults[vm.LiveResults.Count - 1]);
            }
            catch { /* virtualization race — ignore */ }
        }
    }

    private static int GetCount(AnalyzeSnapshot snap, string key) =>
        snap.Counts.TryGetValue(key, out var v) ? v : 0;

    // ── Country bar chart ───────────────────────────────────────────

    private void UpdateCountryBars(Dictionary<string, int>? stats)
    {
        CountryBarsPanel.Children.Clear();

        if (stats is null || stats.Count == 0) return;

        var top = stats
            .Where(kv => kv.Key != "?")
            .OrderByDescending(kv => kv.Value)
            .Take(8)
            .ToList();

        if (top.Count == 0) return;

        int maxVal = top[0].Value;
        double maxBarWidth = 140; // px

        var monoFont = (FontFamily)FindResource("MonoFont");
        var emojiFont = new FontFamily("Segoe UI Emoji, Segoe UI");

        foreach (var (iso, count) in top)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 5) };

            // Count (right-aligned)
            var countTb = new TextBlock
            {
                Text = count.ToString("N0"),
                FontSize = 11,
                FontFamily = monoFont,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
                TextAlignment = TextAlignment.Right,
                MinWidth = 40,
                Margin = new Thickness(6, 0, 0, 0),
            };
            countTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
            DockPanel.SetDock(countTb, Dock.Right);
            row.Children.Add(countTb);

            // Flag + ISO + Name label (left)
            var flag = CountryFlags.GetFlag(iso);
            var name = CountryFlags.GetName(iso);
            var labelPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 140,
                ToolTip = name,
            };

            // Real flag PNG image from Assets/Flags/{ISO}.png
            var flagConverter = new Converters.IsoToFlagImageConverter();
            var flagImg = flagConverter.Convert(iso, typeof(BitmapImage), null, System.Globalization.CultureInfo.InvariantCulture) as BitmapImage;
            var flagBadge = new Image
            {
                Source = flagImg,
                Width = 20, Height = 14,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
                Stretch = System.Windows.Media.Stretch.Uniform,
            };
            labelPanel.Children.Add(flagBadge);

            var isoTb = new TextBlock
            {
                Text = iso,
                FontSize = 10,
                FontWeight = FontWeights.Bold,
                FontFamily = monoFont,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 6, 0),
            };
            isoTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
            labelPanel.Children.Add(isoTb);

            var nameTb = new TextBlock
            {
                Text = name,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            nameTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
            labelPanel.Children.Add(nameTb);

            DockPanel.SetDock(labelPanel, Dock.Left);
            row.Children.Add(labelPanel);

            // Bar (fill remaining space)
            double barFraction = maxVal > 0 ? (double)count / maxVal : 0;
            var barBorder = new Border
            {
                Height = 14,
                CornerRadius = new CornerRadius(3),
                Opacity = 0.7,
                HorizontalAlignment = HorizontalAlignment.Left,
                Width = Math.Max(4, maxBarWidth * barFraction),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(4, 0, 0, 0),
            };
            barBorder.SetResourceReference(Border.BackgroundProperty, "AccentBrush");
            row.Children.Add(barBorder);

            CountryBarsPanel.Children.Add(row);
        }
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AnalyzeViewModel vm)
            vm.StopCommand.Execute(null);
    }
}
