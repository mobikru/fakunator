using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core;

/// <summary>
/// Background pipeline for email analysis: DB pass → AI pass 1 → (optional) AI pass 2.
/// Reports progress via IProgress&lt;AnalyzeSnapshot&gt; at ~5 Hz.
/// </summary>
public class AnalyzeWorker
{
    private readonly List<string> _emails;
    private readonly string _provider;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly double _spendCapUsd;
    private readonly int _batchSize;
    private readonly int _maxConcurrent;
    private readonly bool _skipJunk;
    private readonly bool _twoPass;
    private readonly int _batchDelayMs;
    private readonly string? _dbPath;

    // Accumulated results
    private readonly Dictionary<string, AnalysisResult> _results = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _resultsLock = new();
    private int _processed;
    private int _inputTokens;
    private int _outputTokens;
    private double _costUsd;
    private int _aiErrors;
    private string _lastError = "";
    private int _pass2Total;
    private int _pass2Processed;
    private readonly Dictionary<string, int> _counts = new();
    private readonly Dictionary<string, int> _countryStats = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _countsLock = new();

    // Regex to extract name tokens from email local part
    private static readonly Regex ReName = new(@"[a-zA-ZÀ-ɏ]+", RegexOptions.Compiled);

    public AnalyzeWorker(
        List<string> emails,
        string provider,
        string apiKey,
        string model,
        double spendCapUsd,
        int batchSize,
        int maxConcurrent,
        bool skipJunk,
        bool twoPass,
        string? dbPath,
        double batchDelaySec = 0)
    {
        _emails = emails;
        _provider = provider;
        _apiKey = apiKey;
        _model = model;
        _spendCapUsd = spendCapUsd;
        _batchSize = Math.Max(1, batchSize);
        _maxConcurrent = Math.Max(1, maxConcurrent);
        _skipJunk = skipJunk;
        _twoPass = twoPass;
        _dbPath = dbPath;
        _batchDelayMs = (int)Math.Max(0, batchDelaySec * 1000);
    }

