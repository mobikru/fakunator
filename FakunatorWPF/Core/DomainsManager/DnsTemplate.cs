using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Text.RegularExpressions;

namespace Fakunator.Core.DomainsManager;

/// <summary>
/// Шаблон DNS-записей — сохраняется в config.json и применяется одним кликом
/// ко всем DNS-записям указанного домена (через IspApiClient.UpsertRecordAsync).
/// Плейсхолдеры вида {domain}/{ip}/{mail_server}/… заменяются при применении.
/// </summary>
public class DnsTemplate
{
    public string Name { get; set; } = "";
    public string Description { get; set; } = "";
    public List<DnsTemplateRecord> Records { get; set; } = new();

    /// <summary>true для встроенных пресетов — их нельзя удалить/редактировать, только клонировать.</summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public bool IsBuiltin { get; set; }
}

/// <summary>Одна запись в шаблоне. Value/Name могут содержать плейсхолдеры {key}.</summary>
public class DnsTemplateRecord
{
    public string Type { get; set; } = "A";
    public string Name { get; set; } = "@";
    public string Value { get; set; } = "";
    public int Ttl { get; set; } = 3600;
    public int? Priority { get; set; }

    /// <summary>Заменяет плейсхолдеры {domain} и т.п. на значения из словаря.</summary>
    public string ResolveName(IReadOnlyDictionary<string, string> vars) => Substitute(Name, vars);
    public string ResolveValue(IReadOnlyDictionary<string, string> vars) => Substitute(Value, vars);

    private static string Substitute(string template, IReadOnlyDictionary<string, string> vars)
    {
        if (string.IsNullOrEmpty(template)) return template;
        return Regex.Replace(template, @"\{([a-z_][a-z0-9_]*)\}", m =>
        {
            var key = m.Groups[1].Value;
            return vars.TryGetValue(key, out var v) ? v : m.Value;
        }, RegexOptions.IgnoreCase);
    }

    /// <summary>Извлекает все плейсхолдеры {key} из name+value.</summary>
    public IEnumerable<string> ExtractPlaceholders()
    {
        foreach (Match m in Regex.Matches(Name + " " + Value, @"\{([a-z_][a-z0-9_]*)\}", RegexOptions.IgnoreCase))
            yield return m.Groups[1].Value.ToLowerInvariant();
    }
}

/// <summary>Результат валидации одной записи шаблона.</summary>
public record DnsRecordValidation(bool Ok, string? Error = null);

public static class DnsTemplateValidator
{
    private static readonly HashSet<string> Types = new(StringComparer.OrdinalIgnoreCase)
        { "A", "AAAA", "CNAME", "MX", "TXT", "NS", "SRV" };

    /// <summary>Проверяет одну запись шаблона (без резолвинга плейсхолдеров).</summary>
    public static DnsRecordValidation Validate(DnsTemplateRecord r)
    {
        if (string.IsNullOrWhiteSpace(r.Type) || !Types.Contains(r.Type))
            return new(false, $"неизвестный тип «{r.Type}»");
        if (string.IsNullOrWhiteSpace(r.Name))
            return new(false, "пустое имя (используй @ для корня)");
        if (string.IsNullOrWhiteSpace(r.Value))
            return new(false, "пустое значение");
        if (r.Ttl <= 0)
            return new(false, "TTL должен быть > 0");
        if (r.Type.Equals("MX", StringComparison.OrdinalIgnoreCase) && (r.Priority == null || r.Priority <= 0))
            return new(false, "MX требует приоритет > 0");
        return new(true);
    }

