using System;
using System.Collections.Generic;

namespace Fakunator.Core;

/// <summary>
/// Эвристическая коррекция доменов почтовых провайдеров.
/// Срабатывает когда статичная карта <c>TypoMap</c> не нашла совпадение.
///
/// Логика:
///   1. Берём часть домена до первой точки (например, "gmail" из "gmail.com.ru")
///   2. Точное совпадение с известным провайдером → подмена
///   3. Префикс начинается с известного провайдера → подмена (ловит "gmail368", "gmailcom", "gmail")
///   4. Levenshtein distance ≤ 1 → подмена (ловит "gmaill", "gmaile", "gmial")
///   5. Провайдеры с коротким именем (например, "mail") — только точное совпадение,
///      иначе ломаем mail.com / postmail.com и т.п.
///
/// Примеры исправлений:
///   gmail.cim, gmail.kom, gmail.com.com, gmail.com.ru, gmail.coma → gmail.com
///   gmail368.com, gmailcom.ru → gmail.com
///   gmaill.ru, gmaile.com → gmail.com
/// </summary>
public static class TypoFixer
{
    // Провайдеры с уникальным префиксом (≥4 символов, нет коллизий с реальными доменами)
    private static readonly (string Prefix, string Canonical)[] SafePrefixProviders =
    {
        ("gmail",   "gmail.com"),
        ("yahoo",   "yahoo.com"),
        ("hotmail", "hotmail.com"),
        ("outlook", "outlook.com"),
        ("icloud",  "icloud.com"),
        ("yandex",  "yandex.ru"),
        ("rambler", "rambler.ru"),
        ("protonmail", "protonmail.com"),
    };

    // Провайдеры с короткими/двусмысленными именами — только точное совпадение firstPart.
    // ВНИМАНИЕ: убрал inbox/list/bk — у них есть реальные регионалки (inbox.lv, list.kz и т.п.),
    // и мы рискуем заменить их на .ru-версию. "mail" оставлен потому что mail.com тоже в whitelist —
    // он не пройдёт сюда; mail.<typo_tld> логично исправлять на mail.ru.
    private static readonly (string Name, string Canonical)[] ExactOnlyProviders =
    {
        ("mail", "mail.ru"),
    };

    /// <summary>
    /// Реально существующие домены крупных провайдеров (включая регионалки yandex.by/kz/com,
    /// yahoo.co.uk, hotmail.de и т.п.). Эти домены НИКОГДА не правятся — иначе мы будем ломать
    /// валидные адреса пользователей не из РФ.
    /// </summary>
    private static readonly HashSet<string> KnownValidDomains = new(StringComparer.OrdinalIgnoreCase)
    {
        // Gmail
        "gmail.com", "googlemail.com",
        // Yandex regional
        "yandex.ru", "yandex.com", "yandex.by", "yandex.kz", "yandex.ua",
        "yandex.com.tr", "yandex.com.am", "yandex.fr", "yandex.az",
        "ya.ru", "yandex.kg", "yandex.tj", "yandex.tm", "yandex.uz",
        // Mail.ru family
        "mail.ru", "inbox.ru", "list.ru", "bk.ru", "internet.ru", "mail.com",
        // Rambler
        "rambler.ru", "lenta.ru", "autorambler.ru", "ro.ru", "myrambler.ru",
        // Yahoo regional
        "yahoo.com", "ymail.com", "rocketmail.com",
        "yahoo.co.uk", "yahoo.fr", "yahoo.de", "yahoo.es", "yahoo.it",
        "yahoo.com.br", "yahoo.com.ar", "yahoo.com.mx", "yahoo.com.au",
        "yahoo.ca", "yahoo.co.in", "yahoo.co.jp", "yahoo.co.id",
        // Microsoft regional
        "hotmail.com", "outlook.com", "live.com", "msn.com",
        "hotmail.co.uk", "hotmail.fr", "hotmail.de", "hotmail.es", "hotmail.it",
        "hotmail.com.br", "hotmail.com.mx", "hotmail.com.ar",
        "live.co.uk", "live.fr", "live.de", "live.it", "live.com.au",
        "outlook.co.uk", "outlook.fr", "outlook.de", "outlook.com.br",
        // Apple
        "icloud.com", "me.com", "mac.com",
        // Proton
        "protonmail.com", "proton.me", "pm.me",
        // AOL
        "aol.com", "aim.com",
        // GMX / Web.de / European
        "gmx.com", "gmx.net", "gmx.de", "gmx.at", "gmx.ch",
        "web.de", "t-online.de", "freenet.de", "fastmail.com",
        // Ukraine
        "ukr.net", "i.ua", "meta.ua", "bigmir.net", "online.ua", "email.ua",
        // Belarus
        "tut.by", "tut.com",
        // Kazakhstan
        "nur.kz", "list.kz",
    };

