using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shapes;
using Fakunator.Core;
using Fakunator.ViewModels;

namespace Fakunator.Views;

public partial class CleanupRunningView : UserControl
{
    private const int MaxFeedItems = 15;

    // Category counter card references (non-clean categories)
    private static readonly string[] CounterCategories =
    [
        "duplicate", "invalid", "reserved", "trap",
        "role", "disposable", "corporate", "typo"
    ];

    private readonly Dictionary<string, (TextBlock Label, TextBlock Count, TextBlock Pct)> _counterCards = new();
    private readonly List<UIElement> _feedRows = new();
    private CleanupViewModel? _vm;

    public CleanupRunningView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        BuildCounterCards();
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            foreach (var cat in CounterCategories)
                if (_counterCards.TryGetValue(cat, out var refs))
                    refs.Label.Text = Loc.T($"cat.{cat}");
            if (_vm != null) UpdateFromSnapshot(_vm.CurrentSnapshot, _vm);
        };
    }

    // ── DataContext wiring ────────────────────────────────────────────

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is CleanupViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is CleanupViewModel vm)
        {
            _vm = vm;
            vm.PropertyChanged += OnViewModelPropertyChanged;
            Sparkline.Clear();
            FeedPanel.Children.Clear();
            _feedRows.Clear();
            UpdateFromSnapshot(vm.CurrentSnapshot, vm);
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CleanupViewModel vm) return;

        if (e.PropertyName == nameof(CleanupViewModel.CurrentSnapshot))
        {
            UpdateFromSnapshot(vm.CurrentSnapshot, vm);
        }
    }

    // ── Main update ──────────────────────────────────────────────────

    private void UpdateFromSnapshot(SnapshotBatch? snap, CleanupViewModel vm)
    {
        if (snap is null)
        {
            TxtPercent.Text = "0.0%";
            TxtProcessed.Text = string.Format(Loc.T("cleanup.running.processed"), 0, 0);
            TxtSpeed.Text = string.Format(Loc.T("cleanup.running.speed"), 0);
            KpiProcessed.Text = "0";
            KpiClean.Text = "0";
            KpiRejected.Text = "0";
            KpiSpeed.Text = "0/с";
            KpiElapsed.Text = "00:00";
            ProgressFill.Width = 0;
            ProviderLegendPanel.Children.Clear();
            Sparkline.Clear();
            FeedPanel.Children.Clear();
            _feedRows.Clear();
            ResetCounterCards();
            return;
        }

        double pct = snap.Total > 0 ? (double)snap.Processed / snap.Total * 100 : 0;
        TxtPercent.Text = $"{pct:F1}%";
        TxtProcessed.Text = string.Format(Loc.T("cleanup.running.processed"), snap.Processed, snap.Total);
        TxtSpeed.Text = string.Format(Loc.T("cleanup.running.speed"), snap.Speed);

        // Progress bar: calculate width relative to parent track
        var trackBorder = ProgressFill.Parent as FrameworkElement;
        double trackWidth = trackBorder?.ActualWidth ?? 0;
        if (trackWidth > 0)
        {
            ProgressFill.Width = Math.Max(0, Math.Min(trackWidth, trackWidth * pct / 100.0));
        }
        else
        {
            ProgressFill.Width = 0;
        }

        // KPIs
        KpiProcessed.Text = snap.Processed.ToString("N0");

        int cleanCount = snap.Counts.TryGetValue("clean", out var c) ? c : 0;
        KpiClean.Text = cleanCount.ToString("N0");

        int rejected = snap.Processed - cleanCount;
        KpiRejected.Text = rejected.ToString("N0");

        KpiSpeed.Text = string.Format(Loc.T("unit.perSecShort"), snap.Speed);

        var ts = TimeSpan.FromSeconds(snap.Elapsed);
        KpiElapsed.Text = ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"mm\:ss");

        // Provider legend
        UpdateProviderLegend(snap.Providers);

        // Sparkline
        Sparkline.AddPoint(snap.ThroughputPoint);

        // Live feed
        UpdateFeed(snap.FreshFeed);

        // Counter cards
        UpdateCounterCards(snap.Counts, snap.Processed);
    }

    // ── Provider legend ──────────────────────────────────────────────

    private void UpdateProviderLegend(Dictionary<string, int> providers)
    {
        ProviderLegendPanel.Children.Clear();

        // Sort providers by count descending, skip zero
        var sorted = providers
            .Where(kv => kv.Value > 0)
            .OrderByDescending(kv => kv.Value);

        foreach (var kv in sorted)
        {
            var key = kv.Key;
            var count = kv.Value;

            var colorHex = Tokens.ProviderColors.TryGetValue(key, out var ch) ? ch : "#64748b";
            var label = Tokens.ProviderColors.ContainsKey(key) ? Loc.T($"provider.{key}") : key;
            var color = ColorFromHex(colorHex);

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 6) };

            // Colored dot
            var dot = new Ellipse
            {
                Width = 10,
                Height = 10,
                Fill = new SolidColorBrush(color),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);

            // Count (right-aligned, mono)
            var countTb = new TextBlock
            {
                Text = count.ToString("N0"),
                FontFamily = (FontFamily)FindResource("MonoFont"),
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            countTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
            DockPanel.SetDock(countTb, Dock.Right);
            row.Children.Add(countTb);

            // Label
            var labelTb = new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            labelTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
            row.Children.Add(labelTb);

            ProviderLegendPanel.Children.Add(row);
        }
    }

    // ── Live feed ────────────────────────────────────────────────────

    private void UpdateFeed(List<(int LineNo, string Email, string Category, string Note)> freshFeed)
    {
        if (freshFeed is null || freshFeed.Count == 0) return;

        foreach (var item in freshFeed)
        {
            var row = BuildFeedRow(item.Email, item.Category);
            FeedPanel.Children.Add(row);
            _feedRows.Add(row);
        }

        // Trim to MaxFeedItems
        while (_feedRows.Count > MaxFeedItems)
        {
            var oldest = _feedRows[0];
            _feedRows.RemoveAt(0);
            FeedPanel.Children.Remove(oldest);
        }

        // Auto-scroll to bottom
        FeedScroller.ScrollToEnd();
    }

    private UIElement BuildFeedRow(string email, string category)
    {
        var colorHex = Tokens.CategoryColors.TryGetValue(category, out var ch) ? ch : "#71717a";
        var label = Tokens.CategoryColors.ContainsKey(category) ? Loc.T($"cat.{category}") : category;
        var catColor = ColorFromHex(colorHex);

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };

        // Category badge (right)
        var badgeBg = Color.FromArgb(26, catColor.R, catColor.G, catColor.B);
        var badge = new Border
        {
            Background = new SolidColorBrush(badgeBg),
            CornerRadius = new CornerRadius(4),
            Padding = new Thickness(6, 2, 6, 2),
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 0, 0),
            Child = new TextBlock
            {
                Text = label,
                FontSize = 10,
                Foreground = new SolidColorBrush(catColor),
                FontWeight = FontWeights.SemiBold,
            }
        };
        DockPanel.SetDock(badge, Dock.Right);
        row.Children.Add(badge);

        // Email (monospace, elided)
        var emailTb = new TextBlock
        {
            Text = email,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        emailTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
        row.Children.Add(emailTb);

        return row;
    }

    // ── Counter cards ────────────────────────────────────────────────

    private void BuildCounterCards()
    {
        _counterCards.Clear();
        CounterGrid.Children.Clear();

        foreach (var cat in CounterCategories)
        {
            var colorHex = Tokens.CategoryColors.TryGetValue(cat, out var ch) ? ch : "#71717a";
            var label = Loc.T($"cat.{cat}");
            var catColor = ColorFromHex(colorHex);

            // Outer border (Card style applied manually so we can add the stripe)
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(12),
                Margin = new Thickness(4),
                ClipToBounds = true,
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "Border1Brush");

            var innerStack = new StackPanel();

            // Colored top stripe
            var stripe = new Border
            {
                Background = new SolidColorBrush(catColor),
                Height = 3,
                Margin = new Thickness(-1, -1, -1, 0), // bleed into the card border
            };
            innerStack.Children.Add(stripe);

            // Content area
            var content = new StackPanel
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(10, 8, 10, 10),
            };

            // Label
            var labelTb = new TextBlock
            {
                Text = label,
                FontSize = 10,
                FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Center,
            };
            labelTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
            content.Children.Add(labelTb);

            // Count
            var countTb = new TextBlock
            {
                Text = "0",
                FontSize = 16,
                FontWeight = FontWeights.Bold,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                Foreground = new SolidColorBrush(catColor),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 0),
            };
            content.Children.Add(countTb);

            // Percentage
            var pctTb = new TextBlock
            {
                Text = "0%",
                FontSize = 10,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            };
            pctTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
            content.Children.Add(pctTb);

            innerStack.Children.Add(content);
            card.Child = innerStack;
            CounterGrid.Children.Add(card);

            _counterCards[cat] = (labelTb, countTb, pctTb);
        }
    }

    private void UpdateCounterCards(Dictionary<string, int> counts, int processed)
    {
        foreach (var cat in CounterCategories)
        {
            if (!_counterCards.TryGetValue(cat, out var refs)) continue;

            int count = counts.TryGetValue(cat, out var v) ? v : 0;
            double pct = processed > 0 ? (double)count / processed * 100 : 0;

            refs.Count.Text = count.ToString("N0");
            refs.Pct.Text = $"{pct:F1}%";
        }
    }

    private void ResetCounterCards()
    {
        foreach (var refs in _counterCards.Values)
        {
            refs.Count.Text = "0";
            refs.Pct.Text = "0%";
        }
    }

    // ── Stop button ──────────────────────────────────────────────────

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is CleanupViewModel vm)
        {
            vm.StopCommand.Execute(null);
        }
    }

    // ── Helpers ───────────────────────────────────────────────────────

    private static Color ColorFromHex(string hex)
    {
        if (hex.StartsWith('#')) hex = hex[1..];

        return hex.Length switch
        {
            6 => Color.FromRgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16)),
            8 => Color.FromArgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16),
                Convert.ToByte(hex[6..8], 16)),
            _ => Colors.Gray,
        };
    }
}
