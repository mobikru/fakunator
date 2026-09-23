using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using Fakunator.Core;
using Fakunator.Core.Pmta;
using LiveChartsCore;
using LiveChartsCore.SkiaSharpView;
using LiveChartsCore.SkiaSharpView.Painting;
using SkiaSharp;

namespace Fakunator.ViewModels;

public class PmtaViewModel : INotifyPropertyChanged
{
    private readonly object _lock = new();

    public ObservableCollection<PmtaPanelVm> Panels { get; } = new();

    private PmtaPanelVm? _selectedPanel;
    public PmtaPanelVm? SelectedPanel
    {
        get => _selectedPanel;
        set
        {
            if (SetField(ref _selectedPanel, value))
            {
                if (value != null) _isOverview = false;
                OnPropertyChanged(nameof(HasSelection));
                OnPropertyChanged(nameof(NoSelection));
                OnPropertyChanged(nameof(IsOverview));
                OnPropertyChanged(nameof(IsDetail));
            }
        }
    }
    public bool HasSelection => _selectedPanel != null;
    public bool NoSelection => _selectedPanel == null;

    // Режим: обзор (сводка по всем панелям) vs детали выбранной панели.
    private bool _isOverview = true;
    public bool IsOverview
    {
        get => _isOverview;
        set
        {
            if (SetField(ref _isOverview, value))
            {
                if (value) SelectedPanel = null;
                OnPropertyChanged(nameof(IsDetail));
            }
        }
    }
    public bool IsDetail => !_isOverview && _selectedPanel != null;
    public bool NoPanels => Panels.Count == 0;

    // ── Aggregate stats по всем панелям ──────────────────────────────
    private long _totalOutRcpLastMin;
    public long TotalOutRcpLastMin { get => _totalOutRcpLastMin; set => SetField(ref _totalOutRcpLastMin, value); }
    private long _totalInRcpLastMin;
    public long TotalInRcpLastMin { get => _totalInRcpLastMin; set => SetField(ref _totalInRcpLastMin, value); }
    private long _totalQueueRcp;
    public long TotalQueueRcp { get => _totalQueueRcp; set => SetField(ref _totalQueueRcp, value); }
    private int _totalSmtpOutCur;
    public int TotalSmtpOutCur { get => _totalSmtpOutCur; set => SetField(ref _totalSmtpOutCur, value); }
    private int _totalPanels;
    public int TotalPanels { get => _totalPanels; set => SetField(ref _totalPanels, value); }
    private int _onlinePanels;
    public int OnlinePanels { get => _onlinePanels; set => SetField(ref _onlinePanels, value); }

    // Текст подзаголовка обзора — пересчитывается ТОЛЬКО из уже загруженных TotalPanels/OnlinePanels,
    // без похода в сеть. См. также подписку на Loc.Instance.LanguageChanged в конструкторе.
    public string OverviewSubtitleText => string.Format(Loc.T("pmta.overview.subtitleFormat"), OnlinePanels, TotalPanels);

    // ── Общие графики по всем панелям ────────────────────────────────
    public ObservableCollection<double> AggregateOutSeries { get; } = new();
    public ObservableCollection<double> AggregateQueueValues { get; } = new();
    public ISeries[] AggregateSeries { get; }
    public ISeries[] AggregateQueueSeries { get; }
    public Axis[] AggregateXAxes { get; } = { new Axis { IsVisible = false } };
    public Axis[] AggregateYAxes { get; }

    // ── Commands ─────────────────────────────────────────────────────
    public ICommand AddPanelCommand { get; }
    public ICommand EditPanelCommand { get; }
    public ICommand RemovePanelCommand { get; }
    public ICommand RefreshCommand { get; }
    public ICommand SelectPanelCommand { get; }
    public ICommand ShowOverviewCommand { get; }
    public ICommand OpenPanelDetailCommand { get; }
    // Управление PMTA через /command — одна команда открывает диалог с чекбоксами.
    public ICommand OpenCommandsCommand { get; }

