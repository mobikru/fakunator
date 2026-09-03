using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace Fakunator.Core;

public static class EmailHelpers
{
    private static readonly HashSet<string> GmailDomains = new(StringComparer.Ordinal)
    {
        "gmail.com", "googlemail.com"
    };

    // "Голые" имена провайдеров без TLD — то к чему нужно дописать .com/.ru
    private static readonly Dictionary<string, string> BareProviderToDomain = new(StringComparer.OrdinalIgnoreCase)
    {
        ["gmail"]    = "gmail.com",
        ["googlemail"] = "gmail.com",
        ["yahoo"]    = "yahoo.com",
        ["hotmail"]  = "hotmail.com",
        ["outlook"]  = "outlook.com",
        ["live"]     = "live.com",
        ["msn"]      = "msn.com",
        ["icloud"]   = "icloud.com",
        ["aol"]      = "aol.com",
        ["proton"]   = "proton.me",
        ["protonmail"] = "protonmail.com",
        ["mail"]     = "mail.ru",
        ["yandex"]   = "yandex.ru",
        ["rambler"]  = "rambler.ru",
        ["inbox"]    = "inbox.ru",
        ["bk"]       = "bk.ru",
        ["list"]     = "list.ru",
        ["ya"]       = "yandex.ru",
    };

    // gmailcom/yahoocom/hotmailru → gmail.com/yahoo.com/hotmail.ru (точка между provider и TLD)
    private static readonly (string Pattern, string Canonical)[] FusedProviderTld =
    {
        ("gmailcom",    "gmail.com"),
        ("gmailru",     "gmail.ru"),     // дальше уйдёт в typo→gmail.com
        ("yahoocom",    "yahoo.com"),
        ("hotmailcom",  "hotmail.com"),
        ("hotmailru",   "hotmail.ru"),
        ("outlookcom",  "outlook.com"),
        ("mailru",      "mail.ru"),
        ("yandexru",    "yandex.ru"),
        ("yandexcom",   "yandex.com"),
        ("iclooudcom",  "icloud.com"),
        ("icloudcom",   "icloud.com"),
        ("livecom",     "live.com"),
        ("msncom",      "msn.com"),
        ("inboxru",     "inbox.ru"),
        ("ramblerru",   "rambler.ru"),
        ("bkru",        "bk.ru"),
        ("listru",      "list.ru"),
    };

    // Regex для отрезания хвостового мусора после ".tld"
    private static readonly Regex TrailingJunkRe = new(
        @"^([a-z0-9._+\-]+@[a-z0-9.\-]+\.(?:com|ru|net|org|by|kz|ua|me|fr|de|it|es|uk|jp|cn|kr|in|br|mx|co\.uk|co\.in|co\.jp|com\.ua|com\.br|com\.au|com\.tr))[^a-z0-9.\-].*$",
        RegexOptions.Compiled);

