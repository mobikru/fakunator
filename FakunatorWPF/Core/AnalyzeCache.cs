using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Fakunator.Core;

/// <summary>
/// Локальный кэш AI-ответов. Экономит cost/время на повторных прогонах:
/// перед отправкой batch в AI — проверяем что этот email уже классифицирован
/// текущей моделью и версией промпта. Если да — берём готовый результат.
///
/// Инвалидация: (model + prompt_version) — если промпт переписан, старые
/// записи не мешают (просто не матчатся, новые пишутся поверх).
///
/// Concurrent access: SQLite сам сериализует writes; чтения атомарны.
/// В Worker'е — читаем один раз в начале в память (LoadAll), пишем по одному
/// после каждого AI batch через lock.
/// </summary>
public class AnalyzeCache : IDisposable
{
    /// <summary>
    /// Автоматический fingerprint от текста SystemPrompt в AiClient.
    /// Любое изменение промпта → новый ключ в кэше, старые записи игнорируются.
    /// Раньше была ручная константа "v3" — легко забыть инкрементировать.
    /// </summary>
    public static string ComputePromptVersion(string systemPrompt)
    {
        if (string.IsNullOrEmpty(systemPrompt)) return "empty";
        var bytes = System.Text.Encoding.UTF8.GetBytes(systemPrompt);
        var hash = System.Security.Cryptography.SHA1.HashData(bytes);
        // 8 hex chars достаточно для уникальности между версиями промпта.
        return Convert.ToHexString(hash, 0, 4).ToLowerInvariant();
    }

    private readonly SqliteConnection _conn;
    private readonly string _model;
    private readonly string _promptVersion;
    private readonly object _writeLock = new();
    private bool _disposed;

    public AnalyzeCache(string dbPath, string model, string promptVersion)
    {
        _model = model ?? "";
        _promptVersion = promptVersion ?? "";

        var dir = Path.GetDirectoryName(dbPath);
        if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            Directory.CreateDirectory(dir);

        _conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadWriteCreate");
        _conn.Open();

        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"
CREATE TABLE IF NOT EXISTS ai_cache (
    email TEXT NOT NULL,
    model TEXT NOT NULL,
    prompt_version TEXT NOT NULL,
    name_token TEXT,
    gender TEXT,
    iso TEXT,
    source TEXT,
    match_tier INTEGER,
    signals TEXT,
    added_at INTEGER,
    PRIMARY KEY (email, model, prompt_version)
);
CREATE INDEX IF NOT EXISTS ix_ai_cache_lookup ON ai_cache(email, model, prompt_version);
";
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Прогружает все записи для текущей (model, prompt_version) в память.
    /// Быстрее чем query per email — при 100k emails один SELECT + один hash lookup.
    /// </summary>
    public Dictionary<string, AnalysisResult> LoadAll()
    {
        var dict = new Dictionary<string, AnalysisResult>(StringComparer.OrdinalIgnoreCase);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = @"SELECT email, name_token, gender, iso, source, match_tier, signals
                            FROM ai_cache WHERE model=$m AND prompt_version=$v";
        cmd.Parameters.AddWithValue("$m", _model);
        cmd.Parameters.AddWithValue("$v", _promptVersion);
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            var email = r.GetString(0);
            dict[email] = new AnalysisResult(
                Email: email,
                Gender: r.IsDBNull(2) ? "?" : r.GetString(2),
                Iso: r.IsDBNull(3) ? "?" : r.GetString(3),
                Source: r.IsDBNull(4) ? "cache" : r.GetString(4) + "_cache",
                MatchTier: r.IsDBNull(5) ? 0 : r.GetInt32(5))
            {
                NameToken = r.IsDBNull(1) ? "" : r.GetString(1),
                Signals = r.IsDBNull(6) ? "cache_hit" : r.GetString(6),
            };
        }
        return dict;
    }

    /// <summary>
    /// Сохраняет один AI-результат. Не кэшируем "unknown" (может AI ответит лучше в след. прогоне).
    /// </summary>
    public void Insert(AnalysisResult r)
    {
        if (_disposed) return;
        if (r.Source != "ai" && r.Source != "ai+ai2") return;
        if (r.Gender == "?" && r.Iso == "?") return;

        lock (_writeLock)
        {
            try
            {
                using var cmd = _conn.CreateCommand();
                cmd.CommandText = @"INSERT OR REPLACE INTO ai_cache
                    (email, model, prompt_version, name_token, gender, iso, source, match_tier, signals, added_at)
                    VALUES ($e, $m, $v, $n, $g, $i, $s, $t, $sig, $ts)";
                cmd.Parameters.AddWithValue("$e", r.Email);
                cmd.Parameters.AddWithValue("$m", _model);
                cmd.Parameters.AddWithValue("$v", _promptVersion);
                cmd.Parameters.AddWithValue("$n", r.NameToken ?? "");
                cmd.Parameters.AddWithValue("$g", r.Gender ?? "?");
                cmd.Parameters.AddWithValue("$i", r.Iso ?? "?");
                cmd.Parameters.AddWithValue("$s", r.Source ?? "ai");
                cmd.Parameters.AddWithValue("$t", r.MatchTier);
                cmd.Parameters.AddWithValue("$sig", r.Signals ?? "");
                cmd.Parameters.AddWithValue("$ts", DateTimeOffset.UtcNow.ToUnixTimeSeconds());
                cmd.ExecuteNonQuery();
            }
            catch { /* best-effort — не роняем пайплайн из-за ошибки записи */ }
        }
    }

    /// <summary>Количество записей в кэше для текущей (model, prompt_version).</summary>
    public int CountForCurrent()
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM ai_cache WHERE model=$m AND prompt_version=$v";
        cmd.Parameters.AddWithValue("$m", _model);
        cmd.Parameters.AddWithValue("$v", _promptVersion);
        return Convert.ToInt32(cmd.ExecuteScalar() ?? 0);
    }

    public static string DefaultDbPath()
    {
        var dataDir = Blocklists.FindDataDir();
        return Path.Combine(dataDir, "analyze_cache.db");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _conn.Close(); _conn.Dispose(); } catch { }
    }
}
