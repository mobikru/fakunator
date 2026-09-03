using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Data.Sqlite;

namespace Fakunator.Core.DomainScanner;

/// <summary>
/// SQLite-хранилище "брошенных" доменов и агрегированной статистики.
/// Порт db.py, упрощённый: без FTS5 (для нашего use-case — статистика по
/// провайдерам — полнотекстовый поиск не нужен, обычные индексы справляются).
///
/// Схема:
/// - domains: обзор всей найденной базы, PK = domain (текстовый). На миллион
///   строк это ~150 MB — приемлемо.
/// - ns_hosts JSON — компактно в одном поле. Не пробовал разбирать в
///   отдельную таблицу (в отличие от Python-версии), т.к. поиск по конкретному
///   NS-хосту у нас редкий, в основном фильтруем по агрегированному provider.
/// - scan_history: журнал прогонов для UI "последнее обновление N дней назад".
///
/// Concurrency: батч-запись через очередь + один writer-loop (SQLite не любит
/// параллельные writes, WAL помогает, но writer-per-thread всё равно эффективнее).
/// </summary>
public class DomainDb : IAsyncDisposable
{
    private const string Schema = @"
CREATE TABLE IF NOT EXISTS domains (
    domain TEXT PRIMARY KEY,
    zone TEXT NOT NULL,
    ns_provider TEXT,
    ns_hosts TEXT,
    status TEXT,
    has_a INTEGER,
    a_status TEXT,
    checked_at INTEGER
);
CREATE INDEX IF NOT EXISTS idx_zone ON domains(zone);
CREATE INDEX IF NOT EXISTS idx_provider ON domains(ns_provider);
CREATE INDEX IF NOT EXISTS idx_zone_provider ON domains(zone, ns_provider);
CREATE INDEX IF NOT EXISTS idx_status ON domains(status);

CREATE TABLE IF NOT EXISTS domain_health (
    domain TEXT PRIMARY KEY,
    created_at TEXT,
    expires_at TEXT,
    rbl_hits TEXT,
    whois_error TEXT,
    checked_at INTEGER NOT NULL
);
CREATE INDEX IF NOT EXISTS idx_dh_checked ON domain_health(checked_at);

CREATE TABLE IF NOT EXISTS scan_history (
    id INTEGER PRIMARY KEY,
    zone TEXT NOT NULL,
    started_at INTEGER NOT NULL,
    finished_at INTEGER,
    total_fetched INTEGER,
    total_saved INTEGER
);
CREATE INDEX IF NOT EXISTS idx_hist_started ON scan_history(started_at DESC);
";

    private const string Pragmas = @"
PRAGMA journal_mode=WAL;
PRAGMA synchronous=NORMAL;
PRAGMA temp_store=MEMORY;
PRAGMA cache_size=-64000;
";

    private readonly string _dbPath;
    private readonly SqliteConnection _conn;
    private readonly BlockingCollection<DomainRecord> _queue = new(new ConcurrentQueue<DomainRecord>());
    private readonly CancellationTokenSource _writerCts = new();
    private readonly Task _writerTask;
    private bool _disposed;

    public DomainDb(string dbPath)
    {
        _dbPath = dbPath;
        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
        _conn.Open();

        using (var cmd = _conn.CreateCommand())
        {
            cmd.CommandText = Pragmas + Schema;
            cmd.ExecuteNonQuery();
        }

        _writerTask = Task.Run(WriterLoop);
    }

    /// <summary>Кладёт запись в очередь на запись. Не блокирует.</summary>
    public void Enqueue(DomainRecord rec)
    {
        if (_disposed) return;
        _queue.Add(rec);
    }

