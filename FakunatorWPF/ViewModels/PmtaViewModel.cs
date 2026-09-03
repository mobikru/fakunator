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
                Name = "Отправлено (rcp/мин)",
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
                Name = "Очередь (rcp)",
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
        TotalOutRcpLastMin = (long)Math.Round(all.Sum(x => x.DerivedOutRatePerMin));
        TotalInRcpLastMin = all.Sum(x => x.LastStatus?.Data?.Status?.Traffic?.LastMin?.In?.Rcp ?? 0);
        TotalQueueRcp = all.Sum(x => x.LastStatus?.Data?.Status?.Queue?.Smtp?.Rcp ?? 0);
        TotalSmtpOutCur = all.Sum(x => x.LastStatus?.Data?.Status?.Conn?.SmtpOut?.Cur ?? 0);

        // Одна точка на цикл (событие BatchCompleted вызвано после завершения всех panels-пollов).
        AggregateOutSeries.Add(TotalOutRcpLastMin);
        while (AggregateOutSeries.Count > 200) AggregateOutSeries.RemoveAt(0);
        AggregateQueueValues.Add(TotalQueueRcp);
        while (AggregateQueueValues.Count > 200) AggregateQueueValues.RemoveAt(0);
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
            MessageBox.Show("Сначала выбери панель.", "PMTA",
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
                $"⚠ Среди выбранных есть необратимые команды:\n\n • {dangerList}\n\n" +
                $"Панель: {panel.Label} ({panel.EndpointText})\nПродолжить?",
                "PMTA · подтверждение",
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
            report.AppendLine("Общая ошибка: " + ex.Message);
        }
        MessageBox.Show(report.ToString(), "PMTA · результат",
            MessageBoxButton.OK, MessageBoxImage.Information);
    }

    private static string Truncate(string s, int max) =>
        string.IsNullOrEmpty(s) || s.Length <= max ? s ?? "" : s[..max] + "…";

    private void RemovePanel(PmtaPanelVm? vm)
    {
        if (vm == null) return;
        var confirm = MessageBox.Show(
            $"Удалить панель «{vm.Label}»?",
            "PMTA", MessageBoxButton.YesNo, MessageBoxImage.Warning);
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

/// <summary>Сгруппированная категория ошибок — «сколько раз встретилось + пример».</summary>
public record PmtaErrorGroup(string Category, int Count, string LatestExample);

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
    public string StatusText => Runtime.Online ? "онлайн" :
        (Runtime.LastError == null ? "нет данных" : $"⚠ {Runtime.LastError}");
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

    /// <summary>Список последних ошибок из всех очередей (плоский, только уникальные тексты).</summary>
    public IEnumerable<PmtaError> RecentErrors =>
        (Runtime.LastQueues?.Data?.Queues?.SelectMany(q => q.Errors) ?? Enumerable.Empty<PmtaError>())
        .Concat(Runtime.LastDomains?.Data?.Domains?.SelectMany(d => d.Errors) ?? Enumerable.Empty<PmtaError>())
        .OrderByDescending(e => e.Time).Take(15);

    /// <summary>Группировка всех наблюдаемых ошибок по категории — сразу видно
    /// почему стоит рассылка. Rate-limit / MX rejection / auth / dns / etc.</summary>
    public IEnumerable<PmtaErrorGroup> ErrorSummary
    {
        get
        {
            var all = (Runtime.LastQueues?.Data?.Queues?.SelectMany(q => q.Errors) ?? Enumerable.Empty<PmtaError>())
                .Concat(Runtime.LastDomains?.Data?.Domains?.SelectMany(d => d.Errors) ?? Enumerable.Empty<PmtaError>())
                .ToList();
            return all.GroupBy(e => Classify(e.Text))
                .Select(g => new PmtaErrorGroup(g.Key, g.Count(),
                    g.OrderByDescending(e => e.Time).First().Text))
                .OrderByDescending(g => g.Count).Take(6);
        }
    }

    private static string Classify(string text)
    {
        if (string.IsNullOrEmpty(text)) return "прочее";
        var s = text.ToLowerInvariant();
        if (s.Contains("rate limit")) return "🚦 Rate-limit (лимит скорости)";
        if (s.Contains("skip of mx")) return "↩ MX skip (250 OK, но сброс)";
        if (s.Contains("recipient errors detected")) return "✋ Много reject-получателей";
        if (s.Contains("connection refused") || s.Contains("timed out") || s.Contains("timeout")) return "⏱ Проблемы соединения";
        if (s.Contains("550") || s.Contains("554") || s.Contains("permanent")) return "❌ 5xx (постоянный отказ)";
        if (s.Contains("421") || s.Contains("451") || s.Contains("temporary")) return "⚠ 4xx (временный отказ)";
        if (s.Contains("blocked") || s.Contains("blacklist") || s.Contains("blocklist") || s.Contains("spamhaus")) return "🚫 Блок-лист";
        if (s.Contains("relay") || s.Contains("access denied")) return "🔒 Relay/access denied";
        if (s.Contains("dns") || s.Contains("resolve") || s.Contains("nxdomain")) return "🌐 DNS-ошибки";
        if (s.Contains("auth")) return "🔑 Auth-проблемы";
        return "📄 Прочее";
    }

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
                Name = "rcp/мин",
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
                Name = "очередь",
            }
        };
        YAxes = new[] { new Axis { LabelsPaint = new SolidColorPaint(SKColor.Parse("8b8b95")), TextSize = 10 } };
    }

    /// <summary>Вызывается сервисом при новом snapshot — тянет из Runtime новые значения.</summary>
    public void Refresh()
    {
        OutSeries.Add(OutRcpLastMin);
        while (OutSeries.Count > 200) OutSeries.RemoveAt(0);
        QueueSeries.Add(QueueRcp);
        while (QueueSeries.Count > 200) QueueSeries.RemoveAt(0);
        // Пустая строка = все свойства обновятся у всех биндингов.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(""));
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
