using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using DnsClient;

namespace Fakunator.Core.DomainScanner;

/// <summary>Компактный health-снимок домена: возраст, срок, RBL-хиты.</summary>
public class DomainHealth
{
    public DateTime? CreatedAt { get; set; }
    public DateTime? ExpiresAt { get; set; }
    public List<string> RblHits { get; set; } = new();
    /// <summary>Когда чекали — для TTL кэша.</summary>
    public DateTime CheckedAt { get; set; } = DateTime.UtcNow;

    /// <summary>Ошибка whois (rate-limit / timeout) — чтобы кэш не долгосрочно фиксировал провал.</summary>
    public string? WhoisError { get; set; }
}

/// <summary>
/// Сервис проверки здоровья домена: WHOIS/RDAP (возраст, expiry) + RBL DNS-лукапы.
/// Работает через кэш в <see cref="DomainDb"/> (таблица domain_health) с TTL.
/// Rate-limited: whois для .ru идёт к whois.tcinet.ru:43, лимит ~10-20 req/sec с одного IP —
/// параллельность зажата семафором.
/// </summary>
public class DomainHealthChecker
{
    private readonly DomainDb _db;
    private readonly HttpClient _http;
    private readonly LookupClient _dns;
    private readonly SemaphoreSlim _whoisSem = new(6);   // .ru whois лимит
    private readonly SemaphoreSlim _rdapSem = new(12);   // HTTP-RDAP лимиты мягче

    // Кэш валиден 30 дней — whois/expiry меняются редко, RBL проверяем чаще если нужно
    public static readonly TimeSpan CacheTtl = TimeSpan.FromDays(30);

    private static readonly (string Name, string Zone)[] RblZones =
    {
        ("Spamhaus", "dbl.spamhaus.org"),
        ("SURBL",    "multi.surbl.org"),
    };

    public DomainHealthChecker(DomainDb db)
    {
        _db = db;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(12) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Fakunator-Health/2.0 (+windows)");
        _http.DefaultRequestHeaders.Accept.ParseAdd("application/rdap+json, application/json;q=0.9");
        _dns = new LookupClient(new LookupClientOptions
        {
            Timeout = TimeSpan.FromSeconds(3),
            UseCache = false,
            Retries = 0,
        });
    }

    /// <summary>
    /// Забирает health из БД если свежий (в пределах TTL). Иначе null — надо вызвать <see cref="EnrichAsync"/>.
    /// </summary>
    public DomainHealth? GetCached(string domain) => _db.GetHealth(domain);