    public PmtaViewModel()
    {
        BindingOperations.EnableCollectionSynchronization(Panels, _lock);
        // При любых изменениях коллекции — пересчитываем NoPanels и SelectedPanel по-умолчанию.
        Panels.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(NoPanels));
            if (_selectedPanel == null && Panels.Count > 0) SelectedPanel = Panels[0];
        };

        var accent = SKColor.Parse("5e6ad2");
        var amber = SKColor.Parse("f59e0b");
        AggregateSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = AggregateOutSeries,
                Fill = new LinearGradientPaint(
                    new[] { accent.WithAlpha(80), accent.WithAlpha(0) },
                    new SKPoint(0.5f, 0f), new SKPoint(0.5f, 1f)),
                Stroke = new SolidColorPaint(accent, 2.5f),
                GeometryStroke = null, GeometryFill = null, GeometrySize = 0,
                LineSmoothness = 0.5,
                Name = Loc.T("pmta.chart.aggregateSentName"),
            }
        };
        AggregateQueueSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = AggregateQueueValues,
                Fill = new LinearGradientPaint(
                    new[] { amber.WithAlpha(80), amber.WithAlpha(0) },
                    new SKPoint(0.5f, 0f), new SKPoint(0.5f, 1f)),
                Stroke = new SolidColorPaint(amber, 2.5f),
                GeometryStroke = null, GeometryFill = null, GeometrySize = 0,
                LineSmoothness = 0.5,
                Name = Loc.T("pmta.chart.aggregateQueueName"),
            }
        };
        AggregateYAxes = new[] { new Axis { LabelsPaint = new SolidColorPaint(SKColor.Parse("8b8b95")), TextSize = 10 } };

        AddPanelCommand = new RelayCommand(_ => OpenPanelDialog(null));
        EditPanelCommand = new RelayCommand(o => OpenPanelDialog((o as PmtaPanelVm)?.Runtime.Panel ?? _selectedPanel?.Runtime.Panel));
        RemovePanelCommand = new RelayCommand(o => RemovePanel((o as PmtaPanelVm) ?? _selectedPanel));
        RefreshCommand = new RelayCommand(_ =>
        {
            PmtaMonitorService.Instance.SyncFromConfig();
            ReloadPanels();
            _ = PmtaMonitorService.Instance.PollAllNowAsync();
        });
        SelectPanelCommand = new RelayCommand(o => { if (o is PmtaPanelVm p) SelectedPanel = p; });
        ShowOverviewCommand = new RelayCommand(_ => IsOverview = true);
        OpenPanelDetailCommand = new RelayCommand(o =>
        {
            if (o is PmtaPanelVm p) { SelectedPanel = p; IsOverview = false; }
        });
        OpenCommandsCommand = new RelayCommand(_ => _ = OpenCommandsDialogAsync());

        // Порядок важен! Сначала синхронизируем сервисный реестр из Config,
        // затем ReloadPanels — иначе первый заход в PMTA-вкладку показывает пусто,
        // а панели появляются только после ручного «Обновить».
        PmtaMonitorService.Instance.SyncFromConfig();
        ReloadPanels();
        PmtaMonitorService.Instance.SnapshotUpdated += OnSnapshotUpdated;
        PmtaMonitorService.Instance.BatchCompleted += OnBatchCompleted;
        PmtaMonitorService.Instance.Start();
        // Форсированный первый опрос — чтобы верстка сразу заполнилась данными,
        // а не ждала 7 секунд следующего тика.
        _ = PmtaMonitorService.Instance.PollAllNowAsync();

        // При смене языка НИКАКИХ повторных SSH/HTTP-запросов к PMTA-панелям —
        // только пересчёт текстовых представлений из уже загруженного Runtime-снапшота
        // (см. PmtaPanelVm.Refresh(), которая тоже не трогает сеть).
        Loc.Instance.LanguageChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(OverviewSubtitleText));
            foreach (var p in Panels) p.Refresh();
        };
    }

    private void ReloadPanels()
    {
        lock (_lock)
        {
            var byId = Panels.ToDictionary(p => p.Runtime.Panel.Id);
            var wantedIds = new HashSet<string>();
            foreach (var rt in PmtaMonitorService.Instance.Panels.Values)
            {
                wantedIds.Add(rt.Panel.Id);
                if (!byId.ContainsKey(rt.Panel.Id))
                    Panels.Add(new PmtaPanelVm(rt));
            }
            for (int i = Panels.Count - 1; i >= 0; i--)
                if (!wantedIds.Contains(Panels[i].Runtime.Panel.Id)) Panels.RemoveAt(i);
        }
        // Автовыбор первой панели — только если сейчас НЕ в общем обзоре.
        // Иначе юзер жмёт «Обновить» на overview и его выкидывает в детали первой панели.
        if (SelectedPanel == null && Panels.Count > 0 && !_isOverview) SelectedPanel = Panels[0];
        RecomputeTotals();
    }

    private void OnSnapshotUpdated(object? sender, PmtaPanelRuntime rt)
    {
        // Точечное обновление одной строки/панели. Тайлы и графики — в OnBatchCompleted.
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            var vm = Panels.FirstOrDefault(p => p.Runtime.Panel.Id == rt.Panel.Id);
            vm?.Refresh();
        });
    }

    private void OnBatchCompleted(object? sender, EventArgs e)
    {
        // Ровно один пересчёт тайлов + одна точка на график за tick — после того как
        // все панели свежие. Иначе получались фантомные пики (одна панель новая, другая ещё старая).
        Application.Current?.Dispatcher.BeginInvoke(RecomputeTotals);
    }

    private void RecomputeTotals()
    {
        var all = PmtaMonitorService.Instance.Panels.Values.ToList();
        TotalPanels = all.Count;
        OnlinePanels = all.Count(x => x.Online);
        OnPropertyChanged(nameof(OverviewSubtitleText));
        TotalOutRcpLastMin = (long)Math.Round(all.Sum(x => x.DerivedOutRatePerMin));
        TotalInRcpLastMin = all.Sum(x => x.LastStatus?.Data?.Status?.Traffic?.LastMin?.In?.Rcp ?? 0);
        TotalQueueRcp = all.Sum(x => x.LastStatus?.Data?.Status?.Queue?.Smtp?.Rcp ?? 0);
        TotalSmtpOutCur = all.Sum(x => x.LastStatus?.Data?.Status?.Conn?.SmtpOut?.Cur ?? 0);

        // Одна точка на цикл (событие BatchCompleted вызвано после завершения всех panels-пollов).
        // Ёмкость на ~1 час при опросе раз в 7с (PmtaMonitorService.PollInterval) — PMTA сам
        // историю не хранит (даже его штатный веб-монитор рисует график с нуля от открытия
        // страницы), так что это единственный источник «памяти» графика.
        AggregateOutSeries.Add(TotalOutRcpLastMin);
        while (AggregateOutSeries.Count > PmtaPanelVm.SeriesCapacity) AggregateOutSeries.RemoveAt(0);
        AggregateQueueValues.Add(TotalQueueRcp);
        while (AggregateQueueValues.Count > PmtaPanelVm.SeriesCapacity) AggregateQueueValues.RemoveAt(0);
        OnPropertyChanged(nameof(NoPanels));
    }

    private void OpenPanelDialog(PmtaPanel? existing)
    {
        var dlg = new Views.PmtaPanelEditDialog(existing);
        dlg.Owner = Application.Current?.MainWindow;
        if (dlg.ShowDialog() != true) return;
        var cfg = Config.Current;
        if (existing == null)
        {
            cfg.PmtaPanels.Add(dlg.Result!);
        }
        else
        {
            var idx = cfg.PmtaPanels.FindIndex(p => p.Id == existing.Id);
            if (idx >= 0) cfg.PmtaPanels[idx] = dlg.Result!;
        }
        cfg.Save();
        PmtaMonitorService.Instance.SyncFromConfig();
        ReloadPanels();
    }

    /// <summary>Открывает диалог с чекбоксами команд и выполняет выбранные
    /// последовательно на выбранной панели, показывая объединённый отчёт.</summary>
    private async Task OpenCommandsDialogAsync()
    {
        var panel = _selectedPanel;
        if (panel == null)
        {
            MessageBox.Show(Loc.T("pmta.commandDialog.err.noPanelSelectedBody"), Loc.T("pmta.commandDialog.err.noPanelSelectedTitle"),
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        var dlg = new Views.PmtaCommandDialog(panel.Label + "  ·  " + panel.EndpointText)
        {
            Owner = Application.Current?.MainWindow
        };
        if (dlg.ShowDialog() != true || dlg.Selected.Count == 0) return;

        // Danger confirm — если среди выбранных есть необратимые.
        if (dlg.Selected.Any(x => x.Dangerous))
        {
            var dangerList = string.Join("\n • ", dlg.Selected.Where(x => x.Dangerous).Select(x => x.Label));
            var yes = MessageBox.Show(
                string.Format(Loc.T("pmta.commandDialog.confirmDangerBodyFormat"), dangerList, panel.Label, panel.EndpointText),
                Loc.T("pmta.commandDialog.confirmDangerTitle"),
                MessageBoxButton.YesNo, MessageBoxImage.Warning);
            if (yes != MessageBoxResult.Yes) return;
        }

        // Выполняем последовательно, собираем отчёт.
        var report = new System.Text.StringBuilder();
        try
        {
            using var client = new PmtaClient(panel.Runtime.Panel);
            foreach (var (label, cmd, _) in dlg.Selected)
            {
                report.Append(label).Append("  ·  ").AppendLine(cmd);
                try
                {
                    var resp = await client.SendCommandAsync(cmd);
                    report.Append("  ✓ ").AppendLine(Truncate(resp, 220)).AppendLine();
                }
                catch (Exception ex)
                {
                    report.Append("  ❌ ").AppendLine(ex.Message).AppendLine();
                }
            }
        }
        catch (Exception ex)
        {
            report.AppendLine(string.Format(Loc.T("pmta.commandDialog.genericErrorFormat"), ex.Message));
        }
        MessageBox.Show(report.ToString(), Loc.T("pmta.commandDialog.resultTitle"),
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..max] + "…";

    private void RemovePanel(PmtaPanelVm? vm)
    {
        if (vm == null) return;
        var confirm = MessageBox.Show(
            string.Format(Loc.T("pmta.confirm.removePanelBodyFormat"), vm.Label),
            Loc.T("pmta.confirm.removePanelTitle"), MessageBoxButton.YesNo, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.Yes) return;
        var cfg = Config.Current;
        cfg.PmtaPanels.RemoveAll(p => p.Id == vm.Runtime.Panel.Id);
        cfg.Save();
        PmtaMonitorService.Instance.SyncFromConfig();
        ReloadPanels();
    }

    // ── INotifyPropertyChanged ──────────────────────────────────────
    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? p = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        OnPropertyChanged(name);
        return true;
    }
}

/// <summary>Language-independent категория ошибки PMTA — SSOT для группировки/фильтра.
/// Локализованный текст достаётся отдельно (см. CategoryLabel в PmtaPanelVm) только для
/// отображения, чтобы смена языка не ломала фильтр и не требовала повторной классификации.</summary>
public enum PmtaErrorCategory
{
    Other, RateLimit, MxSkip, RecipientReject, ConnectionIssue,
    Perm5xx, Temp4xx, Blocklist, RelayDenied, DnsError, AuthIssue,
}

/// <summary>Сгруппированная категория ошибок — «сколько раз встретилось + пример».</summary>
public record PmtaErrorGroup(string Category, int Count, string LatestExample, string LatestTime, Geometry Icon, Brush Color);

/// <summary>Строка полной таблицы «Последние ошибки»: конкретное событие + классификация
/// (иконка/цвет/подпись) + сколько всего таких ошибок сейчас на панели (Повторы).
/// CategoryKind — language-independent ключ для фильтрации; Category — уже локализованная
/// подпись для отображения.</summary>
public record PmtaRecentErrorRow(string Time, PmtaErrorCategory CategoryKind, string Category, Geometry Icon, Brush Color, string Text, int RepeatCount);

/// <summary>Обёртка runtime-панели для UI. Держит собственную LiveCharts коллекцию
/// для realtime-графика этой конкретной панели.</summary>
public class PmtaPanelVm : INotifyPropertyChanged
{
    public PmtaPanelRuntime Runtime { get; }
    public string Id => Runtime.Panel.Id;
    public string Label => string.IsNullOrEmpty(Runtime.Panel.Label)
        ? $"{Runtime.Panel.Host}:{Runtime.Panel.Port}" : Runtime.Panel.Label;
    public string EndpointText => $"{(Runtime.Panel.UseHttps ? "https" : "http")}://{Runtime.Panel.Host}:{Runtime.Panel.Port}";

    public bool Online => Runtime.Online;
    // Вычисляется на лету из Runtime.Online/Runtime.LastError (уже загруженные данные) —
    // безопасно пересчитывать на LanguageChanged без похода в сеть/SSH.
    public string StatusText => Runtime.Online ? Loc.T("pmta.status.online") :
        (Runtime.LastError == null ? Loc.T("pmta.status.noData") : string.Format(Loc.T("pmta.status.errorFormat"), ShortenError(Runtime.LastError)));

    /// <summary>Полный, необрезанный текст последней ошибки — для ToolTip у пилюли статуса.
    /// StatusText показывает только короткую сводку (HTTP-код и т.п.), т.к. сырое сообщение
    /// исключения иногда включает целиком HTML-тело ответа сервера (403-страницу и т.п.),
    /// которое не помещается в бейдж фиксированной ширины.</summary>
    public string StatusTextFull => Runtime.Online ? Loc.T("pmta.status.online") :
        (Runtime.LastError == null ? Loc.T("pmta.status.noData") : string.Format(Loc.T("pmta.status.errorFormat"), Runtime.LastError));

    private static string ShortenError(string raw)
    {
        if (raw.Contains("timed out", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("timeout", StringComparison.OrdinalIgnoreCase))
            return Loc.T("pmta.status.errTimeout");
        if (raw.Contains("No connection", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("actively refused", StringComparison.OrdinalIgnoreCase) ||
            raw.Contains("Connection refused", StringComparison.OrdinalIgnoreCase))
            return Loc.T("pmta.status.errConnect");

        // HttpRequestException обычно даёт "...does not indicate success: 403 (...)" — вытаскиваем
        // только код, остальное (включая возможный HTML в теле ответа) отбрасываем.
        var m = Regex.Match(raw, @"\b([1-5]\d{2})\b");
        if (m.Success)
        {
            var code = m.Groups[1].Value;
            var label = code switch
            {
                "401" => Loc.T("pmta.status.err401"),
                "403" => Loc.T("pmta.status.err403"),
                "404" => Loc.T("pmta.status.err404"),
                _ when code[0] is '5' => Loc.T("pmta.status.err5xx"),
                _ => "",
            };
            return string.IsNullOrEmpty(label) ? $"HTTP {code}" : $"HTTP {code} — {label}";
        }

        const int maxLen = 60;
        var oneLine = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        return oneLine.Length > maxLen ? oneLine[..maxLen] + "…" : oneLine;
    }

    /// <summary>Возраст последнего опроса — чтобы визуально подтвердить что данные живые,
    /// а не «залипли» (актуально для «Сводки ошибок», которая иначе выглядит статично,
    /// когда PMTA просто не видел новых попыток по этому домену).</summary>
    public string LastPolledText
    {
        get
        {
            if (Runtime.LastUpdated == default) return Loc.T("pmta.status.noData");
            var age = DateTime.UtcNow - Runtime.LastUpdated;
            if (age < TimeSpan.FromSeconds(1)) return Loc.T("pmta.lastPolled.justNow");
            if (age < TimeSpan.FromMinutes(1)) return string.Format(Loc.T("pmta.lastPolled.secAgoFormat"), (int)age.TotalSeconds);
            return string.Format(Loc.T("pmta.lastPolled.minAgoFormat"), (int)age.TotalMinutes);
        }
    }
    public Brush StatusColor => Runtime.Online
        ? (Brush)new SolidColorBrush(Color.FromRgb(0x22, 0xc5, 0x5e))
        : new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44));

    // ── Подсветка строки таблицы по активности ───────────────────────
    // < 1 мин с последнего движения → зеленоватый фон (панель активна)
    // 1..3 мин → default (transparent) — "покой", но недавно была активна
    // >= 3 мин → красноватый (простой, без отправки)
    // Если ни разу активности не было и панель онлайн — тоже красный.
    public Brush ActivityBackground
    {
        get
        {
            if (Runtime.LastActivityAt == null)
                return Runtime.Online
                    ? new SolidColorBrush(Color.FromArgb(0x18, 0xef, 0x44, 0x44))  // red 9%
                    : Brushes.Transparent;
            var age = DateTime.UtcNow - Runtime.LastActivityAt.Value;
            if (age < TimeSpan.FromMinutes(1))
                return new SolidColorBrush(Color.FromArgb(0x22, 0x22, 0xc5, 0x5e));  // green 13%
            if (age >= TimeSpan.FromMinutes(3))
                return new SolidColorBrush(Color.FromArgb(0x22, 0xef, 0x44, 0x44));  // red 13%
            return Brushes.Transparent;
        }
    }

    // Реальная скорость через delta (не «залипающая» PMTA lastMin.out.rcp — та даёт
    // одинаковое число 60 секунд подряд).
    public long OutRcpLastMin => (long)Math.Round(Runtime.DerivedOutRatePerMin);
    public long OutRcpLastHr => Runtime.LastStatus?.Data?.Status?.Traffic?.LastHr?.Out?.Rcp ?? 0;
    public long OutRcpTotal => Runtime.LastStatus?.Data?.Status?.Traffic?.Total?.Out?.Rcp ?? 0;
    public long InRcpTotal => Runtime.LastStatus?.Data?.Status?.Traffic?.Total?.In?.Rcp ?? 0;
    public long InRcpLastHr => Runtime.LastStatus?.Data?.Status?.Traffic?.LastHr?.In?.Rcp ?? 0;
    // Реальная очередь из status.queue.smtp.rcp (raw уровень демона), а не sum по top-10.
    public long QueueRcp => Runtime.LastStatus?.Data?.Status?.Queue?.Smtp?.Rcp ?? 0;
    public int QueueCount => Runtime.LastQueues?.Data?.Queues?.Count ?? 0;
    public int SmtpOutCur => Runtime.LastStatus?.Data?.Status?.Conn?.SmtpOut?.Cur ?? 0;
    public int SmtpOutMax => Runtime.LastStatus?.Data?.Status?.Conn?.SmtpOut?.Max ?? 0;
    public string Version => Runtime.LastStatus?.Data?.Mta?.Product?.Version ?? "—";
    public string HostName => Runtime.LastStatus?.Data?.Mta?.FullHostName ?? "";

    public IEnumerable<PmtaQueue> Queues => Runtime.LastQueues?.Data?.Queues ?? Enumerable.Empty<PmtaQueue>();
    public IEnumerable<PmtaDomain> Domains => Runtime.LastDomains?.Data?.Domains ?? Enumerable.Empty<PmtaDomain>();
    public IEnumerable<PmtaVmta> Vmtas => Runtime.LastVmtas?.Data?.Vmtas ?? Enumerable.Empty<PmtaVmta>();

    // ── Поиск по таблицам «Топ очередей» / «Virtual MTA» ─────────────
    private string _queueSearch = "";
    public string QueueSearch
    {
        get => _queueSearch;
        set { _queueSearch = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(QueueSearch))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredQueues))); }
    }
    public IEnumerable<PmtaQueue> FilteredQueues => string.IsNullOrWhiteSpace(QueueSearch)
        ? Queues : Queues.Where(q => q.Name.Contains(QueueSearch, StringComparison.OrdinalIgnoreCase));

    private string _vmtaSearch = "";
    public string VmtaSearch
    {
        get => _vmtaSearch;
        set { _vmtaSearch = value; PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(VmtaSearch))); PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredVmtas))); }
    }
    public IEnumerable<PmtaVmta> FilteredVmtas => string.IsNullOrWhiteSpace(VmtaSearch)
        ? Vmtas : Vmtas.Where(v => v.Name.Contains(VmtaSearch, StringComparison.OrdinalIgnoreCase));

    /// <summary>Ошибки из /queues и /domains — не поток «живых» событий, а последняя записанная
    /// ошибка на каждую очередь/домен: она остаётся в ответе API, пока в очереди есть
    /// недоставленные письма, даже если новых попыток доставки не было уже дни (застрявший
    /// домен просто ждёт следующего ретрая по backoff-расписанию PMTA). Поэтому фильтруем
    /// по времени — иначе «Сводка»/«Последние ошибки» показывают вообще всё, что PMTA когда-либо
    /// туда записал, а не то, что происходит прямо сейчас.</summary>
    private static readonly TimeSpan RecentWindow = TimeSpan.FromHours(3);

    private List<PmtaError> RecentErrorsRaw()
    {
        var full = (Runtime.LastQueues?.Data?.Queues?.SelectMany(q => q.Errors) ?? Enumerable.Empty<PmtaError>())
            .Concat(Runtime.LastDomains?.Data?.Domains?.SelectMany(d => d.Errors) ?? Enumerable.Empty<PmtaError>());
        var nowUtc = DateTime.UtcNow;
        return full.Where(e =>
            !Runtime.ErrorFirstSeenUtc.TryGetValue(e.Text, out var firstSeen) || (nowUtc - firstSeen) <= RecentWindow
        ).ToList();
    }

    /// <summary>Группировка наблюдаемых ошибок по категории — сразу видно
    /// почему стоит рассылка. Rate-limit / MX rejection / auth / dns / etc.</summary>
    public IEnumerable<PmtaErrorGroup> ErrorSummary
    {
        get
        {
            var all = RecentErrorsRaw();
            return all.GroupBy(e => Classify(e.Text).Category)
                .Select(g =>
                {
                    var latest = g.OrderByDescending(e => e.Time).First();
                    var (category, icon, color) = Classify(latest.Text);
                    return new PmtaErrorGroup(CategoryLabel(category), g.Count(), latest.Text, latest.Time, icon, color);
                })
                .OrderByDescending(g => g.Count).Take(4);
        }
    }

    // ── Полная таблица «Последние ошибки»: тип с иконкой/цветом + счётчик повторов
    // (сколько всего таких ошибок этой категории сейчас наблюдается на панели). ──
    private static readonly Brush RedBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xef, 0x44, 0x44)));
    private static readonly Brush AmberBrush = Freeze(new SolidColorBrush(Color.FromRgb(0xf5, 0x9e, 0x0b)));
    private static readonly Brush BlueBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x3b, 0x82, 0xf6)));
    private static readonly Brush GrayBrush = Freeze(new SolidColorBrush(Color.FromRgb(0x94, 0xa3, 0xb8)));

    private static Brush Freeze(SolidColorBrush b) { b.Freeze(); return b; }

    // Классификация ошибок по RAW-тексту из PMTA (английские подстроки протокола/сервера —
    // не зависят от языка UI, поэтому сравнение строк тут не антипаттерн). Категория —
    // language-independent enum; локализованная подпись достаётся через CategoryLabel()
    // ТОЛЬКО в момент отображения, так что смена языка не требует новой классификации.
    private static (PmtaErrorCategory Category, Geometry Icon, Brush Color) Classify(string text)
    {
        if (string.IsNullOrEmpty(text)) return (PmtaErrorCategory.Other, PhosphorIcons.FileText, GrayBrush);
        var s = text.ToLowerInvariant();
        if (s.Contains("rate limit")) return (PmtaErrorCategory.RateLimit, PhosphorIcons.ArrowClockwise, AmberBrush);
        if (s.Contains("skip of mx")) return (PmtaErrorCategory.MxSkip, PhosphorIcons.ArrowClockwise, AmberBrush);
        if (s.Contains("recipient errors detected")) return (PmtaErrorCategory.RecipientReject, PhosphorIcons.Warning, AmberBrush);
        if (s.Contains("connection refused") || s.Contains("timed out") || s.Contains("timeout")) return (PmtaErrorCategory.ConnectionIssue, PhosphorIcons.Warning, AmberBrush);
        if (s.Contains("550") || s.Contains("554") || s.Contains("permanent")) return (PmtaErrorCategory.Perm5xx, PhosphorIcons.XCircle, RedBrush);
        if (s.Contains("421") || s.Contains("451") || s.Contains("temporary")) return (PmtaErrorCategory.Temp4xx, PhosphorIcons.Warning, AmberBrush);
        if (s.Contains("blocked") || s.Contains("blacklist") || s.Contains("blocklist") || s.Contains("spamhaus")) return (PmtaErrorCategory.Blocklist, PhosphorIcons.XCircle, RedBrush);
        if (s.Contains("relay") || s.Contains("access denied")) return (PmtaErrorCategory.RelayDenied, PhosphorIcons.XCircle, RedBrush);
        if (s.Contains("dns") || s.Contains("resolve") || s.Contains("nxdomain")) return (PmtaErrorCategory.DnsError, PhosphorIcons.Globe, BlueBrush);
        if (s.Contains("auth")) return (PmtaErrorCategory.AuthIssue, PhosphorIcons.Warning, AmberBrush);
        return (PmtaErrorCategory.Other, PhosphorIcons.FileText, GrayBrush);
    }

    private static string CategoryLabel(PmtaErrorCategory c) => Loc.T(c switch
    {
        PmtaErrorCategory.RateLimit => "pmta.errCat.rateLimit",
        PmtaErrorCategory.MxSkip => "pmta.errCat.mxSkip",
        PmtaErrorCategory.RecipientReject => "pmta.errCat.recipientReject",
        PmtaErrorCategory.ConnectionIssue => "pmta.errCat.connectionIssue",
        PmtaErrorCategory.Perm5xx => "pmta.errCat.perm5xx",
        PmtaErrorCategory.Temp4xx => "pmta.errCat.temp4xx",
        PmtaErrorCategory.Blocklist => "pmta.errCat.blocklist",
        PmtaErrorCategory.RelayDenied => "pmta.errCat.relayDenied",
        PmtaErrorCategory.DnsError => "pmta.errCat.dnsError",
        PmtaErrorCategory.AuthIssue => "pmta.errCat.authIssue",
        _ => "pmta.errCat.other",
    });

    public IEnumerable<PmtaRecentErrorRow> RecentErrorRows
    {
        get
        {
            var full = RecentErrorsRaw();
            var counts = full.GroupBy(e => Classify(e.Text).Category).ToDictionary(g => g.Key, g => g.Count());
            return full.OrderByDescending(e => e.Time).Take(20)
                .Select(e =>
                {
                    var (category, icon, color) = Classify(e.Text);
                    return new PmtaRecentErrorRow(e.Time, category, CategoryLabel(category), icon, color, e.Text,
                        counts.TryGetValue(category, out var c) ? c : 1);
                });
        }
    }

    // SSOT фильтра — language-independent (null = «все типы»), а не текст текущего языка.
    // Раньше сравнение шло по локализованной строке "Все типы"/категории — это тот самый
    // антипаттерн (см. DomainScannerViewModel/DomainsManagerViewModel), из-за которого смена
    // языка ломала выбранный фильтр. ErrorTypeFilter — обёртка для UI-биндинга ComboBox.
    private PmtaErrorCategory? _errorCategoryFilter;
    public string ErrorTypeFilter
    {
        get => _errorCategoryFilter == null ? Loc.T("pmta.errFilter.allTypes") : CategoryLabel(_errorCategoryFilter.Value);
        set
        {
            var allLabel = Loc.T("pmta.errFilter.allTypes");
            if (string.IsNullOrEmpty(value) || value == allLabel)
            {
                _errorCategoryFilter = null;
            }
            else
            {
                var match = Enum.GetValues<PmtaErrorCategory>()
                    .Where(c => CategoryLabel(c) == value)
                    .Select(c => (PmtaErrorCategory?)c)
                    .FirstOrDefault();
                _errorCategoryFilter = match;
            }
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(ErrorTypeFilter)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(FilteredRecentErrorRows)));
        }
    }
    public IEnumerable<string> ErrorTypeFilterOptions =>
        new[] { Loc.T("pmta.errFilter.allTypes") }.Concat(
            RecentErrorRows.Select(r => r.CategoryKind).Distinct().Select(CategoryLabel).OrderBy(x => x));
    public IEnumerable<PmtaRecentErrorRow> FilteredRecentErrorRows =>
        _errorCategoryFilter == null ? RecentErrorRows : RecentErrorRows.Where(r => r.CategoryKind == _errorCategoryFilter.Value);
    public bool HasRecentErrors => FilteredRecentErrorRows.Any();
    public bool NoRecentErrors => !HasRecentErrors;

    // Графики этой панели
    public ObservableCollection<double> OutSeries { get; } = new();
    public ObservableCollection<double> QueueSeries { get; } = new();
    public ISeries[] SendSeries { get; }
    public ISeries[] QueueSeriesChart { get; }
    public Axis[] EmptyXAxes { get; } = { new Axis { IsVisible = false } };
    public Axis[] YAxes { get; }

    public PmtaPanelVm(PmtaPanelRuntime rt)
    {
        Runtime = rt;
        var green = SKColor.Parse("22c55e");
        var amber = SKColor.Parse("f59e0b");
        SendSeries = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = OutSeries,
                Fill = new LinearGradientPaint(new[] { green.WithAlpha(80), green.WithAlpha(0) },
                    new SKPoint(0.5f, 0f), new SKPoint(0.5f, 1f)),
                Stroke = new SolidColorPaint(green, 2.5f),
                GeometryStroke = null, GeometryFill = null, GeometrySize = 0,
                LineSmoothness = 0.5,
                Name = Loc.T("pmta.chart.panelSentName"),
            }
        };
        QueueSeriesChart = new ISeries[]
        {
            new LineSeries<double>
            {
                Values = QueueSeries,
                Fill = new LinearGradientPaint(new[] { amber.WithAlpha(80), amber.WithAlpha(0) },
                    new SKPoint(0.5f, 0f), new SKPoint(0.5f, 1f)),
                Stroke = new SolidColorPaint(amber, 2.5f),
                GeometryStroke = null, GeometryFill = null, GeometrySize = 0,
                LineSmoothness = 0.5,
                Name = Loc.T("pmta.chart.panelQueueName"),
            }
        };
        YAxes = new[] { new Axis { LabelsPaint = new SolidColorPaint(SKColor.Parse("8b8b95")), TextSize = 10 } };

        // Смена языка НЕ дёргает сеть/SSH — только пересчитывает текстовые представления
        // (StatusText/LastPolledText/ErrorSummary/RecentErrorRows/...) из уже закешированного
        // Runtime-снапшота этой панели. Пустая строка в PropertyChanged = "обнови все биндинги".
        Loc.Instance.LanguageChanged += (_, _) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }

    /// <summary>Ёмкость графиков — ~1 час при опросе раз в 7с (PmtaMonitorService.PollInterval).
    /// Живёт только пока запущена программа: PMTA историю не отдаёт (см. Refresh).</summary>
    public const int SeriesCapacity = 520;

    /// <summary>Вызывается сервисом при новом snapshot — тянет из Runtime новые значения.</summary>
    public void Refresh()
    {
        OutSeries.Add(OutRcpLastMin);
        while (OutSeries.Count > SeriesCapacity) OutSeries.RemoveAt(0);
        QueueSeries.Add(QueueRcp);
        while (QueueSeries.Count > SeriesCapacity) QueueSeries.RemoveAt(0);
        // Пустая строка = все свойства обновятся у всех биндингов.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
