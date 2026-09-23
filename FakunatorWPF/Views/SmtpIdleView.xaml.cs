using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using Fakunator.Core;
using Fakunator.ViewModels;
using Microsoft.Win32;

namespace Fakunator.Views;

public partial class SmtpIdleView : UserControl
{
    private SmtpViewModel? _vm;

    public SmtpIdleView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            if (_vm != null)
            {
                UpdateCounts(_vm);
                UpdateRunInfo(_vm);
            }
        };
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        if (e.OldValue is SmtpViewModel oldVm)
            oldVm.PropertyChanged -= OnVmPropertyChanged;

        if (e.NewValue is SmtpViewModel vm)
        {
            _vm = vm;
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
        TxtEmailCount.Text = string.Format(Loc.T("smtp.idle.emailCount"), vm.EmailCount);
        TxtProxyCount.Text = string.Format(Loc.T("smtp.idle.proxyCount"), vm.ProxyCount);
    }

    private void UpdateRunInfo(SmtpViewModel vm)
    {
        TxtRunInfo.Text = string.Format(Loc.T("smtp.idle.runInfo"), vm.ProxyCount, vm.EmailCount);
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
            Title = Loc.T("smtp.idle.dialogLoadEmailsTitle"),
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
                MessageBox.Show(string.Format(Loc.T("smtp.err.genericBody"), ex.Message),
                    Loc.T("smtp.err.genericTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
                    TxtEmailCount.Text = string.Format(Loc.T("smtp.idle.emailCount"), vm.EmailCount);
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
            MessageBox.Show(Loc.T("smtp.err.noProxiesBody"), Loc.T("smtp.err.noProxiesTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var dlg = new ProxyCheckDialog(proxyLines) { Owner = Window.GetWindow(this) };
        var useAlive = dlg.ShowDialog() == true;

        TxtProxyStatus.Foreground = (System.Windows.Media.Brush)FindResource(
            dlg.AliveCount > 0 ? "CleanBrush" : "DangerBrush");
        TxtProxyStatus.Text = string.Format(Loc.T("smtp.idle.proxyStatus"), dlg.AliveCount, dlg.DeadCount);

        if (useAlive && dlg.AliveProxies.Count > 0)
        {
            TxtProxies.Text = string.Join("\n", dlg.AliveProxies);
        }
    }

    private void OnLoadProxiesFile(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = Loc.T("smtp.idle.dialogLoadProxiesTitle"),
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
                MessageBox.Show(string.Format(Loc.T("smtp.err.genericBody"), ex.Message),
                    Loc.T("smtp.err.genericTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
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
