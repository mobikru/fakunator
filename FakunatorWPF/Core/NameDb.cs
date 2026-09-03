using System;
using System.Collections.Generic;
using System.IO;
using Microsoft.Data.Sqlite;

namespace Fakunator.Core;

public record NameLookupResult(string Name, string Gender, string Culture, int Tier, string[] AllCultures);

public class NameDb : IDisposable
{
    private readonly Dictionary<string, NameLookupResult> _exactMap = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, NameLookupResult> _variantMap = new(StringComparer.OrdinalIgnoreCase);
    private bool _disposed;

    public int Count => _exactMap.Count;

    public NameDb(string dbPath)
    {
        if (!File.Exists(dbPath))
            throw new FileNotFoundException($"names.db not found at {dbPath}");

        using var conn = new SqliteConnection($"Data Source={dbPath};Mode=ReadOnly");
        conn.Open();

        // First pass: collect ALL cultures per name_id
        var culturesById = new Dictionary<long, List<string>>();
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name_id, culture_clean FROM name_cultures";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var cult = r.IsDBNull(1) ? "" : r.GetString(1);
                if (!culturesById.TryGetValue(id, out var list))
                    culturesById[id] = list = new List<string>();
                if (!string.IsNullOrEmpty(cult))
                    list.Add(cult);
            }
        }

        // Pick best culture when ambiguous. English идёт первым как самый нейтральный
        // default для международной аудитории (раньше был Russian first — это ломало
        // определение для gmail-баз где Maria/Alex/Michael → всегда RU).
        // Специфичные страны (RU/UA/PL и т.п.) корректно определяются через TLD
        // в Worker (см. AllCultures + TldToIso матчинг), а не через этот приоритет.
        static string PickBest(List<string>? cultures)
        {
            if (cultures == null || cultures.Count == 0) return "";
            if (cultures.Count == 1) return cultures[0];
            string[] preferred = ["English", "Spanish", "German", "French", "Italian",
                "Portuguese", "Polish", "Turkish", "Indian", "Russian", "Ukrainian"];
            foreach (var p in preferred)
                foreach (var c in cultures)
                    if (c.Equals(p, StringComparison.OrdinalIgnoreCase)) return c;
            return cultures[0];
        }

        // Preload names
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT name_id, name_lower, name, gender FROM names";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var id = r.GetInt64(0);
                var key = r.GetString(1);
                if (_exactMap.ContainsKey(key)) continue;
                culturesById.TryGetValue(id, out var allCult);
                var best = PickBest(allCult);
                _exactMap[key] = new NameLookupResult(
                    r.GetString(2),
                    r.IsDBNull(3) ? "?" : r.GetString(3),
                    best,
                    Tier: 1,
                    AllCultures: allCult?.ToArray() ?? Array.Empty<string>());
            }
        }

        // Preload variants
        using (var cmd = conn.CreateCommand())
        {
            cmd.CommandText = "SELECT v.variant_lower, v.name_id, n.name, n.gender FROM name_variants v JOIN names n ON n.name_id = v.name_id";
            using var r = cmd.ExecuteReader();
            while (r.Read())
            {
                var key = r.GetString(0);
                if (_variantMap.ContainsKey(key) || _exactMap.ContainsKey(key)) continue;
                var id = r.GetInt64(1);
                culturesById.TryGetValue(id, out var allCult);
                var best = PickBest(allCult);
                _variantMap[key] = new NameLookupResult(
                    r.GetString(2),
                    r.IsDBNull(3) ? "?" : r.GetString(3),
                    best,
                    Tier: 2,
                    AllCultures: allCult?.ToArray() ?? Array.Empty<string>());
            }
        }
    }

    public NameLookupResult? Lookup(string tokenLower)
    {
        if (string.IsNullOrWhiteSpace(tokenLower)) return null;
        if (_exactMap.TryGetValue(tokenLower, out var exact)) return exact;
        if (_variantMap.TryGetValue(tokenLower, out var variant)) return variant;
        return null;
    }

    public static string? FindDbPath()
    {
        var dataDir = Blocklists.FindDataDir();
        var candidate = Path.Combine(dataDir, "names.db");
        if (File.Exists(candidate)) return candidate;

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var p = Path.Combine(dir.FullName, "data", "names.db");
            if (File.Exists(p)) return p;
            var rel = Path.Combine(dir.FullName, "Release", "data", "names.db");
            if (File.Exists(rel)) return rel;
            var rel2 = Path.Combine(dir.FullName, "Release_v1.5.0", "data", "names.db");
            if (File.Exists(rel2)) return rel2;
            dir = dir.Parent;
        }
        return null;
    }

    public void Dispose()
    {
        if (!_disposed) _disposed = true;
    }
}
