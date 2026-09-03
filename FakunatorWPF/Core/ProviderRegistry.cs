using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Fakunator.Core;

/// <summary>
/// Загружает базу почтовых провайдеров из <c>data/email_providers.json</c> один раз при первом
/// обращении. Любой домен из <see cref="AllDomains"/> считается легитимным —
/// <see cref="TypoFixer"/> его не правит, <see cref="Classifier"/> не помечает как corporate.
///
/// Пользователь может добавлять новые провайдеры/домены в JSON руками, перезапуск не нужен —
/// см. <see cref="Reload"/> (сейчас программа кеширует на сессию, перезагружается при рестарте).
/// </summary>
public static class ProviderRegistry
{
    public sealed class ProviderEntry
    {
        [JsonPropertyName("name")]
        public string Name { get; set; } = "";
        [JsonPropertyName("canonical")]
        public string Canonical { get; set; } = "";
        [JsonPropertyName("group")]
        public string Group { get; set; } = "free";
        [JsonPropertyName("domains")]
        public List<string> Domains { get; set; } = new();
    }

    private sealed class Root
    {
        [JsonPropertyName("providers")]
        public List<ProviderEntry> Providers { get; set; } = new();
    }

    private static readonly object _lock = new();
    private static HashSet<string>? _allDomains;
    private static HashSet<string>? _freeDomains;
    private static Dictionary<string, string>? _providerNameToCanonical;
    private static List<ProviderEntry>? _providers;

    /// <summary>Все домены из базы (валидные адреса, которые НЕ надо править).</summary>
    public static HashSet<string> AllDomains { get { EnsureLoaded(); return _allDomains!; } }

    /// <summary>Домены с group=free (для классификатора, чтобы не пометить как corporate).</summary>
    public static HashSet<string> FreeDomains { get { EnsureLoaded(); return _freeDomains!; } }

    /// <summary>provider name → canonical domain. Используется TypoFixer для определения цели коррекции.</summary>
    public static IReadOnlyDictionary<string, string> ProviderCanonicals
    {
        get { EnsureLoaded(); return _providerNameToCanonical!; }
    }

    public static IReadOnlyList<ProviderEntry> Providers { get { EnsureLoaded(); return _providers!; } }

    /// <summary>Принудительная перезагрузка из файла (если пользователь руками отредактировал JSON).</summary>
    public static void Reload()
    {
        lock (_lock)
        {
            _allDomains = null;
            _freeDomains = null;
            _providerNameToCanonical = null;
            _providers = null;
            EnsureLoadedUnsafe();
        }
    }

    private static void EnsureLoaded()
    {
        if (_allDomains != null) return;
        lock (_lock)
        {
            if (_allDomains != null) return;
            EnsureLoadedUnsafe();
        }
    }

    private static void EnsureLoadedUnsafe()
    {
        var all = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var free = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var canonicals = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<ProviderEntry>();

        try
        {
            var path = FindProvidersFile();
            if (path != null && File.Exists(path))
            {
                var json = File.ReadAllText(path);
                var root = JsonSerializer.Deserialize<Root>(json);
                if (root?.Providers != null)
                {
                    foreach (var p in root.Providers)
                    {
                        if (string.IsNullOrWhiteSpace(p.Name) || p.Domains == null) continue;
                        list.Add(p);
                        if (!string.IsNullOrWhiteSpace(p.Canonical))
                            canonicals[p.Name] = p.Canonical;
                        foreach (var d in p.Domains)
                        {
                            if (string.IsNullOrWhiteSpace(d)) continue;
                            var dl = d.Trim().ToLowerInvariant();
                            all.Add(dl);
                            if (string.Equals(p.Group, "free", StringComparison.OrdinalIgnoreCase))
                                free.Add(dl);
                        }
                    }
                }
            }
        }
        catch
        {
            // Best effort — если JSON сломан, работаем с пустой базой.
        }

        _allDomains = all;
        _freeDomains = free;
        _providerNameToCanonical = canonicals;
        _providers = list;
    }

    private static string? FindProvidersFile()
    {
        // 1. Рядом с exe: data/email_providers.json
        var exeDir = Paths.ExeDir;
        var candidate = Path.Combine(exeDir, "data", "email_providers.json");
        if (File.Exists(candidate)) return candidate;

        // 2. В data-папке вычисляемой Blocklists.FindDataDir() (dev-окружение, поднимается до проекта)
        try
        {
            var dataDir = Blocklists.FindDataDir();
            candidate = Path.Combine(dataDir, "email_providers.json");
            if (File.Exists(candidate)) return candidate;
        }
        catch { }

        return null;
    }
}