    /// <summary>
    /// Полная проверка: RDAP/whois для возраста+expiry, RBL DNS. Сохраняет результат в БД.
    /// </summary>
    public async Task<DomainHealth> EnrichAsync(string domain, CancellationToken ct = default)
    {
        var h = new DomainHealth();

        var tld = domain.Contains('.') ? domain[(domain.LastIndexOf('.') + 1)..].ToLowerInvariant() : "";

        // ── whois/RDAP ────────────────────────────────────────────────
        try
        {
            if (tld is not ("ru" or "su" or "рф"))
            {
                await _rdapSem.WaitAsync(ct);
                try
                {
                    var (c, e) = await TryRdapAsync(domain, ct);
                    h.CreatedAt = c; h.ExpiresAt = e;
                }
                finally { _rdapSem.Release(); }
            }
            if (h.CreatedAt == null || h.ExpiresAt == null)
            {
                var host = WhoisHostFor(tld);
                if (host != null)
                {
                    await _whoisSem.WaitAsync(ct);
                    try
                    {
                        var text = await WhoisRawAsync(host, domain, ct);
                        h.CreatedAt ??= ParseDate(text, @"(?:created|creation date|registered on|domain registration date):\s*([^\r\n]+)");
                        h.ExpiresAt ??= ParseDate(text, @"(?:paid-till|expir(?:y|es)(?:\s+date)?|registry expiry date|expires on):\s*([^\r\n]+)");
                        if (h.CreatedAt == null && h.ExpiresAt == null && text.Length < 100)
                            h.WhoisError = "empty whois response";
                    }
                    finally { _whoisSem.Release(); }
                }
            }
        }
        catch (Exception ex) { h.WhoisError = ex.Message; }

        // ── RBL ───────────────────────────────────────────────────────
        // Сохраняем ответ как "list:category" — категория раскодируется по IP-коду.
        // Для Spamhaus DBL используем последний октет (127.0.1.X); для SURBL —
        // побитовый маппинг (127.0.0.2=ph, 4=mw, 8=abuse, 16=cr, комбинации через bitmask).
        foreach (var (name, zone) in RblZones)
        {
            try
            {
                var q = $"{domain}.{zone}";
                var ans = await _dns.QueryAsync(q, QueryType.A, cancellationToken: ct);
                var ips = ans.Answers.OfType<DnsClient.Protocol.ARecord>()
                    .Select(a => a.Address.ToString()).ToList();
                var real = ips.Where(ip => ip != "127.0.0.1" && !ip.StartsWith("127.255.")).ToList();
                if (real.Count == 0) continue;
                var codes = new HashSet<string>();
                foreach (var ip in real)
                {
                    var parts = ip.Split('.');
                    if (parts.Length != 4 || !byte.TryParse(parts[3], out var last)) continue;
                    if (name == "Spamhaus")
                    {
                        // DBL: 127.0.1.X → X = категория
                        codes.Add(last switch
                        {
                            2 => "spam", 4 => "phishing", 5 => "malware", 6 => "botnet",
                            102 => "abused-spam", 103 => "abused-phishing",
                            104 => "abused-malware", 105 => "abused-botnet",
                            _ => "listed",
                        });
                    }
                    else if (name == "SURBL")
                    {
                        // SURBL bitmask (последний октет 127.0.0.X)
                        if ((last & 2) != 0) codes.Add("phishing");
                        if ((last & 4) != 0) codes.Add("malware");
                        if ((last & 8) != 0) codes.Add("abuse");
                        if ((last & 16) != 0) codes.Add("cracked");
                        if ((last & 32) != 0) codes.Add("redirector");
                        if ((last & 64) != 0) codes.Add("jwspamspy");
                        if ((last & 128) != 0) codes.Add("spam");
                        // Fallback — код в чистом виде чтобы понять что именно
                        if (codes.Count == 0) codes.Add($"code{last}");
                    }
                }
                foreach (var c in codes) h.RblHits.Add($"{name}:{c}");
            }
            catch { }
        }

        h.CheckedAt = DateTime.UtcNow;
        _db.SaveHealth(domain, h);
        return h;
    }

    // ── helpers ──────────────────────────────────────────────────────

    private async Task<(DateTime? c, DateTime? e)> TryRdapAsync(string domain, CancellationToken ct)
    {
        try
        {
            using var resp = await _http.GetAsync($"https://rdap.org/domain/{Uri.EscapeDataString(domain)}", ct);
            if (!resp.IsSuccessStatusCode) return (null, null);
            var body = await resp.Content.ReadAsStringAsync(ct);
            using var doc = JsonDocument.Parse(body);
            DateTime? c = null, e = null;
            if (doc.RootElement.TryGetProperty("events", out var evs))
            {
                foreach (var ev in evs.EnumerateArray())
                {
                    var a = ev.TryGetProperty("eventAction", out var av) ? av.GetString() : "";
                    var d = ev.TryGetProperty("eventDate", out var dv) ? dv.GetString() : "";
                    if (string.IsNullOrEmpty(d) || !DateTime.TryParse(d, out var dt)) continue;
                    if (a == "registration") c = dt;
                    if (a == "expiration") e = dt;
                }
            }
            return (c, e);
        }
        catch { return (null, null); }
    }

    private static string? WhoisHostFor(string tld) => tld switch
    {
        "ru" or "su" or "рф" => "whois.tcinet.ru",
        "com" or "net" => "whois.verisign-grs.com",
        "org" => "whois.pir.org",
        "io" => "whois.nic.io",
        _ => null,
    };

    private static async Task<string> WhoisRawAsync(string host, string domain, CancellationToken ct)
    {
        using var tcp = new TcpClient();
        await tcp.ConnectAsync(host, 43, ct);
        using var stream = tcp.GetStream();
        var q = Encoding.ASCII.GetBytes(domain + "\r\n");
        await stream.WriteAsync(q, 0, q.Length, ct);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        return await reader.ReadToEndAsync(ct);
    }

    private static DateTime? ParseDate(string text, string re)
    {
        var m = Regex.Match(text, re, RegexOptions.IgnoreCase);
        if (!m.Success) return null;
        return DateTime.TryParse(m.Groups[1].Value.Trim(), out var dt) ? dt : null;
    }
}