    public async Task RunAsync(IProgress<AnalyzeSnapshot> progress, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var freshBuf = new ConcurrentBag<AnalysisResult>();

        // Throttle ReportProgress to ~5 Hz to avoid flooding the UI dispatcher
        // when many AI batches finish near-simultaneously (was freezing the window).
        long lastReportMs = -1000;
        var reportLock = new object();

        // Reset state
        _processed = 0;
        _inputTokens = 0; _outputTokens = 0;
        _costUsd = 0; _aiErrors = 0;
        _lastError = "";
        _pass2Total = 0; _pass2Processed = 0;
        lock (_countsLock) { _counts.Clear(); _countryStats.Clear(); }
        lock (_resultsLock) _results.Clear();

        // Send initial 0% so UI resets immediately
        progress.Report(new AnalyzeSnapshot
        {
            Processed = 0, Total = 0,
            Counts = new Dictionary<string, int>(),
            Phase = "init", FreshFeed = new List<AnalysisResult>(),
        });

        // Deduplicate emails
        var unique = _emails
            .Select(e => e.Trim().ToLowerInvariant())
            .Where(e => e.Contains('@'))
            .Distinct()
            .ToList();

        int total = unique.Count;

        void ReportProgress(string phase, bool force = false)
        {
            // Throttle: skip if last report < 200ms ago (unless forced — phase transitions / final).
            lock (reportLock)
            {
                var nowMs = sw.ElapsedMilliseconds;
                if (!force && nowMs - lastReportMs < 200) return;
                lastReportMs = nowMs;
            }

            var fresh = new List<AnalysisResult>();
            while (freshBuf.TryTake(out var item))
                fresh.Add(item);

            Dictionary<string, int> countsCopy;
            Dictionary<string, int> countryCopy;
            lock (_countsLock)
            {
                countsCopy = new Dictionary<string, int>(_counts);
                countryCopy = new Dictionary<string, int>(_countryStats);
            }

            progress.Report(new AnalyzeSnapshot
            {
                Processed = _processed,
                Total = total,
                Counts = countsCopy,
                CountryStats = countryCopy,
                InputTokens = _inputTokens,
                OutputTokens = _outputTokens,
                CostUsd = _costUsd,
                Elapsed = sw.Elapsed.TotalSeconds,
                Speed = sw.Elapsed.TotalSeconds > 0 ? _processed / sw.Elapsed.TotalSeconds : 0,
                Phase = phase,
                FreshFeed = fresh,
                AiErrors = _aiErrors,
                LastError = _lastError,
                SpendCapHit = _costUsd >= _spendCapUsd,
                Pass2Total = _pass2Total,
                Pass2Processed = _pass2Processed,
            });
        }

        void AddCount(string source, int delta = 1)
        {
            lock (_countsLock)
            {
                _counts.TryGetValue(source, out var v);
                _counts[source] = v + delta;
            }
        }

        void AddCountry(string iso)
        {
            if (string.IsNullOrEmpty(iso) || iso == "?") return;
            lock (_countsLock)
            {
                _countryStats.TryGetValue(iso, out var v);
                _countryStats[iso] = v + 1;
            }
        }

        // ── Phase 1: DB pass ────────────────────────────────────────────
        ReportProgress("db", force: true);

        NameDb? nameDb = null;
        var aiQueue = new List<string>();

        try
        {
            var dbPathResolved = _dbPath ?? NameDb.FindDbPath();
            if (dbPathResolved != null && File.Exists(dbPathResolved))
                nameDb = new NameDb(dbPathResolved);
        }
        catch
        {
            // DB not available; everything goes to AI
        }

        // DB pass — optimized: no per-item alloc for freshBuf, batch counting
        int dbHits = 0, dbMorphHits = 0, junkCount = 0;
        var lastFewDb = new List<AnalysisResult>(20); // only last 20 for feed display
        var dbTick = Stopwatch.StartNew();

        foreach (var email in unique)
        {
            ct.ThrowIfCancellationRequested();

            if (dbTick.ElapsedMilliseconds >= 100)
            {
                // Batch update counts
                lock (_countsLock)
                {
                    _counts.TryGetValue("db", out var dc); _counts["db"] = dc + dbHits;
                    _counts.TryGetValue("db+morph", out var dmc); _counts["db+morph"] = dmc + dbMorphHits;
                    _counts.TryGetValue("unknown", out var uc); _counts["unknown"] = uc + junkCount;
                }
                // Feed only last few
                foreach (var r in lastFewDb) freshBuf.Add(r);
                lastFewDb.Clear();
                dbHits = 0; dbMorphHits = 0; junkCount = 0;
                ReportProgress("db");
                dbTick.Restart();
            }

            var atIdx = email.IndexOf('@');
            if (atIdx <= 0) { aiQueue.Add(email); continue; }

            var localPart = email[..atIdx];

            if (_skipJunk && IsJunkLocalPart(localPart))
            {
                var r = new AnalysisResult(email, "?", "?", "unknown") { NameToken = localPart };
                _results[email] = r;
                _processed++;
                junkCount++;
                continue;
            }

            // Inline token extraction + DB lookup (avoid List allocation when possible)
            NameLookupResult? bestHit = null;
            string bestToken = "";

            if (nameDb != null)
            {
                int tokStart = -1;
                for (int ci = 0; ci <= localPart.Length; ci++)
                {
                    bool isLet = ci < localPart.Length && char.IsLetter(localPart[ci]);
                    if (isLet && tokStart < 0) { tokStart = ci; }
                    else if (!isLet && tokStart >= 0)
                    {
                        int len = ci - tokStart;
                        if (len >= 2)
                        {
                            var tok = localPart.Substring(tokStart, len).ToLowerInvariant();
                            var hit = nameDb.Lookup(tok);
                            if (hit != null && (bestHit == null || hit.Tier < bestHit.Tier))
                            { bestHit = hit; bestToken = tok; }
                        }
                        tokStart = -1;
                    }
                }
            }

            if (bestHit != null)
            {
                var gender = NormalizeGender(bestHit.Gender);
                var domain = email[(atIdx + 1)..];
                var tldIso = TldToIso(domain);

                // ISO resolution:
                // 1. TLD страны выигрывает всегда (mail.ru → RU независимо от имени)
                // 2. Если TLD generic (.com/.net/gmail) и имя есть в нескольких культурах —
                //    ищем culture чей ISO ≠ "?" (fuzzy CultureToIso), берём первую.
                // 3. Fallback на PickBest culture (English-first).
                string iso;
                if (tldIso != null)
                {
                    iso = tldIso;
                }
                else if (bestHit.AllCultures.Length > 1)
                {
                    string? chosen = null;
                    foreach (var c in bestHit.AllCultures)
                    {
                        var candidate = CultureToIso(c);
                        if (candidate != "?" && !string.IsNullOrEmpty(candidate))
                        {
                            chosen = candidate;
                            break;
                        }
                    }
                    iso = chosen ?? CultureToIso(bestHit.Culture);
                }
                else
                {
                    iso = CultureToIso(bestHit.Culture);
                }

                var source = bestHit.Tier == 1 ? "db" : "db+morph";
                var result = new AnalysisResult(email, gender, iso, source, bestHit.Tier) { NameToken = bestHit.Name };
                _results[email] = result;
                _processed++;
                if (source == "db") dbHits++; else dbMorphHits++;
                AddCountry(iso);
                if (lastFewDb.Count < 20) lastFewDb.Add(result);
                continue;
            }

            aiQueue.Add(email);
        }

        // Flush remaining batch counts
        lock (_countsLock)
        {
            _counts.TryGetValue("db", out var dc2); _counts["db"] = dc2 + dbHits;
            _counts.TryGetValue("db+morph", out var dmc2); _counts["db+morph"] = dmc2 + dbMorphHits;
            _counts.TryGetValue("unknown", out var uc2); _counts["unknown"] = uc2 + junkCount;
        }
        foreach (var r in lastFewDb) freshBuf.Add(r);
        nameDb?.Dispose();
        ReportProgress("db", force: true);

        // ── Phase 1.5: локальный AI-кэш (перед платным AI) ──────────────
        // Смотрим что уже классифицировано в analyze_cache.db текущей моделью
        // и fingerprint'ом промпта — берём готовые результаты, чтобы не платить повторно.
        // Fingerprint промпта — SHA1 от текста SystemPrompt (авто, не ручная константа).
        AnalyzeCache? cache = null;
        try
        {
            var promptFingerprint = AnalyzeCache.ComputePromptVersion(AiClient.GetSystemPrompt());
            cache = new AnalyzeCache(AnalyzeCache.DefaultDbPath(), _model, promptFingerprint);
            var cached = cache.LoadAll();
            if (cached.Count > 0 && aiQueue.Count > 0)
            {
                var remaining = new List<string>(aiQueue.Count);
                int cacheHits = 0;
                foreach (var email in aiQueue)
                {
                    if (cached.TryGetValue(email, out var cachedResult))
                    {
                        SetResult(email, cachedResult);
                        freshBuf.Add(cachedResult);
                        Interlocked.Increment(ref _processed);
                        AddCount("cache");
                        AddCountry(cachedResult.Iso);
                        cacheHits++;
                    }
                    else
                    {
                        remaining.Add(email);
                    }
                }
                aiQueue = remaining;
                if (cacheHits > 0) ReportProgress("cache", force: true);
            }
        }
        catch
        {
            cache = null; // Кэш недоступен — работаем как раньше через AI.
        }
        // Гарантированное закрытие соединения при любом выходе из метода
        // (exception, cancel, normal completion). Раньше Dispose был в конце
        // — при exception в pass1/pass2 SQLite connection оставался висеть.
        using var _cacheGuard = cache;

        // ── Phase 2: AI pass 1 ──────────────────────────────────────────
        if (aiQueue.Count > 0 && !string.IsNullOrWhiteSpace(_apiKey))
        {
            ReportProgress("ai1", force: true);

            var batches = new List<List<string>>();
            for (int i = 0; i < aiQueue.Count; i += _batchSize)
                batches.Add(aiQueue.GetRange(i, Math.Min(_batchSize, aiQueue.Count - i)));

            using var aiClient = new AiClient(_provider, _apiKey, _model);
            using var semaphore = new SemaphoreSlim(_maxConcurrent);

            // Post-process: если AI вернул iso="?", но TLD страны известен — берём TLD.
            // Дешевле чем добавлять domain hint в промпт (+30% input tokens).
            AnalysisResult ApplyTldFallback(AnalysisResult r)
            {
                if (r.Iso != "?" && !string.IsNullOrEmpty(r.Iso)) return r;
                var at = r.Email.IndexOf('@');
                if (at <= 0) return r;
                var tld = TldToIso(r.Email[(at + 1)..]);
                if (tld == null) return r;
                var newSignals = string.IsNullOrEmpty(r.Signals) ? "tld_fallback" : r.Signals + ";tld_fallback";
                return r with { Iso = tld, Signals = newSignals };
            }

            // Обрабатывает batch с рекурсивным split при HTTP-ошибках.
            // Если batch fail'нет — сплитим пополам и retry'им половины.
            // Раньше 1 batch fail = 20 emails потеряно; теперь теряется только один
            // "битый" email в самом низу рекурсии (когда batch.Count == 1).
            async Task ProcessBatch(List<string> batch)
            {
                if (batch.Count == 0) return;

                if (_costUsd >= _spendCapUsd)
                {
                    foreach (var email in batch)
                    {
                        if (!_results.ContainsKey(email))
                        {
                            var r = new AnalysisResult(email, "?", "?", "unknown")
                                { Signals = "spend_cap" };
                            SetResult(email, r);
                            freshBuf.Add(r);
                            Interlocked.Increment(ref _processed);
                            AddCount("unknown");
                        }
                    }
                    return;
                }

                ct.ThrowIfCancellationRequested();

                try
                {
                    var (results, inTok, outTok) = await aiClient.AnalyzeBatchAsync(batch, ct);

                    Interlocked.Add(ref _inputTokens, inTok);
                    Interlocked.Add(ref _outputTokens, outTok);

                    var batchCost = AiClient.EstimateCost(_model, inTok, outTok);
                    lock (_countsLock) { _costUsd += batchCost; }

                    foreach (var r in results)
                    {
                        var final = ApplyTldFallback(r);
                        SetResult(final.Email, final);
                        freshBuf.Add(final);
                        Interlocked.Increment(ref _processed);
                        AddCount("ai");
                        AddCountry(final.Iso);
                        cache?.Insert(final);
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch (Exception ex) when (batch.Count > 1)
                {
                    // Batch fail — сплитим пополам и retry'им отдельными задачами.
                    Interlocked.Increment(ref _aiErrors);
                    _lastError = $"split@{batch.Count}: {ex.Message}";
                    int half = batch.Count / 2;
                    var left = batch.GetRange(0, half);
                    var right = batch.GetRange(half, batch.Count - half);
                    // Небольшая задержка между split-retry чтобы не усилить rate-limit.
                    await Task.Delay(500, ct);
                    await ProcessBatch(left);
                    await ProcessBatch(right);
                }
                catch (Exception ex)
                {
                    // batch.Count == 1 — сдаёмся, marking единственный email.
                    Interlocked.Increment(ref _aiErrors);
                    _lastError = ex.Message;
                    var email = batch[0];
                    if (!_results.ContainsKey(email))
                    {
                        var r = new AnalysisResult(email, "?", "?", "unknown")
                            { Signals = $"ai_error: {ex.Message}" };
                        SetResult(email, r);
                        freshBuf.Add(r);
                        Interlocked.Increment(ref _processed);
                        AddCount("unknown");
                    }
                }
            }

            var tasks = batches.Select(async batch =>
            {
                await semaphore.WaitAsync(ct);
                try
                {
                    await ProcessBatch(batch);
                    // Throttle между запросами (rate-limit friendly).
                    if (_batchDelayMs > 0)
                        await Task.Delay(_batchDelayMs, ct);
                    ReportProgress("ai1");
                }
                finally
                {
                    semaphore.Release();
                }
            }).ToArray();

            await Task.WhenAll(tasks);
        }
        else if (aiQueue.Count > 0)
        {
            // No API key — mark remaining as unknown
            foreach (var email in aiQueue)
            {
                var r = new AnalysisResult(email, "?", "?", "unknown")
                    { Signals = "no_api_key" };
                SetResult(email, r);
                freshBuf.Add(r);
                Interlocked.Increment(ref _processed);
                AddCount("unknown");
            }
        }

        // ── Phase 3: AI pass 2 (optional) ───────────────────────────────
        if (_twoPass && !string.IsNullOrWhiteSpace(_apiKey) && _costUsd < _spendCapUsd)
        {
            var unknowns = _results.Values
                .Where(r => r.Gender == "?" || r.Iso == "?")
                .Where(r => r.Source != "unknown" || !r.Signals.Contains("junk"))
                .Select(r => r.Email)
                .ToList();

            if (unknowns.Count > 0)
            {
                _pass2Total = unknowns.Count;
                _pass2Processed = 0;
                ReportProgress("ai2", force: true);

                // Use the same provider with a different prompt emphasis
                var batches2 = new List<List<string>>();
                for (int i = 0; i < unknowns.Count; i += _batchSize)
                    batches2.Add(unknowns.GetRange(i, Math.Min(_batchSize, unknowns.Count - i)));

                using var ai2 = new AiClient(_provider, _apiKey, _model);

                foreach (var batch in batches2)
                {
                    if (_costUsd >= _spendCapUsd) break;
                    ct.ThrowIfCancellationRequested();

                    try
                    {
                        var (results, inTok, outTok) = await ai2.AnalyzeBatchAsync(batch, ct);

                        Interlocked.Add(ref _inputTokens, inTok);
                        Interlocked.Add(ref _outputTokens, outTok);
                        lock (_countsLock)
                        {
                            _costUsd += AiClient.EstimateCost(_model, inTok, outTok);
                        }

                        foreach (var r in results)
                        {
                            if (_results.TryGetValue(r.Email, out var existing))
                            {
                                // Only update if pass2 has better data
                                var newGender = r.Gender != "?" ? r.Gender : existing.Gender;
                                var newIso = r.Iso != "?" ? r.Iso : existing.Iso;
                                var updated = new AnalysisResult(r.Email, newGender, newIso, "ai+ai2", r.MatchTier)
                                    { NameToken = existing.NameToken, Signals = existing.Signals + ";pass2" };
                                SetResult(r.Email, updated);
                                freshBuf.Add(updated);
                                AddCountry(newIso);
                                cache?.Insert(updated);

                                // Reclassify count
                                AddCount("ai", -1);
                                AddCount("ai+ai2");
                            }
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Interlocked.Increment(ref _aiErrors);
                        _lastError = ex.Message;
                    }

                    Interlocked.Add(ref _pass2Processed, batch.Count);
                    ReportProgress("ai2", force: true);

                    // Delay between pass2 batches — используем настройку из конфига,
                    // fallback на 500ms (было хардкодом 2000, слишком медленно).
                    if (_batchDelayMs > 0)
                        await Task.Delay(_batchDelayMs, ct);
                    else
                        await Task.Delay(500, ct);
                }
            }
        }

        // ── Done ────────────────────────────────────────────────────────
        ReportProgress("done", force: true);

        // Write results CSV
        await WriteResultsCsvAsync(ct);

        // Cache Dispose делается через using var _cacheGuard выше — не нужен явный вызов.
    }

    private async Task WriteResultsCsvAsync(CancellationToken ct)
    {
        try
        {
            // Output next to exe in output/analyze/
            var outputDir = Paths.AnalyzeRoot;
            var csvPath = Path.Combine(outputDir, "analyze_results.csv");

            var sb = new StringBuilder();
            sb.AppendLine("email,name_token,gender,country,source,match_tier,signals");

            foreach (var r in _results.Values.OrderBy(r => r.Email))
            {
                ct.ThrowIfCancellationRequested();
                var signals = r.Signals.Replace("\"", "\"\"");
                sb.AppendLine($"{r.Email},{r.NameToken},{r.Gender},{r.Iso},{r.Source},{r.MatchTier},\"{signals}\"");
            }

            await File.WriteAllTextAsync(csvPath, sb.ToString(), Encoding.UTF8, ct);
        }
        catch (Exception ex)
        {
            Debug.WriteLine($"Error writing analyze CSV: {ex}");
        }
    }

    /// <summary>
    /// Get all accumulated results.
    /// </summary>
    public List<AnalysisResult> GetResults()
    {
        lock (_resultsLock)
            return _results.Values.OrderBy(r => r.Email).ToList();
    }

    private void SetResult(string email, AnalysisResult result)
    {
        lock (_resultsLock)
            _results[email] = result;
    }

    // ── Helpers ──────────────────────────────────────────────────────────

    private static List<string> ExtractNameTokens(string localPart)
    {
        var tokens = new List<string>(4);
        int start = -1;
        for (int i = 0; i <= localPart.Length; i++)
        {
            bool isLetter = i < localPart.Length && char.IsLetter(localPart[i]);
            if (isLetter && start < 0)
                start = i;
            else if (!isLetter && start >= 0)
            {
                int len = i - start;
                if (len >= 2)
                    tokens.Add(localPart.Substring(start, len).ToLowerInvariant());
                start = -1;
            }
        }
        return tokens;
    }

    private static bool IsJunkLocalPart(string local)
    {
        // Pure digits, very short, or looks like generated hash
        if (local.Length <= 2) return true;
        if (local.All(c => char.IsDigit(c) || c == '.')) return true;
        // Hex-looking strings (e.g. cafebabe12ab). Порог 12 — 8 давал ложные срабатывания
        // на реальных никах типа abcdef12. Оставляем только явно auto-generated hashes.
        if (local.Length >= 12 && local.All(c => "0123456789abcdef".Contains(c))) return true;
        // No alpha chars at all
        if (!local.Any(char.IsLetter)) return true;
        return false;
    }

    private static string NormalizeGender(string g)
    {
        if (string.IsNullOrEmpty(g)) return "?";
        var first = g.Trim().ToUpperInvariant();
        if (first.StartsWith("M")) return "M";
        if (first.StartsWith("F")) return "F";
        if (first.Contains("&") || first.Contains("UNISEX") || first.Contains("N"))
            return "N";
        return "?";
    }

    // Map culture string from behindthename → ISO 2-letter country code.
    // Список составлен из реального `SELECT DISTINCT culture_clean` в names.db.
    // Порядок проверки: exact match, потом substring hints ("Biblical Hebrew" → IL).
    // Мифологические/античные категории → "?" (они не привязаны к современной стране).
    private static readonly Dictionary<string, string> CultureExactMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["English"] = "US", ["American"] = "US", ["African American"] = "US", ["Hawaiian"] = "US",
        ["British"] = "GB", ["Scottish"] = "GB", ["Welsh"] = "GB",
        ["Scottish Gaelic"] = "GB", ["Anglo-Saxon"] = "GB", ["Medieval English"] = "GB",
        ["Irish"] = "IE", ["Old Irish"] = "IE",
        ["French"] = "FR", ["Breton"] = "FR",
        ["German"] = "DE", ["Low German"] = "DE", ["Frisian"] = "NL", ["Germanic"] = "DE",
        ["Italian"] = "IT",
        ["Spanish"] = "ES", ["Basque"] = "ES", ["Catalan"] = "ES", ["Galician"] = "ES",
        ["Portuguese"] = "PT", ["Brazilian"] = "BR",
        ["Russian"] = "RU", ["Tatar"] = "RU",
        ["Ukrainian"] = "UA",
        ["Belarusian"] = "BY", ["Belarus"] = "BY", ["Belarussian"] = "BY",
        ["Polish"] = "PL",
        ["Czech"] = "CZ", ["Slovak"] = "SK", ["Hungarian"] = "HU",
        ["Romanian"] = "RO", ["Bulgarian"] = "BG",
        ["Serbian"] = "RS", ["Croatian"] = "HR", ["Bosnian"] = "BA",
        ["Slovenian"] = "SI", ["Slovene"] = "SI",
        ["Albanian"] = "AL", ["Macedonian"] = "MK",
        ["Dutch"] = "NL",
        ["Swedish"] = "SE", ["Norwegian"] = "NO", ["Danish"] = "DK", ["Finnish"] = "FI",
        ["Icelandic"] = "IS", ["Old Norse"] = "IS",
        ["Estonian"] = "EE", ["Latvian"] = "LV", ["Lithuanian"] = "LT",
        ["Greek"] = "GR",
        ["Turkish"] = "TR", ["Azerbaijani"] = "AZ",
        ["Arabic"] = "SA",
        ["Hebrew"] = "IL", ["Jewish"] = "IL", ["Yiddish"] = "IL",
        ["Japanese"] = "JP", ["Chinese"] = "CN", ["Korean"] = "KR", ["Vietnamese"] = "VN",
        ["Indian"] = "IN", ["Hindi"] = "IN", ["Hindiism"] = "IN", ["Hinduism"] = "IN",
        ["Bengali"] = "IN", ["Tamil"] = "IN", ["Telugu"] = "IN", ["Marathi"] = "IN",
        ["Malayalam"] = "IN", ["Kannada"] = "IN", ["Punjabi"] = "IN", ["Gujarati"] = "IN",
        ["Nepali"] = "NP", ["Urdu"] = "PK",
        ["Thai"] = "TH", ["Indonesian"] = "ID", ["Malay"] = "MY", ["Filipino"] = "PH",
        ["Persian"] = "IR",
        ["Georgian"] = "GE", ["Armenian"] = "AM",
        ["Kazakh"] = "KZ", ["Uzbek"] = "UZ", ["Kyrgyz"] = "KG", ["Turkmen"] = "TM", ["Tajik"] = "TJ",
        ["Nigerian"] = "NG", ["Yoruba"] = "NG", ["Igbo"] = "NG", ["Hausa"] = "NG",
        ["Egyptian"] = "EG", ["Moroccan"] = "MA", ["South African"] = "ZA",
        ["Canadian"] = "CA", ["Mexican"] = "MX",
        ["Australian"] = "AU",
    };

    // Substring hints — срабатывают когда точного совпадения нет.
    // "Biblical Hebrew" → IL, "African Kikuyu" → неизвестно → "?".
    private static readonly (string Contains, string Iso)[] CultureContainsMap =
    [
        ("Biblical Hebrew", "IL"), ("Biblical Greek", "GR"), ("Biblical Latin", "IT"),
        ("Biblical", "?"),
        ("Ancient Greek", "GR"), ("Ancient Roman", "IT"), ("Late Roman", "IT"),
        ("Ancient", "?"),
        ("Mythology", "?"), ("Cycle", "?"), ("Literature", "?"), ("History", "?"),
    ];

    private static string CultureToIso(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return "?";
        var c = culture.Trim();
        if (CultureExactMap.TryGetValue(c, out var exact)) return exact;
        foreach (var (contains, iso) in CultureContainsMap)
            if (c.Contains(contains, StringComparison.OrdinalIgnoreCase))
                return iso;
        return "?";
    }

    private static readonly Dictionary<string, string> TldIsoMap = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ru"] = "RU", ["su"] = "RU",
        ["ua"] = "UA", ["by"] = "BY", ["kz"] = "KZ", ["uz"] = "UZ",
        ["de"] = "DE", ["at"] = "AT", ["ch"] = "CH",
        ["fr"] = "FR", ["it"] = "IT", ["es"] = "ES", ["pt"] = "PT",
        ["nl"] = "NL", ["be"] = "BE",
        ["pl"] = "PL", ["cz"] = "CZ", ["sk"] = "SK", ["hu"] = "HU",
        ["ro"] = "RO", ["bg"] = "BG", ["rs"] = "RS", ["hr"] = "HR",
        ["se"] = "SE", ["no"] = "NO", ["dk"] = "DK", ["fi"] = "FI",
        ["gr"] = "GR", ["tr"] = "TR",
        ["jp"] = "JP", ["cn"] = "CN", ["kr"] = "KR", ["in"] = "IN",
        ["br"] = "BR", ["mx"] = "MX", ["ar"] = "AR",
        ["il"] = "IL", ["ir"] = "IR", ["sa"] = "SA",
        ["au"] = "AU", ["nz"] = "NZ",
        ["gb"] = "GB", ["uk"] = "GB", ["ie"] = "IE",
        ["ca"] = "CA",
        ["lv"] = "LV", ["lt"] = "LT", ["ee"] = "EE",
        ["ge"] = "GE", ["am"] = "AM", ["az"] = "AZ",
        ["vn"] = "VN", ["th"] = "TH", ["id"] = "ID", ["ph"] = "PH",
        ["ng"] = "NG", ["za"] = "ZA", ["eg"] = "EG", ["ke"] = "KE",
    };

    // Known RU-zone domains (not just TLD)
    private static readonly HashSet<string> RuDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "mail.ru", "inbox.ru", "list.ru", "bk.ru", "internet.ru",
        "yandex.ru", "yandex.com", "ya.ru", "yandex.by", "yandex.kz", "yandex.ua",
        "rambler.ru", "lenta.ru", "autorambler.ru",
    };

    private static readonly HashSet<string> UaDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "ukr.net", "i.ua", "meta.ua", "bigmir.net",
    };

    private static string? TldToIso(string domain)
    {
        // Check full domain first (mail.ru → RU)
        if (RuDomains.Contains(domain)) return "RU";
        if (UaDomains.Contains(domain)) return "UA";

        // Check TLD
        var dotIdx = domain.LastIndexOf('.');
        if (dotIdx < 0) return null;
        var tld = domain[(dotIdx + 1)..];
        return TldIsoMap.TryGetValue(tld, out var iso) ? iso : null;
    }
}