    /// <summary>Возвращает true если домен — реальный (трогать его нельзя).
    /// Источники истины (по приоритету):
    ///   1. <c>ProviderRegistry</c> (база из data/email_providers.json — пользователь может редактировать)
    ///   2. Встроенный <c>KnownValidDomains</c> (hardcoded fallback)
    ///   3. Эвристика <c>LooksLikeValidRegionalProvider</c> для длинного хвоста
    /// </summary>
    public static bool IsKnownValid(string domain) =>
        ProviderRegistry.AllDomains.Contains(domain) ||
        KnownValidDomains.Contains(domain) ||
        LooksLikeValidRegionalProvider(domain);

    /// <summary>
    /// Названия провайдеров с реальной мульти-TLD сеткой. У Gmail нет регионалок (только gmail.com),
    /// поэтому его сюда не включаем.
    /// </summary>
    private static readonly HashSet<string> MultiTldProviderNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "yandex", "yahoo", "hotmail", "outlook", "live", "msn", "gmx",
    };

    /// <summary>
    /// Допустимые TLD под которыми может существовать регионалка провайдера. Если домен матчит
    /// формат <c>&lt;known_provider&gt;.&lt;valid_tld&gt;</c> — считаем что это легит и не трогаем.
    /// </summary>
    private static readonly HashSet<string> ValidTlds = new(StringComparer.OrdinalIgnoreCase)
    {
        // Country codes
        "ru","by","kz","ua","de","fr","es","it","ch","at","nl","be","pl","cz","sk","hu","ro","bg","rs",
        "hr","si","ba","se","no","dk","fi","gr","tr","jp","cn","kr","in","br","mx","ar","il","ir","sa",
        "au","nz","ie","ca","lv","lt","ee","ge","am","az","vn","th","id","ph","ng","za","eg","ke","tw",
        "hk","sg","my","pt","co","ve","pe","jo","lb","cl",
        // Generic
        "com","org","net",
        // Second-level (co.X, com.X)
        "co.uk","co.in","co.jp","co.kr","co.id","co.nz","co.th","co.za",
        "com.tr","com.au","com.br","com.mx","com.ar","com.tw","com.hk","com.sg","com.ph","com.cn",
        "com.vn","com.my","com.eg","com.sa","com.lb","com.jo","com.co","com.ve","com.pe","com.ua",
    };

    private static bool LooksLikeValidRegionalProvider(string domain)
    {
        var dotIdx = domain.IndexOf('.');
        if (dotIdx <= 0 || dotIdx == domain.Length - 1) return false;
        var name = domain[..dotIdx];
        var tld = domain[(dotIdx + 1)..];
        return MultiTldProviderNames.Contains(name) && ValidTlds.Contains(tld);
    }

    private static readonly string[] AllKnownDomains;

    static TypoFixer()
    {
        var list = new List<string>();
        foreach (var (_, c) in SafePrefixProviders) list.Add(c);
        foreach (var (_, c) in ExactOnlyProviders) list.Add(c);
        AllKnownDomains = list.ToArray();
    }

    /// <summary>
    /// Результат исправления, когда нужно править и local, и domain (например, "user@1986gmail.com" → "user1986@gmail.com").
    /// </summary>
    public record EmailFix(string Local, string Domain, string Reason);

    /// <summary>
    /// Ловит паттерн "цифры написаны после собаки вместо до":
    ///   user@1986gmail.com → user1986@gmail.com
    ///   user1986@1986gmail.com → user1986@gmail.com (digits задублированы)
    /// Возвращает null если домен не начинается с digits + known provider.
    /// </summary>
    public static EmailFix? TryFixDigitsBeforeDomain(string local, string domain)
    {
        if (string.IsNullOrEmpty(domain)) return null;
        if (IsKnownValid(domain)) return null;  // Сам домен валиден — нечего править

        // Извлекаем ведущие цифры из домена
        int digitsLen = 0;
        while (digitsLen < domain.Length && char.IsDigit(domain[digitsLen]))
            digitsLen++;

        if (digitsLen == 0) return null;

        var digits = domain[..digitsLen];
        var withoutDigits = domain[digitsLen..];

        // Оставшаяся после digits часть должна быть валидным доменом
        if (!IsKnownValid(withoutDigits)) return null;

        // Если local уже заканчивается на те же цифры — это дубликат, не дописываем повторно
        if (local.EndsWith(digits, StringComparison.Ordinal))
        {
            return new EmailFix(local, withoutDigits, "digits duplicated before & after @");
        }

        return new EmailFix(local + digits, withoutDigits, "digits moved from domain prefix to local");
    }

    /// <summary>
    /// Пытается распознать опечатку в домене. Возвращает канонический домен или null если уверенности нет.
    /// </summary>
    public static string? TryFix(string domain)
    {
        if (string.IsNullOrWhiteSpace(domain)) return null;

        var lower = domain.ToLowerInvariant();

        // Не трогаем валидные домены: mail.ru, yandex.by, yahoo.co.in, hotmail.ch, …
        if (IsKnownValid(lower)) return null;

        var dotIdx = lower.IndexOf('.');
        var firstPart = dotIdx > 0 ? lower[..dotIdx] : lower;

        if (firstPart.Length < 1) return null;

        foreach (var (prefix, canonical) in SafePrefixProviders)
        {
            // Точное совпадение первой части → "gmail.X" / "gmail.X.Y" / "gmail" → canonical
            if (firstPart == prefix) return canonical;

            // Префикс — ловит gmail368.com, gmailcom.ru, gmail.coma (где первая часть длиннее но начинается с провайдера)
            if (firstPart.Length > prefix.Length && firstPart.StartsWith(prefix, StringComparison.Ordinal))
                return canonical;

            // Damerau-Levenshtein ≤ 2 — ловит транспозиции и 2-substitution опечатки:
            //   gimal/gmile/gaiml/gailm (swap pair) → gmail (DL=1)
            //   gemil/gemal/gmel/gmale (shifts/2 subs) → gmail (DL=2)
            // Применяем только к prefix длиной ≥ 5, иначе короткие имена ловят слишком много.
            if (prefix.Length >= 5 && DamerauLevenshteinAtMostK(firstPart, prefix, 2))
                return canonical;

            // Truncation matchers — для случаев когда пользователь обрезал имя провайдера:
            //   Strict prefix (≥1 символ): g.com, gm.com, gma.com, gmai.com → gmail.com
            //                              y.com, ya.com → yahoo.com
            //                              ho.com, hot.com → hotmail.com
            //   Substring (≥3): ail.com, mai.com → gmail.com (mail.com отсеян IsKnownValid выше)
            //                   ahoo.com → yahoo.com, look.com → outlook.com
            if (firstPart.Length >= 1 && firstPart.Length < prefix.Length
                && prefix.StartsWith(firstPart, StringComparison.Ordinal))
                return canonical;

            if (firstPart.Length >= 3 && firstPart.Length < prefix.Length
                && prefix.Contains(firstPart, StringComparison.Ordinal))
                return canonical;
        }

        foreach (var (name, canonical) in ExactOnlyProviders)
        {
            if (firstPart == name) return canonical;
        }

        return null;
    }

    /// <summary>
    /// Damerau-Levenshtein distance с порогом <paramref name="k"/>. Транспозиция соседних
    /// символов (например, "im"↔"mi" в gimal/gmail) считается одной операцией —
    /// в отличие от классического Левенштейна, где это две замены.
    /// Early-exit когда минимум в текущей строке матрицы превышает k.
    /// </summary>
    private static bool DamerauLevenshteinAtMostK(string a, string b, int k)
    {
        if (a == b) return true;

        int la = a.Length, lb = b.Length;
        if (Math.Abs(la - lb) > k) return false;

        // Стандартное 3-строчное DP с поддержкой транспозиции
        var prevPrev = new int[lb + 1];
        var prev = new int[lb + 1];
        var curr = new int[lb + 1];

        for (int j = 0; j <= lb; j++) prev[j] = j;

        for (int i = 1; i <= la; i++)
        {
            curr[0] = i;
            int rowMin = curr[0];

            for (int j = 1; j <= lb; j++)
            {
                int cost = a[i - 1] == b[j - 1] ? 0 : 1;
                int del = prev[j] + 1;
                int ins = curr[j - 1] + 1;
                int sub = prev[j - 1] + cost;
                int v = Math.Min(Math.Min(del, ins), sub);

                // Transposition (Damerau): a[i-1]a[i-2] swap with b[j-2]b[j-1]
                if (i > 1 && j > 1 && a[i - 1] == b[j - 2] && a[i - 2] == b[j - 1])
                    v = Math.Min(v, prevPrev[j - 2] + cost);

                curr[j] = v;
                if (v < rowMin) rowMin = v;
            }

            if (rowMin > k) return false;  // Early exit

            // Rotate buffers
            var tmp = prevPrev;
            prevPrev = prev;
            prev = curr;
            curr = tmp;
        }

        return prev[lb] <= k;
    }
}