    /// <summary>
    /// Глубокая нормализация: пробует починить часто встречающийся мусор.
    ///   1. BOM/trim/lowercase/mailto/&lt;&gt; (как раньше)
    ///   2. Удалить пробелы внутри email
    ///   3. Заменить ',' на '.' в домене
    ///   4. Дедуп подряд идущих точек
    ///   5. Двойной @email@email → отрезать первое
    ///   6. Хвостовой мусор после .com/.ru → отрезать
    ///   7. gmailcom/yahoocom/mailru без точки → gmail.com и т.п.
    ///   8. @gmail без TLD → @gmail.com
    /// </summary>
    public static string Normalize(string raw)
    {
        if (string.IsNullOrEmpty(raw)) return "";

        var s = raw.Trim().TrimStart('﻿').ToLowerInvariant();
        if (s.StartsWith("mailto:", StringComparison.Ordinal))
            s = s[7..];
        if (s.StartsWith('<') && s.EndsWith('>'))
            s = s[1..^1].Trim();

        // 1. Удалить все пробелы и табы внутри
        if (s.Contains(' ') || s.Contains('\t'))
            s = s.Replace(" ", "").Replace("\t", "");

        // 2. Если нет '@' — возвращаем как есть (классификатор пометит invalid)
        var atIdx = s.IndexOf('@');
        if (atIdx < 0 || atIdx == 0) return s;

        // 3. Дубль вида user@dom.com@something — отрезать всё после первого валидного домена
        // (определяем валидный домен как часть, не содержащую '@' и заканчивающуюся на известный TLD)
        var trailingFixed = TrailingJunkRe.Replace(s, "$1");
        if (trailingFixed != s) s = trailingFixed;

        // 4. Двойной @email@email или @dom@dom — оставляем первое полное вхождение
        int firstAt = s.IndexOf('@');
        int secondAt = s.IndexOf('@', firstAt + 1);
        if (secondAt >= 0)
        {
            // Если после второго @ повторяется тот же домен или похоже — отрезаем
            var domAfterFirst = s[(firstAt + 1)..secondAt];
            var rest = s[(secondAt + 1)..];
            // Если rest начинается с того же дома или с известного TLD — это дубль
            if (rest.StartsWith(domAfterFirst, StringComparison.Ordinal) ||
                rest.Contains('.'))
            {
                s = s[..secondAt];
            }
        }

        // 5. Разделить на local и domain для пост-обработки
        atIdx = s.IndexOf('@');
        if (atIdx < 0 || atIdx == s.Length - 1) return s;

        var local = s[..atIdx];
        var domain = s[(atIdx + 1)..];

        // 6. Заменить запятые на точки в домене, схлопнуть подряд идущие точки и пробелы
        if (domain.Contains(','))
            domain = domain.Replace(',', '.');
        while (domain.Contains(".."))
            domain = domain.Replace("..", ".");
        domain = domain.Trim('.');

        // 7. Голый домен без точки: @gmail / @yahoo / @mail → @gmail.com / @yahoo.com / @mail.ru
        if (!domain.Contains('.'))
        {
            if (BareProviderToDomain.TryGetValue(domain, out var full))
                domain = full;
            else
            {
                // Слитые варианты: gmailcom/mailru/yahoocom → gmail.com/mail.ru/yahoo.com
                foreach (var (pat, can) in FusedProviderTld)
                {
                    if (domain == pat) { domain = can; break; }
                }
            }
        }

        // 8. Также ловим случай "gmailcom" даже когда есть посторонние точки в local
        if (!domain.Contains('.'))
        {
            foreach (var (pat, can) in FusedProviderTld)
            {
                if (domain == pat) { domain = can; break; }
            }
        }

        return $"{local}@{domain}";
    }

    /// <summary>
    /// Canonical form for deduplication. Strips +suffix from local part.
    /// If gmailStrict and domain is gmail/googlemail, also removes dots.
    /// Exact port of Python canonical_for_dedup().
    /// </summary>
    public static string CanonicalForDedup(string email, bool gmailStrict)
    {
        var atIdx = email.LastIndexOf('@');
        if (atIdx < 0)
            return email;

        var local = email[..atIdx];
        var domain = email[(atIdx + 1)..];

        var plusIdx = local.IndexOf('+');
        if (plusIdx >= 0)
            local = local[..plusIdx];

        if (gmailStrict && GmailDomains.Contains(domain))
            local = local.Replace(".", "");

        return $"{local}@{domain}";
    }

    /// <summary>
    /// Fast line counter: reads 64 KB buffers, counts newline bytes.
    /// Adds 1 if file does not end with newline.
    /// </summary>
    public static int CountLines(string filePath)
    {
        const int bufferSize = 65536;
        int count = 0;
        byte lastByte = 0;

        using (var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, bufferSize))
        {
            var buffer = new byte[bufferSize];
            int bytesRead;
            while ((bytesRead = fs.Read(buffer, 0, buffer.Length)) > 0)
            {
                for (int i = 0; i < bytesRead; i++)
                {
                    if (buffer[i] == (byte)'\n')
                        count++;
                }
                lastByte = buffer[bytesRead - 1];
            }
        }

        // If the file is not empty and doesn't end with newline, count the last line
        if (lastByte != 0 && lastByte != (byte)'\n')
            count++;

        return count;
    }
}
