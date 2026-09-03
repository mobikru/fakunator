using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
using Fakunator.Core;

namespace Fakunator.Views;

public partial class SidebarCleanupView : UserControl
{
    private readonly CheckBox[] _categoryChecks;
    private CancellationTokenSource? _updateCts;

    public SidebarCleanupView()
    {
        InitializeComponent();
        _categoryChecks = new CheckBox[Tokens.CategoryOrder.Length];
        BuildCategoryPopup();
        LoadCategoryStateFromConfig();
        UpdateCategoryButtonText();

        // Restore Spamhaus DBL toggle from config
        SpamhausToggle.IsOn = Config.Current.UseSpamhausDbl;
    }

    private void BuildCategoryPopup()
    {
        for (int i = 0; i < Tokens.CategoryOrder.Length; i++)
        {
            var key = Tokens.CategoryOrder[i];
            var label = Tokens.CategoryLabels[key];
            var color = Tokens.CategoryColors[key];

            var row = new DockPanel { Margin = new Thickness(4, 3, 4, 3) };

            // Colored dot
            var dot = new Ellipse
            {
                Width = 8,
                Height = 8,
                Fill = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color)),
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 8, 0)
            };
            DockPanel.SetDock(dot, Dock.Left);
            row.Children.Add(dot);

            // Count (right-aligned placeholder)
            var countBlock = new TextBlock
            {
                Text = "—",
                FontSize = 11,
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(8, 0, 0, 0)
            };
            countBlock.SetResourceReference(TextBlock.ForegroundProperty, "Fg4Brush");
            DockPanel.SetDock(countBlock, Dock.Right);
            row.Children.Add(countBlock);

            // CheckBox with label
            var isForced = key == "invalid" || key == "duplicate";
            var cb = new CheckBox
            {
                Content = label,
                IsChecked = true,
                IsEnabled = !isForced,
                FontSize = 12,
                VerticalAlignment = VerticalAlignment.Center,
                Tag = key
            };
            cb.SetResourceReference(ForegroundProperty, "Fg1Brush");
            cb.Checked += OnCategoryCheckChanged;
            cb.Unchecked += OnCategoryCheckChanged;
            _categoryChecks[i] = cb;

            row.Children.Add(cb);
            CategoryCheckList.Children.Add(row);
        }
    }

    /// <summary>
    /// Restore category checkbox states from the config.
    /// </summary>
    private void LoadCategoryStateFromConfig()
    {
        var cfg = Config.Current;
        var enabledSet = cfg.EnabledFilters
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var cb in _categoryChecks)
        {
            if (cb.Tag is string key)
            {
                var isForced = key == "invalid" || key == "duplicate";
                cb.IsChecked = isForced || enabledSet.Contains(key);
            }
        }
    }

    /// <summary>
    /// Returns the list of currently enabled filter categories.
    /// </summary>
    public string[] GetEnabledFilters()
    {
        return _categoryChecks
            .Where(cb => cb.IsChecked == true && cb.Tag is string)
            .Select(cb => (string)cb.Tag!)
            .ToArray();
    }

    private void OnCategoryDropdownClick(object sender, RoutedEventArgs e)
    {
        CategoryPopup.IsOpen = !CategoryPopup.IsOpen;
    }

    private void OnCategoryCheckChanged(object sender, RoutedEventArgs e)
    {
        UpdateCategoryButtonText();
        SaveCategoryStateToConfig();
    }

    private void SaveCategoryStateToConfig()
    {
        var cfg = Config.Current;
        cfg.EnabledFilters = _categoryChecks
            .Where(cb => cb.IsChecked == true && cb.Tag is string)
            .Select(cb => (string)cb.Tag!)
            .ToList();
        cfg.Save();
    }

    private void UpdateCategoryButtonText()
    {
        var checkedCount = _categoryChecks.Count(cb => cb.IsChecked == true);
        var total = _categoryChecks.Length;

        var prefix = checkedCount == total ? "Все категории" : $"{checkedCount} категорий";
        TxtCategoryBtn.Text = $"{prefix} · {checkedCount} / {total} ▾";
    }

    private void OnOpenBlacklistFile(object sender, MouseButtonEventArgs e)
    {
        if (sender is TextBlock tb && tb.Tag is string tag)
        {
            var dataDir = Blocklists.FindDataDir();
            var fileName = tag switch
            {
                "disposable" => "disposable_domains.txt",
                "trap" => "spamtrap_patterns.txt",
                "role" => "role_prefixes.txt",
                "whitelist" => "allow_domains.txt",
                _ => null
            };

            if (fileName == null) return;

            var path = System.IO.Path.Combine(dataDir, fileName);
            if (File.Exists(path))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    MessageBox.Show($"Не удалось открыть файл:\n{ex.Message}",
                        "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
                }
            }
            else
            {
                MessageBox.Show($"Файл не найден:\n{path}",
                    "Чёрные списки", MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
    }

    private async void OnRefreshFromGitHub(object sender, RoutedEventArgs e)
    {
        // Cancel any previous update
        _updateCts?.Cancel();
        _updateCts = new CancellationTokenSource();
        var ct = _updateCts.Token;

        BtnRefreshGitHub.IsEnabled = false;
        TxtUpdateProgress.Visibility = Visibility.Visible;
        TxtUpdateProgress.Text = "Подключение к GitHub...";

        var dataDir = Blocklists.FindDataDir();
        var progress = new Progress<string>(msg =>
        {
            TxtUpdateProgress.Text = msg;
        });

        try
        {
            var (updated, error) = await BlocklistUpdater.UpdateAsync(dataDir, progress, ct);

            if (error != null)
            {
                // Показываем краткую ошибку в статус-строке, полный текст — в MessageBox.
                var shortErr = error.Length > 80 ? error.Substring(0, 77) + "..." : error;
                TxtUpdateProgress.Text = $"Обновлено: {updated}, ошибка: {shortErr}";
                TxtUpdateProgress.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
                System.Windows.MessageBox.Show(
                    $"При обновлении списков произошла ошибка:\n\n{error}",
                    "Ошибка обновления", System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Warning);
            }
            else if (updated > 0)
            {
                TxtUpdateProgress.SetResourceReference(TextBlock.ForegroundProperty, "CleanBrush");
            }
            else
            {
                TxtUpdateProgress.SetResourceReference(TextBlock.ForegroundProperty, "Fg3Brush");
            }
        }
        catch (OperationCanceledException)
        {
            TxtUpdateProgress.Text = "Обновление отменено";
        }
        catch (Exception ex)
        {
            TxtUpdateProgress.Text = $"Ошибка: {ex.Message}";
            TxtUpdateProgress.SetResourceReference(TextBlock.ForegroundProperty, "DangerBrush");
        }
        finally
        {
            BtnRefreshGitHub.IsEnabled = true;
        }
    }

    private void OnSpamhausToggled(object? sender, EventArgs e)
    {
        if (sender is Controls.ToggleSwitch ts)
        {
            Config.Current.UseSpamhausDbl = ts.IsOn;
            Config.Current.Save();
        }
    }
}