    /// <summary>Проверка уже с подстановленными значениями (после resolve плейсхолдеров).</summary>
    public static DnsRecordValidation ValidateResolved(string type, string name, string value)
    {
        // Наличие незакрытых плейсхолдеров = ошибка
        if (Regex.IsMatch(name + " " + value, @"\{[a-z_][a-z0-9_]*\}", RegexOptions.IgnoreCase))
            return new(false, "остался незаполненный плейсхолдер {…}");
        // Формат значения по типу
        switch (type.ToUpperInvariant())
        {
            case "A":
                if (!IPAddress.TryParse(value, out var ip4) || ip4.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    return new(false, "A требует IPv4 (например 1.2.3.4)");
                break;
            case "AAAA":
                if (!IPAddress.TryParse(value, out var ip6) || ip6.AddressFamily != System.Net.Sockets.AddressFamily.InterNetworkV6)
                    return new(false, "AAAA требует IPv6");
                break;
            case "CNAME":
            case "MX":
            case "NS":
                // хостнейм — минимальная проверка: содержит точку, без пробелов
                if (value.Contains(' ') || !value.Contains('.'))
                    return new(false, $"{type} требует hostname (например mail.example.com)");
                break;
            case "TXT":
                // TXT практически всё принимает; в кавычки не оборачиваем
                if (value.StartsWith("\"") && value.EndsWith("\""))
                    return new(false, "не оборачивай TXT в кавычки — ISPmanager сам добавит");
                break;
            case "SRV":
                // _service._proto → hostname[:port]
                if (!value.Contains(' '))
                    return new(false, "SRV: «weight port target», например «10 5 443 sipserver.example.com»");
                break;
        }
        return new(true);
    }
}

/// <summary>Встроенные пресеты — всегда доступны, не сериализуются в config.</summary>
public static class DnsTemplatePresets
{
    public static IReadOnlyList<DnsTemplate> All { get; } = new List<DnsTemplate>
    {
        new()
        {
            Name = "Mail.ru стандарт",
            Description = "MX + SPF + DMARC для рассылок через mail.ru",
            IsBuiltin = true,
            Records = new()
            {
                new() { Type = "MX",  Name = "@", Value = "emx.mail.ru", Ttl = 3600, Priority = 10 },
                new() { Type = "TXT", Name = "@", Value = "v=spf1 include:_spf.mail.ru ~all", Ttl = 3600 },
                new() { Type = "TXT", Name = "_dmarc", Value = "v=DMARC1; p=none; rua=mailto:postmaster@{domain}", Ttl = 3600 },
            },
        },
        new()
        {
            Name = "Yandex 360",
            Description = "MX + SPF + DKIM-заготовка для Яндекс.Почты",
            IsBuiltin = true,
            Records = new()
            {
                new() { Type = "MX",  Name = "@", Value = "mx.yandex.net", Ttl = 3600, Priority = 10 },
                new() { Type = "TXT", Name = "@", Value = "v=spf1 include:_spf.yandex.ru ~all", Ttl = 3600 },
                new() { Type = "TXT", Name = "_dmarc", Value = "v=DMARC1; p=none; rua=mailto:postmaster@{domain}", Ttl = 3600 },
                new() { Type = "CNAME", Name = "mail", Value = "domain.mail.yandex.net", Ttl = 3600 },
            },
        },
        new()
        {
            Name = "Google Workspace",
            Description = "5×MX (Google) + SPF",
            IsBuiltin = true,
            Records = new()
            {
                new() { Type = "MX",  Name = "@", Value = "aspmx.l.google.com",       Ttl = 3600, Priority = 1 },
                new() { Type = "MX",  Name = "@", Value = "alt1.aspmx.l.google.com",  Ttl = 3600, Priority = 5 },
                new() { Type = "MX",  Name = "@", Value = "alt2.aspmx.l.google.com",  Ttl = 3600, Priority = 5 },
                new() { Type = "MX",  Name = "@", Value = "alt3.aspmx.l.google.com",  Ttl = 3600, Priority = 10 },
                new() { Type = "MX",  Name = "@", Value = "alt4.aspmx.l.google.com",  Ttl = 3600, Priority = 10 },
                new() { Type = "TXT", Name = "@", Value = "v=spf1 include:_spf.google.com ~all", Ttl = 3600 },
            },
        },
        new()
        {
            Name = "Сайт: A + www CNAME",
            Description = "IP на корень + www как алиас",
            IsBuiltin = true,
            Records = new()
            {
                new() { Type = "A",     Name = "@",   Value = "{ip}", Ttl = 3600 },
                new() { Type = "CNAME", Name = "www", Value = "{domain}", Ttl = 3600 },
            },
        },
    };
}
