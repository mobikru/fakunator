using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core.Pmta;

/// <summary>
/// Фоновый поллер PMTA-панелей. Каждые ~7 секунд делает snapshot по
/// enabled-панелям (status/queues/domains/vmtas/jobs), пишет в rolling
/// history и уведомляет подписчиков (VM) через <see cref="SnapshotUpdated"/>.
/// </summary>
public class PmtaMonitorService
{
    private static PmtaMonitorService? _instance;
    public static PmtaMonitorService Instance => _instance ??= new PmtaMonitorService();

    private readonly ConcurrentDictionary<string, PmtaPanelRuntime> _panels = new();
    private CancellationTokenSource? _cts;
    private Task? _loop;
    public TimeSpan PollInterval { get; set; } = TimeSpan.FromSeconds(7);

    /// <summary>Максимум точек истории (~7s × 300 = ~35 минут).</summary>
    public int HistoryCapacity { get; set; } = 300;

    public event EventHandler<PmtaPanelRuntime>? SnapshotUpdated;
    /// <summary>Срабатывает один раз после завершения tick'а — когда все панели опрошены.
    /// Используем чтобы обновлять агрегатный график ровно 1 раз на цикл, а не N раз.</summary>
    public event EventHandler? BatchCompleted;

    public IReadOnlyDictionary<string, PmtaPanelRuntime> Panels => _panels;

    /// <summary>Синхронизирует список отслеживаемых панелей с Config.</summary>
    public void SyncFromConfig()
    {
        var cfg = Config.Current;
        var configIds = new HashSet<string>(cfg.PmtaPanels.Select(p => p.Id));

        // Удаляем runtime-панели которых нет в Config
        foreach (var oldId in _panels.Keys.Where(k => !configIds.Contains(k)).ToList())
            _panels.TryRemove(oldId, out _);

        // Добавляем/обновляем
        foreach (var panel in cfg.PmtaPanels)
        {
            if (_panels.TryGetValue(panel.Id, out var existing))
                existing.Panel = panel;
            else
                _panels[panel.Id] = new PmtaPanelRuntime { Panel = panel };
        }
    }

