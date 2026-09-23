using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using Fakunator.Core;
using Fakunator.Core.DomainScanner;
using Fakunator.Core.TelegramBot;

namespace Fakunator.ViewModels;

public class DomainScannerViewModel : INotifyPropertyChanged
{
    // ── Persisted settings (from Config) ─────────────────────────────
    private string _zones = "";
    private int _concurrency;
    private string _targetNs = "";

    public string Zones
    {
        get => _zones;
        set { if (SetField(ref _zones, value)) SaveToConfig(); }
    }
    public int Concurrency
    {
        get => _concurrency;
        set { if (SetField(ref _concurrency, value)) SaveToConfig(); }
    }
    public string TargetNs
    {
        get => _targetNs;
        set { if (SetField(ref _targetNs, value)) SaveToConfig(); }
    }

    // ── Runtime state ────────────────────────────────────────────────
    private DomainScanSnapshot? _snapshot;
    public DomainScanSnapshot? Snapshot
    {
        get => _snapshot;
        set => SetField(ref _snapshot, value);
    }

    private bool _running;
    public bool Running
    {
        get => _running;
        set
        {
            if (SetField(ref _running, value))
                OnPropertyChanged(nameof(StatusDotColor));
        }
    }

    private string _statusText = "";
    public string StatusText
    {
        get => _statusText;
        set
        {
            if (SetField(ref _statusText, value))
                OnPropertyChanged(nameof(StatusDotColor));
        }
    }

    /// <summary>Смысловая "категория" статуса — определяет цвет dot-индикатора.
    /// Держим отдельно от StatusText, потому что текст теперь локализуется (ru/en)
    /// и сравнивать его по префиксу (как раньше) больше нельзя.</summary>
    private enum StatusColorKind { Neutral, Warn, Error }
    private StatusColorKind _statusKind = StatusColorKind.Neutral;
    private string _statusKey = "domainscanner.status.notStarted";
    private object[] _statusArgs = Array.Empty<object>();

    /// <summary>Задаёт статус через ключ локализации + параметры форматирования и его "цвет".
    /// StatusText пересобирается сразу и повторно — при смене языка (см. подписку в конструкторе).</summary>
    private void SetStatus(string key, StatusColorKind kind, params object[] args)
    {
        _statusKey = key;
        _statusArgs = args;
        _statusKind = kind;
        ApplyStatusText();
    }

    private void ApplyStatusText()
    {
        var fmt = Loc.T(_statusKey);
        StatusText = _statusArgs.Length > 0 ? string.Format(fmt, _statusArgs) : fmt;
        OnPropertyChanged(nameof(StatusDotColor));
    }

    /// <summary>
    /// Цвет dot-индикатора в статусе:
    /// зелёный — идёт скан, оранжевый — остановлено/есть данные в базе,
    /// серый — пусто, ничего не было, красный — ошибка.
    /// </summary>
    public string StatusDotColor
    {
        get
        {
            if (_running) return "#22c55e";
            return _statusKind switch
            {
                StatusColorKind.Warn => "#f59e0b",
                StatusColorKind.Error => "#ef4444",
                _ => "#71717a",
            };
        }
    }

    private string _elapsedText = "00:00";
    public string ElapsedText
    {
        get => _elapsedText;
        set => SetField(ref _elapsedText, value);
    }

    /// <summary>Живой поток последних обработанных доменов (для таблицы).</summary>
    public ObservableCollection<DomainRecord> FreshFeed { get; } = new();
    public int FreshFeedCap { get; set; } = 40;
    private readonly object _freshLock = new();

    /// <summary>Свёрнут ли «Живой поток обработки». Юзер прячет когда работает с браузером.</summary>
    private bool _isFeedExpanded = true;
    public bool IsFeedExpanded
    {
        get => _isFeedExpanded;
        set => SetField(ref _isFeedExpanded, value);
    }

    // Внутренний буфер плавного «выпуска» строк в feed. Worker пишет сюда пачками
    // (по 40 за snapshot), а UI-таймер каждые 33ms (~30fps) забирает 1-3 записи —
    // так получается визуальный поток без "рывков" по 40 строк сразу.
    private readonly System.Collections.Concurrent.ConcurrentQueue<DomainRecord> _pendingFeed = new();
    private System.Windows.Threading.DispatcherTimer? _feedFlushTimer;

