using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net;
using System.Text.RegularExpressions;
using System.Threading.Tasks;

namespace Fakunator.Core;

public class Classifier
{
    private readonly Blocklists _lists;
    private readonly HashSet<string> _enabled;
    private readonly bool _gmailStrict;
    private readonly SpamhausDbl? _dbl;

    // RFC-5322-ish syntax check — exact port from Python EMAIL_RE
    private static readonly Regex EmailRe = new(
        @"^[a-z0-9](?:[a-z0-9._+\-]{0,62}[a-z0-9])?@(?=.{1,253}$)(?:[a-z0-9](?:[a-z0-9\-]{0,61}[a-z0-9])?\.)+[a-z]{2,24}$",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> ReservedTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        "test", "example", "invalid", "localhost", "local", "lan", "internal"
    };

    private static readonly HashSet<string> ReservedDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "example.com", "example.net", "example.org", "test.com", "localhost.localdomain"
    };

    // Suspicious patterns: very long random-looking local parts or alternating letter/digit runs
    private static readonly Regex SuspiciousLocalRe = new(
        @"(^[a-z0-9]{30,}$|[a-z]{1,2}\d{5,}[a-z]{1,2}\d{5,})",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    // All-digits 8+ local part
    private static readonly Regex DigitsOnlyLocalRe = new(
        @"^[0-9]{8,}$",
        RegexOptions.Compiled);

    // Western providers where digits-only local IS suspicious
    private static readonly HashSet<string> DigitsTrapDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        "gmail.com", "googlemail.com",
        "yahoo.com", "ymail.com", "rocketmail.com",
        "hotmail.com", "outlook.com", "live.com", "msn.com",
        "icloud.com", "me.com", "mac.com",
        "aol.com", "aim.com",
        "protonmail.com", "proton.me",
    };

    // Hardcoded "always free" providers — supplements free_providers.txt
    // (GitHub list misses regional zones like yandex.kz/by, mail.ru aliases, etc).
    private static readonly HashSet<string> AlwaysFreeProviders = new(StringComparer.OrdinalIgnoreCase)
    {
        // Yandex family (RU + CIS)
        "yandex.ru", "yandex.com", "yandex.by", "yandex.kz", "yandex.ua",
        "yandex.com.tr", "yandex.com.am", "yandex.fr", "yandex.az",
        "ya.ru", "yandex.kg", "yandex.tj", "yandex.tm", "yandex.uz",
        // Mail.ru family
        "mail.ru", "inbox.ru", "list.ru", "bk.ru", "internet.ru",
        // Rambler
        "rambler.ru", "lenta.ru", "autorambler.ru", "ro.ru", "myrambler.ru",
        // RU other
        "qip.ru", "pochta.ru", "narod.ru",
        // UA
        "ukr.net", "i.ua", "meta.ua", "bigmir.net", "online.ua", "email.ua",
        // BY
        "tut.by", "tut.com",
        // KZ
        "nur.kz", "list.kz",
        // Major global (sometimes missing from list)
        "gmail.com", "googlemail.com",
        "yahoo.com", "ymail.com", "rocketmail.com",
        "hotmail.com", "outlook.com", "live.com", "msn.com",
        "icloud.com", "me.com", "mac.com",
        "aol.com", "aim.com",
        "protonmail.com", "proton.me", "pm.me",
        "gmx.com", "gmx.net", "gmx.de", "gmx.at", "gmx.ch",
        "web.de", "t-online.de", "freenet.de",
        "mail.com", "fastmail.com",
        // Yahoo regional
        "yahoo.co.uk", "yahoo.fr", "yahoo.de", "yahoo.es", "yahoo.it",
        "yahoo.com.br", "yahoo.com.ar", "yahoo.com.mx", "yahoo.com.au",
        "yahoo.ca", "yahoo.co.in", "yahoo.co.jp", "yahoo.co.id",
        // Microsoft regional
        "hotmail.co.uk", "hotmail.fr", "hotmail.de", "hotmail.es", "hotmail.it",
        "hotmail.com.br", "hotmail.com.mx", "hotmail.com.ar",
        "live.co.uk", "live.fr", "live.de", "live.it", "live.com.au",
        "outlook.co.uk", "outlook.fr", "outlook.de", "outlook.com.br",
    };

    public Classifier(Blocklists lists, HashSet<string> enabledFilters, bool gmailStrict,
        bool useSpamhausDbl = false)
    {
        _lists = lists;
        _enabled = enabledFilters;
        _gmailStrict = gmailStrict;
        _dbl = useSpamhausDbl ? new SpamhausDbl() : null;
    }

    /// <summary>
    /// Classify a normalized email. Returns a Verdict with category, note, provider, and suggested correction.
    /// Priority chain exactly matches the Python classifier.
    /// Note: duplicate detection is handled by the worker (dedup set), not here.
    /// </summary>
    public Verdict Classify(string email)
    {
        // 1. Invalid — regex mismatch
        if (!EmailRe.IsMatch(email))
            return new Verdict("invalid", "regex mismatch");

        var atIdx = email.LastIndexOf('@');
        var local = email[..atIdx];
        var domain = email[(atIdx + 1)..];
        var dotIdx = domain.LastIndexOf('.');
        var tld = dotIdx >= 0 ? domain[(dotIdx + 1)..] : domain;

        // 2. Reserved
        if (ReservedTlds.Contains(tld) || ReservedDomains.Contains(domain))
            return new Verdict("reserved", $"reserved: {domain}");

        // Allow-list: skip domain-level disposable/trap, NOT the whole chain
        bool inAllow = _lists.AllowDomains.Contains(domain);

        // 3. Disposable (PRE-typo) — иначе фьюзер ловит yopmail.com → hotmail.com и т.п.
        // Disposable домены реальные, имеют MX, их нельзя "лечить" коррекцией опечаток.
        if (_enabled.Contains("disposable") && !inAllow)
        {
            if (_lists.Disposable.Contains(domain))
                return new Verdict("disposable", $"disposable: {domain}");

            foreach (var suffix in _lists.DisposableWildcards)
            {
                if (string.Equals(domain, suffix, StringComparison.OrdinalIgnoreCase) ||
                    domain.EndsWith("." + suffix, StringComparison.OrdinalIgnoreCase))
                    return new Verdict("disposable", $"disposable wildcard: *.{suffix}");
            }
        }

        // 4. Typo correction — static map → digits-before-domain → heuristic fuzzy fix
        if (_enabled.Contains("typo"))
        {
            if (_lists.TypoMap.TryGetValue(domain, out var correct))
                return new Verdict("typo", $"{domain} → {correct}", Suggested: $"{local}@{correct}");

            var digitsFix = TypoFixer.TryFixDigitsBeforeDomain(local, domain);
            if (digitsFix != null)
                return new Verdict("typo",
                    $"{email} → {digitsFix.Local}@{digitsFix.Domain} ({digitsFix.Reason})",
                    Suggested: $"{digitsFix.Local}@{digitsFix.Domain}");

            var fuzzyFix = TypoFixer.TryFix(domain);
            if (fuzzyFix != null && !string.Equals(fuzzyFix, domain, StringComparison.OrdinalIgnoreCase))
                return new Verdict("typo", $"{domain} → {fuzzyFix} (fuzzy)", Suggested: $"{local}@{fuzzyFix}");
        }

        // 5. Trap heuristics
        if (_enabled.Contains("trap"))
        {
            // Domain-level trap blacklist (skipped if allow-listed)
            if (!inAllow && _lists.TrapDomains.Contains(domain))
                return new Verdict("trap", $"trap domain: {domain}");

            // Local-part heuristics ALWAYS apply
            var bareLocal = local;
            var plusIdx = local.IndexOf('+');
            if (plusIdx >= 0)
                bareLocal = local[..plusIdx];

            foreach (var kw in _lists.TrapKeywords)
            {
                if (bareLocal.Contains(kw, StringComparison.OrdinalIgnoreCase))
                    return new Verdict("trap", $"trap keyword: {kw}");
            }

            if (SuspiciousLocalRe.IsMatch(local))
                return new Verdict("trap", "suspicious local part");

            // Spamhaus DBL online check (only when enabled, allow-list overrides)
            if (_dbl != null && !inAllow && _dbl.IsListed(domain))
                return new Verdict("trap", $"spamhaus dbl: {domain}");
        }

        // 6. Role-based
        if (_enabled.Contains("role"))
        {
            var bareLocal = local;
            var plusIdx = local.IndexOf('+');
            if (plusIdx >= 0)
                bareLocal = local[..plusIdx];

            if (_lists.RolePrefixes.Contains(bareLocal))
                return new Verdict("role", $"role: {bareLocal}@");
        }

        // 7. Corporate (not in free providers).
        // Источники "free": data/free_providers.txt + hardcoded AlwaysFreeProviders
        //                 + data/email_providers.json (ProviderRegistry, ред. пользователем).
        if (_enabled.Contains("corporate")
            && !_lists.FreeProviders.Contains(domain)
            && !AlwaysFreeProviders.Contains(domain)
            && !ProviderRegistry.FreeDomains.Contains(domain))
            return new Verdict("corporate", $"corporate domain: {domain}");

        // 8. Clean — detect provider
        var provider = Tokens.DomainToProvider.TryGetValue(domain, out var p) ? p : "other";
        return new Verdict("clean", "", Provider: provider);
    }
}

