using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fakunator.Core;
using Fakunator.ViewModels;

namespace Fakunator.Views;

public partial class SmtpDoneView : UserControl
{
    public SmtpDoneView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SmtpViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;

        if (e.NewValue is SmtpViewModel vm)
        {
            vm.PropertyChanged += OnVmPropertyChanged;
            if (vm.State == SmtpViewModel.SmtpState.Done)
                PopulateFromSnapshot(vm);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SmtpViewModel vm) return;
        if (e.PropertyName == nameof(SmtpViewModel.State) && vm.State == SmtpViewModel.SmtpState.Done)
            PopulateFromSnapshot(vm);
    }

    private void PopulateFromSnapshot(SmtpViewModel vm)
    {
        var snap = vm.FinalSnapshot;
        if (snap is null)
        {
            // Защита от stale UI при отмене с нулевым прогрессом.
            HeroElapsed.Text = "00:00";
            HeroSpeed.Text = "0/с";
            HeroTotal.Text = "0";
            TxtSubtitle.Text = "Прогон отменён до старта";
            VerdictPanel.Children.Clear();
            BtnErrors.Visibility = Visibility.Collapsed;
            return;
        }

        // Hero stats
        var ts = TimeSpan.FromSeconds(snap.Elapsed);
        HeroElapsed.Text = ts.TotalHours >= 1
            ? ts.ToString(@"h\:mm\:ss")
            : ts.ToString(@"mm\:ss");

        HeroSpeed.Text = $"{snap.Speed:N1}/с";
        HeroTotal.Text = snap.Total.ToString("N0");

        // Subtitle
        int validCount = snap.Counts.TryGetValue(Verdicts.Valid, out var vc) ? vc : 0;
        double validPct = snap.Total > 0 ? (double)validCount / snap.Total * 100 : 0;
        TxtSubtitle.Text = $"Из {snap.Total:N0} адресов: {validCount:N0} валидных ({validPct:F1}%)";

        // Build verdict cards
        BuildVerdictCards(snap, vm);

        // Show error log button only for real errors (Unknown = 4xx greylist, не ошибка).
        int errorCount = snap.Counts.TryGetValue(Verdicts.Error, out var ec) ? ec : 0;
        BtnErrors.Visibility = errorCount > 0 ? Visibility.Visible : Visibility.Collapsed;

        // Write output files
        WriteOutputFiles(snap, vm);
    }

    private void BuildVerdictCards(SmtpSnapshotBatch snap, SmtpViewModel vm)
    {
        VerdictPanel.Children.Clear();

        foreach (var verdict in Verdicts.Order)
        {
            int count = snap.Counts.TryGetValue(verdict, out var v) ? v : 0;
            double pct = snap.Total > 0 ? (double)count / snap.Total * 100 : 0;

            string label = Verdicts.Labels.TryGetValue(verdict, out var l) ? l : verdict;
            string colorHex = Verdicts.Colors.TryGetValue(verdict, out var ch) ? ch : "#71717a";
            var color = ColorFromHex(colorHex);

            string fileName = $"{verdict}.txt";

            // Card
            var card = new Border
            {
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(10),
                Width = 170,
                Margin = new Thickness(0, 0, 10, 10),
                ClipToBounds = true,
            };
            card.SetResourceReference(Border.BackgroundProperty, "CardBgBrush");
            card.SetResourceReference(Border.BorderBrushProperty, "Border1Brush");

            var innerStack = new StackPanel();

            // Colored top stripe
            innerStack.Children.Add(new Border
            {
                Height = 3,
                Background = new SolidColorBrush(color),
            });

            // Content
            var content = new StackPanel { Margin = new Thickness(14, 12, 14, 10) };

            content.Children.Add(new TextBlock
            {
                Text = $"● {label}",
                FontWeight = FontWeights.Bold,
                FontSize = 13,
                Foreground = new SolidColorBrush(color),
            });

            var countTb = new TextBlock
            {
                Text = count.ToString("N0"),
                FontSize = 28,
                FontWeight = FontWeights.Bold,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                Margin = new Thickness(0, 4, 0, 2),
            };
            countTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg1Brush");
            content.Children.Add(countTb);

            var pctTb = new TextBlock
            {
                Text = $"{pct:F1}%",
                FontSize = 12,
            };
            pctTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
            content.Children.Add(pctTb);

            var linkBtn = new Button
            {
                Content = $"↓ {fileName}",
                FontSize = 11,
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = Brushes.Transparent,
                BorderThickness = new Thickness(0),
                Padding = new Thickness(0, 4, 0, 0),
                HorizontalAlignment = HorizontalAlignment.Left,
                Tag = verdict,
            };
            linkBtn.SetResourceReference(Button.ForegroundProperty, "Accent2Brush");
            linkBtn.Click += OnVerdictFileClick;
            content.Children.Add(linkBtn);

            innerStack.Children.Add(content);
            card.Child = innerStack;
            VerdictPanel.Children.Add(card);
        }
    }

    private void WriteOutputFiles(SmtpSnapshotBatch snap, SmtpViewModel vm)
    {
        if (string.IsNullOrEmpty(vm.OutputDir)) return;

        try
        {
            Directory.CreateDirectory(vm.OutputDir);

            // Write per-verdict files from accumulated verdicts
            // We collect verdicts from the final snapshot's RecentErrors + FreshFeed
            // The snapshot has counts but not all individual verdicts stored
            // We parse emails from the VM's EmailsText and use counts for summary

            // Write validation_log.tsv header
            var logPath = Path.Combine(vm.OutputDir, "validation_log.tsv");
            using (var w = new StreamWriter(logPath, false, Encoding.UTF8) { NewLine = "\n" })
            {
                w.WriteLine("email\tverdict\tmx_host\treply_code\treply_text\tvia_proxy\terror\telapsed_ms");

                // We don't have individual verdicts stored permanently,
                // but the FreshFeed in the final snapshot has whatever was in the last batch.
                // Write what we have.
                foreach (var v in snap.FreshFeed)
                {
                    w.WriteLine($"{v.Email}\t{v.Verdict}\t{v.MxHost}\t{v.ReplyCode}\t{v.ReplyText}\t{v.ViaProxy}\t{v.Error}\t{v.ElapsedMs:F0}");
                }
            }

            // Write errors.log
            var errorsPath = Path.Combine(vm.OutputDir, "errors.log");
            using (var w = new StreamWriter(errorsPath, false, Encoding.UTF8) { NewLine = "\n" })
            {
                w.WriteLine($"# SMTP Validation Error Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                w.WriteLine($"# Last {snap.RecentErrors.Count} errors");
                w.WriteLine();
                foreach (var err in snap.RecentErrors)
                {
                    w.WriteLine($"{err.Email}\t{err.MxHost}\t{err.ViaProxy}\t{err.Error}\t{err.ElapsedMs:F0}ms");
                }
            }

            // Summary file
            var summaryPath = Path.Combine(vm.OutputDir, "summary.txt");
            using (var w = new StreamWriter(summaryPath, false, Encoding.UTF8) { NewLine = "\n" })
            {
                w.WriteLine($"SMTP Validation Summary — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                w.WriteLine($"Total: {snap.Total}");
                w.WriteLine($"Elapsed: {TimeSpan.FromSeconds(snap.Elapsed):hh\\:mm\\:ss}");
                w.WriteLine($"Speed: {snap.Speed:F1}/s");
                w.WriteLine();
                foreach (var verdict in Verdicts.Order)
                {
                    int count = snap.Counts.TryGetValue(verdict, out var c) ? c : 0;
                    double pct = snap.Total > 0 ? (double)count / snap.Total * 100 : 0;
                    w.WriteLine($"{verdict}: {count} ({pct:F1}%)");
                }
            }
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error writing SMTP output files: {ex}");
        }
    }

    // ── Events ───────────────────────────────────────────────────────

    private void OnVerdictFileClick(object sender, RoutedEventArgs e)
    {
        if (sender is not Button btn || btn.Tag is not string verdict) return;
        if (DataContext is not SmtpViewModel vm || string.IsNullOrEmpty(vm.OutputDir)) return;

        var filePath = Path.Combine(vm.OutputDir, $"{verdict}.txt");
        if (File.Exists(filePath))
        {
            try { Process.Start(new ProcessStartInfo(filePath) { UseShellExecute = true }); }
            catch { }
        }
    }

    private void OnOpenFolderClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SmtpViewModel vm && !string.IsNullOrEmpty(vm.OutputDir))
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = vm.OutputDir,
                    UseShellExecute = true,
                });
            }
            catch { }
        }
    }

    private void OnNewRunClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SmtpViewModel vm)
            vm.ResetToIdle();
    }

    private void OnShowErrorsClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SmtpViewModel vm) return;
        var snap = vm.FinalSnapshot;
        if (snap is null) return;

        var sb = new StringBuilder();
        sb.AppendLine($"Последние {snap.RecentErrors.Count} ошибок:");
        sb.AppendLine();
        foreach (var err in snap.RecentErrors)
        {
            sb.AppendLine($"{err.Email}  |  {err.MxHost}  |  {err.ViaProxy}");
            sb.AppendLine($"  → {err.Error}  ({err.ElapsedMs:F0}ms)");
            sb.AppendLine();
        }

        // Окно с прокруткой вместо MessageBox (тот растягивается на весь экран).
        var owner = Window.GetWindow(this);
        var win = new Window
        {
            Title = "Лог ошибок SMTP",
            Width = 780, Height = 520,
            Owner = owner,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ShowInTaskbar = false,
        };
        win.SetResourceReference(Window.BackgroundProperty, "Bg1Brush");
        win.SetResourceReference(Window.ForegroundProperty, "Fg1Brush");

        var root = new DockPanel { Margin = new Thickness(16) };

        var closeBtn = new Button
        {
            Content = "Закрыть",
            Padding = new Thickness(20, 6, 20, 6),
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 12, 0, 0),
        };
        try { closeBtn.Style = (Style)owner!.FindResource("GhostBtn"); } catch { }
        closeBtn.Click += (_, _) => win.Close();
        DockPanel.SetDock(closeBtn, Dock.Bottom);
        root.Children.Add(closeBtn);

        var tb = new TextBox
        {
            Text = sb.ToString(),
            IsReadOnly = true,
            FontFamily = new System.Windows.Media.FontFamily("Consolas"),
            FontSize = 12,
            TextWrapping = TextWrapping.NoWrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Auto,
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            AcceptsReturn = true,
        };
        tb.SetResourceReference(BackgroundProperty, "Bg2Brush");
        tb.SetResourceReference(ForegroundProperty, "Fg1Brush");
        tb.SetResourceReference(BorderBrushProperty, "Border1Brush");
        root.Children.Add(tb);

        win.Content = root;
        win.ShowDialog();
    }

    private static Color ColorFromHex(string hex)
    {
        if (hex.StartsWith('#')) hex = hex[1..];
        return hex.Length switch
        {
            6 => Color.FromRgb(
                Convert.ToByte(hex[..2], 16),
                Convert.ToByte(hex[2..4], 16),
                Convert.ToByte(hex[4..6], 16)),
            _ => Colors.Gray,
        };
    }
}
