using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using Ellipse = System.Windows.Shapes.Ellipse;
using Fakunator.Core;
using Fakunator.Core.Server;
using Fakunator.Core.TelegramBot;
using Fakunator.ViewModels;

namespace Fakunator.Views;

public partial class ServerInstallView : UserControl
{
    private int _wizardStep = 1;
    private readonly ServerRegistry _registry;
    private readonly ServerListViewModel _vm;
    private ServerInstaller? _activeInstaller;
    private string? _installingServerId; // id сервера, на котором идёт установка (для stub'а на других)
    private SshTailer? _activeTailer;
    private readonly System.Collections.ObjectModel.ObservableCollection<PreflightCheck> _preflight = new();
    private readonly System.Collections.ObjectModel.ObservableCollection<PhaseChip> _phaseChips = new();
    private readonly DnsTrustVm _dnsVm = new();
    private bool _userCancelledInstall;

    // ── Дефолтные ссылки на пакеты. Поля "Ссылки на пакеты" в UI показывают
    // макрос %default_url% вместо самого URL (чтобы не светить наши ссылки) —
    // если юзер его не заменил своей ссылкой, при установке подставляется значение отсюда. ──
    private const string UrlMacro = "%default_url%";
    private const string DefaultPmtaDebUrl     = "http://zennoclub.com/files/PMTA/PowerMTA5.0r8.deb/powermta_5.0r8-202104262143_amd64.deb";
    private const string DefaultPmtadUrl       = "http://zennoclub.com/files/PMTA/PowerMTA5.0r8.deb/pmtad";
    private const string DefaultPmtaHttpdUrl   = "http://zennoclub.com/files/PMTA/PowerMTA5.0r8.deb/pmtahttpd";
    private const string DefaultPmtaLicenseUrl = "http://zennoclub.com/files/PMTA/PowerMTA5.0r8.deb/license";
    private const string DefaultZtdsUrl        = "https://zennoclub.com/files/python_scripts/telegram_bots/pmta_installer/zTDS.zip";
    private const string DefaultMailerUrl      = "https://zennoclub.com/files/python_scripts/telegram_bots/pmta_installer/phpmailer.zip";
    private const string DefaultLandingUrl     = "http://zennoclub.com/files/Automatic%20mailing/landing_pages/i36yn7.zip";

    /// <summary>Пусто или буквально %default_url% (макрос) → встроенная ссылка. Иначе — что вставил юзер.</summary>
    private static string ResolveUrl(TextBox tb, string defaultUrl)
    {
        var t = tb.Text.Trim();
        return (t.Length == 0 || t.Equals(UrlMacro, StringComparison.OrdinalIgnoreCase)) ? defaultUrl : t;
    }

    /// <summary>Клик по полю со значением %default_url% сразу выделяет текст — можно печатать свою
    /// ссылку поверх без ручной очистки. Если ушли с фокуса оставив пусто — возвращаем макрос.</summary>
    private void OnUrlFieldGotFocus(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        if (tb.Text.Trim().Equals(UrlMacro, StringComparison.OrdinalIgnoreCase))
            tb.SelectAll();
        tb.LostFocus -= UrlField_RestoreDefaultIfEmpty;
        tb.LostFocus += UrlField_RestoreDefaultIfEmpty;
        tb.TextChanged -= UrlField_TextChanged;
        tb.TextChanged += UrlField_TextChanged;
    }

