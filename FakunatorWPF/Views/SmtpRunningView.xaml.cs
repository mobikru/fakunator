using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Fakunator.Core;
using Fakunator.ViewModels;

namespace Fakunator.Views;

public partial class SmtpRunningView : UserControl
{
    private const int MaxFeedItems = 20;
    private readonly List<UIElement> _feedRows = new();

    public SmtpRunningView()
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
            FeedPanel.Children.Clear();
            _feedRows.Clear();
            UpdateFromSnapshot(vm.CurrentSnapshot);
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SmtpViewModel vm) return;
        if (e.PropertyName == nameof(SmtpViewModel.CurrentSnapshot))
            UpdateFromSnapshot(vm.CurrentSnapshot);
    }

    private void UpdateFromSnapshot(SmtpSnapshotBatch? snap)
    {
        if (snap is null)
        {
            TxtPercent.Text = "0.0%";
            TxtProcessed.Text = "0 / 0 обработано";
            TxtSpeed.Text = "скорость 0/с · 0 сессий";
            KpiProcessed.Text = "0";
            KpiValid.Text = "0";
            KpiInvalid.Text = "0";
            KpiCatchall.Text = "0";
            KpiUnknown.Text = "0";
            KpiErrors.Text = "0";
            ProgressFill.Width = 0;
            FeedPanel.Children.Clear();
            _feedRows.Clear();
            return;
        }

        double pct = snap.Total > 0 ? (double)snap.Processed / snap.Total * 100 : 0;
        TxtPercent.Text = $"{pct:F1}%";
        TxtProcessed.Text = $"{snap.Processed:N0} / {snap.Total:N0} обработано";
        TxtSpeed.Text = $"скорость {snap.Speed:N1}/с · {snap.ActiveSessions} сессий";

        // Progress bar width
        var trackBorder = ProgressFill.Parent as FrameworkElement;
        double trackWidth = trackBorder?.ActualWidth ?? 0;
        if (trackWidth > 0)
            ProgressFill.Width = Math.Max(0, Math.Min(trackWidth, trackWidth * pct / 100.0));
        else
            ProgressFill.Width = 0;

        // KPIs
        KpiProcessed.Text = snap.Processed.ToString("N0");
        KpiValid.Text = GetCount(snap, Verdicts.Valid).ToString("N0");
        KpiInvalid.Text = GetCount(snap, Verdicts.Invalid).ToString("N0");
        KpiCatchall.Text = GetCount(snap, Verdicts.Catchall).ToString("N0");
        KpiUnknown.Text = GetCount(snap, Verdicts.Unknown).ToString("N0");
        KpiErrors.Text = GetCount(snap, Verdicts.Error).ToString("N0");

        // Live feed
        UpdateFeed(snap.FreshFeed);

        // Proxy monitor
        UpdateProxyStats(snap.ProxyStats);
    }

    private void UpdateProxyStats(List<Fakunator.Core.ProxyStatsEntry>? stats)
    {
        if (stats is null || stats.Count == 0) return;

        ProxyStatsPanel.Children.Clear();
        foreach (var ps in stats)
        {
            var row = new System.Windows.Controls.Grid();
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(70) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(80) });
            row.Margin = new Thickness(0, 0, 0, 2);

            var addr = new TextBlock
            {
                Text = ps.Address, FontSize = 11,
                FontFamily = (FontFamily)FindResource("MonoFont"),
            };
            addr.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
            System.Windows.Controls.Grid.SetColumn(addr, 0);
            row.Children.Add(addr);

            var ok = new TextBlock
            {
                Text = ps.Successes.ToString("N0"), FontSize = 11,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            ok.SetResourceReference(TextBlock.ForegroundProperty, "CleanBrush");
            System.Windows.Controls.Grid.SetColumn(ok, 1);
            row.Children.Add(ok);

            var err = new TextBlock
            {
                Text = ps.Errors.ToString("N0"), FontSize = 11,
                FontFamily = (FontFamily)FindResource("MonoFont"),
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            err.SetResourceReference(TextBlock.ForegroundProperty, ps.Errors > 0 ? "DangerBrush" : "Fg4Brush");
            System.Windows.Controls.Grid.SetColumn(err, 2);
            row.Children.Add(err);

            var statusColor = ps.Status switch
            {
                "Healthy" => "CleanBrush",
                "Slow" => "Fg3Brush",
                "Failed" or "Disabled" => "DangerBrush",
                _ => "Fg4Brush",
            };
            var statusLabel = ps.Status switch
            {
                "Healthy" => "Healthy",
                "Slow" => "Slow",
                "Failed" => "Failed",
                "Disabled" => "Disabled",
                _ => "Idle",
            };
            var st = new TextBlock
            {
                Text = statusLabel, FontSize = 11, FontWeight = FontWeights.SemiBold,
                HorizontalAlignment = HorizontalAlignment.Right,
            };
            st.SetResourceReference(TextBlock.ForegroundProperty, statusColor);
            System.Windows.Controls.Grid.SetColumn(st, 3);
            row.Children.Add(st);

            ProxyStatsPanel.Children.Add(row);
        }
    }

    private static int GetCount(SmtpSnapshotBatch snap, string verdict)
    {
        return snap.Counts.TryGetValue(verdict, out var v) ? v : 0;
    }

    private void UpdateFeed(List<SmtpVerdict> freshFeed)
    {
        if (freshFeed is null || freshFeed.Count == 0) return;

        foreach (var item in freshFeed)
        {
            var row = BuildFeedRow(item);
            FeedPanel.Children.Add(row);
            _feedRows.Add(row);
        }

        // Trim
        while (_feedRows.Count > MaxFeedItems)
        {
            var oldest = _feedRows[0];
            _feedRows.RemoveAt(0);
            FeedPanel.Children.Remove(oldest);
        }

        FeedScroller.ScrollToEnd();
    }

    private UIElement BuildFeedRow(SmtpVerdict v)
    {
        var colorHex = Verdicts.Colors.TryGetValue(v.Verdict, out var ch) ? ch : "#71717a";
        var label = Verdicts.Labels.TryGetValue(v.Verdict, out var lb) ? lb : v.Verdict;
        var catColor = ColorFromHex(colorHex);

        var row = new DockPanel { Margin = new Thickness(0, 0, 0, 3) };

        // Verdict badge (right)
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

        // MX info (right)
        if (!string.IsNullOrEmpty(v.MxHost))
        {
            var mxTb = new TextBlock
            {
                Text = v.MxHost,
                FontSize = 10,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0),
                MaxWidth = 140,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            mxTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
            DockPanel.SetDock(mxTb, Dock.Right);
            row.Children.Add(mxTb);
        }

        // Email (monospace, elided)
        var emailTb = new TextBlock
        {
            Text = v.Email,
            FontFamily = (FontFamily)FindResource("MonoFont"),
            FontSize = 11,
            VerticalAlignment = VerticalAlignment.Center,
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        emailTb.SetResourceReference(TextBlock.ForegroundProperty, "Fg2Brush");
        row.Children.Add(emailTb);

        return row;
    }

    private void OnStopClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SmtpViewModel vm)
            vm.StopCommand.Execute(null);
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
