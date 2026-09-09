using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using System.Windows.Threading;
using Fakunator.Core.Server;

namespace Fakunator.ViewModels;

public enum ServerFilter { All, Online, Problems, Offline }

/// <summary>
/// Держит список настроенных серверов + фоновый monitor-поллинг.
/// UI биндится сюда напрямую. Плюс поиск/фильтры/фавориты для нового дизайна панели.
/// </summary>
public sealed class ServerListViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<ServerRowVm> Rows { get; } = new();

    /// <summary>Отфильтрованный + отсортированный вид (для ItemsControl в панели).</summary>
    public ICollectionView RowsView { get; }

    public Visibility EmptyVisibility => Rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;

    // Legacy — оставлено для правого блока (заголовок в info-panel и т.п.).
    public string Summary => Rows.Count == 0
        ? "нет серверов"
        : $"{Rows.Count} · {Rows.Count(r => r.IsOnline)} online";

    // ─── Счётчики для фильтр-пилюль ──────────────────────────
    public int CountAll      => Rows.Count;
    public int CountOnline   => Rows.Count(r => r.HealthState == HealthState.Online);
    public int CountProblems => Rows.Count(r => r.HealthState == HealthState.Degraded);
    public int CountOffline  => Rows.Count(r => r.HealthState == HealthState.Offline);

    public string TotalText => $"{CountAll} всего";
    public string ShownText
    {
        get
        {
            int shown = 0;
            foreach (var _ in RowsView) shown++;
            return $"Показано {shown} из {CountAll}";
        }
    }

    // ─── Поиск ───────────────────────────────────────────────
    private string _searchQuery = "";
    public string SearchQuery
    {
        get => _searchQuery;
        set
        {
            _searchQuery = value ?? "";
            RowsView.Refresh();
            OnChanged();
            OnChanged(nameof(HasSearchQuery));
            OnChanged(nameof(ShownText));
        }
    }
    public bool HasSearchQuery => !string.IsNullOrEmpty(_searchQuery);

    // ─── Фильтр по статусу ───────────────────────────────────
    private ServerFilter _filter = ServerFilter.All;
    public ServerFilter Filter
    {
        get => _filter;
        set
        {
            _filter = value;
            RowsView.Refresh();
            OnChanged();
            OnChanged(nameof(IsFilterAll));
            OnChanged(nameof(IsFilterOnline));
            OnChanged(nameof(IsFilterProblems));
            OnChanged(nameof(IsFilterOffline));
            OnChanged(nameof(ShownText));
        }
    }
    public bool IsFilterAll      => _filter == ServerFilter.All;
    public bool IsFilterOnline   => _filter == ServerFilter.Online;
    public bool IsFilterProblems => _filter == ServerFilter.Problems;
    public bool IsFilterOffline  => _filter == ServerFilter.Offline;

    private readonly ServerRegistry _registry;
    private readonly ServerMonitor _monitor = new();
    private readonly DispatcherTimer _pollTimer;

    private ServerRowVm? _selected;
    public ServerRowVm? Selected
    {
        get => _selected;
        set
        {
            if (_selected == value) return;
            var prev = _selected;
            _selected = value;
            if (prev != null) prev.RaiseSelectedChanged();
            if (_selected != null) _selected.RaiseSelectedChanged();
            OnChanged();
        }
    }

    public ServerListViewModel(ServerRegistry registry)
    {
        _registry = registry;
        _registry.Changed += ReloadFromRegistry;
        ReloadFromRegistry();

        RowsView = CollectionViewSource.GetDefaultView(Rows);
        RowsView.Filter = RowFilter;
        RowsView.SortDescriptions.Add(new SortDescription(nameof(ServerRowVm.IsFavorite), ListSortDirection.Descending));
        RowsView.SortDescriptions.Add(new SortDescription(nameof(ServerRowVm.Name), ListSortDirection.Ascending));

        Rows.CollectionChanged += (_, _) => RaiseCountersAndShown();

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(45) };
        _pollTimer.Tick += (_, _) => _ = PollAllAsync();
        _pollTimer.Start();

        var initial = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        initial.Tick += (_, _) => { initial.Stop(); _ = PollAllAsync(); };
        initial.Start();
    }

    private bool RowFilter(object obj)
    {
        if (obj is not ServerRowVm r) return false;

        if (_filter == ServerFilter.Online   && r.HealthState != HealthState.Online) return false;
        if (_filter == ServerFilter.Problems && r.HealthState != HealthState.Degraded) return false;
        if (_filter == ServerFilter.Offline  && r.HealthState != HealthState.Offline) return false;

        if (!string.IsNullOrEmpty(_searchQuery))
        {
            var q = _searchQuery.Trim();
            if (q.Length == 0) return true;
            if ((r.Name  ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
             && (r.Ip    ?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0
             && (r.Domain?? "").IndexOf(q, StringComparison.OrdinalIgnoreCase) < 0)
                return false;
        }
        return true;
    }

    private void ReloadFromRegistry()
    {
        // Rows пересоздаются целиком (новые ServerRowVm-обёртки) — если просто оставить
        // Selected указывать на старый объект, он "осиротеет": UI, забинженный на Selected
        // (например реквизиты доступа на Обзоре), продолжит показывать снапшот на момент
        // последнего PropertyChanged и не увидит свежие данные (Credentials и т.п.) без
        // повторного клика по строке. Поэтому запоминаем Id и переустанавливаем Selected
        // на новый экземпляр той же записи — это переиспускает уведомление и обновляет UI.
        var selectedId = _selected?.Config.Id;

        Rows.Clear();
        foreach (var srv in _registry.All)
        {
            var vm = new ServerRowVm(srv, this);
            Rows.Add(vm);
        }

        if (selectedId != null)
            Selected = Rows.FirstOrDefault(r => r.Config.Id == selectedId);

        OnChanged(nameof(EmptyVisibility));
        RaiseCountersAndShown();
    }

    public async Task PollAllAsync()
    {
        foreach (var row in Rows.ToList())
        {
            try
            {
                var health = await _monitor.CheckAsync(row.Config);
                row.ApplyHealth(health);
            }
            catch
            {
                row.ApplyHealth(new ServerHealth { CheckedAt = DateTime.UtcNow, IsReachable = false });
            }
        }
        RaiseCountersAndShown();
        try { RowsView?.Refresh(); } catch { }
    }

    public void RegisterInstalledServer(ServerConfig srv) => _registry.Add(srv);

    public void PersistFavorite(ServerRowVm row)
    {
        _registry.Update(row.Config);
        RaiseCountersAndShown();
        try { RowsView?.Refresh(); } catch { }
    }

    public void ClearSearch() => SearchQuery = "";

    internal void RaiseCountersAndShown()
    {
        OnChanged(nameof(CountAll));
        OnChanged(nameof(CountOnline));
        OnChanged(nameof(CountProblems));
        OnChanged(nameof(CountOffline));
        OnChanged(nameof(TotalText));
        OnChanged(nameof(ShownText));
        OnChanged(nameof(Summary));
        OnChanged(nameof(EmptyVisibility));
    }

    private void OnChanged([CallerMemberName] string? p = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public enum HealthState { Unknown, Online, Degraded, Offline }

/// <summary>Строка таблицы сервера — обёртка вокруг ServerConfig с notify.</summary>
public sealed class ServerRowVm : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ServerConfig Config { get; }
    private readonly ServerListViewModel? _owner;

    public string Name => Config.Name;
    public string Ip => Config.Ip;
    public string Domain => Config.Domain;

    public bool IsFavorite
    {
        get => Config.IsFavorite;
        set
        {
            if (Config.IsFavorite == value) return;
            Config.IsFavorite = value;
            NotifyOne(nameof(IsFavorite));
            _owner?.PersistFavorite(this);
        }
    }

    public bool IsSelected => _owner?.Selected == this;
    internal void RaiseSelectedChanged() => NotifyOne(nameof(IsSelected));

    public bool HasCredentials => Config.Credentials != null
        && (!string.IsNullOrEmpty(Config.Credentials.WebmailUrl)
            || !string.IsNullOrEmpty(Config.Credentials.MysqlRootPass)
            || !string.IsNullOrEmpty(Config.Credentials.PmtaMonitorUrl)
            || !string.IsNullOrEmpty(Config.Credentials.LandingUrl));
    public Visibility CredentialsVisibility => HasCredentials ? Visibility.Visible : Visibility.Collapsed;
    public Visibility NoCredentialsVisibility => HasCredentials ? Visibility.Collapsed : Visibility.Visible;

    private ServerHealth? _health;
    public ServerHealth? Health { get => _health; private set { _health = value; NotifyAll(); } }

    public bool IsOnline => HealthState == HealthState.Online;

    public HealthState HealthState
    {
        get
        {
            if (Health == null) return HealthState.Unknown;
            if (!Health.IsReachable) return HealthState.Offline;
            var failed = Health.Services.Count(kv => kv.Value != "active");
            return failed == 0 ? HealthState.Online : HealthState.Degraded;
        }
    }

    /// <summary>Короткий человечный статус — "Онлайн" / "Проблемы" / "Офлайн" / "проверяется…"</summary>
    public string StatusShort => HealthState switch
    {
        HealthState.Online   => "Онлайн",
        HealthState.Degraded => "Проблемы",
        HealthState.Offline  => "Офлайн",
        _                    => "проверяется…"
    };

    public string StatusText
    {
        get
        {
            if (Health == null) return "проверяется…";
            if (!Health.IsReachable) return "offline: " + (string.IsNullOrEmpty(Health.ErrorMessage) ? "SSH недоступен" : Health.ErrorMessage);
            var failed = Health.Services.Count(kv => kv.Value != "active");
            return failed == 0 ? "все сервисы работают" : $"{failed} сервисов не активны";
        }
    }

    public string ErrorText => Health?.ErrorMessage ?? "";
    public Visibility ErrorVisibility => (!string.IsNullOrEmpty(Health?.ErrorMessage))
        ? Visibility.Visible : Visibility.Collapsed;

    private static readonly System.Collections.Generic.Dictionary<string, Brush> BrushCache = new();
    private static Brush MakeBrush(string hex)
    {
        if (BrushCache.TryGetValue(hex, out var b)) return b;
        var brush = (Brush)(new BrushConverter().ConvertFromString(hex)!);
        if (brush.CanFreeze) brush.Freeze();
        BrushCache[hex] = brush;
        return brush;
    }

    public Brush StatusBrush => MakeBrush(HealthState switch
    {
        HealthState.Online   => "#22c55e",
        HealthState.Degraded => "#f59e0b",
        HealthState.Offline  => "#ef4444",
        _                    => "#94a3b8"
    });

    public string LoadText => Health?.LoadAvg ?? "—";
    public string PmtaText => Health?.Services.TryGetValue("pmta", out var v) == true && v == "active" ? "active" : "—";
    public string UptimeText => Health?.Uptime ?? "";

    // ── Detail-panel bindings ──
    public string DetailHeader
    {
        get
        {
            var parts = new System.Collections.Generic.List<string> { Ip };
            if (!string.IsNullOrEmpty(Health?.OsRelease)) parts.Add(Health.OsRelease);
            if (!string.IsNullOrEmpty(Health?.Uptime))    parts.Add(Health.Uptime);
            return string.Join("  ·  ", parts);
        }
    }

    public string CpuText => Health?.LoadAvg == null ? "—" : $"load {Health.LoadAvg}";
    public int CpuPercent => 0;
    public string RamText => Health?.RamTotalMb > 0 ? $"{Health.RamUsedMb / 1024.0:F1} / {Health.RamTotalMb / 1024.0:F1} GB" : "—";
    public int RamPercent => Health?.RamPercent ?? 0;
    public string DiskText => Health?.DiskTotalMb > 0 ? $"{Health.DiskUsedMb / 1024.0:F1} / {Health.DiskTotalMb / 1024.0:F1} GB" : "—";
    public int DiskPercent => Health?.DiskPercent ?? 0;
    public string DiskPercentText => Health?.DiskTotalMb > 0 ? $"{Health.DiskPercent}% занято" : "—";
    public string RamPercentText => Health?.RamTotalMb > 0 ? $"{Health.RamPercent}% занято" : "—";
    public string LoadAvgText => string.IsNullOrEmpty(Health?.LoadAvg) ? "—" : $"load avg {Health.LoadAvg}";

    public Brush PostfixBrush   => SvcBrush("postfix");
    public Brush DovecotBrush   => SvcBrush("dovecot");
    public Brush MariaDbBrush   => SvcBrush("mariadb");
    public Brush Bind9Brush     => SvcBrush("bind9");
    public Brush OpenDkimBrush  => SvcBrush("opendkim");
    public Brush Apache2Brush   => SvcBrush("apache2");
    public Brush PmtaBrush      => SvcBrush("pmta");
    public Brush Proxy3Brush    => SvcBrush("3proxy");
    public string PostfixText  => SvcText("postfix");
    public string DovecotText  => SvcText("dovecot");
    public string MariaDbText  => SvcText("mariadb");
    public string Bind9Text    => SvcText("bind9");
    public string OpenDkimText => SvcText("opendkim");
    public string Apache2Text  => SvcText("apache2");
    public string PmtaStatusText => SvcText("pmta");
    public string Proxy3Text   => SvcText("3proxy");

    private Brush SvcBrush(string name)
    {
        var color = Health == null ? "#94a3b8"
                  : Health.Services.TryGetValue(name, out var v) && v == "active" ? "#22c55e"
                  : "#ef4444";
        return MakeBrush(color);
    }
    private string SvcText(string name)
        => Health == null ? "проверяется"
         : Health.Services.TryGetValue(name, out var v) ? v
         : "не установлен";

    public ServerRowVm(ServerConfig config, ServerListViewModel? owner = null)
    {
        Config = config;
        _owner = owner;
    }

    public void ApplyHealth(ServerHealth? h)
    {
        Health = h;
        _owner?.RaiseCountersAndShown();
    }

    private void NotifyOne(string p)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));

    private void NotifyAll()
    {
        foreach (var p in new[] {
            nameof(Health), nameof(IsOnline), nameof(HealthState),
            nameof(StatusText), nameof(StatusShort), nameof(StatusBrush),
            nameof(LoadText), nameof(PmtaText), nameof(UptimeText),
            nameof(DetailHeader), nameof(CpuText), nameof(CpuPercent),
            nameof(RamText), nameof(RamPercent), nameof(RamPercentText),
            nameof(DiskText), nameof(DiskPercent), nameof(DiskPercentText),
            nameof(LoadAvgText),
            nameof(PostfixBrush), nameof(DovecotBrush), nameof(MariaDbBrush),
            nameof(Bind9Brush), nameof(OpenDkimBrush), nameof(Apache2Brush),
            nameof(PmtaBrush), nameof(Proxy3Brush),
            nameof(PostfixText), nameof(DovecotText), nameof(MariaDbText),
            nameof(Bind9Text), nameof(OpenDkimText), nameof(Apache2Text),
            nameof(PmtaStatusText), nameof(Proxy3Text),
            nameof(ErrorText), nameof(ErrorVisibility),
        })
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    }
}
