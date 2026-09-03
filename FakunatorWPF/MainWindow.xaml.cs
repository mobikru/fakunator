using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fakunator.Core;
using Fakunator.ViewModels;
using Fakunator.Views;
using Microsoft.Win32;

namespace Fakunator;

public partial class MainWindow : Window
{
    private readonly CleanupViewModel _cleanupVm;
    private readonly SmtpViewModel _smtpVm;
    private readonly AnalyzeViewModel _analyzeVm;
    private readonly DomainScannerViewModel _domainVm;
    private readonly DomainsManagerViewModel _domainsMgrVm;
    private readonly AmsViewModel _amsVm;
    private readonly PmtaViewModel _pmtaVm;

    public MainWindow()
    {
        InitializeComponent();

        // Версию в шапке подтягиваем из assembly — синхрон с csproj.
        var v = typeof(MainWindow).Assembly.GetName().Version;
        if (v != null) TxtVersion.Text = $"{v.Major}.{v.Minor}.{v.Build}";

        _cleanupVm = new CleanupViewModel();
        ContentArea.DataContext = _cleanupVm;

        _smtpVm = new SmtpViewModel();
        SmtpContentArea.DataContext = _smtpVm;

        _analyzeVm = new AnalyzeViewModel();
        AnalyzeContentArea.DataContext = _analyzeVm;

        _domainVm = new DomainScannerViewModel();
        DomainsView.DataContext = _domainVm;

        _domainsMgrVm = new DomainsManagerViewModel();
        DomainsMgrView.DataContext = _domainsMgrVm;

        _amsVm = new AmsViewModel();
        AmsView.DataContext = _amsVm;

        _pmtaVm = new PmtaViewModel();
        PmtaViewCtrl.DataContext = _pmtaVm;

        // Wire tab-specific sidebars to their view models
        SidebarSmtp.DataContext = _smtpVm;
        SidebarAnalyze.DataContext = _analyzeVm;
        SidebarDomains.DataContext = _domainVm;
        SidebarDomainsMgr.DataContext = _domainsMgrVm;

        // Keyboard shortcuts
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => OpenFile()), Key.O, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => _ = _cleanupVm.StartCleanup(),
            _ => _cleanupVm.State == CleanupViewModel.CleanupState.Loaded), Key.Return, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => OpenOutputFolder(),
            _ => _cleanupVm.OutputDir != null), Key.E, ModifierKeys.Control));
        InputBindings.Add(new KeyBinding(new RelayCommand(_ => TogglePause()), Key.Space, ModifierKeys.None));

        // Restore window geometry + theme from config
        RestoreFromConfig();
    }

    // ── Keyboard handlers ────────────────────────────────────────────

    private void OpenFile()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Выберите файл с email-адресами",
            Filter = "Text files|*.txt;*.csv;*.tsv|All files|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() == true)
            _cleanupVm.LoadFile(dlg.FileName);
    }

    private void OpenOutputFolder()
    {
        if (!string.IsNullOrEmpty(_cleanupVm.OutputDir) && Directory.Exists(_cleanupVm.OutputDir))
            Process.Start(new ProcessStartInfo(_cleanupVm.OutputDir) { UseShellExecute = true });
    }

    private void TogglePause()
    {
        if (_cleanupVm.State == CleanupViewModel.CleanupState.Running)
            _cleanupVm.StopCommand.Execute(null);
    }

    // ── Theme toggle ─────────────────────────────────────────────────

    private void OnThemeToggle(object sender, RoutedEventArgs e)
    {
        if (sender is Button btn)
        {
            var theme = btn.Name == "BtnSun" ? "light" : "dark";
            App.SwitchTheme(theme);
            Config.Current.Theme = theme;
            Config.Current.Save();
        }
    }

    // ── Settings gear button ─────────────────────────────────────────

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(this);
        dlg.ShowDialog();
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (CleanupPanel == null || SmtpPanel == null || AnalyzePanel == null || DomainsPanel == null || DomainsMgrPanel == null || AmsPanel == null || PmtaPanel == null)
            return; // not yet initialized

        void SetAll(Visibility cln, Visibility smtp, Visibility anz, Visibility dom, Visibility mgr, Visibility ams, Visibility pmta)
        {
            CleanupPanel.Visibility = cln;
            SmtpPanel.Visibility = smtp;
            AnalyzePanel.Visibility = anz;
            DomainsPanel.Visibility = dom;
            DomainsMgrPanel.Visibility = mgr;
            AmsPanel.Visibility = ams;
            PmtaPanel.Visibility = pmta;
            SidebarCleanup.Visibility = cln;
            SidebarSmtp.Visibility = smtp;
            SidebarAnalyze.Visibility = anz;
            SidebarDomains.Visibility = dom;
            SidebarDomainsMgr.Visibility = mgr;
        }

        // Вкладки «Домены»/«AMS»/«PMTA» — full-width layout, прячем sidebar-колонку.
        void ShowSidebar(bool show)
        {
            SidebarBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            SidebarColumn.Width = show ? new GridLength(300) : new GridLength(0);
        }

        var V = Visibility.Visible; var C = Visibility.Collapsed;
        if (TabCleanup.IsChecked == true) { SetAll(V, C, C, C, C, C, C); ShowSidebar(true); }
        else if (TabSmtp.IsChecked == true) { SetAll(C, V, C, C, C, C, C); ShowSidebar(true); }
        else if (TabAnalyze.IsChecked == true) { SetAll(C, C, V, C, C, C, C); ShowSidebar(true); }
        else if (TabDomains.IsChecked == true) { SetAll(C, C, C, V, C, C, C); ShowSidebar(true); }
        else if (TabDomainsMgr.IsChecked == true) { SetAll(C, C, C, C, V, C, C); ShowSidebar(false); }
        else if (TabAms.IsChecked == true) { SetAll(C, C, C, C, C, V, C); ShowSidebar(false); _amsVm.OnTabActivated(); }
        else if (TabPmta.IsChecked == true) { SetAll(C, C, C, C, C, C, V); ShowSidebar(false); }
        else { SetAll(C, C, C, C, C, C, C); ShowSidebar(true); }
    }

    // ── Window geometry persistence via Config ───────────────────────

    protected override void OnClosing(CancelEventArgs e)
    {
        // Confirm if cleanup, SMTP, or analyze is running
        bool isRunning = _cleanupVm.State is CleanupViewModel.CleanupState.Running
                                            or CleanupViewModel.CleanupState.Paused
                      || _smtpVm.State is SmtpViewModel.SmtpState.Running
                      || _analyzeVm.State is AnalyzeViewModel.AnalyzeState.Running;

        if (isRunning)
        {
            var result = MessageBox.Show(
                "Идёт обработка. Прервать и выйти?",
                "Закрыть?",
                MessageBoxButton.YesNo,
                MessageBoxImage.Question,
                MessageBoxResult.No);

            if (result != MessageBoxResult.Yes)
            {
                e.Cancel = true;
                return;
            }
            _cleanupVm.StopCommand.Execute(null);
            _smtpVm.StopCommand.Execute(null);
            _analyzeVm.StopCommand.Execute(null);
        }

        SaveToConfig();
        base.OnClosing(e);
    }

    private void SaveToConfig()
    {
        try
        {
            var cfg = Config.Current;
            cfg.Theme = App.CurrentTheme;

            if (WindowState == WindowState.Normal)
            {
                cfg.WindowLeft = Left;
                cfg.WindowTop = Top;
                cfg.WindowWidth = Width;
                cfg.WindowHeight = Height;
            }
            cfg.WindowMaximized = WindowState == WindowState.Maximized;

            cfg.SaveNow(); // Immediate save on closing
        }
        catch { }
    }

    private void RestoreFromConfig()
    {
        try
        {
            var cfg = Config.Current;

            // Restore geometry
            if (cfg.WindowWidth > 0)
                Width = Math.Max(MinWidth, cfg.WindowWidth);
            if (cfg.WindowHeight > 0)
                Height = Math.Max(MinHeight, cfg.WindowHeight);

            // Only restore position if it was explicitly saved (non-zero)
            if (cfg.WindowLeft != 0 || cfg.WindowTop != 0)
            {
                Left = cfg.WindowLeft;
                Top = cfg.WindowTop;
                WindowStartupLocation = WindowStartupLocation.Manual;
            }

            if (cfg.WindowMaximized)
                WindowState = WindowState.Maximized;

            // Restore theme
            if (!string.IsNullOrEmpty(cfg.Theme))
                App.SwitchTheme(cfg.Theme);
        }
        catch { }
    }
}