/// <summary>
/// Online Spamhaus DBL check via DNS.
/// Resolves <c>&lt;domain&gt;.dbl.spamhaus.org</c>; a successful A-record lookup
/// indicates the domain is listed.  Results are cached per-domain.
/// Free tier: up to ~100k queries/day for low-volume non-commercial use.
/// Port of Python <c>_SpamhausDBL</c> in <c>fakunator/classifier.py</c>.
/// </summary>
public sealed class SpamhausDbl
{
    private static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(2500);
    private readonly ConcurrentDictionary<string, bool> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Returns true if <paramref name="domain"/> resolves under the DBL zone.
    /// Synchronous wrapper around <see cref="Dns.GetHostAddressesAsync(string)"/>;
    /// uses a 2.5s timeout and per-domain caching.
    /// </summary>
    public bool IsListed(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return false;
        if (_cache.TryGetValue(domain, out var cached)) return cached;

        bool listed;
        try
        {
            var host = $"{domain}.dbl.spamhaus.org";
            var task = Dns.GetHostAddressesAsync(host);
            if (task.Wait(Timeout) && task.IsCompletedSuccessfully)
            {
                listed = task.Result is { Length: > 0 };
            }
            else
            {
                listed = false;
            }
        }
        catch
        {
            // NXDOMAIN, timeout, network failure → treat as not listed
            listed = false;
        }

        _cache[domain] = listed;
        return listed;
    }
}