    /// <summary>Топ NS-провайдеров (после фильтра ProviderSearch).</summary>
    public ObservableCollection<ProviderStat> TopProviders { get; } = new();

    /// <summary>Полный несортированный источник для TopProviders — держим отдельно от отображаемой коллекции,
    /// чтобы при вводе в поиск можно было переприменять фильтр без повторного удара по БД.</summary>
    private List<(string Provider, int Count)> _topProvidersSource = new();

    private string _providerSearch = "";
    public string ProviderSearch
    {
        get => _providerSearch;
        set { if (SetField(ref _providerSearch, value)) RebuildTopProviders(_topProvidersSource); }
    }
    private readonly object _topLock = new();
    private string _topFilterZone = "";
    public string TopFilterZone
    {
        get => _topFilterZone;
        set
        {
            if (SetField(ref _topFilterZone, value)) RefreshTopProvidersFromDb();
        }
    }

    // ── Browser (пейджинг сохранённых доменов) ───────────────────────
    public ObservableCollection<DomainBrowserRowVm> BrowserRows { get; } = new();
    private DomainHealthChecker? _health;
    private CancellationTokenSource? _enrichCts;
    private readonly object _browserLock = new();

    /// <summary>Метка "все / all" — специальный маркер "фильтр не активен" в ComboBox'ах.
    /// Локализуется, но старое значение "все" тоже распознаётся (см. Norm()) —
    /// на случай если оно уже где-то сохранено/сравнивается на старте.</summary>
    private static string AllLabel => Loc.T("domainscanner.filter.all");

    /// <summary>Список зон в БД + пункт "все" в начале — для ComboBox фильтра.</summary>
    public ObservableCollection<string> AvailableZones { get; } = new() { AllLabel };

    /// <summary>Список NS-провайдеров (топ) + пункт "все" — для ComboBox фильтра.</summary>
    public ObservableCollection<string> AvailableProviders { get; } = new() { AllLabel };

    private int _browserPage = 1;
    public int BrowserPage
    {
        get => _browserPage;
        set => SetField(ref _browserPage, value);
    }
    public int BrowserPageSize { get; set; } = 50;

    private int _browserTotal;
    public int BrowserTotal
    {
        get => _browserTotal;
        set { if (SetField(ref _browserTotal, value)) OnPropertyChanged(nameof(BrowserTotalPages)); }
    }
    public int BrowserTotalPages =>
        BrowserPageSize > 0 ? Math.Max(1, (int)Math.Ceiling((double)BrowserTotal / BrowserPageSize)) : 1;

    // Browser filters. Значения "все" — специальный маркер "фильтр не активен"
    // (см. Norm() — превращает в null для БД-запроса). ComboBox используют этот
    // маркер как SelectedItem по умолчанию.
    private string _filterZone = AllLabel;
    private string _filterProvider = AllLabel;
    private string _filterNsHost = "";
    private string _filterQuery = "";
    public string FilterZone { get => _filterZone; set => SetField(ref _filterZone, value); }
    public string FilterProvider { get => _filterProvider; set => SetField(ref _filterProvider, value); }
    public string FilterNsHost { get => _filterNsHost; set => SetField(ref _filterNsHost, value); }
    public string FilterQuery { get => _filterQuery; set => SetField(ref _filterQuery, value); }

    // Commands
    public ICommand StartCommand { get; }
    public ICommand StopCommand { get; }
    public ICommand ApplyFiltersCommand { get; }
    public ICommand ResetFiltersCommand { get; }
    public ICommand NextPageCommand { get; }
    public ICommand PrevPageCommand { get; }
    public ICommand ExportCommand { get; }
    public ICommand ReverifyCommand { get; }
    public ICommand PickProviderCommand { get; }
    public ICommand ToggleFavoriteProviderCommand { get; }
    public ICommand ClearDbCommand { get; }

    // Internals
    private CancellationTokenSource? _cts;
    private DomainDb? _db;
    private DomainScanWorker? _worker;
    private DateTime _startedAt;
    private System.Windows.Threading.DispatcherTimer? _elapsedTimer;
    // Флаг «фоновая задача ещё не свернулась». Юзер уже видит UI как остановленное,
    // но нельзя стартовать новый скан пока предыдущий не завершил все воркеры.
    private volatile bool _taskAlive;