    private void UrlField_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        var isMacro = tb.Text.Trim().Equals(UrlMacro, StringComparison.OrdinalIgnoreCase);
        tb.SetResourceReference(TextBox.ForegroundProperty, isMacro ? "Fg4Brush" : "Fg1Brush");
    }

    private void UrlField_RestoreDefaultIfEmpty(object sender, RoutedEventArgs e)
    {
        if (sender is not TextBox tb) return;
        tb.LostFocus -= UrlField_RestoreDefaultIfEmpty;
        if (string.IsNullOrWhiteSpace(tb.Text))
        {
            tb.Text = UrlMacro;
            tb.SetResourceReference(TextBox.ForegroundProperty, "Fg4Brush");
        }
    }

    // ── Install-progress illustration: парящая анимация + частицы (test design реф) ──
    private readonly List<Ellipse> _particles = new();
    private bool _particlesRunning;
    private static readonly Random _particleRng = new();
    private DispatcherTimer? _elapsedTimer;
    private DateTime _installStartedAtLocal;

    public ServerInstallView()
    {
        InitializeComponent();
        LoadUrlPresetsFromAppData();

        _registry = new ServerRegistry();
        _vm = new ServerListViewModel(_registry);
        DataContext = _vm;
        PreflightList.ItemsSource = _preflight;
        PhaseChips.ItemsSource = _phaseChips;
        TabPaneDns.DataContext = _dnsVm;
        StartFloatAnimation();

        // При старте:
        //   • есть серверы → выбрать первый и показать вкладку Обзор
        //   • нет серверов → сразу показать форму «Новый сервер» (Step 1)
        Loaded += (_, _) =>
        {
            if (_vm.Rows.Count > 0)
            {
                _vm.Selected = _vm.Rows[0];
                TabOverview.IsChecked = true;
            }
            else
            {
                OnNewInstallClick(this, new RoutedEventArgs());
            }
        };
    }

    private async void OnRefreshDnsClick(object sender, RoutedEventArgs e)
    {
        var srv = _vm.Selected?.Config;
        if (srv == null) return;
        await _dnsVm.RefreshAsync(srv.Ip, srv.Domain);
    }

    // ═════════════════ URL PRESETS ═════════════════════════════════════════
    private void LoadUrlPresetsFromAppData()
    {
        try
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "Fakunator", "install_presets.json");
            if (!File.Exists(path)) return;

            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            var root = doc.RootElement;
            SetUrlFieldFromPreset(UrlPmtaDeb,     root, "pmta_deb",     DefaultPmtaDebUrl);
            SetUrlFieldFromPreset(UrlPmtad,       root, "pmtad",        DefaultPmtadUrl);
            SetUrlFieldFromPreset(UrlPmtaHttpd,   root, "pmtahttpd",    DefaultPmtaHttpdUrl);
            SetUrlFieldFromPreset(UrlPmtaLicense, root, "pmta_license", DefaultPmtaLicenseUrl);
            SetUrlFieldFromPreset(UrlZtds,        root, "ztds",         DefaultZtdsUrl);
            SetUrlFieldFromPreset(UrlMailer,      root, "mailer",       DefaultMailerUrl);
            SetUrlFieldFromPreset(UrlLanding,     root, "landing",      DefaultLandingUrl);
        }
        catch { /* ignore */ }
    }

    /// <summary>Подставляет сохранённый URL в поле, только если он реально отличается от
    /// встроенного дефолта — иначе поле остаётся с макросом %default_url% (ссылку не светим).</summary>
    private static void SetUrlFieldFromPreset(TextBox tb, JsonElement root, string key, string defaultUrl)
    {
        if (!root.TryGetProperty(key, out var v)) return;
        var val = (v.GetString() ?? "").Trim();
        if (val.Length == 0 || val.Equals(defaultUrl, StringComparison.OrdinalIgnoreCase)) return;
        tb.Text = val;
        tb.SetResourceReference(TextBox.ForegroundProperty, "Fg1Brush");
    }

    // ═════════════════ NEW INSTALL BUTTON ══════════════════════════════════
    private void OnNewInstallClick(object sender, RoutedEventArgs e)
    {
        // Сбрасываем Selected — чтобы OnServerTabChanged не подставил старые данные
        _vm.Selected = null;
        TabInstall.IsChecked = true;
        _wizardStep = 1;
        UpdateWizardUi();
        // Очищаем поля формы под новый сервер
        TxtDomain.Text = "";
        TxtServerIp.Text = "";
        TxtSshUser.Text = "root";
        TxtSshPort.Text = "22";
        PwdSsh.Password = "";
        TxtLeEmail.Text = "";
    }

    private void OnRefreshAllClick(object sender, RoutedEventArgs e)
    {
        _ = _vm.PollAllAsync();
    }

    private void OnSshClick(object sender, RoutedEventArgs e)
    {
        var row = _vm.Selected;
        if (row == null) { MessageBox.Show("Выбери сервер в таблице слева"); return; }
        try { SshLauncher.OpenTerminal(row.Config); }
        catch (Exception ex) { MessageBox.Show($"Не удалось открыть SSH: {ex.Message}", "SSH", MessageBoxButton.OK, MessageBoxImage.Error); }
    }

    private async void OnRestartServiceClick(object sender, RoutedEventArgs e)
    {
        var row = _vm.Selected;
        if (row == null) return;
        var svcName = (sender as FrameworkElement)?.Tag as string;
        if (string.IsNullOrEmpty(svcName)) return;

        var res = MessageBox.Show($"Перезапустить сервис '{svcName}' на {row.Name}?",
            "Restart", MessageBoxButton.YesNo, MessageBoxImage.Question, MessageBoxResult.No);
        if (res != MessageBoxResult.Yes) return;

        try
        {
            await Task.Run(() =>
            {
                var pass = ServerRegistry.DecryptPassword(row.Config.SshPasswordEnc);
                using var ssh = new Renci.SshNet.SshClient(row.Config.Ip, row.Config.SshPort, row.Config.SshUser, pass);
                ssh.ConnectionInfo.Timeout = TimeSpan.FromSeconds(8);
                ssh.Connect();
                var cmd = ssh.CreateCommand($"systemctl restart {svcName}");
                cmd.CommandTimeout = TimeSpan.FromSeconds(15);
                cmd.Execute();
                ssh.Disconnect();
                return cmd.ExitStatus ?? 0;
            });
            await _vm.PollAllAsync();
            MessageBox.Show($"Сервис '{svcName}' перезапущен", "Restart", MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Не удалось перезапустить {svcName}:\n{ex.GetType().Name}: {ex.Message}",
                "Restart failed", MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private void OnDeleteServerClick(object sender, RoutedEventArgs e)
    {
        var row = _vm.Selected;
        if (row == null) return;
        var res = MessageBox.Show(
            $"Удалить сервер '{row.Name}' из мониторинга?\n(на самом сервере ничего не удалится)",
            "Удалить сервер",
            MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (res != MessageBoxResult.Yes) return;
        _registry.Remove(row.Config.Id);
        _vm.Selected = null;
    }

    // ═════════════════ ROW CLICK ═══════════════════════════════════════════
    private void OnRowClick(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is DependencyObject src)
        {
            var walker = src;
            while (walker != null && walker != sender)
            {
                if (walker is Button) return; // клик по звёздочке — не выделяем строку
                walker = System.Windows.Media.VisualTreeHelper.GetParent(walker);
            }
        }
        if (sender is FrameworkElement fe && fe.DataContext is ServerRowVm row)
        {
            StopTail();
            ResetWizardForServer(row.Config);
            _vm.Selected = row;
            TabOverview.IsChecked = true;
        }
    }

    private void OnClearSearchClick(object sender, RoutedEventArgs e) => _vm.ClearSearch();

    private void OnFilterClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string tag)
        {
            _vm.Filter = tag switch
            {
                "online"   => ServerFilter.Online,
                "problems" => ServerFilter.Problems,
                "offline"  => ServerFilter.Offline,
                _          => ServerFilter.All
            };
        }
    }

    private void OnFavoriteClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is ServerRowVm row)
        {
            row.IsFavorite = !row.IsFavorite;
            e.Handled = true;
        }
    }

    /// <summary>Сброс wizard'а под конкретный сервер.
    /// • Если это сервер, на котором СЕЙЧАС идёт установка — оставляем всё как есть (Step 3, лог, чипы) — юзер вернулся смотреть.
    /// • Если у сервера уже есть Credentials (установлен ранее) — показываем Step 4 с ними, log/chips очищаем.
    /// • Иначе — Step 1 (форма), log/chips очищаем.</summary>
    private void ResetWizardForServer(ServerConfig? cfg)
    {
        if (cfg == null) return;

        // Возврат к устанавливающемуся серверу — не трогаем ни лог, ни чипы, ни Step 3
        if (_installingServerId != null && cfg.Id == _installingServerId)
        {
            _wizardStep = 3;
            UpdateWizardUi();
            return;
        }

        // ВАЖНО: если сейчас идёт установка (пусть даже на другом сервере) — НЕ ТРОГАЕМ коллекции
        // _phaseChips / _log / _preflight, потому что OnInstallEvent продолжает в них писать по индексу.
        // Очистка → IndexOutOfRange → каскад MessageBox'ов на каждое приходящее событие.
        // WStep3 всё равно скрыт для этого сервера (Visibility=Collapsed), лог не мешает.
        if (_installingServerId == null)
        {
            _preflight.Clear();
            _phaseChips.Clear();
            _lastErrorText = null;
            if (InstallErrorBanner != null) InstallErrorBanner.Visibility = Visibility.Collapsed;
            ClearLog();
        }

        _wizardStep = 1;
        UpdateWizardUi();
    }

    // ═════════════════ TAB SWITCH ══════════════════════════════════════════
    private void OnServerTabChanged(object sender, RoutedEventArgs e)
    {
        if (TabPaneOverview == null) return;
        TabPaneOverview.Visibility = TabOverview.IsChecked == true ? Visibility.Visible : Visibility.Collapsed;
        TabPaneInstall.Visibility  = TabInstall.IsChecked  == true ? Visibility.Visible : Visibility.Collapsed;
        TabPaneLogs.Visibility     = TabLogs.IsChecked     == true ? Visibility.Visible : Visibility.Collapsed;
        TabPaneDns.Visibility      = TabDns.IsChecked      == true ? Visibility.Visible : Visibility.Collapsed;

        // Останавливаем tail если ушли с вкладки Logs
        if (TabLogs.IsChecked != true) StopTail();

        // Auto-refresh DNS-проверок при переходе на вкладку (один раз при пустом состоянии)
        if (TabDns.IsChecked == true && _vm.Selected != null && _dnsVm.Required.Count == 0)
        {
            var cfg = _vm.Selected.Config;
            _ = _dnsVm.RefreshAsync(cfg.Ip, cfg.Domain);
        }

        // При переключении на Install — подставляем данные выбранного сервера
        // и сбрасываем wizard под этот сервер (уходим от чужого Step 3/4 остаточного)
        if (TabInstall.IsChecked == true && _vm.Selected != null)
        {
            var cfg = _vm.Selected.Config;
            TxtDomain.Text    = cfg.Domain;
            TxtServerIp.Text  = cfg.Ip;
            TxtSshUser.Text   = cfg.SshUser;
            TxtSshPort.Text   = cfg.SshPort.ToString();
            TxtLeEmail.Text   = string.IsNullOrEmpty(cfg.LetsEncryptEmail) ? "info@" + cfg.Domain : cfg.LetsEncryptEmail;
            // Пароль подставляем расшифрованным (Fakunator работает локально)
            var pass = ServerRegistry.DecryptPassword(cfg.SshPasswordEnc);
            if (!string.IsNullOrEmpty(pass)) PwdSsh.Password = pass;
            ResetWizardForServer(cfg);
        }
    }

    // ═════════════════ WIZARD NAV ══════════════════════════════════════════
    private void OnWizardNextClick(object sender, RoutedEventArgs e)
    {
        if (_wizardStep == 1)
        {
            _wizardStep = 2;
            UpdateWizardUi();
            _ = RunPreflightAsync();
            return;
        }
        if (_wizardStep == 2)
        {
            // Блок параллельной установки — не переключаемся на Step 3, остаёмся на Step 2 с disabled-кнопкой.
            if (_installingServerId != null)
            {
                var busyRow = _vm.Rows.FirstOrDefault(r => r.Config.Id == _installingServerId);
                MessageBox.Show(
                    $"Уже идёт установка на сервере {busyRow?.Config.Domain ?? "?"}. Дождись её окончания.",
                    "Установка занята", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            _wizardStep = 3;
            UpdateWizardUi();
            _ = StartInstallAsync();
            return;
        }
        if (_wizardStep >= 4) { _wizardStep = 1; UpdateWizardUi(); return; }
        _wizardStep++;
        UpdateWizardUi();
    }

    private void OnRerunPreflightClick(object sender, RoutedEventArgs e)
        => _ = RunPreflightAsync();

    private bool _preflightRunning;

    private async Task RunPreflightAsync()
    {
        _preflight.Clear();
        _preflightRunning = true;
        UpdatePreflightSummary();

        var srv = BuildServerConfigFromForm();
        var opts = new InstallOptions
        {
            PmtaDebUrl = ResolveUrl(UrlPmtaDeb, DefaultPmtaDebUrl),
            PmtadUrl = ResolveUrl(UrlPmtad, DefaultPmtadUrl),
            PmtaHttpdUrl = ResolveUrl(UrlPmtaHttpd, DefaultPmtaHttpdUrl),
            PmtaLicenseUrl = ResolveUrl(UrlPmtaLicense, DefaultPmtaLicenseUrl),
            ZtdsUrl = ResolveUrl(UrlZtds, DefaultZtdsUrl),
            MailerUrl = ResolveUrl(UrlMailer, DefaultMailerUrl),
            LandingUrl = ResolveUrl(UrlLanding, DefaultLandingUrl),
        };
        var pf = new Preflight();
        pf.Updated += c => Dispatcher.BeginInvoke(new Action(() =>
        {
            var existing = _preflight.FirstOrDefault(x => x.Name == c.Name);
            if (existing == null) _preflight.Add(c);
            // (existing уже notifiable, ничего делать не нужно — UI обновится через PropertyChanged)
            UpdatePreflightSummary();
        }));
        try { await pf.RunAsync(srv, opts); }
        catch (Exception ex)
        {
            MessageBox.Show($"Preflight упал: {ex.GetType().Name}: {ex.Message}", "Preflight", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            _preflightRunning = false;
            UpdatePreflightSummary();
        }
    }

    private void OnTogglePackagesClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is PreflightCheck chk)
            chk.IsExpanded = !chk.IsExpanded;
    }

    private void UpdatePreflightSummary()
    {
        int ok = _preflight.Count(c => c.Status == PreflightStatus.Ok);
        int warn = _preflight.Count(c => c.Status == PreflightStatus.Warn);
        int fail = _preflight.Count(c => c.Status == PreflightStatus.Fail);

        TxtPreflightOk.Text = ok.ToString();
        TxtPreflightWarn.Text = warn.ToString();
        TxtPreflightFail.Text = fail.ToString();
        TxtPreflightFailLabel.Text = fail == 1 ? " ошибка" : " ошибок";
        PreflightSummaryRunning.Visibility = _preflightRunning ? Visibility.Visible : Visibility.Collapsed;
        PreflightSummaryCounts.Visibility = _preflightRunning ? Visibility.Collapsed : Visibility.Visible;

        BtnRerunPreflight.IsEnabled = !_preflightRunning;
    }

    private void OnWizardBackClick(object sender, RoutedEventArgs e)
    {
        if (_wizardStep <= 1) return;
        _wizardStep--;
        UpdateWizardUi();
    }

    private void OnSaveOnlyClick(object sender, RoutedEventArgs e)
    {
        // Сохранить в реестр без запуска установки — только для мониторинга существующего сервера
        var srv = BuildServerConfigFromForm();
        srv.Status = ServerStatus.Unknown;
        srv.InstalledAt = DateTime.UtcNow;
        _registry.Add(srv);
        _ = _vm.PollAllAsync();

        _vm.Selected = _vm.Rows.FirstOrDefault(r => r.Config.Id == srv.Id);
        TabOverview.IsChecked = true;
        MessageBox.Show(
            $"Сервер {srv.Name} добавлен в мониторинг.\nHealth-статус обновится через 1-2 сек.",
            "Добавлен",
            MessageBoxButton.OK,
            MessageBoxImage.Information);
    }

    private ServerConfig BuildServerConfigFromForm() => new ServerConfig
    {
        Domain = TxtDomain.Text.Trim(),
        Name = TxtDomain.Text.Trim(),
        Ip = TxtServerIp.Text.Trim(),
        SshUser = TxtSshUser.Text.Trim(),
        SshPort = int.TryParse(TxtSshPort.Text, out var p) ? p : 22,
        SshPasswordEnc = ServerRegistry.EncryptPassword(PwdSsh.Password),
        LetsEncryptEmail = TxtLeEmail.Text.Trim(),
    };

    private void UpdateWizardUi()
    {
        WStep1.Visibility = _wizardStep == 1 ? Visibility.Visible : Visibility.Collapsed;
        WStep2.Visibility = _wizardStep == 2 ? Visibility.Visible : Visibility.Collapsed;
        WStep3.Visibility = _wizardStep == 3 ? Visibility.Visible : Visibility.Collapsed;
        WStep4.Visibility = _wizardStep == 4 ? Visibility.Visible : Visibility.Collapsed;

        UpdateStepState(1, StepCircle1, StepNumber1, StepCheck1, StepLabel1);
        UpdateStepState(2, StepCircle2, StepNumber2, StepCheck2, StepLabel2);
        UpdateStepState(3, StepCircle3, StepNumber3, StepCheck3, StepLabel3);
        UpdateStepState(4, StepCircle4, StepNumber4, StepCheck4, StepLabel4);
        UpdateStepSegment(StepSeg1, _wizardStep >= 2);
        UpdateStepSegment(StepSeg2, _wizardStep >= 3);
        UpdateStepSegment(StepSeg3, _wizardStep >= 4);

        TxtWizardStepLabel.Text = $"  Шаг {_wizardStep} из 4";

        BtnWizBack.IsEnabled = _wizardStep > 1 && _wizardStep != 3;
        BtnRerunPreflight.Visibility = _wizardStep == 2 ? Visibility.Visible : Visibility.Collapsed;

        // Если параллельная установка не на этом сервере — блокируем «Начать установку»
        // (проходить Step 1 → Step 2 можно свободно, но запуск ставим на паузу).
        var busyOnOther = _installingServerId != null
            && _vm.Selected != null
            && _vm.Selected.Config.Id != _installingServerId;

        BtnWizNext.Content = _wizardStep switch
        {
            2 when busyOnOther => "Занято другой установкой",
            2 => "Начать установку",
            3 => "…идёт установка",
            4 => "Готово · закрыть",
            _ => "Далее"
        };
        BtnWizNext.IsEnabled = _wizardStep != 3 && !(busyOnOther && _wizardStep == 2);
        BtnWizNext.ToolTip = busyOnOther && _wizardStep == 2
            ? "Уже идёт установка на другом сервере — дождись её окончания"
            : null;
    }

    // Градиент активного круга (яркий, glow-effect эмулируем толстой рамкой)
    private static readonly Brush ActiveCircleBrush = new LinearGradientBrush(
        new GradientStopCollection
        {
            new GradientStop((Color)ColorConverter.ConvertFromString("#52a2ff"), 0.0),
            new GradientStop((Color)ColorConverter.ConvertFromString("#6972ff"), 0.52),
            new GradientStop((Color)ColorConverter.ConvertFromString("#9768ff"), 1.0),
        }, new Point(0, 0), new Point(1, 1));

    // Градиент завершённого круга (немного темнее)
    private static readonly Brush DoneCircleBrush = new LinearGradientBrush(
        new GradientStopCollection
        {
            new GradientStop((Color)ColorConverter.ConvertFromString("#58a0ff"), 0.0),
            new GradientStop((Color)ColorConverter.ConvertFromString("#746cf6"), 1.0),
        }, new Point(0, 0), new Point(1, 1));

    // Градиент сегмента-линии (blue → violet)
    private static readonly Brush FilledSegmentBrush = new LinearGradientBrush(
        new GradientStopCollection
        {
            new GradientStop((Color)ColorConverter.ConvertFromString("#4f9cff"), 0.0),
            new GradientStop((Color)ColorConverter.ConvertFromString("#786cff"), 0.5),
            new GradientStop((Color)ColorConverter.ConvertFromString("#9b69ff"), 1.0),
        }, new Point(0, 0), new Point(1, 0));

    private static readonly Brush InactiveCircleBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#eef0f5"));
    private static readonly Brush InactiveSegmentBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#e7eaf2"));
    private static readonly Brush InactiveNumberBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#9ca3b6"));
    private static readonly Brush InactiveLabelBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#a1a7b8"));
    private static readonly Brush ActiveLabelBrush = new SolidColorBrush((Color)ColorConverter.ConvertFromString("#6476f5"));

    private void UpdateStepState(int stepIndex, Border circle, TextBlock number, TextBlock check, TextBlock label)
    {
        if (stepIndex < _wizardStep)
        {
            // done: галочка на градиенте, номер скрыт
            circle.Background = DoneCircleBrush;
            number.Visibility = Visibility.Collapsed;
            check.Visibility = Visibility.Visible;
            label.Foreground = InactiveLabelBrush;
            label.FontWeight = FontWeights.Normal;
        }
        else if (stepIndex == _wizardStep)
        {
            // active: яркий градиент, номер белый
            circle.Background = ActiveCircleBrush;
            number.Visibility = Visibility.Visible;
            number.Foreground = Brushes.White;
            check.Visibility = Visibility.Collapsed;
            label.Foreground = ActiveLabelBrush;
            label.FontWeight = FontWeights.SemiBold;
        }
        else
        {
            // inactive
            circle.Background = InactiveCircleBrush;
            number.Visibility = Visibility.Visible;
            number.Foreground = InactiveNumberBrush;
            check.Visibility = Visibility.Collapsed;
            label.Foreground = InactiveLabelBrush;
            label.FontWeight = FontWeights.Normal;
        }
    }

    private void UpdateStepSegment(Border seg, bool filled)
    {
        seg.Background = filled ? FilledSegmentBrush : InactiveSegmentBrush;
    }

    // ═════════════════ INSTALL FLOW ════════════════════════════════════════
    private void OnJumpToInstallingClick(object sender, RoutedEventArgs e)
    {
        if (_installingServerId == null) return;
        var row = _vm.Rows.FirstOrDefault(r => r.Config.Id == _installingServerId);
        if (row == null) return;
        _vm.Selected = row;
        TabInstall.IsChecked = true;
        UpdateWizardUi();
    }

    // ═════════════════ ИЛЛЮСТРАЦИЯ: парение + частицы ══════════════════════
    private void StartFloatAnimation()
    {
        var anim = new DoubleAnimation(0, -8, TimeSpan.FromSeconds(3))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        ServerArtFloatTransform.BeginAnimation(TranslateTransform.YProperty, anim);
    }

    private void StartParticles()
    {
        if (_particlesRunning) return;
        _particlesRunning = true;

        double w = ServerVisualGrid.ActualWidth > 0 ? ServerVisualGrid.ActualWidth : 300;
        double h = ServerVisualGrid.ActualHeight > 0 ? ServerVisualGrid.ActualHeight : 440;

        ParticleCanvas.Children.Clear();
        _particles.Clear();

        for (int i = 0; i < 16; i++)
        {
            var size = 2.5 + _particleRng.NextDouble() * 2.5;
            var dot = new Ellipse
            {
                Width = size,
                Height = size,
                Fill = new SolidColorBrush(Color.FromRgb(0x5E, 0x8D, 0xFF)),
                Effect = new System.Windows.Media.Effects.BlurEffect { Radius = 4 },
                Opacity = 0,
            };
            Canvas.SetLeft(dot, w * (0.15 + _particleRng.NextDouble() * 0.7));
            Canvas.SetTop(dot, h + 10);
            ParticleCanvas.Children.Add(dot);
            _particles.Add(dot);

            double dur = 3.0 + _particleRng.NextDouble() * 2.0;
            double delay = _particleRng.NextDouble() * dur;

            var topAnim = new DoubleAnimation(h + 10, -10, TimeSpan.FromSeconds(dur))
            {
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(delay)
            };
            dot.BeginAnimation(Canvas.TopProperty, topAnim);

            var opacityAnim = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromSeconds(dur),
                RepeatBehavior = RepeatBehavior.Forever,
                BeginTime = TimeSpan.FromSeconds(delay)
            };
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.6, KeyTime.FromPercent(0.18)));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0.6, KeyTime.FromPercent(0.75)));
            opacityAnim.KeyFrames.Add(new LinearDoubleKeyFrame(0, KeyTime.FromPercent(1.0)));
            dot.BeginAnimation(UIElement.OpacityProperty, opacityAnim);
        }
    }

    private void StopParticles()
    {
        if (!_particlesRunning) return;
        _particlesRunning = false;
        foreach (var p in _particles)
        {
            p.BeginAnimation(Canvas.TopProperty, null);
            p.BeginAnimation(UIElement.OpacityProperty, null);
        }
        ParticleCanvas.Children.Clear();
        _particles.Clear();
    }

    private void StartElapsedTimer()
    {
        StopElapsedTimer();
        _installStartedAtLocal = DateTime.Now;
        _elapsedTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
        {
            var el = DateTime.Now - _installStartedAtLocal;
            TxtElapsed.Text = $"{(int)el.TotalMinutes:00}:{el.Seconds:00}";
        };
        _elapsedTimer.Start();
    }

    private void StopElapsedTimer()
    {
        _elapsedTimer?.Stop();
        _elapsedTimer = null;
    }

    // ═════════════════ ЖУРНАЛ (свёрнут по умолчанию) / ОТМЕНА ══════════════
    private void OnToggleLogClick(object sender, RoutedEventArgs e)
    {
        bool show = LogPanelBorder.Visibility != Visibility.Visible;
        LogPanelBorder.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        BtnToggleLog.Content = show ? "Скрыть журнал" : "Показать журнал";
    }

    private void OnCancelInstallClick(object sender, RoutedEventArgs e)
    {
        if (_activeInstaller == null) return;
        var res = MessageBox.Show(
            "Остановить установку?\nУже установленные компоненты останутся на сервере.",
            "Отменить установку", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
        if (res != MessageBoxResult.Yes) return;

        _userCancelledInstall = true;
        TxtFootStatus.Text = "Останавливаем установку…";
        BtnCancelInstall.IsEnabled = false;
        _activeInstaller.Cancel();
    }

    private async Task StartInstallAsync()
    {
        // Блок на параллельную установку — сейчас поддерживается только одна за раз
        if (_installingServerId != null)
        {
            var busyRow = _vm.Rows.FirstOrDefault(r => r.Config.Id == _installingServerId);
            MessageBox.Show(
                $"Уже идёт установка на сервере {busyRow?.Config.Domain ?? "?"}. Дождись её окончания, потом запусти следующую.",
                "Установка занята", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        // Если в форме те же IP+Domain что у выбранного сервера — переиспользуем его Id
        // (иначе upsert в _registry создаст дубликат). Или ищем по IP+Domain среди всех.
        var domain = TxtDomain.Text.Trim();
        var ip = TxtServerIp.Text.Trim();
        var existing = _vm.Rows.FirstOrDefault(r =>
            string.Equals(r.Config.Ip, ip, StringComparison.OrdinalIgnoreCase) &&
            string.Equals(r.Config.Domain, domain, StringComparison.OrdinalIgnoreCase))?.Config;

        // Защита от случайной переустановки: если у сервера уже есть валидные Credentials
        // (полностью прошедшая установка), предупреждаем — новая установка сгенерирует новые пароли
        // и старые (валидные для реальной системы) будут потеряны.
        if (existing?.Credentials != null && !string.IsNullOrEmpty(existing.Credentials.WebmailUrl))
        {
            var res = MessageBox.Show(
                $"Сервер {existing.Domain} уже полностью установлен и имеет сохранённые реквизиты доступа " +
                "(Webmail, PMTA, phpMyAdmin, zTDS, 3proxy, MySQL root).\n\n" +
                "Если запустить установку снова:\n" +
                "• Сгенерируются НОВЫЕ пароли и запишутся в servers.json\n" +
                "• Старые (валидные для уже стоящей системы) будут потеряны\n" +
                "• Установка скорее всего упадёт на apt install (пакеты уже стоят) — в итоге получишь набор паролей, которые не работают\n\n" +
                "Продолжить установку?\n" +
                "(если просто хочешь посмотреть креды — Нет и открой вкладку «📊 Обзор» → «🔑 Реквизиты доступа»)",
                "Сервер уже установлен", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (res != MessageBoxResult.Yes) return;
        }

        var srv = new ServerConfig
        {
            Id = existing?.Id ?? Guid.NewGuid().ToString("N"),
            Domain = domain,
            Ip = ip,
            SshUser = TxtSshUser.Text.Trim(),
            SshPort = int.TryParse(TxtSshPort.Text, out var p) ? p : 22,
            SshPasswordEnc = ServerRegistry.EncryptPassword(PwdSsh.Password),
            LetsEncryptEmail = TxtLeEmail.Text.Trim(),
            Name = TxtDomain.Text.Trim(),
            Status = ServerStatus.Installing,
        };
        var opts = new InstallOptions
        {
            PmtaDebUrl = ResolveUrl(UrlPmtaDeb, DefaultPmtaDebUrl),
            PmtadUrl = ResolveUrl(UrlPmtad, DefaultPmtadUrl),
            PmtaHttpdUrl = ResolveUrl(UrlPmtaHttpd, DefaultPmtaHttpdUrl),
            PmtaLicenseUrl = ResolveUrl(UrlPmtaLicense, DefaultPmtaLicenseUrl),
            ZtdsUrl = ResolveUrl(UrlZtds, DefaultZtdsUrl),
            MailerUrl = ResolveUrl(UrlMailer, DefaultMailerUrl),
            LandingUrl = ResolveUrl(UrlLanding, DefaultLandingUrl),
            AmsStats = ChkAmsStats.IsChecked == true,
        };
        srv.LastInstallOptions = opts;
        _registry.Add(srv);
        _vm.Selected = _vm.Rows.FirstOrDefault(r => r.Config.Id == srv.Id);

        // Стартуем инсталлер
        _activeInstaller?.Dispose();
        _activeInstaller = new ServerInstaller(srv, opts);
        _activeInstaller.Event += OnInstallEvent;
        _installingServerId = srv.Id;

        ClearLog();
        SeedPhaseChips();
        _lastErrorText = null;
        _userCancelledInstall = false;
        InstallErrorBanner.Visibility = Visibility.Collapsed;
        TxtInstallHeader.Text = "Установка идёт";
        TxtInstTitle.Text = "Собираем ваш сервер";
        TxtInstSubtitle.Text = "Устанавливаем и настраиваем необходимые компоненты. Это может занять несколько минут.";
        TxtLogHeader.Text = $"root@{srv.Ip} — live install log";
        TxtFootStatus.Text = "Не закрывайте окно до завершения";
        BtnCancelInstall.IsEnabled = true;
        BtnCancelInstall.Content = "Отменить установку";
        StartElapsedTimer();
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded, new Action(StartParticles));
        AppendLog(EventLevel.Info, $"Стартую установку {srv.Domain} на {srv.Ip}");

        var startedAt = DateTime.Now;
        var ok = await _activeInstaller.RunAsync();
        var duration = DateTime.Now - startedAt;

        Dispatcher.Invoke(() =>
        {
            StopElapsedTimer();
            StopParticles();
            BtnCancelInstall.IsEnabled = false;

            _registry.Update(srv);
            if (ok)
            {
                InstallErrorBanner.Visibility = Visibility.Collapsed;
                TxtInstallHeader.Text = "Установка завершена ✓";
                TxtInstTitle.Text = "Ваш сервер готов";
                TxtFootStatus.Text = "Установка успешно завершена";
                PopulateStep4(srv, ok, duration);
                _wizardStep = 4;
                UpdateWizardUi();
                NotificationHub.Notify(BuildInstallReport(srv, duration));
            }
            else if (_userCancelledInstall)
            {
                TxtInstallHeader.Text = "Установка отменена";
                TxtInstTitle.Text = "Установка отменена";
                TxtFootStatus.Text = "Остановлено пользователем — уже установленные компоненты остались на сервере";
                InstallErrorBanner.Visibility = Visibility.Collapsed;
                var activePhase = _phaseChips.FirstOrDefault(c => c.State == PhaseChipState.Active);
                if (activePhase != null) activePhase.State = PhaseChipState.Pending;
                PopulateStep4(srv, ok, duration);
            }
            else
            {
                // При failure — остаёмся на Step 3, показываем красный баннер
                TxtInstallHeader.Text = $"Установка прервана за {duration:mm\\:ss}";
                TxtInstTitle.Text = "Установка приостановлена";
                TxtFootStatus.Text = "Установка прервана — смотри причину ниже или журнал";
                InstallErrorBanner.Visibility = Visibility.Visible;
                TxtInstallErrorMsg.Text = _lastErrorText ?? "Причина неизвестна — смотри последние строки лога ниже.";
                // Пометить активную фазу как Failed
                var activePhase = _phaseChips.FirstOrDefault(c => c.State == PhaseChipState.Active);
                if (activePhase != null) activePhase.State = PhaseChipState.Failed;
                // Всё равно сохраним креды на Step 4 — они уже сгенерированы (MysqlRootPass итп)
                PopulateStep4(srv, ok, duration);
            }
            _ = _vm.PollAllAsync();
            _installingServerId = null; // установка завершилась → снимаем блокировку stub'а
        });
    }

    private string? _lastErrorText;

    private void OnCopyInstallLogClick(object sender, RoutedEventArgs e)
    {
        try
        {
            var full = new TextRange(InstallLogRtb.Document.ContentStart, InstallLogRtb.Document.ContentEnd).Text;
            Clipboard.SetText(full);
            var btn = (Button)sender;
            var orig = btn.Content;
            btn.Content = "✓ скопировано";
            _ = Task.Delay(1200).ContinueWith(_ => Dispatcher.BeginInvoke(new Action(() => btn.Content = orig)));
        }
        catch { }
    }

    private const string ReportSep = "╺╺╺╺╺╺╺╺╺╺╺╺╺╺╺╺╺╺╺╺╺";

    /// <summary>HTML-отчёт по всем реквизитам доступа для Telegram-уведомления после установки.
    /// Отправка идёт через NotificationHub — он сам проверяет TelegramEnabled/токен и молча
    /// ничего не делает, если бот не подключён в настройках.</summary>
    private static string BuildInstallReport(ServerConfig srv, TimeSpan duration)
    {
        var c = srv.Credentials ?? new Credentials();
        var sb = new System.Text.StringBuilder();

        sb.AppendLine($"✅ <b>{Esc(srv.Domain)}</b> успешно настроен! <i>({duration:mm\\:ss})</i>");
        sb.AppendLine();
        sb.AppendLine("🔐 <b>Данные доступов:</b>");
        sb.AppendLine();
        if (!string.IsNullOrEmpty(c.PmtaMonitorUrl))
        {
            sb.AppendLine($"➛ Monitor: {Esc(c.PmtaMonitorUrl)}");
            sb.AppendLine($"➛ Логин: <code>{Esc(c.PmtaMonitorUser)}</code>");
            sb.AppendLine($"➛ Пароль: <code>{Esc(c.PmtaMonitorPass)}</code>");
        }
        if (!string.IsNullOrEmpty(c.PmtaSmtpUser))
        {
            sb.AppendLine($"➛ SMTP логин: <code>{Esc(c.PmtaSmtpUser)}</code>");
            sb.AppendLine($"➛ SMTP пароль: <code>{Esc(c.PmtaSmtpPass)}</code>");
            sb.AppendLine($"➛ SMTP порт: <code>{Esc(c.PmtaSmtpPort)}</code>");
        }

        if (!string.IsNullOrEmpty(c.WebmailUrl))
        {
            sb.AppendLine();
            sb.AppendLine(ReportSep);
            sb.AppendLine();
            sb.AppendLine($"➛ Почта: {Esc(c.WebmailUrl)}");
            if (c.MailAliases.Any(a => a.StartsWith("abuse@")))
                sb.AppendLine($"➛ FBL: <code>{Esc(c.MailAliases.First(a => a.StartsWith("abuse@")))}</code>");
            sb.AppendLine($"➛ <code>{Esc(c.WebmailAdminEmail)}</code>");
            foreach (var alias in c.MailAliases.Where(a => !a.StartsWith("abuse@")))
                sb.AppendLine($"➛ <code>{Esc(alias)}</code>");
            sb.AppendLine($"➛ Пароль к почте: <code>{Esc(c.WebmailAdminPass)}</code>");
        }

        if (!string.IsNullOrEmpty(c.TdsUrl))
        {
            sb.AppendLine();
            sb.AppendLine(ReportSep);
            sb.AppendLine();
            sb.AppendLine($"➛ TDS: {Esc(c.TdsUrl)}");
            sb.AppendLine($"➛ Логин: <code>{Esc(c.TdsUser)}</code>");
            sb.AppendLine($"➛ Пароль: <code>{Esc(c.TdsPass)}</code>");
        }

        if (!string.IsNullOrEmpty(c.PhpMyAdminUrl))
        {
            sb.AppendLine();
            sb.AppendLine(ReportSep);
            sb.AppendLine();
            sb.AppendLine($"➛ phpMyAdmin: {Esc(c.PhpMyAdminUrl)}");
            sb.AppendLine($"➛ Логин: <code>{Esc(c.PhpMyAdminUser)}</code>");
            sb.AppendLine($"➛ Пароль: <code>{Esc(c.PhpMyAdminPass)}</code>");
        }

        if (!string.IsNullOrEmpty(c.Proxy3Url))
        {
            sb.AppendLine();
            sb.AppendLine(ReportSep);
            sb.AppendLine();
            sb.AppendLine("📌 <b>Ваш прокси:</b>");
            sb.AppendLine($"<code>{Esc(c.Proxy3Url)}</code>");
        }

        if (!string.IsNullOrEmpty(c.LandingUrl))
        {
            sb.AppendLine();
            sb.AppendLine(ReportSep);
            sb.AppendLine();
            sb.AppendLine($"🌐 Лендинг: {Esc(c.LandingUrl)}");
        }

        if (!string.IsNullOrEmpty(c.AmsStatsUrl))
        {
            sb.AppendLine();
            sb.AppendLine(ReportSep);
            sb.AppendLine();
            sb.AppendLine("📈 <b>AMS RealTime статистика:</b>");
            sb.AppendLine(Esc(c.AmsStatsUrl));
            if (!string.IsNullOrEmpty(c.AmsStatsUrlIp)) sb.AppendLine($"по IP: {Esc(c.AmsStatsUrlIp)}");
            sb.AppendLine($"Пароль: <code>{Esc(c.AmsStatsPassword)}</code>");
        }

        if (!string.IsNullOrEmpty(c.PtrHostname))
        {
            sb.AppendLine();
            sb.AppendLine(ReportSep);
            sb.AppendLine();
            sb.AppendLine("📡 <b>PTR (пропиши у хостинг-провайдера):</b>");
            sb.AppendLine($"{Esc(srv.Ip)} → <code>{Esc(c.PtrHostname)}</code>");
        }

        if (c.DnsRecords.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine(ReportSep);
            sb.AppendLine();
            sb.AppendLine("📃 <b>DNS записи (для регистратора):</b>");
            var aRecords = c.DnsRecords.Where(r => r.Type == "A").ToList();
            if (aRecords.Count > 0)
            {
                sb.AppendLine("▫️ A записи:");
                foreach (var r in aRecords)
                {
                    var name = r.Name == "@" ? srv.Domain : $"{r.Name}.{srv.Domain}";
                    sb.AppendLine($"➛ {Esc(name)}. → <code>{Esc(r.Value)}</code>");
                }
                sb.AppendLine();
            }
            foreach (var r in c.DnsRecords.Where(r => r.Type is "MX" or "TXT"))
            {
                var name = r.Name == "@" ? srv.Domain : $"{r.Name}.{srv.Domain}";
                var label = r.Type == "MX" ? "MX запись" :
                            r.Name.Contains("_domainkey") ? "DKIM" :
                            r.Name.Contains("_dmarc") ? "DMARC" : "SPF";
                sb.AppendLine($"▫️ {label}:");
                sb.AppendLine($"➛ Имя: <code>{Esc(name)}.</code>");
                sb.AppendLine($"➛ Значение: <code>{Esc(r.Value)}</code>");
                if (r.Priority.HasValue) sb.AppendLine($"➛ Приоритет: {r.Priority.Value}");
                sb.AppendLine();
            }
        }

        if (!string.IsNullOrEmpty(c.MysqlRootPass))
        {
            sb.AppendLine(ReportSep);
            sb.AppendLine();
            sb.AppendLine($"🔑 MySQL root: <code>{Esc(c.MysqlRootPass)}</code>");
        }

        return sb.ToString();
    }

    private static string Esc(string s) => string.IsNullOrEmpty(s) ? s
        : s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");

    private void PopulateStep4(ServerConfig srv, bool success, TimeSpan duration)
    {
        var creds = srv.Credentials ?? new Credentials();

        TxtDoneTitle.Text = success
            ? $"{srv.Domain} настроен"
            : $"{srv.Domain} — установка провалилась";
        TxtDoneSubtitle.Text = success
            ? $"Все компоненты установлены за {duration:mm\\:ss}"
            : "Смотри Шаг 3 (лог) — там причина. Часть шагов могла успеть.";

        TxtPmtaUrl.Text   = string.IsNullOrEmpty(creds.PmtaMonitorUrl) ? "не устанавливалось" : creds.PmtaMonitorUrl;
        TxtPmtaCreds.Text = string.IsNullOrEmpty(creds.PmtaMonitorUser) ? "" : $"{creds.PmtaMonitorUser} · {creds.PmtaMonitorPass}";
        TxtPmtaSmtp.Text  = string.IsNullOrEmpty(creds.PmtaSmtpUser) ? "" : $"SMTP {creds.PmtaSmtpUser} · {creds.PmtaSmtpPass} · порт {creds.PmtaSmtpPort}";
        BtnCopyPmta.Tag   = $"PMTA Monitor\n{TxtPmtaUrl.Text}\n{TxtPmtaCreds.Text}\n{TxtPmtaSmtp.Text}";

        TxtWebmailUrl.Text   = creds.WebmailUrl;
        TxtWebmailCreds.Text = $"{creds.WebmailAdminEmail} · {creds.WebmailAdminPass}";
        BtnCopyWebmail.Tag   = $"Webmail\n{TxtWebmailUrl.Text}\n{TxtWebmailCreds.Text}";

        TxtPmaUrl.Text   = creds.PhpMyAdminUrl;
        TxtPmaCreds.Text = $"{creds.PhpMyAdminUser} · {creds.PhpMyAdminPass}";
        BtnCopyPma.Tag   = $"phpMyAdmin\n{TxtPmaUrl.Text}\n{TxtPmaCreds.Text}";

        TxtTdsUrl.Text   = string.IsNullOrEmpty(creds.TdsUrl) ? "не устанавливалось" : creds.TdsUrl;
        TxtTdsCreds.Text = string.IsNullOrEmpty(creds.TdsUser) ? "" : $"{creds.TdsUser} · {creds.TdsPass}";
        BtnCopyTds.Tag   = $"zTDS Admin\n{TxtTdsUrl.Text}\n{TxtTdsCreds.Text}";

        Txt3proxyUrl.Text   = string.IsNullOrEmpty(creds.Proxy3Url) ? "не устанавливалось" : creds.Proxy3Url;
        Txt3proxyCreds.Text = string.IsNullOrEmpty(creds.Proxy3User) ? "" : $"{creds.Proxy3User} · {creds.Proxy3Pass}";
        BtnCopy3proxy.Tag   = $"3proxy SOCKS5\n{Txt3proxyUrl.Text}\n{Txt3proxyCreds.Text}";

        TxtLandingUrl.Text = string.IsNullOrEmpty(creds.LandingUrl) ? "не устанавливалось" : creds.LandingUrl;
        BtnCopyLanding.Tag = $"Лендинг\n{TxtLandingUrl.Text}";

        TxtAmsStatsUrl.Text   = string.IsNullOrEmpty(creds.AmsStatsUrl) ? "не устанавливалось" : creds.AmsStatsUrl;
        TxtAmsStatsUrlIp.Text = string.IsNullOrEmpty(creds.AmsStatsUrlIp) ? "" : $"по IP: {creds.AmsStatsUrlIp}";
        TxtAmsStatsCreds.Text = string.IsNullOrEmpty(creds.AmsStatsPassword) ? "" : $"пароль: {creds.AmsStatsPassword}";
        BtnCopyAmsStats.Tag   = $"AMS RealTime статистика\n{TxtAmsStatsUrl.Text}\n{TxtAmsStatsUrlIp.Text}\n{TxtAmsStatsCreds.Text}";

        // DNS records — рендерим в текстовый плоский формат для копипаста
        var sb = new System.Text.StringBuilder();
        foreach (var r in creds.DnsRecords)
        {
            var pri = r.Priority.HasValue ? $" (priority {r.Priority.Value})" : "";
            sb.AppendLine($"{r.Type,-5} {r.Name,-25} {r.Value}{pri}");
        }
        TxtDnsRecords.Text = sb.Length > 0 ? sb.ToString() : "нет данных";
    }

    // Фазы установщика (в порядке появления). Первое поле = ключевое слово для матча Event.Text,
    // второе = человекочитаемый label.
    private static readonly (string Key, string Label)[] InstallPhases =
    {
        ("apt update",         "apt update"),
        ("apt install",        "apt install"),
        ("MySQL",              "mysql"),
        ("Postfix",            "postfix"),
        ("Dovecot",            "dovecot"),
        ("OpenDKIM",           "opendkim"),
        ("Bind9",              "bind9"),
        ("Apache",             "apache"),
        ("phpMyAdmin",         "phpMyAdmin"),
        ("Roundcube",          "roundcube"),
        ("Certbot",            "certbot"),
        ("PMTA (BYO)",         "pmta files"),
        ("PMTA config",        "pmta start"),
        ("zTDS",               "ztds"),
        ("Mailer",             "mailer"),
        ("Landing",            "landing"),
        ("3proxy",             "3proxy"),
        ("AMS RealTime статистика", "ams stats"),
    };

    private void SeedPhaseChips()
    {
        _phaseChips.Clear();
        foreach (var (_, label) in InstallPhases)
            _phaseChips.Add(new PhaseChip { Label = label, State = PhaseChipState.Pending });
        InstallProgressBar.Value = 0;
        TxtInstallProgressLbl.Text = "0%";
        ProgressFillCol.Width = new GridLength(0, GridUnitType.Star);
        ProgressRestCol.Width = new GridLength(100, GridUnitType.Star);
        TxtPackagesDone.Text = $"0 из {_phaseChips.Count} компонентов";
        TxtCurrentIcon.Text = "⚙";
        TxtCurrentName.Text = "Подготовка";
        TxtCurrentDesc.Text = "Ожидание запуска установки";
        TxtElapsed.Text = "00:00";
    }

    private void OnInstallEvent(InstallEvent ev)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            AppendLog(ev.Level, ev.Text);

            if (ev.Level == EventLevel.Phase)
            {
                TxtWizardCurrent.Text = "▶ " + ev.Text;
                int idx = Array.FindIndex(InstallPhases, p => ev.Text.Contains(p.Key, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && idx < _phaseChips.Count)
                {
                    // Всё что до текущей фазы — Done (в т.ч. Active из предыдущей)
                    for (int i = 0; i < idx; i++)
                        if (_phaseChips[i].State != PhaseChipState.Failed)
                            MarkPhaseDone(_phaseChips[i]);
                    var chip = _phaseChips[idx];
                    chip.Detail = ev.Text;
                    chip.StartedAt = DateTime.Now;
                    chip.State = PhaseChipState.Active;
                    UpdateProgress();
                    UpdateCurrentComponent(chip);
                }
            }
            else if (ev.Level == EventLevel.Ok && ev.Text.StartsWith("✓ "))
            {
                int idx = Array.FindIndex(InstallPhases, p => ev.Text.Contains(p.Key, StringComparison.OrdinalIgnoreCase));
                if (idx >= 0 && idx < _phaseChips.Count)
                {
                    MarkPhaseDone(_phaseChips[idx]);
                    UpdateProgress();
                }
            }
            else if (ev.Level == EventLevel.Error)
            {
                _lastErrorText = ev.Text;
            }
        }));
    }

    private static void MarkPhaseDone(PhaseChip chip)
    {
        if (chip.StartedAt.HasValue && !chip.Duration.HasValue)
            chip.Duration = DateTime.Now - chip.StartedAt.Value;
        chip.State = PhaseChipState.Done;
    }

    private void UpdateCurrentComponent(PhaseChip chip)
    {
        TxtCurrentIcon.Text = "◐";
        TxtCurrentName.Text = chip.Label;
        TxtCurrentDesc.Text = string.IsNullOrEmpty(chip.Detail) ? "Установка и настройка компонента" : chip.Detail;
    }

    private void UpdateProgress()
    {
        int done = _phaseChips.Count(c => c.State == PhaseChipState.Done);
        int active = _phaseChips.Count(c => c.State == PhaseChipState.Active);
        double pct = _phaseChips.Count == 0 ? 0 : 100.0 * (done + active * 0.5) / _phaseChips.Count;
        InstallProgressBar.Value = pct;
        TxtInstallProgressLbl.Text = $"{(int)pct}%";
        ProgressFillCol.Width = new GridLength(pct, GridUnitType.Star);
        ProgressRestCol.Width = new GridLength(100 - pct, GridUnitType.Star);
        TxtPackagesDone.Text = $"{done} из {_phaseChips.Count} компонентов";
    }

    private void ClearLog()
    {
        if (InstallLogRtb == null) return;
        InstallLogRtb.Document = new FlowDocument
        {
            PageWidth = 2000,
            Blocks = { new Paragraph { LineHeight = 15, Margin = new Thickness(0) } }
        };
    }

    private void AppendLog(EventLevel level, string text)
    {
        if (InstallLogRtb == null) return;
        var doc = InstallLogRtb.Document;
        var para = doc.Blocks.FirstBlock as Paragraph;
        if (para == null)
        {
            para = new Paragraph { LineHeight = 15, Margin = new Thickness(0) };
            doc.Blocks.Add(para);
        }

        var color = level switch
        {
            EventLevel.Phase   => "#bb9af7",
            EventLevel.Command => "#7dcfff",
            EventLevel.Ok      => "#9ece6a",
            EventLevel.Warn    => "#e0af68",
            EventLevel.Error   => "#f7768e",
            EventLevel.Info    => "#c0caf5",
            _                  => "#c0caf5"
        };
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        para.Inlines.Add(new Run($"[{DateTime.Now:HH:mm:ss}] {text}") { Foreground = brush });
        para.Inlines.Add(new LineBreak());

        InstallLogRtb.ScrollToEnd();
    }

    // ═════════════════ LIVE TAIL (Logs tab) ════════════════════════════════
    private void OnTailToggleClick(object sender, RoutedEventArgs e)
    {
        if (_activeTailer != null && _activeTailer.IsRunning)
        {
            StopTail();
            return;
        }
        var row = _vm.Selected;
        if (row == null) { MessageBox.Show("Выбери сервер в таблице слева"); return; }

        var selectedItem = CmbLogSource.SelectedItem as ComboBoxItem;
        var path = selectedItem?.Tag as string ?? "/var/log/mail.log";

        StartTail(row.Config, path);
    }

    private void StartTail(ServerConfig srv, string logPath)
    {
        StopTail();
        ClearTail();
        TxtTailHeader.Text = $"root@{srv.Ip}  ·  tail -Fn 200 {logPath}";
        BtnTailToggle.Content = "Стоп";
        BtnTailToggle.Style = (Style)FindResource("ActionBtnDanger");
        IconButtonHelper.SetIcon(BtnTailToggle, PhosphorIcons.XCircle);

        _activeTailer = new SshTailer(srv, $"tail -Fn 200 {logPath}");
        _activeTailer.LineReceived += line => Dispatcher.BeginInvoke(new Action(() => AppendTailLine(line)));
        _activeTailer.Error += err => Dispatcher.BeginInvoke(new Action(() => AppendTailLine("[ошибка] " + err, isError: true)));
        _activeTailer.Start();
    }

    private void StopTail()
    {
        _activeTailer?.Stop();
        _activeTailer = null;
        BtnTailToggle.Content = "Запустить";
        BtnTailToggle.Style = (Style)FindResource("ActionBtnGreen");
        IconButtonHelper.SetIcon(BtnTailToggle, PhosphorIcons.Play);
        TxtTailHeader.Text = "not connected";
    }

    private void OnClearTailClick(object sender, RoutedEventArgs e) => ClearTail();

    private void ClearTail()
    {
        TailRtb.Document = new FlowDocument
        {
            PageWidth = 3000,
            Blocks = { new Paragraph { LineHeight = 15, Margin = new Thickness(0) } }
        };
    }

    private void AppendTailLine(string line, bool isError = false)
    {
        var para = TailRtb.Document.Blocks.FirstBlock as Paragraph
                   ?? throw new InvalidOperationException("no paragraph");
        var color = isError ? "#f7768e"
                  : line.Contains("error", StringComparison.OrdinalIgnoreCase) ? "#f7768e"
                  : line.Contains("warn",  StringComparison.OrdinalIgnoreCase) ? "#e0af68"
                  : line.Contains("postfix") ? "#7dcfff"
                  : line.Contains("dovecot") ? "#bb9af7"
                  : line.Contains("pmta")    ? "#9ece6a"
                  : line.Contains("named")   ? "#e0af68"
                  : "#c0caf5";
        var brush = (Brush)new BrushConverter().ConvertFromString(color)!;
        para.Inlines.Add(new Run(line) { Foreground = brush });
        para.Inlines.Add(new LineBreak());

        // Ограничение — не хранить больше 2000 строк
        if (para.Inlines.Count > 4000)
        {
            var toRemove = new List<System.Windows.Documents.Inline>();
            var count = para.Inlines.Count - 3000;
            foreach (var inl in para.Inlines)
            {
                if (count-- <= 0) break;
                toRemove.Add(inl);
            }
            foreach (var inl in toRemove) para.Inlines.Remove(inl);
        }

        if (ChkAutoScroll.IsChecked == true) TailRtb.ScrollToEnd();
    }

    // ═════════════════ COPY BUTTONS (Step 4) ═══════════════════════════════
    private void OnCopyClick(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.Tag is string s && !string.IsNullOrEmpty(s))
        {
            try { Clipboard.SetText(s); FlashButton((Button)sender, "✓ скопировано"); }
            catch { }
        }
    }

    private void OnCopyDnsClick(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(TxtDnsRecords.Text); FlashButton((Button)sender, "✓ скопировано"); }
        catch { }
    }

    private async void FlashButton(Button btn, string tempLabel)
    {
        var original = btn.Content;
        btn.Content = tempLabel;
        await Task.Delay(1200);
        btn.Content = original;
    }
}
