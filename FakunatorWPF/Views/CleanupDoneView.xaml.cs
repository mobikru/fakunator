using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fakunator.Core;
using Fakunator.ViewModels;

namespace Fakunator.Views;

public partial class CleanupDoneView : UserControl
{
    public CleanupDoneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is CleanupViewModel oldVm)
        {
            oldVm.PropertyChanged -= OnViewModelPropertyChanged;
        }

        if (e.NewValue is CleanupViewModel vm)
        {
            vm.PropertyChanged += OnViewModelPropertyChanged;
            if (vm.State == CleanupViewModel.CleanupState.Done)
            {
                PopulateFromSnapshot(vm);
            }
        }
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not CleanupViewModel vm) return;

        if (e.PropertyName == nameof(CleanupViewModel.State) &&
            vm.State == CleanupViewModel.CleanupState.Done)
        {
            PopulateFromSnapshot(vm);
        }
    }

    private void PopulateFromSnapshot(CleanupViewModel vm)
    {
        var snap = vm.FinalSnapshot;
        if (snap is null) return;

        // Hero stats
        var ts = TimeSpan.FromSeconds(snap.Elapsed);
        HeroElapsed.Text = ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"mm\:ss");

        HeroSpeed.Text = $"{snap.Speed:N0}/с";
        HeroFileSize.Text = FormatSize(vm.FileSize);

        // Subtitle
        int cleanCount = snap.Counts.TryGetValue("clean", out var c) ? c : 0;
        double cleanPct = snap.Total > 0 ? (double)cleanCount / snap.Total * 100 : 0;
        TxtSubtitle.Text = $"Из {snap.Total:N0} строк получено {cleanCount:N0} чистых адресов ({cleanPct:F1}%)";

        // Clean + Duplicate big cards
        CleanCount.Text = cleanCount.ToString("N0");
        CleanPct.Text = $"{cleanPct:F1}% · итоговая база";

        int dupCount = snap.Counts.TryGetValue("duplicate", out var d) ? d : 0;
        double dupPct = snap.Total > 0 ? (double)dupCount / snap.Total * 100 : 0;
        DupCount.Text = dupCount.ToString("N0");
        DupPct.Text = $"{dupPct:F1}% · учтены правила gmail";

        // Provider donut legend
        BuildDonutLegend(snap);

        // Category cards (without clean and duplicate — they're above)
        BuildCategoryCards(snap, vm);
    }

    private void BuildDonutLegend(SnapshotBatch snap)
    {
        DonutLegend.Children.Clear();
        int totalClean = snap.Providers.Values.Sum();
        if (totalClean == 0) totalClean = 1;

        foreach (var key in Tokens.ProviderColors.Keys)
        {
            int count = snap.Providers.TryGetValue(key, out var v) ? v : 0;
            if (count == 0) continue;

            string label = Tokens.ProviderLabels.TryGetValue(key, out var l) ? l : key;
            string colorHex = Tokens.ProviderColors[key];
            var color = (Color)ColorConverter.ConvertFromString(colorHex);

            var row = new DockPanel { Margin = new Thickness(0, 0, 0, 4) };

            // Color dot
            var dot = new Border
            {
                Width = 10, Height = 10,
                CornerRadius = new CornerRadius(5),
                Background = new SolidColorBrush(color),
                Margin = new Thickness(0, 0, 8, 0),
                VerticalAlignment = VerticalAlignment.Center,
            };
            row.Children.Add(dot);

            // Label
            var lblText = new TextBlock
            {
                Text = label,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
            };
            lblText.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
            row.Children.Add(lblText);

            // Count (right-aligned)
            var countText = new TextBlock
            {
                Text = count.ToString("N0"),
                FontSize = 12,
                FontWeight = FontWeights.SemiBold,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                HorizontalAlignment = HorizontalAlignment.Right,
                VerticalAlignment = VerticalAlignment.Center,
            };
            countText.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
            DockPanel.SetDock(countText, Dock.Right);
            row.Children.Insert(0, countText);

            DonutLegend.Children.Add(row);
        }
    }

    private void BuildCategoryCards(SnapshotBatch snap, CleanupViewModel vm)
    {
        CategoryPanel.Children.Clear();

        foreach (var cat in Tokens.CategoryOrder)
        {
            if (cat is "clean" or "duplicate") continue;

            int count = snap.Counts.TryGetValue(cat, out var v) ? v : 0;
            double pct = snap.Total > 0 ? (double)count / snap.Total * 100 : 0;

            string label = Tokens.CategoryLabels.TryGetValue(cat, out var l) ? l : cat;
            string colorHex = Tokens.CategoryColors.TryGetValue(cat, out var ch) ? ch : "#71717a";

            var color = (Color)ColorConverter.ConvertFromString(colorHex);

            // Outer card border
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Width = 150,
                Margin = new Thickness(0, 0, 10, 10),
                Padding = new Thickness(0),
                ClipToBounds = true,
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "Border1Brush");

            var innerStack = new StackPanel();

            // Colored top stripe
            var stripe = new Border
            {
                Height = 3,
                Background = new SolidColorBrush(color),
            };
            innerStack.Children.Add(stripe);

            // Content area
            var contentPanel = new StackPanel { Margin = new Thickness(12, 10, 12, 10) };

            // Category label
            var lblText = new TextBlock
            {
                Text = label,
                FontWeight = FontWeights.Bold,
                FontSize = 12,
            };
            lblText.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
            contentPanel.Children.Add(lblText);

            // Count
            var countText = new TextBlock
            {
                Text = count.ToString("N0"),
                FontSize = 20,
                FontWeight = FontWeights.Bold,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                Margin = new Thickness(0, 4, 0, 2),
            };
            countText.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
            contentPanel.Children.Add(countText);

            // Percentage
            var pctText = new TextBlock
            {
                Text = $"{pct:F1}%",
                FontSize = 11,
            };
            pctText.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
            contentPanel.Children.Add(pctText);

            // Download link button. Typo is special: corrections live in typo_corrected.txt
            // (the rejected_typo.txt is empty after auto-fix).
            var fileName = cat == "typo" ? "typo_corrected.txt" : $"rejected_{cat}.txt";
            var linkBtn = new Button
            {
                Content = $"↓ {fileName}",
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = cat,
            };
            linkBtn.SetResourceReference(Button.ForegroundProperty, "Accent2Brush");
            linkBtn.Click += OnCategoryFileClick;
            contentPanel.Children.Add(linkBtn);

            innerStack.Children.Add(contentPanel);
            card.Child = innerStack;
            CategoryPanel.Children.Add(card);
        }
    }

    private void OnCategoryFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string cat) return;
        if (DataContext is not CleanupViewModel vm || string.IsNullOrEmpty(vm.OutputDir)) return;

        var fileName = cat == "typo" ? "typo_corrected.txt" : $"rejected_{cat}.txt";
        var filePath = Path.Combine(vm.OutputDir, fileName);
        if (File.Exists(filePath))
        {
            try
            {
                Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true });
            }
            catch
            {
                // Best effort
            }
        }
    }

    private void OnCleanFileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not CleanupViewModel vm || string.IsNullOrEmpty(vm.OutputDir)) return;
        var filePath = Path.Combine(vm.OutputDir, "clean.txt");
        if (File.Exists(filePath))
        {
            try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
            catch { }
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is CleanupViewModel vm && !string.IsNullOrEmpty(vm.OutputDir))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = vm.OutputDir,
                    UseShellExecute = true,
                });
            }
            catch
            {
                // Best effort
            }
        }
    }

    private void OnNewFileClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is CleanupViewModel vm)
        {
            vm.ResetToIdle();
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024.0 * 1024):F1} MB";
        return $"{bytes / (1024.0 * 1024 * 1024):F2} GB";
    }
}