    public DomainScannerViewModel()
    {
        BindingOperations.EnableCollectionSynchronization(FreshFeed, _freshLock);
        BindingOperations.EnableCollectionSynchronization(TopProviders, _topLock);
        BindingOperations.EnableCollectionSynchronization(BrowserRows, _browserLock);
        BindingOperations.EnableCollectionSynchronization(AvailableZones, _browserLock);
        BindingOperations.EnableCollectionSynchronization(AvailableProviders, _browserLock);

        var cfg = Config.Current;
        _zones = string.IsNullOrEmpty(cfg.DomainScanZones) ? "ru, com" : cfg.DomainScanZones;
        _concurrency = cfg.DomainScanConcurrency > 0 ? cfg.DomainScanConcurrency : 300;
        _targetNs = cfg.DomainScanTargetNs ?? "";

        StartCommand = new RelayCommand(_ => _ = StartAsync(), _ => !Running);
        StopCommand = new RelayCommand(_ => Stop(), _ => Running);
        ApplyFiltersCommand = new RelayCommand(_ => RefreshBrowser());
        ResetFiltersCommand = new RelayCommand(_ =>
        {
            // AllLabel — потому что фильтры теперь ComboBox с этим пунктом
            FilterZone = AllLabel; FilterProvider = AllLabel;
            FilterNsHost = ""; FilterQuery = "";
            BrowserPage = 1;
            RefreshBrowser();
        });
        NextPageCommand = new RelayCommand(_ =>
        {
            if (BrowserPage < BrowserTotalPages) { BrowserPage++; RefreshBrowser(); }
        });
        PrevPageCommand = new RelayCommand(_ =>
        {
            if (BrowserPage > 1) { BrowserPage--; RefreshBrowser(); }
        });
        ExportCommand = new RelayCommand(_ => _ = ExportAsync());
        ReverifyCommand = new RelayCommand(_ => _ = ReverifyAsync());

        // Клик по провайдеру в сайдбар-топе → фильтруем браузер по нему.
        ClearDbCommand = new RelayCommand(_ =>
        {
            if (_db == null) return;
            if (Running)
            {
                MessageBox.Show(Loc.T("domainscanner.confirm.clearDbBusyBody"),
                    Loc.T("domainscanner.confirm.clearDbBusyTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            var total = _db.TotalCount();
            var confirm = MessageBox.Show(
                string.Format(Loc.T("domainscanner.confirm.clearDbBody"), total),
                Loc.T("domainscanner.confirm.clearDbTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (confirm != MessageBoxResult.Yes) return;

            int cleared = 0;
            try { cleared = _db.ClearAll(); }
            catch (Exception ex)
            {
                MessageBox.Show(string.Format(Loc.T("domainscanner.err.clearDbBody"), ex.Message),
                    Loc.T("domainscanner.err.clearDbTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
                return;
            }

            // Сбросить UI-состояние
            lock (_freshLock) FreshFeed.Clear();
            lock (_topLock) TopProviders.Clear();
            lock (_browserLock) { BrowserRows.Clear(); AvailableZones.Clear(); AvailableProviders.Clear(); AvailableZones.Add(AllLabel); AvailableProviders.Add(AllLabel); }
            Snapshot = null;
            BrowserTotal = 0;
            BrowserPage = 1;
            FilterZone = AllLabel; FilterProvider = AllLabel;
            FilterNsHost = ""; FilterQuery = "";
            SetStatus("domainscanner.status.notStarted", StatusColorKind.Neutral);
            ElapsedText = "00:00";

            MessageBox.Show(string.Format(Loc.T("domainscanner.confirm.clearDbDoneBody"), cleared),
                Loc.T("domainscanner.confirm.clearDbDoneTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        });

        PickProviderCommand = new RelayCommand(name =>
        {
            if (name is not string s) return;
            // Убеждаемся что провайдер есть в списке ComboBox (иначе SelectedItem = null)
            lock (_browserLock)
            {
                if (!AvailableProviders.Contains(s)) AvailableProviders.Add(s);
            }
            FilterProvider = s;
            BrowserPage = 1;
            RefreshBrowser();
        });

        ToggleFavoriteProviderCommand = new RelayCommand(o =>
        {
            var name = (o as ProviderStat)?.Name ?? o as string;
            if (string.IsNullOrEmpty(name)) return;
            var favs = Config.Current.FavoriteProviders;
            var existing = favs.FirstOrDefault(x => string.Equals(x, name, StringComparison.OrdinalIgnoreCase));
            if (existing != null) favs.Remove(existing);
            else favs.Add(name);
            Config.Current.FavoriteProviders = favs;
            Config.Current.Save();
            RefreshTopProvidersFromDb();
        });

        // Открываем БД, чтобы сразу показать сохранённое (даже без старта)
        try
        {
            var dbPath = Path.Combine(Paths.DataDir, "domains.db");
            _db = new DomainDb(dbPath);
            RefreshBrowser();
            RefreshTopProvidersFromDb();
            RefreshFilterLists();
            var total = _db.TotalCount();
            if (total > 0) SetStatus("domainscanner.status.inDb", StatusColorKind.Warn, total);
            else SetStatus("domainscanner.status.notStarted", StatusColorKind.Neutral);
        }
        catch (Exception ex)
        {
            SetStatus("domainscanner.status.dbError", StatusColorKind.Error, ex.Message);
        }

        // При смене языка: пересобрать StatusText из сохранённого ключа+параметров,
        // обновить метку "все/all" в списках и текущих значениях фильтров (если стоял маркер "все всё"),
        // пересобрать текстовые колонки браузера (AgeText/DaysLeftText/RblText локализуются на лету).
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            ApplyStatusText();
            bool zoneWasAll = _filterZone == "все" || _filterZone == AllLabel;
            bool providerWasAll = _filterProvider == "все" || _filterProvider == AllLabel;
            RefreshFilterLists();
            if (zoneWasAll) FilterZone = AllLabel;
            if (providerWasAll) FilterProvider = AllLabel;
            RefreshBrowser();
            RefreshTopProvidersFromDb();
        };
    }

    private void SaveToConfig()
    {
        var cfg = Config.Current;
        cfg.DomainScanZones = _zones;
        cfg.DomainScanConcurrency = _concurrency;
        cfg.DomainScanTargetNs = _targetNs;
        cfg.Save();
    }

    public async Task StartAsync()
    {
        if (Running || _taskAlive)
        {
            MessageBox.Show(Loc.T("domainscanner.err.notReadyBody"),
                Loc.T("domainscanner.err.notReadyTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        _taskAlive = true;
        var cfg = Config.Current;
        var apiKey = cfg.DomainsMonitorApiKey;
        if (string.IsNullOrWhiteSpace(apiKey))
        {
            MessageBox.Show(Loc.T("domainscanner.err.noApiKeyBody"),
                Loc.T("domainscanner.err.noApiKeyTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        var zones = ParseZones(_zones);
        if (zones.Count == 0)
        {
            MessageBox.Show(Loc.T("domainscanner.err.noZonesBody"),
                Loc.T("domainscanner.err.noZonesTitle"), MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }

        _db ??= new DomainDb(Path.Combine(Paths.DataDir, "domains.db"));
        _worker = new DomainScanWorker(
            apiKey, zones, _concurrency, _targetNs, "",
            cfg.DomainDnsTimeoutSec, _db);

        _cts = new CancellationTokenSource();
        Running = true;
        SetStatus("domainscanner.status.starting", StatusColorKind.Neutral);
        _startedAt = DateTime.UtcNow;
        NotificationHub.Notify($"🌐 <b>Скан доменов запущен</b>\nзоны: <code>{string.Join(", ", zones)}</code>");

        _elapsedTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _elapsedTimer.Tick += (_, _) =>
        {
            var d = DateTime.UtcNow - _startedAt;
            ElapsedText = d.TotalHours >= 1 ? d.ToString(@"h\:mm\:ss") : d.ToString(@"mm\:ss");
        };
        _elapsedTimer.Start();

        // Живой feed: ~30fps, до 3 записей за тик. Тюнинг под визуальную плавность
        // без слома FPS даже когда воркеры валят по 200+ доменов/сек.
        _feedFlushTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(33)
        };
        _feedFlushTimer.Tick += FeedFlushTick;
        _feedFlushTimer.Start();

        var progress = new Progress<DomainScanSnapshot>(OnSnapshot);
        try
        {
            await Task.Run(() => _worker.RunAsync(progress, _cts.Token));
            SetStatus("domainscanner.status.done", StatusColorKind.Warn);
            var total = _db?.TotalCount() ?? 0;
            NotificationHub.Notify($"✅ <b>Скан доменов завершён</b>\nв базе: <b>{total:N0}</b>");
        }
        catch (OperationCanceledException)
        {
            SetStatus("domainscanner.status.stopped", StatusColorKind.Warn);
            NotificationHub.Notify("⏸ <b>Скан доменов остановлен</b>");
        }
        catch (Exception ex)
        {
            SetStatus("domainscanner.status.error", StatusColorKind.Error, ex.Message);
            NotificationHub.Notify($"❌ <b>Скан упал</b>\n<code>{ex.Message}</code>");
        }
        finally
        {
            _elapsedTimer?.Stop();
            _elapsedTimer = null;
            _feedFlushTimer?.Stop();
            _feedFlushTimer = null;
            // Дошлём остатки очереди в UI (быстро, не по одной)
            DrainPendingFeed(int.MaxValue);
            Running = false;
            _taskAlive = false;
            RefreshBrowser();
            RefreshTopProvidersFromDb();
            RefreshFilterLists();
        }
    }

    private void RefreshFilterLists()
    {
        if (_db == null) return;
        try
        {
            var zones = _db.Zones();
            var providers = _db.TopProviders(200).Select(p => p.Provider).ToList();

            lock (_browserLock)
            {
                AvailableZones.Clear();
                AvailableZones.Add(AllLabel);
                foreach (var z in zones) AvailableZones.Add(z);

                AvailableProviders.Clear();
                AvailableProviders.Add(AllLabel);
                foreach (var p in providers) AvailableProviders.Add(p);
            }
        }
        catch { }
    }

    private void FeedFlushTick(object? sender, EventArgs e)
    {
        DrainPendingFeed(3);
    }

    /// <summary>Забирает до <paramref name="maxItems"/> записей из очереди и добавляет в UI.</summary>
    private void DrainPendingFeed(int maxItems)
    {
        int taken = 0;
        while (taken < maxItems && _pendingFeed.TryDequeue(out var rec))
        {
            FreshFeed.Insert(0, rec);
            taken++;
        }
        // Тримим один раз в конце пачки, не по одному
        while (FreshFeed.Count > FreshFeedCap)
            FreshFeed.RemoveAt(FreshFeed.Count - 1);
    }

    private void Stop()
    {
        if (!Running) return;

        _worker?.Stop();
        _cts?.Cancel();

        // Немедленно освобождаем UI — таймеры глушим и статус меняем СРАЗУ.
        // Task.WhenAll внутри worker'а завершится в фоне и добьёт финальный refresh.
        // Иначе dispatcher захлёбывается continuations от 300 отменённых await'ов —
        // окно "зависает" пока task не свернётся полностью.
        _feedFlushTimer?.Stop();
        _feedFlushTimer = null;
        _elapsedTimer?.Stop();
        _elapsedTimer = null;
        DrainPendingFeed(int.MaxValue);
        Running = false;
        SetStatus("domainscanner.status.stoppedDraining", StatusColorKind.Warn);
    }

    private DateTime _lastTopRebuild = DateTime.MinValue;

    private void OnSnapshot(DomainScanSnapshot snap)
    {
        Snapshot = snap;
        if (snap.Running)
            SetStatus("domainscanner.status.scanning", StatusColorKind.Neutral, snap.CurrentZone, snap.Speed);
        else
            SetStatus("domainscanner.status.done", StatusColorKind.Warn);

        foreach (var rec in snap.FreshFeed)
            _pendingFeed.Enqueue(rec);

        // Top providers rebuild — тяжёлая операция (LINQ sort + ObservableCollection.Clear+Add
        // с N CollectionChanged событий). При частых snapshot от сканера это лочит UI.
        // Throttle: обновляем максимум раз в 2 секунды или при завершении.
        if (!snap.Running || (DateTime.UtcNow - _lastTopRebuild).TotalSeconds >= 2)
        {
            _lastTopRebuild = DateTime.UtcNow;
            RebuildTopProviders(snap.TopProviders);
        }
    }

    /// <summary>Пересобирает TopProviders: применяем поиск, избранные сверху, остальные по Count DESC.</summary>
    private void RebuildTopProviders(IEnumerable<(string Provider, int Count)> src)
    {
        _topProvidersSource = src.ToList();
        var favs = new HashSet<string>(Config.Current.FavoriteProviders, StringComparer.OrdinalIgnoreCase);
        var q = _providerSearch?.Trim() ?? "";
        var filtered = string.IsNullOrEmpty(q)
            ? (IEnumerable<(string Provider, int Count)>)_topProvidersSource
            : _topProvidersSource.Where(x => x.Provider != null
                && x.Provider.Contains(q, StringComparison.OrdinalIgnoreCase));
        var arr = filtered.ToList();
        var max = arr.Count > 0 ? Math.Max(1, arr.Max(x => x.Count)) : 1;
        var ordered = arr
            .Select(x => (x.Provider, x.Count, IsFav: favs.Contains(x.Provider)))
            .OrderByDescending(x => x.IsFav)
            .ThenBy(x => x.IsFav ? x.Provider : "")
            .ThenByDescending(x => x.Count)
            .ToList();
        lock (_topLock)
        {
            TopProviders.Clear();
            foreach (var (p, c, fav) in ordered)
                TopProviders.Add(new ProviderStat(p, c, (double)c / max, fav));
        }
    }

    private void RefreshTopProvidersFromDb()
    {
        if (_db == null) return;
        try
        {
            var zone = string.IsNullOrEmpty(_topFilterZone) ? null : _topFilterZone;
            var list = _db.TopProviders(200, zone);
            RebuildTopProviders(list);
        }
        catch { }
    }

    private static string? Norm(string? s) =>
        string.IsNullOrWhiteSpace(s) || s == "все" || s == AllLabel ? null : s;

    /// <summary>
    /// Прогоняет NS-проверку заново для доменов под текущими фильтрами браузера.
    /// Те что перестали быть lame_delegation — удаляются из БД (перестали быть брошенными).
    /// </summary>
    private async Task ReverifyAsync()
    {
        if (_db == null || Running) return;

        var domains = _db.ListAllDomainNames(
            Norm(_filterZone), Norm(_filterProvider),
            Norm(_filterNsHost), Norm(_filterQuery));

        if (domains.Count == 0)
        {
            MessageBox.Show(Loc.T("domainscanner.confirm.reverifyEmptyBody"),
                Loc.T("domainscanner.confirm.reverifyEmptyTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var confirm = MessageBox.Show(
            string.Format(Loc.T("domainscanner.confirm.reverifyBody"), domains.Count),
            Loc.T("domainscanner.confirm.reverifyTitle"), MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.Yes) return;

        Running = true;
        SetStatus("domainscanner.status.reverifying", StatusColorKind.Neutral, 0, domains.Count);
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        var toDelete = new System.Collections.Concurrent.ConcurrentBag<string>();
        int processed = 0;
        int kept = 0;
        var sw = System.Diagnostics.Stopwatch.StartNew();

        try
        {
            var resolver = new NsResolver(Config.Current.DomainDnsTimeoutSec);
            var concurrency = Math.Max(1, Config.Current.DomainScanConcurrency);
            using var sem = new SemaphoreSlim(concurrency);

            var tasks = new List<Task>(domains.Count);
            foreach (var domain in domains)
            {
                ct.ThrowIfCancellationRequested();
                await sem.WaitAsync(ct);
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        var nsTask = resolver.ResolveNsAsync(domain, ct);
                        var aTask = resolver.ResolveAAsync(domain, ct);
                        await Task.WhenAll(nsTask, aTask).ConfigureAwait(false);
                        var (nsHosts, nsStatus) = nsTask.Result;
                        var (_, aStatus) = aTask.Result;
                        bool hasA = aStatus == "ok";

                        // has_a=true → сайт реально резолвится и работает,
                        // такой домен НЕ может быть брошенным. Удаляется из базы.
                        bool stillLame = nsStatus == "lame_delegation" && !hasA;
                        if (stillLame && nsHosts.Count == 0)
                            nsHosts = await resolver.ResolveNsAtRegistryAsync(domain).ConfigureAwait(false);

                        if (stillLame)
                        {
                            var provider = NsProviderDetector.Detect(nsHosts);
                            var zone = domain.Contains('.') ? domain[(domain.LastIndexOf('.') + 1)..] : "";
                            _db.Enqueue(new DomainRecord(domain, zone, provider, nsHosts, nsStatus, hasA, aStatus));
                            Interlocked.Increment(ref kept);
                        }
                        else
                        {
                            toDelete.Add(domain);
                        }

                        var done = Interlocked.Increment(ref processed);
                        if (done % 20 == 0)
                        {
                            var speed = sw.Elapsed.TotalSeconds > 0 ? done / sw.Elapsed.TotalSeconds : 0;
                            SetStatus("domainscanner.status.reverifyingSpeed", StatusColorKind.Neutral, done, domains.Count, speed);
                        }
                    }
                    finally { sem.Release(); }
                }, ct));
            }
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { /* stop pressed */ }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("domainscanner.err.reverifyBody"), ex.Message),
                Loc.T("domainscanner.err.reverifyTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            int deleted = 0;
            if (!toDelete.IsEmpty)
                deleted = _db.DeleteDomains(toDelete);

            Running = false;
            SetStatus("domainscanner.status.reverifyDone", StatusColorKind.Warn, kept, deleted);
            RefreshBrowser();
            RefreshTopProvidersFromDb();
            RefreshFilterLists();

            MessageBox.Show(
                string.Format(Loc.T("domainscanner.confirm.reverifyDoneBody"), processed, kept, deleted),
                Loc.T("domainscanner.confirm.reverifyDoneTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }

    private void RefreshBrowser()
    {
        if (_db == null) return;
        try
        {
            var (rows, total) = _db.ListDomains(
                Norm(_filterZone), Norm(_filterProvider),
                Norm(_filterNsHost), Norm(_filterQuery),
                BrowserPage, BrowserPageSize);
            var vms = rows.Select(r => new DomainBrowserRowVm(r)).ToList();
            lock (_browserLock)
            {
                BrowserRows.Clear();
                foreach (var v in vms) BrowserRows.Add(v);
            }
            BrowserTotal = total;
            _ = EnrichVisiblePageAsync(vms);
        }
        catch { }
    }

    /// <summary>
    /// Для каждой видимой строки: сначала пробуем кэш из БД, иначе fetch фоново.
    /// Ограничиваем параллелизм чтобы не задудосить whois.tcinet.ru.
    /// </summary>
    private async Task EnrichVisiblePageAsync(List<DomainBrowserRowVm> rows)
    {
        if (_db == null) return;
        _health ??= new DomainHealthChecker(_db);
        _enrichCts?.Cancel();
        _enrichCts = new CancellationTokenSource();
        var ct = _enrichCts.Token;

        // 1. Мгновенно применяем то что уже есть в кэше — это дешёвая операция
        foreach (var r in rows)
        {
            var cached = _health.GetCached(r.Domain);
            if (cached != null) r.SetHealth(cached);
        }
        // 2. Whois-запросы не запускаем пока идёт скан — иначе UI лагает
        // (whois-fetch + scan одновременно = tcp-конкуренция и spike активности на UI-потоке).
        if (Running) return;
        // 3. Кого нет в кэше — фоново, max 3 concurrent (снижено с 6 чтобы не лагать)
        var toFetch = rows.Where(r => _health.GetCached(r.Domain) == null).ToList();
        if (toFetch.Count == 0) return;
        _ = Task.Run(async () =>
        {
            using var sem = new System.Threading.SemaphoreSlim(3);
            var tasks = toFetch.Select(async r =>
            {
                await sem.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) return;
                    var h = await _health.EnrichAsync(r.Domain, ct);
                    if (!ct.IsCancellationRequested) r.SetHealth(h);
                }
                catch { }
                finally { sem.Release(); }
            });
            try { await Task.WhenAll(tasks); } catch { }
        }, ct);
    }

    private async Task ExportAsync()
    {
        if (_db == null) return;

        // Соберём картину: какие фильтры реально применены (те что нашлись
        // в UI-полях, а не «поиск в сайдбаре» — эта путаница ловила пользователя раньше).
        var provider = Norm(_filterProvider);
        var zone = Norm(_filterZone);
        var nsHost = Norm(_filterNsHost);
        var query = Norm(_filterQuery);
        var filters = new List<string>();
        if (provider != null) filters.Add(string.Format(Loc.T("domainscanner.export.filterProvider"), provider));
        if (zone != null) filters.Add(string.Format(Loc.T("domainscanner.export.filterZone"), zone));
        if (nsHost != null) filters.Add(string.Format(Loc.T("domainscanner.export.filterNsHost"), nsHost));
        if (query != null) filters.Add(string.Format(Loc.T("domainscanner.export.filterQuery"), query));
        var filterLine = filters.Count > 0
            ? Loc.T("domainscanner.export.filtersHeader") + string.Join("\n  · ", filters)
            : Loc.T("domainscanner.export.noFilters");

        var total = BrowserTotal > 0 ? BrowserTotal : _db.TotalCount();
        var confirm = MessageBox.Show(
            string.Format(Loc.T("domainscanner.confirm.exportBody"), total, filterLine),
            Loc.T("domainscanner.confirm.exportTitle"),
            MessageBoxButton.OKCancel,
            filters.Count > 0 ? MessageBoxImage.Information : MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        // Имя файла осмысленное — по фильтрам
        var slug = SafeSlug(provider ?? "all") + (zone != null ? "_" + zone : "");
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = Loc.T("domainscanner.export.dialogTitle"),
            Filter = Loc.T("domainscanner.export.dialogFilter"),
            FileName = $"domains_{slug}.txt",
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            await _db.ExportToFileAsync(zone, provider, nsHost, query,
                dlg.FileName, CancellationToken.None);
            MessageBox.Show(string.Format(Loc.T("domainscanner.confirm.exportDoneBody"), total, dlg.FileName),
                Loc.T("domainscanner.confirm.exportDoneTitle"), MessageBoxButton.OK, MessageBoxImage.Information);
        }
        catch (Exception ex)
        {
            MessageBox.Show(string.Format(Loc.T("domainscanner.err.exportBody"), ex.Message),
                Loc.T("domainscanner.err.exportTitle"), MessageBoxButton.OK, MessageBoxImage.Error);
        }
    }

    private static string SafeSlug(string s)
    {
        if (string.IsNullOrEmpty(s)) return "all";
        var sb = new System.Text.StringBuilder(s.Length);
        foreach (var c in s.ToLowerInvariant())
            sb.Append(char.IsLetterOrDigit(c) ? c : '-');
        return sb.ToString().Trim('-');
    }

    private static List<string> ParseZones(string csv)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(csv)) return list;
        foreach (var part in csv.Split(new[] { ',', ';', '\n', ' ', '\t' }, StringSplitOptions.RemoveEmptyEntries))
        {
            var v = part.Trim().TrimStart('.').ToLowerInvariant();
            if (!string.IsNullOrEmpty(v) && !list.Contains(v)) list.Add(v);
        }
        return list;
    }

    // ── INotifyPropertyChanged ──────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }

    /// <summary>
    /// Провайдер в топе. <see cref="Percent"/> — доля от лидера (0..1) для полоски.
    /// <see cref="IsFavorite"/> — сохраняется в Config.FavoriteProviders, избранные
    /// сортируются в начало списка независимо от Count.
    /// </summary>
    public class ProviderStat : INotifyPropertyChanged
    {
        public string Name { get; }
        public int Count { get; }
        public double Percent { get; }

        private bool _isFavorite;
        public bool IsFavorite
        {
            get => _isFavorite;
            set { if (_isFavorite != value) { _isFavorite = value; OnPc(); OnPc(nameof(FavoriteIcon)); } }
        }
        public string FavoriteIcon => _isFavorite ? "★" : "☆";

        public ProviderStat(string name, int count, double percent, bool isFavorite = false)
        {
            Name = name; Count = count; Percent = percent; _isFavorite = isFavorite;
        }

        public event PropertyChangedEventHandler? PropertyChanged;
        private void OnPc([CallerMemberName] string? p = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