    /// <summary>Стартовая запись прогона в history. Возвращает id для последующего Finish.</summary>
    public long BeginScan(string zone)
    {
        lock (_conn)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT INTO scan_history (zone, started_at) VALUES ($z, $t); SELECT last_insert_rowid();";
            cmd.Parameters.AddWithValue("$z", zone);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            return (long)(cmd.ExecuteScalar() ?? 0L);
        }
    }

    public void FinishScan(long scanId, int totalFetched, int totalSaved)
    {
        lock (_conn)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"UPDATE scan_history SET finished_at=$t, total_fetched=$f, total_saved=$s WHERE id=$id";
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.Parameters.AddWithValue("$f", totalFetched);
            cmd.Parameters.AddWithValue("$s", totalSaved);
            cmd.Parameters.AddWithValue("$id", scanId);
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>Общее количество записей в базе (для KPI «всего в базе»).</summary>
    public int TotalCount()
    {
        lock (_conn)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM domains";
            return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
        }
    }

    /// <summary>Топ-N NS-провайдеров (для sidebar-списка).</summary>
    public List<(string Provider, int Count)> TopProviders(int limit = 200, string? zone = null)
    {
        lock (_conn)
        {
            using var cmd = _conn.CreateCommand();
            if (string.IsNullOrEmpty(zone))
            {
                cmd.CommandText = @"SELECT ns_provider, COUNT(*) c FROM domains
                                    WHERE ns_provider IS NOT NULL AND ns_provider != ''
                                    GROUP BY ns_provider ORDER BY c DESC LIMIT $l";
            }
            else
            {
                cmd.CommandText = @"SELECT ns_provider, COUNT(*) c FROM domains
                                    WHERE zone=$z AND ns_provider IS NOT NULL AND ns_provider != ''
                                    GROUP BY ns_provider ORDER BY c DESC LIMIT $l";
                cmd.Parameters.AddWithValue("$z", zone);
            }
            cmd.Parameters.AddWithValue("$l", limit);

            var list = new List<(string, int)>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
                list.Add((r.GetString(0), r.GetInt32(1)));
            return list;
        }
    }

    /// <summary>Список зон в базе (для UI-фильтра).</summary>
    public List<string> Zones()
    {
        lock (_conn)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "SELECT DISTINCT zone FROM domains ORDER BY zone";
            var list = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    public record DomainRow(string Domain, string Zone, string NsProvider, string NsHosts, string? Registrar, long? CheckedAt);

    /// <summary>Возвращает свежий health из кэша (или null если не проверялся / устарел).</summary>
    public DomainHealth? GetHealth(string domain)
    {
        lock (_conn)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"SELECT created_at, expires_at, rbl_hits, whois_error, checked_at
                                FROM domain_health WHERE domain = $d";
            cmd.Parameters.AddWithValue("$d", domain);
            using var r = cmd.ExecuteReader();
            if (!r.Read()) return null;
            var h = new DomainHealth
            {
                CreatedAt = r.IsDBNull(0) ? null : DateTime.TryParse(r.GetString(0), out var c) ? c : null,
                ExpiresAt = r.IsDBNull(1) ? null : DateTime.TryParse(r.GetString(1), out var e) ? e : null,
                RblHits = r.IsDBNull(2) ? new() : r.GetString(2).Split(',', StringSplitOptions.RemoveEmptyEntries).ToList(),
                WhoisError = r.IsDBNull(3) ? null : r.GetString(3),
                CheckedAt = DateTimeOffset.FromUnixTimeSeconds(r.GetInt64(4)).UtcDateTime,
            };
            // TTL: если старше 30 дней — возвращаем null, инициатор пусть перечекнет
            if (DateTime.UtcNow - h.CheckedAt > DomainHealthChecker.CacheTtl) return null;
            // Инвалидация старого формата: раньше RBL-хиты сохранялись без категории
            // ("SURBL" вместо "SURBL:phishing"). Такие записи форсим на перечек.
            if (h.RblHits.Any(x => !x.Contains(':'))) return null;
            // Инвалидация generic "listed" — раскодируем в конкретный SURBL bitmask
            if (h.RblHits.Any(x => x.EndsWith(":listed"))) return null;
            // Sanity check дат: разница между регистрацией и expiry должна быть кратна ~году.
            // Если >0.4 года разницы от ближайшего целого — regex схватил не ту строку. Форсим перечек.
            if (h.CreatedAt != null && h.ExpiresAt != null)
            {
                var lifeSpan = h.ExpiresAt.Value - h.CreatedAt.Value;
                var lifeYears = lifeSpan.TotalDays / 365.25;
                var fracYear = Math.Abs(lifeYears - Math.Round(lifeYears));
                if (fracYear > 0.4) return null;
                // .ru регистратура не даёт продление больше чем на 5 лет. Если >5 —
                // почти наверняка сохранена «древняя» дата регистрации (парсер зацепил
                // старую versify-строку), надо перечекнуть.
                var tld = domain.Contains('.') ? domain[(domain.LastIndexOf('.') + 1)..].ToLowerInvariant() : "";
                if (tld is "ru" or "su" or "рф" && lifeYears > 5) return null;
            }
            return h;
        }
    }

    /// <summary>UPSERT health в кэш (перезаписываем полностью).</summary>
    public void SaveHealth(string domain, DomainHealth h)
    {
        lock (_conn)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = @"INSERT OR REPLACE INTO domain_health
                (domain, created_at, expires_at, rbl_hits, whois_error, checked_at)
                VALUES ($d, $c, $e, $r, $w, $t)";
            cmd.Parameters.AddWithValue("$d", domain);
            cmd.Parameters.AddWithValue("$c", (object?)h.CreatedAt?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$e", (object?)h.ExpiresAt?.ToString("o") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$r", (object?)string.Join(",", h.RblHits) ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$w", (object?)h.WhoisError ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$t", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            cmd.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Пейджинг для браузера доменов.
    /// </summary>
    public (List<DomainRow> Rows, int Total) ListDomains(
        string? zone, string? provider, string? nsHost, string? query,
        int page, int pageSize)
    {
        lock (_conn)
        {
            var where = new List<string>();
            var parameters = new List<(string, object)>();
            if (!string.IsNullOrEmpty(zone)) { where.Add("zone = $z"); parameters.Add(("$z", zone)); }
            if (!string.IsNullOrEmpty(provider)) { where.Add("ns_provider = $p"); parameters.Add(("$p", provider)); }
            if (!string.IsNullOrEmpty(nsHost)) { where.Add("ns_hosts LIKE $nh"); parameters.Add(("$nh", "%" + nsHost + "%")); }
            if (!string.IsNullOrEmpty(query) && query.Length >= 3) { where.Add("domain LIKE $q"); parameters.Add(("$q", "%" + query + "%")); }
            var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

            int total;
            using (var cnt = _conn.CreateCommand())
            {
                cnt.CommandText = $"SELECT COUNT(*) FROM domains {whereSql}";
                foreach (var (k, v) in parameters) cnt.Parameters.AddWithValue(k, v);
                total = Convert.ToInt32(cnt.ExecuteScalar() ?? 0);
            }

            var offset = Math.Max(0, (page - 1) * pageSize);
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $@"SELECT domain, zone, ns_provider, ns_hosts, NULL AS registrar, checked_at
                                 FROM domains {whereSql}
                                 ORDER BY domain ASC LIMIT $lim OFFSET $off";
            foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
            cmd.Parameters.AddWithValue("$lim", pageSize);
            cmd.Parameters.AddWithValue("$off", offset);

            var rows = new List<DomainRow>();
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                rows.Add(new DomainRow(
                    r.GetString(0),
                    r.GetString(1),
                    r.IsDBNull(2) ? "" : r.GetString(2),
                    r.IsDBNull(3) ? "[]" : r.GetString(3),
                    r.IsDBNull(4) ? null : r.GetString(4),
                    r.IsDBNull(5) ? null : r.GetInt64(5)));
            }
            return (rows, total);
        }
    }

    /// <summary>Получить ВСЕ доменные имена под фильтр (для reverify).</summary>
    public List<string> ListAllDomainNames(string? zone, string? provider, string? nsHost, string? query)
    {
        var where = new List<string>();
        var parameters = new List<(string, object)>();
        if (!string.IsNullOrEmpty(zone)) { where.Add("zone = $z"); parameters.Add(("$z", zone)); }
        if (!string.IsNullOrEmpty(provider)) { where.Add("ns_provider = $p"); parameters.Add(("$p", provider)); }
        if (!string.IsNullOrEmpty(nsHost)) { where.Add("ns_hosts LIKE $nh"); parameters.Add(("$nh", "%" + nsHost + "%")); }
        if (!string.IsNullOrEmpty(query) && query.Length >= 3) { where.Add("domain LIKE $q"); parameters.Add(("$q", "%" + query + "%")); }
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        lock (_conn)
        {
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT domain FROM domains {whereSql} ORDER BY domain ASC";
            foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
            var list = new List<string>();
            using var r = cmd.ExecuteReader();
            while (r.Read()) list.Add(r.GetString(0));
            return list;
        }
    }

    /// <summary>Полная очистка БД: сброс таблиц + VACUUM для возврата места ОС.</summary>
    public int ClearAll()
    {
        lock (_conn)
        {
            using (var cnt = _conn.CreateCommand())
            {
                cnt.CommandText = "SELECT COUNT(*) FROM domains";
                int total = Convert.ToInt32(cnt.ExecuteScalar() ?? 0);
                using (var cmd = _conn.CreateCommand())
                {
                    cmd.CommandText = "DELETE FROM domains; DELETE FROM scan_history;";
                    cmd.ExecuteNonQuery();
                }
                using (var vac = _conn.CreateCommand())
                {
                    vac.CommandText = "VACUUM";
                    vac.ExecuteNonQuery();
                }
                return total;
            }
        }
    }

    /// <summary>Удаляет пачку доменов (в одной транзакции).</summary>
    public int DeleteDomains(IEnumerable<string> domains)
    {
        int deleted = 0;
        lock (_conn)
        {
            using var tx = _conn.BeginTransaction();
            using var cmd = _conn.CreateCommand();
            cmd.CommandText = "DELETE FROM domains WHERE domain = $d";
            var p = cmd.CreateParameter(); p.ParameterName = "$d"; cmd.Parameters.Add(p);
            foreach (var d in domains)
            {
                p.Value = d;
                deleted += cmd.ExecuteNonQuery();
            }
            tx.Commit();
        }
        return deleted;
    }

    /// <summary>
    /// Экспорт списка доменов по фильтру в файл (потоковой записью, без загрузки в память).
    /// </summary>
    public async Task ExportToFileAsync(string? zone, string? provider, string? nsHost, string? query, string outPath, CancellationToken ct)
    {
        var where = new List<string>();
        var parameters = new List<(string, object)>();
        if (!string.IsNullOrEmpty(zone)) { where.Add("zone = $z"); parameters.Add(("$z", zone)); }
        if (!string.IsNullOrEmpty(provider)) { where.Add("ns_provider = $p"); parameters.Add(("$p", provider)); }
        if (!string.IsNullOrEmpty(nsHost)) { where.Add("ns_hosts LIKE $nh"); parameters.Add(("$nh", "%" + nsHost + "%")); }
        if (!string.IsNullOrEmpty(query) && query.Length >= 3) { where.Add("domain LIKE $q"); parameters.Add(("$q", "%" + query + "%")); }
        var whereSql = where.Count > 0 ? "WHERE " + string.Join(" AND ", where) : "";

        await using var w = new StreamWriter(outPath, false, System.Text.Encoding.UTF8) { NewLine = "\n" };
        SqliteDataReader? reader = null;
        lock (_conn)
        {
            var cmd = _conn.CreateCommand();
            cmd.CommandText = $"SELECT domain FROM domains {whereSql} ORDER BY domain ASC";
            foreach (var (k, v) in parameters) cmd.Parameters.AddWithValue(k, v);
            reader = cmd.ExecuteReader();
        }
        try
        {
            while (reader != null && reader.Read())
            {
                ct.ThrowIfCancellationRequested();
                await w.WriteLineAsync(reader.GetString(0)).ConfigureAwait(false);
            }
        }
        finally { reader?.Dispose(); }
    }

    // ── Writer loop ─────────────────────────────────────────────────
    private void WriterLoop()
    {
        var buffer = new List<DomainRecord>(500);
        foreach (var rec in _queue.GetConsumingEnumerable(_writerCts.Token))
        {
            buffer.Add(rec);
            if (buffer.Count >= 500 || _queue.Count == 0)
            {
                FlushBuffer(buffer);
                buffer.Clear();
            }
        }
        if (buffer.Count > 0) FlushBuffer(buffer);
    }

    private void FlushBuffer(List<DomainRecord> buffer)
    {
        if (buffer.Count == 0) return;
        try
        {
            lock (_conn)
            {
                using var tx = _conn.BeginTransaction();
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"
INSERT INTO domains (domain, zone, ns_provider, ns_hosts, status, has_a, a_status, checked_at)
VALUES ($d, $z, $p, $n, $s, $h, $as, $t)
ON CONFLICT(domain) DO UPDATE SET
    zone=excluded.zone,
    ns_provider=excluded.ns_provider,
    ns_hosts=excluded.ns_hosts,
    status=excluded.status,
    has_a=excluded.has_a,
    a_status=excluded.a_status,
    checked_at=excluded.checked_at
";
                var pDomain = cmd.CreateParameter(); pDomain.ParameterName = "$d"; cmd.Parameters.Add(pDomain);
                var pZone = cmd.CreateParameter(); pZone.ParameterName = "$z"; cmd.Parameters.Add(pZone);
                var pProv = cmd.CreateParameter(); pProv.ParameterName = "$p"; cmd.Parameters.Add(pProv);
                var pNs = cmd.CreateParameter(); pNs.ParameterName = "$n"; cmd.Parameters.Add(pNs);
                var pSt = cmd.CreateParameter(); pSt.ParameterName = "$s"; cmd.Parameters.Add(pSt);
                var pHa = cmd.CreateParameter(); pHa.ParameterName = "$h"; cmd.Parameters.Add(pHa);
                var pAs = cmd.CreateParameter(); pAs.ParameterName = "$as"; cmd.Parameters.Add(pAs);
                var pTs = cmd.CreateParameter(); pTs.ParameterName = "$t"; cmd.Parameters.Add(pTs);

                var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
                foreach (var r in buffer)
                {
                    pDomain.Value = r.Domain;
                    pZone.Value = r.Zone;
                    pProv.Value = r.NsProvider ?? "";
                    pNs.Value = JsonSerializer.Serialize(r.NsHosts);
                    pSt.Value = r.Status ?? "";
                    pHa.Value = r.HasA ? 1 : 0;
                    pAs.Value = r.AStatus ?? "";
                    pTs.Value = now;
                    cmd.ExecuteNonQuery();
                }
                tx.Commit();
            }
        }
        catch { /* swallow — write-back best effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        _queue.CompleteAdding();
        try { await _writerTask.ConfigureAwait(false); } catch { }
        _writerCts.Cancel();
        _conn.Close();
        _conn.Dispose();
    }
}
