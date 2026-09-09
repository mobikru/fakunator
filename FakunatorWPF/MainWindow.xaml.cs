using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Fakunator.Core;
using Fakunator.Core.Updater;
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

        // In-app updater — подписываемся ДО Start(), чтобы не пропустить первый чек.
        UpdateService.Instance.UpdateAvailable += OnUpdateAvailable;
        UpdateService.Instance.Start();



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

    // ── Settings gear button ─────────────────────────────────────────

    private void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        var dlg = new SettingsDialog(this);
        dlg.ShowDialog();
    }

    private void OnTabChanged(object sender, RoutedEventArgs e)
    {
        if (CleanupPanel == null || SmtpPanel == null || AnalyzePanel == null || DomainsPanel == null || DomainsMgrPanel == null || AmsPanel == null || PmtaPanel == null || ServerInstallPanel == null)
            return; // not yet initialized

        void SetAll(Visibility cln, Visibility smtp, Visibility anz, Visibility dom, Visibility mgr, Visibility ams, Visibility pmta, Visibility srv)
        {
            CleanupPanel.Visibility = cln;
            SmtpPanel.Visibility = smtp;
            AnalyzePanel.Visibility = anz;
            DomainsPanel.Visibility = dom;
            DomainsMgrPanel.Visibility = mgr;
            AmsPanel.Visibility = ams;
            PmtaPanel.Visibility = pmta;
            ServerInstallPanel.Visibility = srv;
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
        // Sub-tab bar «База» видим только внутри вкладки «База»
        if (DbSubTabBar != null)
            DbSubTabBar.Visibility = TabDatabase.IsChecked == true ? V : C;

        if (TabDatabase.IsChecked == true)
        {
            // Внутри «Базы» — какой из 3 sub-tab'ов активен решает ApplyDbSubTab()
            ApplyDbSubTab();
        }
        else if (TabDomains.IsChecked == true) { SetAll(C, C, C, V, C, C, C, C); ShowSidebar(true); }
        else if (TabDomainsMgr.IsChecked == true) { SetAll(C, C, C, C, V, C, C, C); ShowSidebar(false); }
        else if (TabAms.IsChecked == true) { SetAll(C, C, C, C, C, V, C, C); ShowSidebar(false); _amsVm.OnTabActivated(); }
        else if (TabPmta.IsChecked == true) { SetAll(C, C, C, C, C, C, V, C); ShowSidebar(false); }
        else if (TabServerInstall.IsChecked == true) { SetAll(C, C, C, C, C, C, C, V); ShowSidebar(false); }
        else { SetAll(C, C, C, C, C, C, C, C); ShowSidebar(true); }
    }

    private void OnDbSubTabChanged(object sender, RoutedEventArgs e)
    {
        if (TabDatabase == null || TabDatabase.IsChecked != true) return;
        ApplyDbSubTab();
    }

    private void ApplyDbSubTab()
    {
        if (CleanupPanel == null || SmtpPanel == null || AnalyzePanel == null) return;

        var V = Visibility.Visible; var C = Visibility.Collapsed;
        // Скрываем всё, что не входит в «Базу», и левые side-панели тоже
        DomainsPanel.Visibility = C;
        DomainsMgrPanel.Visibility = C;
        AmsPanel.Visibility = C;
        PmtaPanel.Visibility = C;
        ServerInstallPanel.Visibility = C;
        SidebarDomains.Visibility = C;
        SidebarDomainsMgr.Visibility = C;

        // Sidebar-колонка видима для всех трёх sub-tab'ов
        SidebarBorder.Visibility = V;
        SidebarColumn.Width = new GridLength(300);

        if (SubSmtp.IsChecked == true)
        {
            CleanupPanel.Visibility = C; SmtpPanel.Visibility = V; AnalyzePanel.Visibility = C;
            SidebarCleanup.Visibility = C; SidebarSmtp.Visibility = V; SidebarAnalyze.Visibility = C;
        }
        else if (SubAnalyze.IsChecked == true)
        {
            CleanupPanel.Visibility = C; SmtpPanel.Visibility = C; AnalyzePanel.Visibility = V;
            SidebarCleanup.Visibility = C; SidebarSmtp.Visibility = C; SidebarAnalyze.Visibility = V;
        }
        else // SubCleanup — default
        {
            CleanupPanel.Visibility = V; SmtpPanel.Visibility = C; AnalyzePanel.Visibility = C;
            SidebarCleanup.Visibility = V; SidebarSmtp.Visibility = C; SidebarAnalyze.Visibility = C;
        }
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
        }
        catch { }
    }

    // ── Update notifications (in-app updater) ─────────────────────
    private CancellationTokenSource? _updateCts;

    private void OnUpdateAvailable(UpdateInfo info)
    {
        // Dispatched в UI поток UpdateService'ом
        UpdateBadge.Visibility = Visibility.Visible;
        TxtUpdateBadge.Text = $"доступно {info.RemoteVersion}";
        TxtBannerTitle.Text = $"Fakunator {info.RemoteVersion}";
        var notes = string.IsNullOrWhiteSpace(info.Manifest.Notes)
            ? "Готово новое обновление."
            : info.Manifest.Notes;
        TxtBannerBody.Text = $"{notes}\n\nСкачает {info.DownloadSizeText}, перезапустит приложение.";
        UpdateBanner.Visibility = Visibility.Visible;
    }

    private void OnUpdateBadgeClick(object sender, MouseButtonEventArgs e)
    {
        // Клик по badge — снова показывает баннер
        if (UpdateService.Instance.Pending != null && UpdateBanner.Visibility != Visibility.Visible)
            UpdateBanner.Visibility = Visibility.Visible;
    }

    private void OnUpdateBannerClose(object sender, RoutedEventArgs e)
    {
        UpdateBanner.Visibility = Visibility.Collapsed;
        // Badge остаётся — юзер сможет вернуться к обновлению кликом по нему
    }

    private async void OnUpdateBannerApply(object sender, RoutedEventArgs e)
    {
        var pending = UpdateService.Instance.Pending;
        if (pending == null) return;

        BtnBannerUpdate.IsEnabled = false;
        BtnBannerLater.IsEnabled = false;
        UpdateProgressPanel.Visibility = Visibility.Visible;
        _updateCts = new CancellationTokenSource();

        var progress = new Progress<(double pct, string status)>(p =>
        {
            UpdateProgress.Value = p.pct;
            TxtUpdateStatus.Text = $"{p.status}   {p.pct:0}%";
        });

        try
        {
            await UpdateService.Instance.ApplyAsync(progress, _updateCts.Token);
            // Успех — обновляющий PowerShell-скрипт уже запущен и ждёт нашего выхода.
            // Закрываем приложение → PS сделает copy-over и перезапустит.
            Application.Current.Shutdown();
        }
        catch (OperationCanceledException)
        {
            TxtUpdateStatus.Text = "Отменено";
            BtnBannerUpdate.IsEnabled = true;
            BtnBannerLater.IsEnabled = true;
        }
        catch (Exception ex)
        {
            TxtUpdateStatus.Text = "Ошибка: " + ex.Message;
            BtnBannerUpdate.IsEnabled = true;
            BtnBannerLater.IsEnabled = true;
        }
    }
}
