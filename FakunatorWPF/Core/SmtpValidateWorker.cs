using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core;

public class SmtpValidateWorker
{
    private readonly List<string> _emails;
    private readonly List<ProxySpec> _proxies;
    private readonly SmtpSettings _settings;
    private readonly string? _outputDir;
    private readonly SmtpProber _prober;

    // ── Shared mutable state (all guarded by _lock) ──────────────────
    private readonly object _lock = new();
    private int _processed;
    private readonly Dictionary<string, int> _counts = new();
    private readonly List<SmtpVerdict> _freshFeed = new();
    private readonly List<SmtpVerdict> _recentErrors = new();
    private int _activeSessions;

    // Output file writers (guarded by _lock)
    private readonly Dictionary<string, StreamWriter> _verdictWriters = new();
    private StreamWriter? _logWriter;

    // Per-proxy tracking
    private readonly ConcurrentDictionary<string, int> _proxyConsecErrors = new();
    private readonly ConcurrentDictionary<string, DateTime> _proxyDisabledUntil = new();
    private readonly ConcurrentDictionary<string, int> _proxySuccesses = new();
    private readonly ConcurrentDictionary<string, int> _proxyTotalErrors = new();

    public SmtpValidateWorker(List<string> emails, List<ProxySpec> proxies, SmtpSettings settings,
        string? outputDir = null)
    {
        _emails = emails;
        _proxies = proxies;
        _settings = settings;
        _outputDir = outputDir;
        _prober = new SmtpProber(settings.HeloDomain, settings.MailFromAddress);

        foreach (var v in Verdicts.Order)
            _counts[v] = 0;
    }

    private void OpenOutputFiles()
    {
        if (string.IsNullOrEmpty(_outputDir)) return;
        Directory.CreateDirectory(_outputDir);

        foreach (var v in Verdicts.Order)
        {
            var path = Path.Combine(_outputDir, $"{v}.txt");
            _verdictWriters[v] = new StreamWriter(path, false, Encoding.UTF8) { NewLine = "\n", AutoFlush = true };
        }

        var logPath = Path.Combine(_outputDir, "validation_log.tsv");
        _logWriter = new StreamWriter(logPath, false, Encoding.UTF8) { NewLine = "\n", AutoFlush = true };
        _logWriter.WriteLine("email\tverdict\tmx_host\treply_code\treply_text\tvia_proxy\terror\telapsed_ms");
    }

