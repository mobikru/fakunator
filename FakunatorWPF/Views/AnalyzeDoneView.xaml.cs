using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fakunator.Core;
using Fakunator.ViewModels;

namespace Fakunator.Views;

public partial class AnalyzeDoneView : UserControl
{
    private AnalyzeViewModel? _vm;

    public AnalyzeDoneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            if (_vm?.State == AnalyzeViewModel.AnalyzeState.Done) PopulateFromVm(_vm);
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
            if (vm.State == AnalyzeViewModel.AnalyzeState.Done)
                PopulateFromVm(vm);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not AnalyzeViewModel vm) return;
        if (e.PropertyName == nameof(AnalyzeViewModel.State) && vm.State == AnalyzeViewModel.AnalyzeState.Done)
            PopulateFromVm(vm);
    }

    private void PopulateFromVm(AnalyzeViewModel vm)
    {
        var snap = vm.FinalSnapshot;
        var results = vm.Results;

        if (snap is null) return;

        // Hero card
        var ts = TimeSpan.FromSeconds(snap.Elapsed);
        HeroElapsed.Text = ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"mm\:ss");

        HeroCost.Text = $"$ {snap.CostUsd:F4}";
        HeroTokens.Text = (snap.InputTokens + snap.OutputTokens).ToString("N0");

        TxtHeroTitle.Text = string.Format(Loc.T("analyze.done.titleWithTime"), HeroElapsed.Text);

        int dbCount = GetCount(snap, "db") + GetCount(snap, "db+morph");
        int aiCount = GetCount(snap, "ai") + GetCount(snap, "ai+ai2");
        int unkCount = GetCount(snap, "unknown");

        TxtSubtitle.Text = string.Format(Loc.T("analyze.done.subtitle"), snap.Total, dbCount, aiCount, unkCount);

        // KPIs
        KpiProcessed.Text = snap.Processed.ToString("N0");
        KpiDb.Text = dbCount.ToString("N0");
        KpiAi.Text = aiCount.ToString("N0");
        KpiUnknown.Text = unkCount.ToString("N0");
        KpiSpeed.Text = string.Format(Loc.T("unit.perSecN1"), snap.Speed);

        // Country bar chart
        BuildCountryBars(snap.CountryStats, snap.Processed);

        // Results grid
        if (results != null && results.Count > 0)
        {
            var rows = results.Select(r => new DoneResultRow(r)).ToList();
            ResultsGrid.ItemsSource = rows;
        }
    }

    private static int GetCount(AnalyzeSnapshot snap, string key) =>
        snap.Counts.TryGetValue(key, out var v) ? v : 0;

    // ── Country bar chart ───────────────────────────────────────────

    private void BuildCountryBars(Dictionary<string, int>? stats, int totalProcessed)
    {
        CountryBarsPanel.Children.Clear();

        if (stats is null || stats.Count == 0)
        {
            CountryCard.Visibility = Visibility.Collapsed;
            return;
        }

        var top10 = stats
            .Where(kv => kv.Key != "?")
            .OrderByDescending(kv => kv.Value)
            .Take(10)
            .ToList();

        if (top10.Count == 0)
        {
            CountryCard.Visibility = Visibility.Collapsed;
            return;
        }

        CountryCard.Visibility = Visibility.Visible;
        int maxVal = top10[0].Value;
        double maxBarWidth = 200;

        var monoFont = (FontFamily)FindResource("MonoFont");
        var emojiFont = new FontFamily("Segoe UI Emoji, Segoe UI");

        foreach (var (iso, count) in top10)
        {
            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

            // Percentage + count (right-aligned)
            double pctVal = totalProcessed > 0 ? (double)count / totalProcessed * 100 : 0;
            var statPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
            };
            var countTb = new TextBlock
            {
                Text = count.ToString("N0"),
                FontSize = 11,
                FontFamily = monoFont,
                FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center,
            };
            countTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
            statPanel.Children.Add(countTb);

            var pctTb = new TextBlock
            {
                Text = $" ({pctVal:F1}%)",
                FontSize = 10,
                FontFamily = monoFont,
                VerticalAlignment = VerticalAlignment.Center,
            };
            pctTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
            statPanel.Children.Add(pctTb);

            DockPanel.SetDock(statPanel, Dock.Right);
            row.Children.Add(statPanel);

            // Flag + ISO + Name label (left)
            var flag = CountryFlags.GetFlag(iso);
            var name = CountryFlags.GetName(iso);
            var labelPanel = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                VerticalAlignment = VerticalAlignment.Center,
                Width = 160,
            };

            // Real flag PNG from Assets/Flags/{ISO}.png
            var flagConverter = new Converters.IsoToFlagImageConverter();
            var flagImg = flagConverter.Convert(iso, typeof(System.Windows.Media.Imaging.BitmapImage), null, System.Globalization.CultureInfo.InvariantCulture) as System.Windows.Media.Imaging.BitmapImage;
            var flagBadge = new Image
            {
                Source = flagImg,
                Width = 24, Height = 16,
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

            // Bar (fill remaining)
            double barFraction = maxVal > 0 ? (double)count / maxVal : 0;
            var barBorder = new Border
            {
                Height = 16,
                CornerRadius = new CornerRadius(4),
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

    // ── Export handlers ─────────────────────────────────────────────

    private void OnExportCsvClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AnalyzeViewModel vm || vm.Results is null) return;

        try
        {
            var dir = vm.OutputDir ?? Fakunator.Core.Paths.AnalyzeRoot;
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "analyze_results.csv");

            var sb = new StringBuilder();
            sb.AppendLine("email,name_token,gender,country,source,match_tier,signals");

            foreach (var r in vm.Results)
            {
                var signals = r.Signals.Replace("\"", "\"\"");
                sb.AppendLine($"{r.Email},{r.NameToken},{r.Gender},{r.Iso},{r.Source},{r.MatchTier},\"{signals}\"");
            }

            File.WriteAllText(path, sb.ToString(), Encoding.UTF8);

            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { }
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("analyze.err.exportBody"), ex.Message), Loc.T("analyze.err.exportTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnExportJsonClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AnalyzeViewModel vm || vm.Results is null) return;

        try
        {
            var dir = vm.OutputDir ?? Fakunator.Core.Paths.AnalyzeRoot;
            Directory.CreateDirectory(dir);

            var path = Path.Combine(dir, "analyze_results.json");

            var jsonItems = vm.Results.Select(r => new
            {
                email = r.Email,
                name = r.NameToken,
                gender = r.Gender,
                country = r.Iso,
                source = r.Source,
                tier = r.MatchTier,
                signals = r.Signals,
            }).ToList();

            var json = JsonSerializer.Serialize(jsonItems, new JsonSerializerOptions
            {
                WriteIndented = true,
                PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            });

            File.WriteAllText(path, json, Encoding.UTF8);

            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
            catch { }
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("analyze.err.exportBody"), ex.Message), Loc.T("analyze.err.exportTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnNewFileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is AnalyzeViewModel vm)
            vm.ResetToIdle();
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not AnalyzeViewModel vm) return;

        var dir = vm.OutputDir ?? Fakunator.Core.Paths.AnalyzeRoot;
        if (Directory.Exists(dir))
        {
            try { Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true }); }
            catch { }
        }
    }

    // ── Row wrapper for DataGrid binding ────────────────────────────

    public class DoneResultRow
    {
        public string Email { get; }
        public string NameToken { get; }
        public string Gender { get; }
        public string GenderDisplay { get; }
        public string Iso { get; }
        public string CountryDisplay { get; }
        public string Source { get; }
        public string SourceDisplay { get; }
        public string Signals { get; }

        public DoneResultRow(AnalysisResult r)
        {
            Email = r.Email;
            NameToken = r.NameToken;
            Gender = r.Gender;
            Iso = r.Iso;
            Source = r.Source;
            Signals = r.Signals;

            GenderDisplay = r.Gender switch
            {
                "M" => Loc.T("analyze.done.genderM"),
                "F" => Loc.T("analyze.done.genderF"),
                "N" => Loc.T("analyze.done.genderN"),
                _ => Loc.T("analyze.done.genderUnknown"),
            };

            CountryDisplay = CountryFlags.GetFlagWithIso(r.Iso);

            SourceDisplay = r.Source switch
            {
                "db" or "db+morph" => Loc.T("analyze.done.sourceDb"),
                "ai" => Loc.T("analyze.done.sourceAi"),
                "ai+ai2" => Loc.T("analyze.done.sourceAi2"),
                _ => Loc.T("analyze.done.sourceUnknown"),
            };
        }
    }
}
