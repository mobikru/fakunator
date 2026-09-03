using System;
using System.Collections.Generic;
using System.IO;

namespace Fakunator.Core;

public class Blocklists
{
    public HashSet<string> FreeProviders { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> Disposable { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> DisposableWildcards { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> AllowDomains { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> RolePrefixes { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> TrapKeywords { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public HashSet<string> TrapDomains { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, string> TypoMap { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Bundled default typo corrections, same as Python DEFAULT_TYPO_MAP.
    /// </summary>
    private static readonly Dictionary<string, string> DefaultTypoMap = new(StringComparer.OrdinalIgnoreCase)
    {
        // Gmail
        ["gmial.com"] = "gmail.com", ["gmai.com"] = "gmail.com", ["gnail.com"] = "gmail.com",
        ["gamil.com"] = "gmail.com", ["gmali.com"] = "gmail.com", ["gmaill.com"] = "gmail.com",
        ["gmsil.com"] = "gmail.com", ["gmail.co"] = "gmail.com", ["gmail.cm"] = "gmail.com",
        ["gmail.con"] = "gmail.com", ["gmail.om"] = "gmail.com", ["gmaol.com"] = "gmail.com",
        ["gmail.comm"] = "gmail.com", ["gmail.coom"] = "gmail.com",
        // Gmail TLD swaps (русские пользователи часто пишут .ru)
        ["gmail.ru"] = "gmail.com", ["gmail.net"] = "gmail.com", ["gmail.org"] = "gmail.com",
        ["gmail.rom"] = "gmail.com",
        // Yahoo
        ["yaho.com"] = "yahoo.com", ["yahooo.com"] = "yahoo.com", ["yahoo.cm"] = "yahoo.com",
        ["yahoo.co"] = "yahoo.com", ["yahoo.con"] = "yahoo.com", ["yhaoo.com"] = "yahoo.com",
        ["yahho.com"] = "yahoo.com", ["ahoo.com"] = "yahoo.com", ["yahool.com"] = "yahoo.com",
        ["yahoo.ru"] = "yahoo.com",
        // Hotmail / Microsoft
        ["hotnail.com"] = "hotmail.com", ["hotmial.com"] = "hotmail.com",
        ["hotmaill.com"] = "hotmail.com", ["hotmail.co"] = "hotmail.com",
        ["hotmail.cm"] = "hotmail.com", ["hotmal.com"] = "hotmail.com",
        ["hotmail.ru"] = "hotmail.com",
        // Outlook
        ["outlok.com"] = "outlook.com", ["outloook.com"] = "outlook.com",
        ["outlook.cm"] = "outlook.com", ["outloo.com"] = "outlook.com",
        ["outlook.ru"] = "outlook.com",
        // Apple
        ["icloud.co"] = "icloud.com", ["iclod.com"] = "icloud.com", ["icoud.com"] = "icloud.com",
        ["iclould.com"] = "icloud.com", ["me.co"] = "me.com",
        ["icloud.ru"] = "icloud.com",
        // Russian
        ["mail.r"] = "mail.ru", ["mail.tu"] = "mail.ru", ["yandexd.ru"] = "yandex.ru",
        ["yandes.ru"] = "yandex.ru", ["yndex.ru"] = "yandex.ru", ["yandx.ru"] = "yandex.ru",
        ["rambler.r"] = "rambler.ru",
        // mail.com / yandex.com — реальные домены, не правим (mail.com тоже валидный)
        // Точечные опечатки русских провайдеров (DL > 2 от канонического)
        ["indox.ru"] = "inbox.ru", ["inox.ru"] = "inbox.ru", ["inbx.ru"] = "inbox.ru",
        ["bk.ry"] = "bk.ru", ["bk.tu"] = "bk.ru", ["bk.com"] = "bk.ru",
        ["list.tu"] = "list.ru",
        ["yndexs.ru"] = "yandex.ru", ["yndx.ru"] = "yandex.ru", ["yandes.com"] = "yandex.ru",
        // Усечения mail.ru
        ["mial.ru"] = "mail.ru", ["maill.ru"] = "mail.ru", ["maul.ru"] = "mail.ru",
    };

    /// <summary>
    /// Load blocklists from the data directory. Reads 6 text files.
    /// If a file is missing, returns an empty set (no crash).
    /// </summary>
    public static Blocklists Load(string dataDir)
    {
        var lists = new Blocklists();

        lists.FreeProviders = LoadList(Path.Combine(dataDir, "free_providers.txt"));
        lists.AllowDomains = LoadList(Path.Combine(dataDir, "allow_domains.txt"));
        lists.RolePrefixes = LoadList(Path.Combine(dataDir, "role_prefixes.txt"));

        // Disposable: split *.something into wildcards
        LoadDisposable(Path.Combine(dataDir, "disposable_domains.txt"), lists);

        // Spamtrap: split into [LOCAL_KEYWORDS] and [DOMAINS] sections
        LoadSpamtrap(Path.Combine(dataDir, "spamtrap_patterns.txt"), lists);

        // Typo corrections
        LoadTypoMap(Path.Combine(dataDir, "typo_corrections.txt"), lists);

        return lists;
    }

    private static HashSet<string> LoadList(string path)
    {
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path))
            return set;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith('['))
                continue;
            set.Add(line);
        }
        return set;
    }

    private static void LoadDisposable(string path, Blocklists lists)
    {
        if (!File.Exists(path))
            return;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#') || line.StartsWith('['))
                continue;

            if (line.StartsWith("*."))
            {
                lists.DisposableWildcards.Add(line[2..]);
            }
            else if (line.StartsWith('.'))
            {
                lists.DisposableWildcards.Add(line[1..]);
            }
            else
            {
                lists.Disposable.Add(line);
            }
        }
    }

    private static void LoadSpamtrap(string path, Blocklists lists)
    {
        if (!File.Exists(path))
            return;

        string? section = null;
        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            if (line.StartsWith('[') && line.EndsWith(']'))
            {
                section = line.Trim('[', ']').ToUpperInvariant();
                continue;
            }

            var value = line.ToLowerInvariant();
            if (section == "LOCAL_KEYWORDS")
                lists.TrapKeywords.Add(value);
            else if (section == "DOMAINS")
                lists.TrapDomains.Add(value);
        }
    }

    private static void LoadTypoMap(string path, Blocklists lists)
    {
        // Start with defaults
        foreach (var kv in DefaultTypoMap)
            lists.TypoMap[kv.Key] = kv.Value;

        if (!File.Exists(path))
            return;

        foreach (var raw in File.ReadLines(path))
        {
            var line = raw.Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(line) || line.StartsWith('#'))
                continue;

            // Support both "wrong correct" (space) and "wrong=correct" (equals) formats
            string[] parts;
            if (line.Contains('='))
                parts = line.Split('=', 2);
            else
                parts = line.Split(' ', 2, StringSplitOptions.RemoveEmptyEntries);

            if (parts.Length >= 2)
                lists.TypoMap[parts[0].Trim()] = parts[1].Trim();
        }
    }

    /// <summary>
    /// Find the data/ folder. Tries several paths relative to the exe.
    /// Returns the first one containing free_providers.txt.
    /// </summary>
    public static string FindDataDir()
    {
        // Walk up from exe dir looking for data/free_providers.txt
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (int i = 0; i < 8 && dir != null; i++)
        {
            var dataDir = Path.Combine(dir.FullName, "data");
            if (File.Exists(Path.Combine(dataDir, "free_providers.txt")))
                return dataDir;

            // Also check Release/data and Release_v1.5.0/data at this level
            var releaseData = Path.Combine(dir.FullName, "Release", "data");
            if (File.Exists(Path.Combine(releaseData, "free_providers.txt")))
                return releaseData;

            var release150 = Path.Combine(dir.FullName, "Release_v1.5.0", "data");
            if (File.Exists(Path.Combine(release150, "free_providers.txt")))
                return release150;

            dir = dir.Parent;
        }

        return Path.Combine(AppContext.BaseDirectory, "data");
    }
}
