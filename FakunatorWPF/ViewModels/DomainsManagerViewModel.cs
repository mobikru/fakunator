using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Fakunator.Core;
using Fakunator.Core.DomainScanner;
using Fakunator.Core.DomainsManager;
using Fakunator.Core.Postmaster;

namespace Fakunator.ViewModels;

public class DomainsManagerViewModel : INotifyPropertyChanged
{
    private readonly object _lock = new();

    // ── Аккаунты (chip-обёртки для UI) ────────────────────────────────
    public ObservableCollection<IspAccountChip> Accounts { get; } = new();
    public ObservableCollection<PostmasterAccountChip> PostmasterAccounts { get; } = new();

    // ── Домены в DNS-панели (левая таблица) ───────────────────────────
    public ObservableCollection<DnsDomainRow> DnsRows { get; } = new();

    // ── Домены в постмастере (правая таблица) ─────────────────────────
    public ObservableCollection<PostmasterDomainRow> PostmasterRows { get; } = new();

    // ── Statys подключений (иконки в шапках карточек) ─────────────────
    private static readonly Brush DotGreen = new SolidColorBrush(Color.FromRgb(0x34, 0xd3, 0x99));
    private static readonly Brush DotAmber = new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b));
    private static readonly Brush DotRed = new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));
    private static readonly Brush DotGray = new SolidColorBrush(Color.FromRgb(0x9c, 0xa3, 0xaf));
    static DomainsManagerViewModel()
    {
        DotGreen.Freeze(); DotAmber.Freeze(); DotRed.Freeze(); DotGray.Freeze();
    }

    private Brush _ispConnectedDot = DotGray;
    public Brush IspConnectedDot { get => _ispConnectedDot; set => SetField(ref _ispConnectedDot, value); }
    private string _ispConnectionText = Loc.T("domains.conn.none");
    public string IspConnectionText { get => _ispConnectionText; set => SetField(ref _ispConnectionText, value); }

    private Brush _pmConnectedDot = DotGray;
    public Brush PostmasterConnectedDot { get => _pmConnectedDot; set => SetField(ref _pmConnectedDot, value); }
    private string _pmConnectionText = Loc.T("domains.conn.none");
    public string PostmasterConnectionText { get => _pmConnectionText; set => SetField(ref _pmConnectionText, value); }

    private string _pmFooter = "";
    public string PostmasterFooterText { get => _pmFooter; set => SetField(ref _pmFooter, value); }

    // ── Активные модели (для sidebar-совместимости) ───────────────────
    private IspAccount? _activeAccount;
    public IspAccount? ActiveAccount
    {
        get => _activeAccount;
        set
        {
            if (SetField(ref _activeAccount, value))
            {
                OnPropertyChanged(nameof(ActiveAccountChip));
                _ = LoadDomainsAsync();
            }
        }
    }

    // Обёртки-чипы для ComboBox.SelectedItem в новом хедере вкладки
    public IspAccountChip? ActiveAccountChip
    {
        get => Accounts.FirstOrDefault(c => c.Model == _activeAccount);
        set { if (value != null) ActiveAccount = value.Model; }
    }

    private PostmasterAccount? _activePostmasterAccount;
    public PostmasterAccount? ActivePostmasterAccount
    {
        get => _activePostmasterAccount;
        set
        {
            if (SetField(ref _activePostmasterAccount, value))
            {
                OnPropertyChanged(nameof(ActivePostmasterAccountChip));
                _pmAccessToken = null;
                _ = LoadPostmasterDomainsAsync();
            }
        }
    }
    public PostmasterAccountChip? ActivePostmasterAccountChip
    {
        get => PostmasterAccounts.FirstOrDefault(c => c.Model == _activePostmasterAccount);
        set { if (value != null) ActivePostmasterAccount = value.Model; }
    }

    // Выбранная строка в правой таблице → показывает drawer справа
    private PostmasterDomainRow? _selectedPostmasterDomain;
    public PostmasterDomainRow? SelectedPostmasterDomain
    {
        get => _selectedPostmasterDomain;
        set
        {
            if (SetField(ref _selectedPostmasterDomain, value))
            {
                OnPropertyChanged(nameof(DrawerVisibility));
                OnPropertyChanged(nameof(DrawerTroublesVisibility));
                RefreshDrawer();
                _ = LoadDrawerDailyAsync();
            }
        }
    }

    public Visibility DrawerVisibility => _selectedPostmasterDomain != null ? Visibility.Visible : Visibility.Collapsed;
    public Visibility DrawerTroublesVisibility =>
        _selectedPostmasterDomain != null && _selectedPostmasterDomain.Troubles.Count > 0
            ? Visibility.Visible : Visibility.Collapsed;

    private string _drawerMessagesSent = "—";
    public string DrawerMessagesSent { get => _drawerMessagesSent; set => SetField(ref _drawerMessagesSent, value); }
    private string _drawerDelivered = "—";
    public string DrawerDelivered { get => _drawerDelivered; set => SetField(ref _drawerDelivered, value); }
    private string _drawerSpamPercent = "—";
    public string DrawerSpamPercent { get => _drawerSpamPercent; set => SetField(ref _drawerSpamPercent, value); }
    private string _drawerReputation = "—";
    public string DrawerReputation { get => _drawerReputation; set => SetField(ref _drawerReputation, value); }

    public ObservableCollection<MetricRow> DrawerMetrics { get; } = new();
    public ObservableCollection<ChartLegendItem> DrawerChartLegend { get; } = new();

    private string _drawerChartRangeText = "";
    public string DrawerChartRangeText { get => _drawerChartRangeText; set => SetField(ref _drawerChartRangeText, value); }

    private Visibility _drawerChartEmptyVisibility = Visibility.Visible;
    public Visibility DrawerChartEmptyVisibility { get => _drawerChartEmptyVisibility; set => SetField(ref _drawerChartEmptyVisibility, value); }

    private ImageSource? _drawerChartImage;
    public ImageSource? DrawerChartImage { get => _drawerChartImage; set => SetField(ref _drawerChartImage, value); }

    public ICommand CloseDrawerCommand { get; }

    private bool _busy;
    public bool Busy { get => _busy; set => SetField(ref _busy, value); }

    private string _status = "";
    public string Status { get => _status; set => SetField(ref _status, value); }

    // ── Commands ─────────────────────────────────────────────────────
    public ICommand AddAccountCommand { get; }
    public ICommand AddFirstVdsAccountCommand { get; }
    public ICommand RemoveAccountCommand { get; }
    public ICommand SelectAccountCommand { get; }
    public ICommand RefreshDomainsCommand { get; }
    public ICommand LogoutIspCommand { get; }
    public ICommand AddDomainCommand { get; }
    public ICommand DeleteDomainCommand { get; }
    public ICommand OpenRecordsCommand { get; }

    public ICommand ConnectMailRuCommand { get; }
    public ICommand RemoveMailRuCommand { get; }
    public ICommand SelectPostmasterCommand { get; }
    public ICommand RefreshPostmasterCommand { get; }
    public ICommand LogoutPostmasterCommand { get; }
    public ICommand OpenPostmasterCommand { get; }
    public ICommand MailRuVerifyCommand { get; }

    // Кэш сессий чтобы не логиниться перед каждым вызовом
    private string? _sessionId;
    private IspAccount? _sessionAccount;
    private string? _pmAccessToken;

    public DomainsManagerViewModel()
    {
        BindingOperations.EnableCollectionSynchronization(Accounts, _lock);
        BindingOperations.EnableCollectionSynchronization(PostmasterAccounts, _lock);
        BindingOperations.EnableCollectionSynchronization(DnsRows, _lock);
        BindingOperations.EnableCollectionSynchronization(PostmasterRows, _lock);
        LoadAccountsFromConfig();

        AddAccountCommand = new RelayCommand(_ => _ = AddAccountAsync());
        AddFirstVdsAccountCommand = new RelayCommand(_ =>
        {
            var dlg = new Views.ConnectFirstVdsDialog();
            if (dlg.ShowDialog() != true || dlg.Result == null) return;
            var acc = dlg.Result;
            lock (_lock)
            {
                var existing = Accounts.FirstOrDefault(c =>
                    string.Equals(c.Model.Host, acc.Host, StringComparison.OrdinalIgnoreCase));
                if (existing != null) Accounts.Remove(existing);
                Accounts.Add(new IspAccountChip(acc));
            }
            SaveAccountsToConfig();
            foreach (var c in Accounts) c.IsSelected = c.Model == acc;
            ActiveAccount = acc;
            _sessionId = acc.CachedSessionId;
            _sessionAccount = acc;
            IspConnectedDot = DotGreen;
            IspConnectionText = acc.DisplayName;
            Status = Loc.T("domains.status.firstVdsConnected");
        });
        RemoveAccountCommand = new RelayCommand(o =>
        {
            IspAccount? acc = o switch
            {
                IspAccountChip c => c.Model,
                IspAccount a => a,
                _ => null
            };
            if (acc != null) RemoveAccount(acc);
        });
        SelectAccountCommand = new RelayCommand(o =>
        {
            if (o is IspAccountChip chip)
            {
                foreach (var c in Accounts) c.IsSelected = c == chip;
                ActiveAccount = chip.Model;
            }
        });
        RefreshDomainsCommand = new RelayCommand(_ => _ = LoadDomainsAsync(force: true));
        LogoutIspCommand = new RelayCommand(_ =>
        {
            _sessionId = null;
            _sessionAccount = null;
            IspConnectedDot = DotGray;
            IspConnectionText = Loc.T("domains.conn.none");
            lock (_lock) DnsRows.Clear();
            Status = Loc.T("domains.status.disconnectedDns");
        });

        AddDomainCommand = new RelayCommand(_ => _ = AddDomainAsync());
        DeleteDomainCommand = new RelayCommand(d =>
        {
            IspDomain? dom = d switch
            {
                IspDomain m => m,
                DnsDomainRow r when Accounts.Any() => new IspDomain(r.Domain, "master", null, null),
                _ => null
            };
            if (dom != null) _ = DeleteDomainAsync(dom);
        });
        OpenRecordsCommand = new RelayCommand(d =>
        {
            string? name = d switch
            {
                string s => s,
                IspDomain m => m.Name,
                DnsDomainRow r => r.Domain,
                _ => null
            };
            if (!string.IsNullOrEmpty(name)) _ = OpenRecordsAsync(name);
        });
        MailRuVerifyCommand = new RelayCommand(d =>
        {
            string? name = d switch
            {
                string s => s,
                IspDomain m => m.Name,
                DnsDomainRow r => r.Domain,
                _ => null
            };
            if (!string.IsNullOrEmpty(name)) _ = OpenMailRuVerifyAsync(name);
        });

        OpenPostmasterCommand = new RelayCommand(a =>
        {
            var acc = (a as PostmasterAccountChip)?.Model as PostmasterAccount
                      ?? a as PostmasterAccount
                      ?? (PostmasterAccounts.Count > 0 ? PostmasterAccounts[0].Model : null);
            if (acc == null)
            {
                MessageBox.Show(Loc.T("domains.err.noPostmasterAccountBody"),
                    Loc.T("domains.err.noPostmasterAccountTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            var win = new Views.PostmasterDomainsWindow(acc);
            win.Show();
        });

        ConnectMailRuCommand = new RelayCommand(_ =>
        {
            var dlg = new Views.ConnectMailRuDialog();
            if (dlg.ShowDialog() != true || dlg.Result == null) return;
            var acc = dlg.Result;
            lock (_lock)
            {
                var existing = PostmasterAccounts.FirstOrDefault(x => x.Username == acc.Username);
                if (existing != null) PostmasterAccounts.Remove(existing);
                PostmasterAccounts.Add(new PostmasterAccountChip(acc));
            }
            SavePostmasterToConfig();
            Status = string.Format(Loc.T("domains.status.mailruConnected"), acc.Username);
            if (ActivePostmasterAccount == null)
            {
                ActivePostmasterAccount = acc;
                if (PostmasterAccounts.Count > 0) PostmasterAccounts[^1].IsSelected = true;
            }
        });
        RemoveMailRuCommand = new RelayCommand(a =>
        {
            PostmasterAccount? acc = a switch
            {
                PostmasterAccountChip c => c.Model,
                PostmasterAccount m => m,
                _ => null
            };
            if (acc == null) return;
            var confirm = MessageBox.Show(
                string.Format(Loc.T("domains.confirm.removeMailRuBody"), acc.Username),
                Loc.T("domains.confirm.removeMailRuTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (confirm != MessageBoxResult.Yes) return;
            lock (_lock)
            {
                var chip = PostmasterAccounts.FirstOrDefault(x => x.Model == acc);
                if (chip != null) PostmasterAccounts.Remove(chip);
            }
            SavePostmasterToConfig();
            if (ActivePostmasterAccount == acc)
                ActivePostmasterAccount = PostmasterAccounts.Count > 0 ? PostmasterAccounts[0].Model : null;
            Status = string.Format(Loc.T("domains.status.mailruDisconnected"), acc.Username);
        });
        SelectPostmasterCommand = new RelayCommand(o =>
        {
            if (o is PostmasterAccountChip chip)
            {
                foreach (var c in PostmasterAccounts) c.IsSelected = c == chip;
                ActivePostmasterAccount = chip.Model;
            }
        });
        RefreshPostmasterCommand = new RelayCommand(_ =>
        {
            _pmAccessToken = null;
            _ = LoadPostmasterDomainsAsync();
        });
        LogoutPostmasterCommand = new RelayCommand(_ =>
        {
            _pmAccessToken = null;
            PostmasterConnectedDot = DotGray;
            PostmasterConnectionText = Loc.T("domains.conn.none");
            lock (_lock) PostmasterRows.Clear();
            PostmasterFooterText = "";
            RefreshDnsPostmasterStatus();
            Status = Loc.T("domains.status.disconnectedPostmaster");
        });

        CloseDrawerCommand = new RelayCommand(_ => SelectedPostmasterDomain = null);

        BindingOperations.EnableCollectionSynchronization(DrawerMetrics, _lock);
        BindingOperations.EnableCollectionSynchronization(DrawerChartLegend, _lock);

        Config.Changed += (_, _) => LoadAccountsFromConfig();

        // Локализованные тексты не хранятся статично — при смене языка пересчитываем всё,
        // что уже отображено на экране (аналогично status-key подходу в DomainScannerViewModel).
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            lock (_lock)
            {
                foreach (var r in DnsRows) r.NotifyLocalizationChanged();
                foreach (var r in PostmasterRows) r.NotifyLocalizationChanged();
            }
            RefreshDrawer();
        };
    }

    // ── Drawer: содержимое карточки выбранного домена ─────────────────
    // LabelKey — ключ Loc, а не готовый текст: резолвится заново при каждом RefreshDrawer(),
    // поэтому переживает смену языка (в отличие от строки, вычисленной один раз в статике).
    private static readonly (string Key, string LabelKey, string Color)[] DrawerSeries =
    {
        ("messages_sent",  "domains.metric.messagesSent",  "#8b5cf6"),
        ("delivered",      "domains.metric.delivered",     "#22c55e"),
        ("read",           "domains.metric.read",          "#3b82f6"),
        ("deleted_read",   "domains.metric.deletedRead",   "#a855f7"),
        ("deleted_unread", "domains.metric.deletedUnread", "#f59e0b"),
        ("spam",           "domains.metric.spam",          "#ef4444"),
        ("probably_spam",  "domains.metric.probablySpam",  "#dc2626"),
        ("complaints",     "domains.metric.complaints",    "#7c2d12"),
    };

    private void RefreshDrawer()
    {
        DrawerMetrics.Clear();
        DrawerChartLegend.Clear();
        var row = SelectedPostmasterDomain;
        if (row == null)
        {
            DrawerMessagesSent = DrawerDelivered = DrawerSpamPercent = DrawerReputation = "—";
            DrawerChartImage = null;
            DrawerChartEmptyVisibility = Visibility.Visible;
            return;
        }
        var s = row.AllStats;
        double sent = s.GetValueOrDefault("messages_sent");
        double deliv = s.GetValueOrDefault("delivered");
        double spam = s.GetValueOrDefault("spam");
        double rep = s.GetValueOrDefault("reputation");
        DrawerMessagesSent = sent > 0 ? FormatNum(sent) : "—";
        DrawerDelivered = deliv > 0 ? FormatNum(deliv) : "—";
        DrawerSpamPercent = sent > 0 ? (spam / sent * 100).ToString("N2",
            System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) : "0,00";
        DrawerReputation = rep > 0 ? rep.ToString("N2",
            System.Globalization.CultureInfo.GetCultureInfo("ru-RU")) : "—";

        // Список метрик (то что не влезло в 4 tile'а)
        foreach (var key in new[] { "read", "deleted_read", "deleted_unread",
                                    "spam", "probably_spam", "probably_spam_percent",
                                    "complaints", "trend" })
        {
            if (!s.TryGetValue(key, out var v)) continue;
            var (label, formatted) = FormatMetric(key, v);
            DrawerMetrics.Add(new MetricRow(label, formatted));
        }

        // Легенда графика
        foreach (var (k, labelKey, colorHex) in DrawerSeries)
        {
            if (!s.ContainsKey(k)) continue;
            var color = (Color)ColorConverter.ConvertFromString(colorHex)!;
            var b = new SolidColorBrush(color); b.Freeze();
            DrawerChartLegend.Add(new ChartLegendItem(Loc.T(labelKey), b));
        }
    }

    private static (string label, string formatted) FormatMetric(string key, double v)
    {
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        string label = key switch
        {
            "read" => Loc.T("domains.metric.read"),
            "deleted_read" => Loc.T("domains.metric.deletedRead"),
            "deleted_unread" => Loc.T("domains.metric.deletedUnread"),
            "spam" => Loc.T("domains.metric.spam"),
            "probably_spam" => Loc.T("domains.metric.probablySpam"),
            "probably_spam_percent" => Loc.T("domains.metric.probablySpamPercent"),
            "complaints" => Loc.T("domains.metric.complaints"),
            "trend" => Loc.T("domains.metric.trend"),
            _ => key
        };
        string formatted = key.EndsWith("percent")
            ? v.ToString("N2", ru) + " %"
            : key == "trend"
                ? v.ToString("N2", ru)
                : ((long)v).ToString("N0", ru);
        return (label, formatted);
    }

    private async Task LoadDrawerDailyAsync()
    {
        var row = SelectedPostmasterDomain;
        if (row == null) return;
        var token = _pmAccessToken;
        if (string.IsNullOrEmpty(token))
        {
            DrawerChartEmptyVisibility = Visibility.Visible;
            DrawerChartImage = null;
            return;
        }
        if (row.Daily.Count == 0)
        {
            try
            {
                using var api = new PostmasterApiClient(token);
                var daily = await api.StatListDetailedAsync(row.Domain,
                    DateTime.UtcNow.AddDays(-30), DateTime.UtcNow);
                row.Daily = daily;
            }
            catch { }
        }
        if (row.Daily.Count > 0)
        {
            DrawerChartRangeText = $"{ShortDate(row.Daily[0].Date)} — {ShortDate(row.Daily[^1].Date)}";
            DrawerChartImage = BuildChart(row.Daily);
            DrawerChartEmptyVisibility = Visibility.Collapsed;
        }
        else
        {
            DrawerChartRangeText = "";
            DrawerChartImage = null;
            DrawerChartEmptyVisibility = Visibility.Visible;
        }
    }

    private static string ShortDate(string iso) =>
        DateTime.TryParse(iso, out var dt) ? dt.ToString("dd.MM") : iso;

    /// <summary>
    /// Рисует line-chart серии из <see cref="DrawerSeries"/> как <see cref="DrawingImage"/>.
    /// Через DrawingImage дешевле чем через Canvas в scroll-контейнере и без утечки Visual.
    /// </summary>
    private static ImageSource BuildChart(List<PostmasterDaily> daily)
    {
        const double W = 380, H = 160;
        const double padL = 30, padR = 8, padT = 6, padB = 22;
        double plotW = W - padL - padR;
        double plotH = H - padT - padB;

        double yMax = 0;
        foreach (var (k, _, _) in DrawerSeries)
            foreach (var d in daily)
                if (d.Metrics.TryGetValue(k, out var v) && v > yMax) yMax = v;
        if (yMax <= 0) yMax = 1;
        double step = daily.Count > 1 ? plotW / (daily.Count - 1) : plotW;

        var group = new DrawingGroup();
        var axisPen = new Pen(new SolidColorBrush(Color.FromRgb(0x3f, 0x3f, 0x46)), 1);
        axisPen.Freeze();

        // Axes
        group.Children.Add(new GeometryDrawing(null, axisPen,
            new LineGeometry(new Point(padL, padT), new Point(padL, H - padB))));
        group.Children.Add(new GeometryDrawing(null, axisPen,
            new LineGeometry(new Point(padL, H - padB), new Point(W - padR, H - padB))));

        // Y-labels
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x71, 0x71, 0x7a));
        labelBrush.Freeze();
        var tf = new Typeface("Segoe UI");
        void AddText(string s, double x, double y, double size = 9)
        {
            var ft = new FormattedText(s, System.Globalization.CultureInfo.CurrentCulture,
                FlowDirection.LeftToRight, tf, size, labelBrush, 96);
            group.Children.Add(new GeometryDrawing(labelBrush, null,
                ft.BuildGeometry(new Point(x, y))));
        }
        AddText("0", 4, H - padB - 8);
        AddText(((long)(yMax / 2)).ToString("N0"), 2, padT + plotH / 2 - 8);
        AddText(((long)yMax).ToString("N0"), 2, padT - 4);
        if (daily.Count > 0)
        {
            AddText(ShortDate(daily[0].Date), padL - 12, H - padB + 4);
            AddText(ShortDate(daily[^1].Date), W - padR - 26, H - padB + 4);
        }

        // Series
        foreach (var (key, _, colorHex) in DrawerSeries)
        {
            bool anyPositive = false;
            foreach (var d in daily)
                if (d.Metrics.TryGetValue(key, out var v) && v > 0) { anyPositive = true; break; }
            if (!anyPositive) continue;
            var pen = new Pen(new SolidColorBrush((Color)ColorConverter.ConvertFromString(colorHex)!), 1.6)
            {
                LineJoin = PenLineJoin.Round,
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round
            };
            pen.Freeze();
            var geom = new StreamGeometry();
            using (var ctx = geom.Open())
            {
                bool first = true;
                for (int i = 0; i < daily.Count; i++)
                {
                    daily[i].Metrics.TryGetValue(key, out var v);
                    var pt = new Point(padL + i * step, H - padB - (v / yMax) * plotH);
                    if (first) { ctx.BeginFigure(pt, false, false); first = false; }
                    else ctx.LineTo(pt, true, false);
                }
            }
            geom.Freeze();
            group.Children.Add(new GeometryDrawing(null, pen, geom));
        }

        var img = new DrawingImage(group);
        img.Freeze();
        return img;
    }

    public record MetricRow(string Label, string Value);
    public record ChartLegendItem(string Label, Brush Color);

    private void LoadAccountsFromConfig()
    {
        lock (_lock)
        {
            Accounts.Clear();
            foreach (var a in Config.Current.IspAccounts)
                Accounts.Add(new IspAccountChip(a));
            PostmasterAccounts.Clear();
            foreach (var p in Config.Current.PostmasterAccounts)
                PostmasterAccounts.Add(new PostmasterAccountChip(p));
        }
        if (ActiveAccount == null && Accounts.Count > 0)
        {
            Accounts[0].IsSelected = true;
            ActiveAccount = Accounts[0].Model;
        }
        if (ActivePostmasterAccount == null && PostmasterAccounts.Count > 0)
        {
            PostmasterAccounts[0].IsSelected = true;
            ActivePostmasterAccount = PostmasterAccounts[0].Model;
        }
    }

    private void SaveAccountsToConfig()
    {
        Config.Current.IspAccounts = Accounts.Select(c => c.Model).ToList();
        Config.Current.Save();
    }
    private void SavePostmasterToConfig()
    {
        Config.Current.PostmasterAccounts = PostmasterAccounts.Select(c => c.Model).ToList();
        Config.Current.Save();
    }

    // ── Аккаунты: добавление/удаление ────────────────────────────────

    private async Task AddAccountAsync()
    {
        var dlg = new Views.AddIspAccountDialog();
        if (dlg.ShowDialog() != true) return;
        var acc = dlg.Result;
        if (acc == null) return;

        // FirstVDS: сессия уже получена через WebView2, повторный AuthAsync не нужен
        // (пароля тоже нет — юзер логинился в браузере). Сразу сохраняем.
        if (acc.AuthType == "firstvds")
        {
            lock (_lock)
            {
                var existing = Accounts.FirstOrDefault(c =>
                    string.Equals(c.Model.Host, acc.Host, StringComparison.OrdinalIgnoreCase));
                if (existing != null) Accounts.Remove(existing);
                Accounts.Add(new IspAccountChip(acc));
            }
            SaveAccountsToConfig();
            foreach (var c in Accounts) c.IsSelected = c.Model == acc;
            // Кэш сессии сохраняем ДО ActiveAccount = acc, чтобы LoadDomainsAsync (который
            // триггерится сеттером ActiveAccount) сразу использовал готовую сессию.
            _sessionId = acc.CachedSessionId;
            _sessionAccount = acc;
            ActiveAccount = acc;
            OnPropertyChanged(nameof(ActiveAccountChip));
            IspConnectedDot = DotGreen;
            IspConnectionText = acc.DisplayName;
            Status = Loc.T("domains.status.firstVdsConnected");
            return;
        }

        Busy = true;
        Status = string.Format(Loc.T("domains.status.checkingLogin"), acc.Username, acc.Host);
        IspConnectedDot = DotAmber;
        IspConnectionText = Loc.T("domains.conn.connecting");
        try
        {
            using var client = new IspApiClient(acc.Host);
            var sid = await client.AuthAsync(acc.Username, acc.Password);
            lock (_lock) Accounts.Add(new IspAccountChip(acc));
            SaveAccountsToConfig();
            foreach (var c in Accounts) c.IsSelected = c.Model == acc;
            ActiveAccount = acc;
            _sessionId = sid;
            _sessionAccount = acc;
            IspConnectedDot = DotGreen;
            IspConnectionText = $"{acc.Username}@{acc.Host}";
            Status = Loc.T("domains.status.accountAdded");
        }
        catch (IspApiException ex)
        {
            IspConnectedDot = DotRed;
            IspConnectionText = Loc.T("domains.conn.loginError");
            MessageBox.Show(string.Format(Loc.T("domains.err.loginFailedBody"), ex.Message),
                Loc.T("domains.err.loginFailedTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch (Exception ex)
        {
            IspConnectedDot = DotRed;
            IspConnectionText = Loc.T("domains.conn.networkError");
            MessageBox.Show(string.Format(Loc.T("domains.err.networkBody"), ex.Message),
                Loc.T("domains.err.networkTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Busy = false; }
    }

    private void RemoveAccount(IspAccount acc)
    {
        var confirm = MessageBox.Show(
            string.Format(Loc.T("domains.confirm.removeIspAccountBody"), acc.Username, acc.Host),
            Loc.T("domains.confirm.removeIspAccountTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;
        lock (_lock)
        {
            var chip = Accounts.FirstOrDefault(c => c.Model == acc);
            if (chip != null) Accounts.Remove(chip);
        }
        SaveAccountsToConfig();
        if (ActiveAccount == acc)
        {
            ActiveAccount = Accounts.Count > 0 ? Accounts[0].Model : null;
            if (ActiveAccount != null) Accounts[0].IsSelected = true;
        }
    }

    // ── Домены DNS-панели ────────────────────────────────────────────

    private async Task<string?> EnsureSessionAsync(IspAccount acc)
    {
        if (_sessionAccount == acc && !string.IsNullOrEmpty(_sessionId)) return _sessionId;
        // FirstVDS не имеет пароля — сессия обновляется только через WebView2-логин.
        if (acc.AuthType == "firstvds")
        {
            if (!string.IsNullOrEmpty(acc.CachedSessionId))
            {
                _sessionId = acc.CachedSessionId;
                _sessionAccount = acc;
                return _sessionId;
            }
            MessageBox.Show(
                Loc.T("domains.err.sessionExpiredBody"),
                Loc.T("domains.err.sessionExpiredTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return null;
        }
        try
        {
            using var client = new IspApiClient(acc.Host);
            _sessionId = await client.AuthAsync(acc.Username, acc.Password);
            _sessionAccount = acc;
            return _sessionId;
        }
        catch (Exception ex)
        {
            IspConnectedDot = DotRed;
            IspConnectionText = Loc.T("domains.conn.loginError");
            MessageBox.Show(string.Format(Loc.T("domains.err.panelLoginFailed"), ex.Message),
                Loc.T("domains.err.genericTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    public async Task LoadDomainsAsync(bool force = false)
    {
        var acc = ActiveAccount;
        if (acc == null) { lock (_lock) DnsRows.Clear(); return; }

        Busy = true;
        Status = string.Format(Loc.T("domains.status.loadingDomains"), acc.Username, acc.Host);
        IspConnectedDot = DotAmber;
        IspConnectionText = Loc.T("domains.conn.loading");
        try
        {
            if (force) _sessionId = null;
            var sid = await EnsureSessionAsync(acc);
            if (sid == null) return;

            using var client = new IspApiClient(acc.Host);
            var list = await client.ListDomainsAsync(sid);
            var rows = list.Select(d => new DnsDomainRow(d.Name)).ToList();
            lock (_lock)
            {
                DnsRows.Clear();
                foreach (var r in rows) DnsRows.Add(r);
            }
            IspConnectedDot = DotGreen;
            IspConnectionText = $"{acc.Username}@{acc.Host}";
            Status = string.Format(Loc.T("domains.status.domainsLoaded"), list.Count);

            // Асинхронно резолвим NS + определяем provider (не блокируем UI)
            _ = ResolveNsForRowsAsync(rows);
            RefreshDnsPostmasterStatus();
            // Health: возраст / expiry / RBL — сначала из кэша, потом фоново whois
            _ = EnrichHealthAsync(rows, force);
        }
        catch (Exception ex)
        {
            IspConnectedDot = DotRed;
            IspConnectionText = Loc.T("domains.conn.error");
            MessageBox.Show(string.Format(Loc.T("domains.err.genericBody"), ex.Message), Loc.T("domains.err.loadDomainsTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Busy = false; }
    }

    /// <summary>
    /// Health-обогащение доменов: сначала кэш из БД (мгновенно), потом фоновый whois + RBL
    /// для тех у кого кэша нет. При <paramref name="forceRefresh"/> — стираем кэш и перечекиваем всё.
    /// </summary>
    private DomainDb? _healthDb;
    private DomainHealthChecker? _healthChecker;

    private async Task EnrichHealthAsync(List<DnsDomainRow> rows, bool forceRefresh)
    {
        try
        {
            _healthDb ??= new DomainDb(System.IO.Path.Combine(Paths.DataDir, "domains.db"));
            _healthChecker ??= new DomainHealthChecker(_healthDb);
            // Свежие кэшированные — мгновенно
            foreach (var r in rows)
            {
                if (forceRefresh) continue;
                var cached = _healthChecker.GetCached(r.Domain);
                if (cached != null) r.SetHealth(cached);
            }
            // Кого нет — фоново
            var toFetch = rows.Where(r => forceRefresh || _healthChecker.GetCached(r.Domain) == null).ToList();
            if (toFetch.Count == 0) return;
            _ = Task.Run(async () =>
            {
                using var sem = new System.Threading.SemaphoreSlim(4);
                var tasks = toFetch.Select(async r =>
                {
                    await sem.WaitAsync();
                    try
                    {
                        var h = await _healthChecker.EnrichAsync(r.Domain);
                        r.SetHealth(h);
                    }
                    catch { }
                    finally { sem.Release(); }
                });
                try { await Task.WhenAll(tasks); } catch { }
            });
        }
        catch { }
    }

    private async Task ResolveNsForRowsAsync(List<DnsDomainRow> rows)
    {
        var resolver = new NsResolver(Config.Current.DomainDnsTimeoutSec > 0
            ? Config.Current.DomainDnsTimeoutSec : 3);
        // Ограничим параллельность чтобы не спамить резолверы
        using var sem = new System.Threading.SemaphoreSlim(8);
        var tasks = rows.Select(async r =>
        {
            await sem.WaitAsync();
            try
            {
                var (hosts, _) = await resolver.ResolveNsAsync(r.Domain);
                var provider = NsProviderDetector.Detect(hosts);
                Application.Current?.Dispatcher.Invoke(() => r.Ns = ShortProvider(provider));
            }
            catch
            {
                Application.Current?.Dispatcher.Invoke(() => r.Ns = "—");
            }
            finally { sem.Release(); }
        }).ToList();
        await Task.WhenAll(tasks);
    }

    /// <summary>«REG.RU» → «reg.ru», «Cloudflare» → «cloudflare» и т.п. — короче для колонки NS.</summary>
    private static string ShortProvider(string p)
    {
        if (string.IsNullOrEmpty(p) || p == "Unknown") return "—";
        // Zomro и родственные ISPmanager-панели → «isp» (по макету)
        var lower = p.ToLowerInvariant();
        if (lower.Contains("zomro") || lower.Contains("ispmanager")) return "isp";
        if (lower.Contains("reg.ru")) return "reg.ru";
        if (lower.Contains("nic.ru")) return "nic.ru";
        if (lower.Contains("cloudflare")) return "cloudflare";
        if (lower.Contains("yandex")) return "yandex";
        if (lower.Contains("selectel")) return "selectel";
        if (lower.Contains("beget")) return "beget";
        if (lower.Contains("timeweb")) return "timeweb";
        return p.ToLowerInvariant();
    }

    private async Task AddDomainAsync()
    {
        var acc = ActiveAccount;
        if (acc == null)
        {
            MessageBox.Show(Loc.T("domains.err.selectAccountBody"), Loc.T("domains.err.selectAccountTitle"),
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        var dlg = new Views.AddDomainDialog();
        if (dlg.ShowDialog() != true) return;
        var (domain, ip) = dlg.Result;
        if (string.IsNullOrWhiteSpace(domain)) return;

        Busy = true;
        Status = string.Format(Loc.T("domains.status.addingDomain"), domain);
        try
        {
            var sid = await EnsureSessionAsync(acc);
            if (sid == null) return;
            using var client = new IspApiClient(acc.Host);
            var ok = await client.AddDomainAsync(sid, domain, "master", ip);
            if (ok) { Status = string.Format(Loc.T("domains.status.domainAdded"), domain); await LoadDomainsAsync(); }
        }
        catch (Exception ex)
        {
            if (ex.Message.Contains("already exists", StringComparison.OrdinalIgnoreCase)
                || ex.Message.Contains("type=exists", StringComparison.OrdinalIgnoreCase))
            {
                await LoadDomainsAsync();
                MessageBox.Show(
                    string.Format(Loc.T("domains.err.domainExistsBody"), domain), Loc.T("domains.err.domainExistsTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Information);
            }
            else
            {
                MessageBox.Show(string.Format(Loc.T("domains.err.genericBody"), ex.Message), Loc.T("domains.err.addDomainTitle"),
                    MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
        finally { Busy = false; }
    }

    private async Task OpenRecordsAsync(string domain)
    {
        var acc = ActiveAccount;
        if (acc == null) return;
        var sid = await EnsureSessionAsync(acc);
        if (sid == null) return;
        new Views.DnsRecordsWindow(acc, domain, sid).Show();
    }

    private async Task OpenMailRuVerifyAsync(string domain)
    {
        var acc = ActiveAccount;
        if (acc == null) return;
        var sid = await EnsureSessionAsync(acc);
        if (sid == null) return;
        new Views.MailRuVerifyDialog(acc, sid, domain).Show();
    }

    private async Task DeleteDomainAsync(IspDomain dom)
    {
        var acc = ActiveAccount;
        if (acc == null) return;
        var confirm = MessageBox.Show(
            string.Format(Loc.T("domains.confirm.deleteDomainBody"), dom.Name),
            Loc.T("domains.confirm.deleteDomainTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;

        Busy = true;
        try
        {
            var sid = await EnsureSessionAsync(acc);
            if (sid == null) return;
            using var client = new IspApiClient(acc.Host);
            if (await client.DeleteDomainAsync(sid, dom.Name))
            {
                Status = string.Format(Loc.T("domains.status.domainDeleted"), dom.Name);
                await LoadDomainsAsync();
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("domains.err.genericBody"), ex.Message), Loc.T("domains.err.deleteDomainTitle"),
                MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally { Busy = false; }
    }

    // ── Postmaster: домены + метрики ──────────────────────────────────

    /// <summary>
    /// Возвращает access_token для API. При каждом успешном refresh mail.ru может
    /// прислать НОВЫЙ refresh_token и инвалидировать старый — мы обязаны сохранить
    /// свежий обратно в config, иначе токен «протухнет» через один цикл.
    /// </summary>
    private async Task<string?> EnsurePostmasterAccessAsync()
    {
        if (!string.IsNullOrEmpty(_pmAccessToken)) return _pmAccessToken;
        var acc = ActivePostmasterAccount;
        if (acc == null)
        {
            _pmErrorReason = "нет активного аккаунта";
            return null;
        }
        if (string.IsNullOrEmpty(acc.RefreshToken))
        {
            _pmErrorReason = "нет refresh-токена — жми + и переподключи";
            return null;
        }
        try
        {
            using var oauth = new MailRuOAuthClient();
            var t = await oauth.RefreshAccessAsync(acc.RefreshToken);
            _pmAccessToken = t.AccessToken;
            // Ротация: если mail.ru вернул новый refresh_token — сохраняем.
            if (!string.IsNullOrEmpty(t.RefreshToken) && t.RefreshToken != acc.RefreshToken)
            {
                acc.RefreshToken = t.RefreshToken;
                SavePostmasterToConfig();
            }
            _pmErrorReason = null;
            return _pmAccessToken;
        }
        catch (MailRuAuthException ex)
        {
            _pmErrorReason = ex.Message.Contains("invalid_grant", StringComparison.OrdinalIgnoreCase)
                ? "токен протух — жми + и переподключи"
                : $"OAuth: {ex.Message}";
            return null;
        }
        catch (Exception ex)
        {
            _pmErrorReason = $"сеть: {ex.Message}";
            return null;
        }
    }

    private string? _pmErrorReason;

    public async Task LoadPostmasterDomainsAsync()
    {
        var acc = ActivePostmasterAccount;
        if (acc == null)
        {
            lock (_lock) PostmasterRows.Clear();
            PostmasterConnectedDot = DotGray;
            PostmasterConnectionText = Loc.T("domains.conn.none");
            PostmasterFooterText = "";
            RefreshDnsPostmasterStatus();
            return;
        }
        PostmasterConnectedDot = DotAmber;
        PostmasterConnectionText = Loc.T("domains.conn.loading");
        var token = await EnsurePostmasterAccessAsync();
        if (token == null)
        {
            PostmasterConnectedDot = DotRed;
            PostmasterConnectionText = Loc.T("domains.conn.noToken");
            return;
        }
        try
        {
            using var api = new PostmasterApiClient(token);
            var list = await api.RegListAsync();
            var troubles = await api.TroublesListAsync();
            var byDomain = troubles.GroupBy(t => t.Domain, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(g => g.Key, g => g.ToList(), StringComparer.OrdinalIgnoreCase);

            var rows = new List<PostmasterDomainRow>();
            foreach (var d in list)
            {
                var row = new PostmasterDomainRow(d.Domain)
                {
                    Troubles = byDomain.TryGetValue(d.Domain, out var t) ? t : new()
                };
                bool verified = d.Verified;
                bool rejected = d.Status.Equals("rejected", StringComparison.OrdinalIgnoreCase);
                row.VerificationStatus = rejected ? PmChipStatus.Rejected : (verified ? PmChipStatus.Verified : PmChipStatus.Pending);
                row.VerificationFg = rejected ? DotRed : (verified ? DotGreen : DotAmber);
                row.VerificationIcon = rejected ? "✕" : (verified ? "●" : "○");
                row.StatusDot = row.VerificationFg;
                row.TroublesMark = row.Troubles.Count > 0 ? "▲" : "";
                rows.Add(row);
            }
            lock (_lock)
            {
                PostmasterRows.Clear();
                foreach (var r in rows) PostmasterRows.Add(r);
            }
            PostmasterConnectedDot = DotGreen;
            PostmasterConnectionText = acc.Username;
            PostmasterFooterText = rows.All(r => r.VerificationStatus == PmChipStatus.Verified)
                ? Loc.T("domains.pmList.allVerified")
                : string.Format(Loc.T("domains.pmList.someVerified"),
                    rows.Count(r => r.VerificationStatus == PmChipStatus.Verified), rows.Count);
            RefreshDnsPostmasterStatus();

            // Загрузим метрики по каждому домену параллельно (макс. 4 одновременно, rate-limit API)
            _ = LoadPostmasterMetricsAsync(rows);
        }
        catch (Exception ex)
        {
            PostmasterConnectedDot = DotRed;
            PostmasterConnectionText = Loc.T("domains.conn.apiError");
            Status = string.Format(Loc.T("domains.status.postmasterError"), ex.Message);
        }
    }

    private async Task LoadPostmasterMetricsAsync(List<PostmasterDomainRow> rows)
    {
        var token = _pmAccessToken;
        if (string.IsNullOrEmpty(token)) return;
        using var sem = new System.Threading.SemaphoreSlim(4);
        var tasks = rows.Select(async row =>
        {
            await sem.WaitAsync();
            try
            {
                using var api = new PostmasterApiClient(token);
                var stat = await api.StatListAsync(row.Domain);
                row.AllStats = stat.Metrics;
                var sent = stat.Metrics.TryGetValue("messages_sent", out var s) ? s : 0;
                var spam = stat.Metrics.TryGetValue("spam", out var sp) ? sp : 0;
                var pct = sent > 0 ? spam / sent * 100.0 : 0;
                var rep = stat.Metrics.TryGetValue("reputation", out var r) ? r : (double?)null;
                Application.Current?.Dispatcher.Invoke(() =>
                {
                    var ruCul = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
                    row.MessagesSentText = FormatNum(sent);
                    row.SpamPercentText = pct.ToString("N2", ruCul);
                    row.SpamFg = pct >= 5 ? DotRed : (pct >= 1 ? DotAmber : DotGreen);

                    // Доп. столбцы (как в веб-постмастере)
                    row.ComplaintsText = FormatNum(stat.Metrics.TryGetValue("complaints", out var cv) ? cv : 0);
                    row.ReadText = FormatNum(stat.Metrics.TryGetValue("read", out var rv) ? rv : 0);
                    row.DeletedReadText = FormatNum(stat.Metrics.TryGetValue("deleted_read", out var drv) ? drv : 0);
                    row.DeletedUnreadText = FormatNum(stat.Metrics.TryGetValue("deleted_unread", out var duv) ? duv : 0);
                    var trendV = stat.Metrics.TryGetValue("trend", out var tv) ? tv : 0;
                    row.TrendText = (trendV >= 0 ? "+" : "") + trendV.ToString("N2", ruCul);
                    row.TrendFg = trendV > 0 ? DotGreen : (trendV < 0 ? DotRed : DotGray);
                    row.ReputationPctText = (rep ?? 0).ToString("N2", ruCul);

                    // Доставляемость: 3-сегментная полоса как в веб-постмастере
                    var delivered = stat.Metrics.TryGetValue("delivered", out var dv) ? dv : 0;
                    var probablySpam = stat.Metrics.TryGetValue("probably_spam", out var ps) ? ps : 0;
                    var actualSpam = stat.Metrics.TryGetValue("spam", out var sv) ? sv : 0;
                    var totalDelivered = delivered + probablySpam + actualSpam;
                    const double BarTotalWidth = 130.0;
                    if (totalDelivered > 0)
                    {
                        row.DeliveredPct = delivered / totalDelivered * 100.0;
                        row.ProbablySpamPct = probablySpam / totalDelivered * 100.0;
                        row.SpamPct = actualSpam / totalDelivered * 100.0;
                        row.DeliveredBarWidth = row.DeliveredPct / 100.0 * BarTotalWidth;
                        row.ProbablySpamBarWidth = row.ProbablySpamPct / 100.0 * BarTotalWidth;
                        row.SpamBarWidth = row.SpamPct / 100.0 * BarTotalWidth;
                        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
                        row.DeliverabilityTooltip = string.Format(Loc.T("domains.deliverability.tooltip"),
                            row.DeliveredPct.ToString("N1", ru), row.ProbablySpamPct.ToString("N1", ru), row.SpamPct.ToString("N1", ru));
                    }
                    else
                    {
                        row.DeliveredBarWidth = row.ProbablySpamBarWidth = row.SpamBarWidth = 0;
                        row.DeliverabilityTooltip = Loc.T("domains.deliverability.noData");
                    }
                    if (rep.HasValue)
                    {
                        var repVal = Math.Clamp(rep.Value, 0, 100);
                        row.ReputationText = ((int)repVal).ToString();
                        row.ReputationBarWidth = repVal / 100.0 * 90.0;
                        row.ReputationBarFill = repVal >= 80 ? DotGreen
                                              : repVal >= 50 ? DotAmber
                                              : DotRed;
                    }
                    else
                    {
                        row.ReputationText = "—";
                        row.ReputationBarWidth = 0;
                    }
                    // PostmasterDomainRow теперь notify-props — UI обновится сам,
                    // без Remove+Insert (тот сбрасывал SelectedItem и закрывал drawer).
                });
            }
            catch { /* тихо — метрика не критична */ }
            finally { sem.Release(); }
        });
        await Task.WhenAll(tasks);
    }

    /// <summary>После загрузки Postmaster проставляет status в строках DNS-таблицы.</summary>
    private void RefreshDnsPostmasterStatus()
    {
        Application.Current?.Dispatcher.Invoke(() =>
        {
            var verified = new HashSet<string>(
                PostmasterRows.Where(r => r.VerificationStatus == PmChipStatus.Verified).Select(r => r.Domain),
                StringComparer.OrdinalIgnoreCase);
            var rejected = new HashSet<string>(
                PostmasterRows.Where(r => r.VerificationStatus == PmChipStatus.Rejected).Select(r => r.Domain),
                StringComparer.OrdinalIgnoreCase);
            foreach (var row in DnsRows)
            {
                if (verified.Contains(row.Domain))
                {
                    row.PostmasterStatus = PmChipStatus.Verified;
                    row.PostmasterChipBg = new SolidColorBrush(Color.FromArgb(0x1a, 0x34, 0xd3, 0x99));
                    row.PostmasterChipBd = new SolidColorBrush(Color.FromArgb(0x40, 0x34, 0xd3, 0x99));
                    row.PostmasterChipFg = DotGreen;
                    row.StatusDot = DotGreen;
                }
                else if (rejected.Contains(row.Domain))
                {
                    row.PostmasterStatus = PmChipStatus.Rejected;
                    row.PostmasterChipBg = new SolidColorBrush(Color.FromArgb(0x1a, 0xef, 0x44, 0x44));
                    row.PostmasterChipBd = new SolidColorBrush(Color.FromArgb(0x40, 0xef, 0x44, 0x44));
                    row.PostmasterChipFg = DotRed;
                    row.StatusDot = DotRed;
                }
                else
                {
                    row.PostmasterStatus = PmChipStatus.NotAdded;
                    row.PostmasterChipBg = new SolidColorBrush(Color.FromArgb(0x20, 0x9c, 0xa3, 0xaf));
                    row.PostmasterChipBd = new SolidColorBrush(Color.FromArgb(0x40, 0x9c, 0xa3, 0xaf));
                    row.PostmasterChipFg = new SolidColorBrush(Color.FromRgb(0x9c, 0xa3, 0xaf));
                    row.StatusDot = DotGray;
                }
            }
        });
    }

    private static string FormatNum(double v)
    {
        var ru = System.Globalization.CultureInfo.GetCultureInfo("ru-RU");
        return ((long)v).ToString("N0", ru);
    }

    // ── INotifyPropertyChanged ───────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}
