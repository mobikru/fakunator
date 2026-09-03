using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Fakunator.ViewModels;
using Microsoft.Win32;

namespace Fakunator.Views;

public partial class SmtpIdleView : UserControl
{
    public SmtpIdleView()
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
            UpdateCounts(vm);
            UpdateRunInfo(vm);

            // Push initial values from VM into the textboxes (e.g. proxies restored from config)
            if (!string.IsNullOrEmpty(vm.ProxiesText) && string.IsNullOrEmpty(TxtProxies.Text))
                TxtProxies.Text = vm.ProxiesText;
            if (!string.IsNullOrEmpty(vm.EmailsText) && string.IsNullOrEmpty(TxtEmails.Text))
                TxtEmails.Text = vm.EmailsText;
        }
    }

    private void OnVmPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not SmtpViewModel vm) return;

        if (e.PropertyName is nameof(SmtpViewModel.EmailCount) or nameof(SmtpViewModel.ProxyCount))
        {
            UpdateCounts(vm);
            UpdateRunInfo(vm);
        }
    }

    private void UpdateCounts(SmtpViewModel vm)
    {
        TxtEmailCount.Text = $"{vm.EmailCount} строк";
        TxtProxyCount.Text = $"{vm.ProxyCount} прокси";
    }

    private void UpdateRunInfo(SmtpViewModel vm)
    {
        TxtRunInfo.Text = $"прокси {vm.ProxyCount} · адресов {vm.EmailCount} · в папку smtp_output/";
    }

    // ── Email text ───────────────────────────────────────────────────

    private void OnEmailsTextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is SmtpViewModel vm)
            vm.EmailsText = TxtEmails.Text;
    }

    private void OnLoadEmailsFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Загрузить email-адреса",
            Filter = "Text files|*.txt;*.csv|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                TxtEmails.Text = File.ReadAllText(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void OnLoadFromCleanup(object sender, RoutedEventArgs e)
    {
        if (DataContext is not SmtpViewModel vm) return;

        // Subscribe once to catch the async update
        void OnPropChanged(object? s, System.ComponentModel.PropertyChangedEventArgs args)
        {
            if (args.PropertyName == nameof(SmtpViewModel.EmailsText))
            {
                Dispatcher.Invoke(() =>
                {
                    TxtEmails.Text = vm.EmailsText;
                    TxtEmailCount.Text = $"{vm.EmailCount:N0} строк";
                });
                vm.PropertyChanged -= OnPropChanged;
            }
        }
        vm.PropertyChanged += OnPropChanged;
        vm.LoadFromLastCleanup();
    }

    // ── Proxy text ───────────────────────────────────────────────────

    private void OnProxiesTextChanged(object sender, TextChangedEventArgs e)
    {
        if (DataContext is SmtpViewModel vm)
            vm.ProxiesText = TxtProxies.Text;
    }

    private void OnCheckProxies(object sender, RoutedEventArgs e)
    {
        var proxyLines = TxtProxies.Text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => l.Trim())
            .Where(l => !string.IsNullOrEmpty(l))
            .ToArray();

        if (proxyLines.Length == 0)
        {
            MessageBox.Show("Нет прокси для проверки", "Проверка", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new ProxyCheckDialog(proxyLines) { Owner = Window.GetWindow(this) };
        var useAlive = dlg.ShowDialog() == true;

        TxtProxyStatus.Foreground = (System.Windows.Media.Brush)FindResource(
            dlg.AliveCount > 0 ? "CleanBrush" : "DangerBrush");
        TxtProxyStatus.Text = $"✓ {dlg.AliveCount} живых · ✗ {dlg.DeadCount} мёртвых";

        if (useAlive && dlg.AliveProxies.Count > 0)
        {
            TxtProxies.Text = string.Join("\n", dlg.AliveProxies);
        }
    }

    private void OnLoadProxiesFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Загрузить прокси",
            Filter = "Text files|*.txt|All files|*.*",
            CheckFileExists = true,
        };
        if (dlg.ShowDialog() == true)
        {
            try
            {
                TxtProxies.Text = File.ReadAllText(dlg.FileName);
            }
            catch (Exception ex)
            {
                MessageBox.Show($"Ошибка: {ex.Message}", "Ошибка", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    // ── Actions ──────────────────────────────────────────────────────

    private void OnStartClick(object sender, RoutedEventArgs e)
    {
        if (DataContext is SmtpViewModel vm)
            vm.StartCommand.Execute(null);
    }

    private void OnOpenOutputFolder(object sender, RoutedEventArgs e)
    {
        var dir = Fakunator.Core.Paths.SmtpRoot;
        if (Directory.Exists(dir))
        {
            try
            {
                Process.Start(new ProcessStartInfo(dir) { UseShellExecute = true });
            }
            catch { }
        }
    }
}
