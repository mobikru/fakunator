using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core.DomainScanner;

/// <summary>
/// Оркестрация: стрим из DM API → N параллельных worker'ов резолвят NS+A →
/// определение NS-провайдера → запись в БД. Прогресс в UI через IProgress.
///
/// Отличия от Python-версии (scanner.py):
/// - Храним ВСЕ проверенные домены (не только lame_delegation), потому что цель —
///   статистика по провайдерам, а не поиск брошенных.
/// - Если задан target_ns или target_provider — сохраняем только матчинг +
///   всё что уже помечено lame_delegation (это тоже интересно как индикатор).
/// - Producer/consumer через BlockingCollection (аналог asyncio.Queue).
/// </summary>
public class DomainScanWorker
{
    private readonly string _apiKey;
    private readonly List<string> _zones;
    private readonly int _concurrency;
    private readonly string _targetNs;
    private readonly string _targetProvider;
    private readonly double _dnsTimeoutSec;
    private readonly DomainDb _db;

    private long _totalFetched;
    private long _totalProcessed;
    private long _totalSaved;
    private long _errors;
    private readonly ConcurrentDictionary<string, int> _byStatus = new();
    private readonly ConcurrentQueue<DomainRecord> _freshFeed = new();
    private volatile string _currentZone = "";
    private volatile bool _stopRequested;

    public DomainScanWorker(
        string apiKey, List<string> zones, int concurrency,
        string targetNs, string targetProvider, double dnsTimeoutSec,
        DomainDb db)
    {
        _apiKey = apiKey;
        _zones = zones;
        _concurrency = Math.Max(1, concurrency);
        _targetNs = (targetNs ?? "").Trim().ToLowerInvariant();
        _targetProvider = (targetProvider ?? "").Trim();
        _dnsTimeoutSec = dnsTimeoutSec;
        _db = db;
    }

    public void Stop() => _stopRequested = true;

    private bool NsMatchesTarget(List<string> nsHosts)
    {
        if (string.IsNullOrEmpty(_targetNs)) return true;
        if (_targetNs.EndsWith("*"))
        {
            var prefix = _targetNs[..^1];
            return nsHosts.Any(h => h.StartsWith(prefix));
        }
        return nsHosts.Contains(_targetNs);
    }

    private bool MatchesTarget(List<string> nsHosts, string provider)
    {
        if (!string.IsNullOrEmpty(_targetProvider))
            return provider == _targetProvider;
        return NsMatchesTarget(nsHosts);
    }