    private void CloseOutputFiles()
    {
        foreach (var w in _verdictWriters.Values)
        {
            try { w.Flush(); w.Dispose(); } catch { }
        }
        _verdictWriters.Clear();

        if (_logWriter != null)
        {
            try { _logWriter.Flush(); _logWriter.Dispose(); } catch { }
            _logWriter = null;
        }

        // Write errors.log with last 50 errors
        if (!string.IsNullOrEmpty(_outputDir))
        {
            try
            {
                var errorsPath = Path.Combine(_outputDir, "errors.log");
                using var w = new StreamWriter(errorsPath, false, Encoding.UTF8) { NewLine = "\n" };
                w.WriteLine($"# SMTP Validation Error Log — {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
                w.WriteLine($"# Last {_recentErrors.Count} errors");
                w.WriteLine();
                foreach (var err in _recentErrors)
                    w.WriteLine($"{err.Email}\t{err.MxHost}\t{err.ViaProxy}\t{err.Error}\t{err.ElapsedMs:F0}ms");
            }
            catch { }
        }
    }

    private void WriteVerdict(SmtpVerdict v)
    {
        // Must be called inside _lock
        if (_verdictWriters.TryGetValue(v.Verdict, out var w))
            w.WriteLine(v.Email);

        _logWriter?.WriteLine($"{v.Email}\t{v.Verdict}\t{v.MxHost}\t{v.ReplyCode}\t{v.ReplyText}\t{v.ViaProxy}\t{v.Error}\t{v.ElapsedMs:F0}");
    }

    /// <summary>
    /// Run the validation pipeline. Reports progress ~10 Hz.
    /// </summary>
    public async Task RunAsync(IProgress<SmtpSnapshotBatch> progress, CancellationToken ct)
    {
        if (_emails.Count == 0 || _proxies.Count == 0)
            return;

        OpenOutputFiles();

        var sw = Stopwatch.StartNew();
        var total = _emails.Count;

        // Group emails by domain
        var byDomain = _emails
            .GroupBy(e => e.Contains('@') ? e[(e.IndexOf('@') + 1)..].ToLowerInvariant() : "")
            .Where(g => !string.IsNullOrEmpty(g.Key))
            .ToDictionary(g => g.Key, g => g.ToList());

        // Resolve all MX records in PARALLEL (was sequential — 5-20s тишины при 100 доменах).
        var mxTasks = byDomain.Keys
            .Select(async d => (Domain: d, Mx: await MxResolver.ResolveAsync(d, ct)))
            .ToArray();
        var mxResults = await Task.WhenAll(mxTasks);
        var mxMap = mxResults.ToDictionary(r => r.Domain, r => r.Mx);

        // Build work items: (domain, emails_chunk, mxHosts[]) — передаём ВЕСЬ список MX
        // чтобы Prober мог фоллбэчить на alt1/alt2 при connection-level отказе.
        var workItems = new ConcurrentQueue<(string Domain, List<string> Emails, string[] MxHosts)>();

        foreach (var (domain, emails) in byDomain)
        {
            var mxHosts = mxMap[domain];
            if (mxHosts.Length == 0)
            {
                // No MX — mark all as error
                lock (_lock)
                {
                    foreach (var email in emails)
                    {
                        _processed++;
                        _counts[Verdicts.Error]++;
                        var v = new SmtpVerdict(email, Verdicts.Error, Error: "No MX record");
                        _freshFeed.Add(v);
                        AddError(v);
                        WriteVerdict(v);
                    }
                }
                continue;
            }

            var batchSize = _settings.BatchSize;

            for (int i = 0; i < emails.Count; i += batchSize)
            {
                var chunk = emails.GetRange(i, Math.Min(batchSize, emails.Count - i));
                workItems.Enqueue((domain, chunk, mxHosts));
            }
        }

        // Catch-all detection — ленивая, per-domain Task. Запускается параллельно с
        // основной работой при первом запросе. Все ProcessChunk для одного домена
        // ожидают один и тот же Task. Раньше была sequential pre-pass на ~5 минут.
        var catchallTasks = new ConcurrentDictionary<string, Task<bool>>();
        // Используется в ProcessChunk: catchallTasks.GetOrAdd(domain, d => DetectCatchall(d, mxMap[d]))

        // Use semaphore to limit concurrent sessions
        int maxConcurrency = _proxies.Count * _settings.WorkersPerProxy;
        using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);

        // Progress reporting timer
        var progressTimer = new System.Timers.Timer(100); // 10 Hz
        progressTimer.Elapsed += (_, _) =>
        {
            lock (_lock)
            {
                var proxyStats = _proxies.Select(p =>
                {
                    var key = $"{p.Host}:{p.Port}";
                    _proxySuccesses.TryGetValue(key, out var succ);
                    _proxyTotalErrors.TryGetValue(key, out var errs);
                    _proxyConsecErrors.TryGetValue(key, out var consec);
                    var disabled = _proxyDisabledUntil.TryGetValue(key, out var until) && DateTime.UtcNow < until;
                    return new ProxyStatsEntry
                    {
                        Address = key,
                        Successes = succ,
                        Errors = errs,
                        ConsecutiveErrors = consec,
                        Disabled = disabled,
                    };
                }).ToList();

                progress.Report(new SmtpSnapshotBatch
                {
                    Processed = _processed,
                    Total = total,
                    Counts = new Dictionary<string, int>(_counts),
                    Elapsed = sw.Elapsed.TotalSeconds,
                    Speed = sw.Elapsed.TotalSeconds > 0 ? _processed / sw.Elapsed.TotalSeconds : 0,
                    ActiveSessions = _activeSessions,
                    FreshFeed = new List<SmtpVerdict>(_freshFeed),
                    RecentErrors = new List<SmtpVerdict>(_recentErrors),
                    ProxyStats = proxyStats,
                });
                _freshFeed.Clear();
            }
        };
        progressTimer.Start();

        try
        {
            // Сразу начинаем основной цикл — без sequential catch-all pre-pass.
            // Catch-all детекция теперь lazy: ProcessChunk сам её триггерит при первом
            // обращении к домену (см. catchallTasks).
            var tasks = new List<Task>();

            while (workItems.TryDequeue(out var item))
            {
                ct.ThrowIfCancellationRequested();
                await semaphore.WaitAsync(ct);

                var captured = item;
                tasks.Add(Task.Run(async () =>
                {
                    try
                    {
                        await ProcessChunk(captured.Domain, captured.Emails, captured.MxHosts,
                            catchallTasks, sw, ct);
                    }
                    finally
                    {
                        semaphore.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(tasks);
        }
        finally
        {
            progressTimer.Stop();
            progressTimer.Dispose();
            CloseOutputFiles();
        }

        // Final progress report
        lock (_lock)
        {
            progress.Report(new SmtpSnapshotBatch
            {
                Processed = _processed,
                Total = total,
                Counts = new Dictionary<string, int>(_counts),
                Elapsed = sw.Elapsed.TotalSeconds,
                Speed = sw.Elapsed.TotalSeconds > 0 ? _processed / sw.Elapsed.TotalSeconds : 0,
                ActiveSessions = 0,
                FreshFeed = new List<SmtpVerdict>(_freshFeed),
                RecentErrors = new List<SmtpVerdict>(_recentErrors),
            });
        }
    }

    private async Task ProcessChunk(
        string domain, List<string> emails, string[] mxHosts,
        ConcurrentDictionary<string, Task<bool>> catchallTasks,
        Stopwatch sw, CancellationToken ct)
    {
        var mxHost = mxHosts[0]; // первый используется в логах/верктах

        // Lazy catch-all detection: первый ProcessChunk для домена запускает probe,
        // остальные ждут тот же Task. Не блокирует первый batch — детект параллельно.
        var catchallTask = catchallTasks.GetOrAdd(domain,
            d => DetectCatchallAsync(d, mxHosts, ct));

        var proxy = PickProxy();
        if (proxy == null)
        {
            // All proxies disabled — error
            lock (_lock)
            {
                foreach (var email in emails)
                {
                    _processed++;
                    _counts[Verdicts.Error]++;
                    var v = new SmtpVerdict(email, Verdicts.Error, mxHost, Error: "All proxies disabled");
                    _freshFeed.Add(v);
                    AddError(v);
                    WriteVerdict(v);
                }
            }
            return;
        }

        Interlocked.Increment(ref _activeSessions);
        try
        {
            int retries = 0;
            List<SmtpVerdict>? results = null;

            // Streaming callback: каждый RCPT-ответ сразу обновляет UI/счётчики,
            // не дожидаясь конца batch'а — даёт ощущение "живых" результатов.
            // Catch-all применяется лениво: если домен оказался catchall (детектится
            // параллельно), valid-ответ переклассифицируется как Catchall.
            var streamedEmails = new HashSet<string>(StringComparer.Ordinal);
            bool catchallKnown = false;
            bool isCatchall = false;

            void OnStreamVerdict(SmtpVerdict v)
            {
                // Применяем catchall (если уже задетектили) — valid превращается в catchall
                if (catchallKnown && isCatchall && v.Verdict == Verdicts.Valid)
                    v = v with { Verdict = Verdicts.Catchall };

                lock (_lock)
                {
                    streamedEmails.Add(v.Email);
                    _processed++;
                    _counts[v.Verdict]++;
                    _freshFeed.Add(v);
                    WriteVerdict(v);

                    var proxyOk = $"{proxy.Host}:{proxy.Port}";
                    if (v.Verdict == Verdicts.Error)
                    {
                        AddError(v);
                        _proxyConsecErrors.AddOrUpdate(proxyOk, 1, (_, c) => c + 1);
                        _proxyTotalErrors.AddOrUpdate(proxyOk, 1, (_, c) => c + 1);
                    }
                    else
                    {
                        _proxyConsecErrors[proxyOk] = 0;
                        _proxySuccesses.AddOrUpdate(proxyOk, 1, (_, c) => c + 1);
                    }
                }
            }

            // Запускаем catch-all детекцию параллельно с probing — её результат подхватим
            // когда дойдёт (в OnStreamVerdict через captured isCatchall).
            _ = catchallTask.ContinueWith(t =>
            {
                if (t.IsCompletedSuccessfully) { isCatchall = t.Result; catchallKnown = true; }
                else { catchallKnown = true; /* isCatchall=false по умолчанию */ }
            }, TaskScheduler.Default);

            while (retries <= _settings.Retries)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    results = await _prober.ProbeBatchWithFailoverAsync(
                        emails, mxHosts, proxy, _settings.TimeoutMs, ct, OnStreamVerdict);
                    break;
                }
                catch (Exception) when (retries < _settings.Retries)
                {
                    retries++;
                    var proxyKey = $"{proxy.Host}:{proxy.Port}";
                    var errors = _proxyConsecErrors.AddOrUpdate(proxyKey, 1, (_, v) => v + 1);
                    if (errors >= _settings.MaxConsecErrors)
                    {
                        _proxyDisabledUntil[proxyKey] = DateTime.UtcNow.AddSeconds(_settings.RecoverySeconds);
                        proxy = PickProxy() ?? proxy;
                    }
                    await Task.Delay(Math.Min(1000 * retries, 5000), ct);
                }
            }

            if (results == null)
            {
                // All retries failed
                lock (_lock)
                {
                    foreach (var email in emails)
                    {
                        _processed++;
                        _counts[Verdicts.Error]++;
                        var v = new SmtpVerdict(email, Verdicts.Error, mxHost, ViaProxy: $"{proxy.Host}:{proxy.Port}",
                            Error: "Max retries exceeded");
                        _freshFeed.Add(v);
                        AddError(v);
                        WriteVerdict(v);
                    }
                }
                return;
            }

            // Bulk-ошибки (SOCKS5/banner/EHLO/MAIL FROM fail) НЕ проходят через onVerdict —
            // их Prober заполняет скопом в results. Обрабатываем ТОЛЬКО не-стримленные.
            var proxyOk = $"{proxy.Host}:{proxy.Port}";
            lock (_lock)
            {
                foreach (var v in results)
                {
                    if (streamedEmails.Contains(v.Email)) continue;  // уже застримлено

                    _processed++;
                    _counts[v.Verdict]++;
                    _freshFeed.Add(v);
                    WriteVerdict(v);

                    if (v.Verdict == Verdicts.Error)
                    {
                        AddError(v);
                        _proxyConsecErrors.AddOrUpdate(proxyOk, 1, (_, c) => c + 1);
                        _proxyTotalErrors.AddOrUpdate(proxyOk, 1, (_, c) => c + 1);
                    }
                }
            }

            // Session delay
            if (_settings.SessionDelayMs > 0)
                await Task.Delay(_settings.SessionDelayMs, ct);
        }
        finally
        {
            Interlocked.Decrement(ref _activeSessions);
        }
    }

    /// <summary>
    /// Детектит catch-all домен: отправляет RCPT TO на заведомо несуществующий адрес.
    /// Если сервер возвращает 250 — значит принимает всё → catch-all.
    /// Запускается лениво ОДИН РАЗ на домен (через GetOrAdd на ConcurrentDictionary&lt;Task&gt;).
    /// </summary>
    private async Task<bool> DetectCatchallAsync(string domain, string[] mxHosts, CancellationToken ct)
    {
        try
        {
            var proxy = PickProxy();
            if (proxy == null) return false;
            var testEmail = $"fakutest_{Guid.NewGuid():N}@{domain}";
            var result = await _prober.ProbeAsync(testEmail, mxHosts[0], proxy, _settings.TimeoutMs, ct);
            return result.Verdict == Verdicts.Valid;
        }
        catch { return false; }
    }

    private ProxySpec? PickProxy()
    {
        var now = DateTime.UtcNow;
        var available = _proxies.Where(p =>
        {
            var key = $"{p.Host}:{p.Port}";
            if (_proxyDisabledUntil.TryGetValue(key, out var until) && now < until)
                return false;
            return true;
        }).ToList();

        if (available.Count == 0) return null;

        // Simple round-robin via random pick
        return available[Random.Shared.Next(available.Count)];
    }

    private void AddError(SmtpVerdict v)
    {
        // Must be called inside lock
        _recentErrors.Add(v);
        while (_recentErrors.Count > 50)
            _recentErrors.RemoveAt(0);
    }
}