    public void Start()
    {
        if (_loop != null && !_loop.IsCompleted) return;
        SyncFromConfig();
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => LoopAsync(_cts.Token));
    }

    /// <summary>Немедленный опрос всех включённых панелей. Используется при
    /// открытии вкладки и при клике «Обновить» — чтобы не ждать очередной тик.</summary>
    public async Task PollAllNowAsync()
    {
        var enabled = _panels.Values.Where(rt => rt.Panel.Enabled).ToList();
        if (enabled.Count == 0) return;
        var ct = _cts?.Token ?? CancellationToken.None;
        var tasks = enabled.Select(rt => PollOneAsync(rt, ct)).ToList();
        try { await Task.WhenAll(tasks); } catch { }
        BatchCompleted?.Invoke(this, EventArgs.Empty);
    }

    public async Task StopAsync()
    {
        try { _cts?.Cancel(); } catch { }
        if (_loop != null) { try { await _loop; } catch { } }
        _cts?.Dispose(); _cts = null; _loop = null;
    }

    private async Task LoopAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                var tasks = _panels.Values
                    .Where(rt => rt.Panel.Enabled)
                    .Select(rt => PollOneAsync(rt, ct))
                    .ToList();
                if (tasks.Count > 0) await Task.WhenAll(tasks);
                BatchCompleted?.Invoke(this, EventArgs.Empty);
            }
            catch { }
            try { await Task.Delay(PollInterval, ct); } catch { break; }
        }
    }

    private async Task PollOneAsync(PmtaPanelRuntime rt, CancellationToken ct)
    {
        try
        {
            using var client = new PmtaClient(rt.Panel);
            var statusTask = client.GetStatusAsync(ct);
            var queuesTask = client.GetQueuesAsync(ct);
            var domainsTask = client.GetDomainsAsync(ct);
            var vmtasTask = client.GetVmtasAsync(ct);
            var jobsTask = client.GetJobsAsync(ct);
            await Task.WhenAll(statusTask, queuesTask, domainsTask, vmtasTask, jobsTask);

            // Считаем реальную скорость до перезаписи LastStatus.
            var newStatus = statusTask.Result;
            var newTotalOut = newStatus?.Data?.Status?.Traffic?.Total?.Out?.Rcp ?? 0;
            var now = DateTime.UtcNow;
            if (rt.PrevPollTime != default && rt.PrevTotalOutRcp > 0 && newTotalOut >= rt.PrevTotalOutRcp)
            {
                var dtSec = (now - rt.PrevPollTime).TotalSeconds;
                if (dtSec > 0.5)
                {
                    var delta = newTotalOut - rt.PrevTotalOutRcp;
                    rt.DerivedOutRatePerMin = delta / dtSec * 60.0;
                }
            }
            rt.PrevTotalOutRcp = newTotalOut;
            rt.PrevPollTime = now;

            rt.LastStatus = newStatus;
            rt.LastQueues = queuesTask.Result;
            rt.LastDomains = domainsTask.Result;
            rt.LastVmtas = vmtasTask.Result;
            rt.LastJobs = jobsTask.Result;
            rt.Online = true;
            rt.LastError = null;
            rt.LastUpdated = now;

            // Активность = наблюдаем реальную дельту > 0. Раньше использовали lastMin,
            // но она "залипает" 60 секунд.
            if (rt.DerivedOutRatePerMin > 0) rt.LastActivityAt = now;

            // История для графика
            var stat = rt.LastStatus?.Data?.Status?.Traffic;
            var conns = rt.LastStatus?.Data?.Status?.Conn;
            var point = new PmtaTimePoint
            {
                Ts = DateTime.Now,
                OutRcpLastMin = stat?.LastMin?.Out?.Rcp ?? 0,
                InRcpLastMin = stat?.LastMin?.In?.Rcp ?? 0,
                OutRcpLastHr = stat?.LastHr?.Out?.Rcp ?? 0,
                SmtpOutCur = conns?.SmtpOut?.Cur ?? 0,
                SmtpInCur = conns?.SmtpIn?.Cur ?? 0,
                TotalQueueRcp = rt.LastQueues?.Data?.Queues?.Sum(q => q.Rcp) ?? 0,
                KbLastMin = stat?.LastMin?.Out?.Kb ?? 0,
            };
            rt.History.Add(point);
            while (rt.History.Count > HistoryCapacity) rt.History.RemoveAt(0);
        }
        catch (Exception ex)
        {
            rt.Online = false;
            rt.LastError = ex.Message;
            rt.LastUpdated = DateTime.UtcNow;
        }
        SnapshotUpdated?.Invoke(this, rt);
    }
}

/// <summary>Runtime-состояние одной панели: последние снапшоты + история для графиков.</summary>
public class PmtaPanelRuntime
{
    public PmtaPanel Panel { get; set; } = new();
    public PmtaStatusResponse? LastStatus { get; set; }
    public PmtaQueuesResponse? LastQueues { get; set; }
    public PmtaDomainsResponse? LastDomains { get; set; }
    public PmtaVmtasResponse? LastVmtas { get; set; }
    public PmtaJobsResponse? LastJobs { get; set; }
    public bool Online { get; set; }
    public string? LastError { get; set; }
    public DateTime LastUpdated { get; set; }
    /// <summary>Момент когда был замечен ненулевой out-трафик (rcp/мин &gt; 0). Nullable, если ни разу.</summary>
    public DateTime? LastActivityAt { get; set; }
    /// <summary>Rolling история для timeseries-графика (append-only, старое чистится).</summary>
    public List<PmtaTimePoint> History { get; } = new();

    // ── Для вычисления РЕАЛЬНОЙ скорости через delta ──
    // PMTA lastMin.out.rcp = скользящая сумма за 60с и почти не меняется.
    // Считаем delta(total.out.rcp) / delta(t) * 60 = живой rcp/мин.
    public long PrevTotalOutRcp { get; set; }
    public DateTime PrevPollTime { get; set; }
    /// <summary>Вычисленная реальная скорость rcp/мин на основе дельты total.out.rcp
    /// между текущим и прошлым снапшотом. Обновляется в PollOneAsync.</summary>
    public double DerivedOutRatePerMin { get; set; }
}

/// <summary>Одна временная точка для графиков.</summary>
public class PmtaTimePoint
{
    public DateTime Ts { get; set; }
    public long OutRcpLastMin { get; set; }
    public long InRcpLastMin { get; set; }
    public long OutRcpLastHr { get; set; }
    public int SmtpOutCur { get; set; }
    public int SmtpInCur { get; set; }
    public long TotalQueueRcp { get; set; }
    public double KbLastMin { get; set; }
}