    public async Task RunAsync(IProgress<DomainScanSnapshot> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var resolver = new NsResolver(_dnsTimeoutSec);
        using var api = new DmApiClient(_apiKey);

        // Fast: KPI + live-feed каждые 200мс (без обращения к БД).
        // Slow: агрегации из БД (топ провайдеров, total_in_db) раз в 3 сек.
        var lastSlowMs = 0L;
        var progressTimer = new System.Timers.Timer(200);
        progressTimer.Elapsed += (_, _) =>
        {
            bool doSlow = sw.ElapsedMilliseconds - lastSlowMs >= 3000;
            if (doSlow) lastSlowMs = sw.ElapsedMilliseconds;
            ReportProgress(progress, sw, running: true, includeSlow: doSlow);
        };
        progressTimer.Start();

        try
        {
            foreach (var zone in _zones)
            {
                if (_stopRequested || ct.IsCancellationRequested) break;
                _currentZone = zone;
                var scanId = _db.BeginScan(zone);
                long zoneSavedBefore = _totalSaved;
                long zoneFetchedBefore = _totalFetched;

                var queue = new BlockingCollection<string>(_concurrency * 4);

                var producer = Task.Run(async () =>
                {
                    try
                    {
                        await foreach (var domain in api.StreamDomainsAsync(zone, "text", ct))
                        {
                            if (_stopRequested || ct.IsCancellationRequested) break;
                            Interlocked.Increment(ref _totalFetched);
                            queue.Add(domain, ct);
                        }
                    }
                    catch (Exception ex) when (ex is not OperationCanceledException)
                    {
                        _lastError = $"Ошибка получения списка ({zone}): {ex.Message}";
                    }
                    finally
                    {
                        queue.CompleteAdding();
                    }
                }, ct);

                using var sem = new SemaphoreSlim(_concurrency);
                var workers = new List<Task>();
                for (int i = 0; i < _concurrency; i++)
                {
                    workers.Add(Task.Run(async () =>
                    {
                        foreach (var domain in queue.GetConsumingEnumerable(ct))
                        {
                            if (_stopRequested || ct.IsCancellationRequested) break;
                            try
                            {
                                var nsTask = resolver.ResolveNsAsync(domain, ct);
                                var aTask = resolver.ResolveAAsync(domain, ct);
                                await Task.WhenAll(nsTask, aTask).ConfigureAwait(false);
                                var (nsHosts, nsStatus) = nsTask.Result;
                                var (_, aStatus) = aTask.Result;
                                bool hasA = aStatus == "ok";

                                _byStatus.AddOrUpdate(nsStatus, 1, (_, c) => c + 1);
                                if (nsStatus != "ok" && nsStatus != "lame_delegation")
                                    Interlocked.Increment(ref _errors);

                                // has_a=true → домен реально резолвится в IP → живой.
                                // Страховка от единичных fluke DnsClient/резолверов.
                                bool isAbandoned = nsStatus == "lame_delegation" && !hasA;

                                if (isAbandoned && nsHosts.Count == 0)
                                    nsHosts = await resolver.ResolveNsAtRegistryAsync(domain).ConfigureAwait(false);

                                var provider = NsProviderDetector.Detect(nsHosts);

                                // В БД сохраняем ТОЛЬКО брошенные (lame_delegation).
                                // Раньше я сохранял всё подряд — юзер видел 93% живых сайтов
                                // в браузере и думал что они брошенные. Порт с Python:
                                //   should_save = is_abandoned and _matches_target(...)
                                bool shouldSave = isAbandoned &&
                                    (string.IsNullOrEmpty(_targetNs) && string.IsNullOrEmpty(_targetProvider)
                                        ? true
                                        : MatchesTarget(nsHosts, provider));

                                var rec = new DomainRecord(domain, zone, provider, nsHosts, nsStatus, hasA, aStatus);

                                // В live-feed показываем ВСЕ обработанные (для наблюдения за прогоном),
                                // чтобы юзер видел активные/ошибки/брошенные сразу все статусы.
                                _freshFeed.Enqueue(rec);
                                while (_freshFeed.Count > 40) _freshFeed.TryDequeue(out _);

                                // В БД сохраняем только реально брошенные (см. shouldSave выше).
                                if (shouldSave)
                                {
                                    _db.Enqueue(rec);
                                    Interlocked.Increment(ref _totalSaved);
                                }
                                Interlocked.Increment(ref _totalProcessed);
                            }
                            catch (OperationCanceledException) { throw; }
                            catch { Interlocked.Increment(ref _errors); }
                        }
                    }, ct));
                }

                await Task.WhenAll(new[] { producer }.Concat(workers)).ConfigureAwait(false);

                _db.FinishScan(scanId,
                    (int)(_totalFetched - zoneFetchedBefore),
                    (int)(_totalSaved - zoneSavedBefore));
            }
        }
        finally
        {
            progressTimer.Stop();
            progressTimer.Dispose();
            ReportProgress(progress, sw, running: false, includeSlow: true);
        }
    }

    private volatile string _lastError = "";
    private List<(string Provider, int Count)> _cachedTop = new();
    private int _cachedTotalInDb;

    private void ReportProgress(IProgress<DomainScanSnapshot> progress, Stopwatch sw, bool running, bool includeSlow)
    {
        var elapsed = sw.Elapsed.TotalSeconds;
        var fresh = new List<DomainRecord>();
        while (_freshFeed.TryDequeue(out var r)) fresh.Add(r);

        // Тяжёлые агрегации (SQLite GROUP BY / COUNT) обновляем реже — раз в 3с.
        // Иначе на 300 воркеров SQLite-лок съедает fps ui.
        if (includeSlow)
        {
            try
            {
                _cachedTop = _db.TopProviders(200);
                _cachedTotalInDb = _db.TotalCount();
            }
            catch { }
        }

        progress.Report(new DomainScanSnapshot
        {
            Running = running,
            CurrentZone = _currentZone,
            Zones = _zones,
            TotalFetched = (int)_totalFetched,
            TotalProcessed = (int)_totalProcessed,
            TotalSaved = (int)_totalSaved,
            Errors = (int)_errors,
            ByStatus = _byStatus.ToDictionary(k => k.Key, v => v.Value),
            Elapsed = elapsed,
            Speed = elapsed > 0 ? _totalProcessed / elapsed : 0,
            FreshFeed = fresh,
            TotalInDb = _cachedTotalInDb,
            TopProviders = _cachedTop,
        });
    }
}
