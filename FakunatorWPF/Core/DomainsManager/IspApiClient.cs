using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;

namespace Fakunator.Core.DomainsManager;

/// <summary>
/// HTTP-клиент для ISPmanager DNSmanager API. Точка входа:
/// <c>https://{host}[:1500]/dnsmgr?func=&lt;action&gt;&amp;out=xml&amp;...</c>.
/// Порт 1500 — дефолт для стокового ISPmanager, но некоторые панели вешают
/// свой (например 1501/1502) прямо в hostname (`panel.host.ru:1501`) — в этом
/// случае мы не добавляем :1500 поверх. Также поддерживается host без порта.
///
/// Ответы бывают XML (мы используем это в base flow) или JSON (для edit-операций,
/// где ISP возвращает <c>out=xjson</c> — вы получите &lt;ok/&gt; или &lt;error/&gt;).
///
/// SSL: панели часто с самоподписанными сертификатами, HttpClient настроен
/// игнорировать ошибки цепочки.
/// </summary>
public class IspApiClient : IDisposable
{
    private readonly string _host;
    private readonly HttpClient _http;

    public IspApiClient(string host)
    {
        _host = host.Trim();
        var handler = new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true,
            AllowAutoRedirect = false,
        };
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
    }

    /// <summary>
    /// Строит base-URL к панели. Если в hostname уже указан порт (`:1501`), берём как есть.
    /// Иначе добавляем стандартный ISPmanager порт 1500 и путь /dnsmgr.
    /// </summary>
    private string BaseUrl()
    {
        var h = _host;
        // Убираем протокол если случайно ввели
        if (h.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) h = h[7..];
        if (h.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) h = h[8..];
        // Убираем trailing /
        h = h.TrimEnd('/');

        // Проверяем: указан ли уже путь (`/dnsmgr` / `/ispmgr`)? Тогда берём как есть.
        var slashIdx = h.IndexOf('/');
        if (slashIdx > 0) return $"https://{h}";

        // Порт указан? (`:NNNN` в конце) — берём без дописки
        if (System.Text.RegularExpressions.Regex.IsMatch(h, @":\d+$"))
            return $"https://{h}/dnsmgr";

        // Голый host — стандартный ISPmanager порт + /dnsmgr
        return $"https://{h}:1500/dnsmgr";
    }

    /// <summary>Логин. Возвращает sessionId или бросает <see cref="IspApiException"/>.</summary>
    public async Task<string> AuthAsync(string username, string password, CancellationToken ct = default)
    {
        var url = $"{BaseUrl()}?out=xml&func=auth"
                  + $"&username={Uri.EscapeDataString(username)}"
                  + $"&password={Uri.EscapeDataString(password)}";
        var xml = await GetXmlAsync(url, ct);

        var auth = xml.Root?.Element("auth");
        if (auth == null)
        {
            var errMsg = xml.Root?.Element("error")?.Element("msg")?.Value
                         ?? "Ошибка авторизации";
            throw new IspApiException(errMsg);
        }
        return auth.Value.Trim();
    }

    /// <summary>Список доменов активного аккаунта.</summary>
    public async Task<List<IspDomain>> ListDomainsAsync(string sessionId, CancellationToken ct = default)
    {
        var url = $"{BaseUrl()}?auth={Uri.EscapeDataString(sessionId)}&out=xml&func=domain";
        var xml = await GetXmlAsync(url, ct);

        var err = xml.Root?.Element("error");
        if (err != null)
            throw new IspApiException(err.Element("msg")?.Value ?? "Ошибка получения доменов");

        var list = new List<IspDomain>();
        foreach (var el in xml.Root?.Elements("elem") ?? Array.Empty<XElement>())
        {
            var name = el.Element("name")?.Value ?? "";
            if (string.IsNullOrEmpty(name)) continue;
            list.Add(new IspDomain(
                Name: name,
                DomainType: el.Element("dtype")?.Value ?? "",
                IpAddress: el.Element("ip")?.Value,
                Owner: el.Element("owner")?.Value));
        }
        return list;
    }

    /// <summary>DNS-записи домена.</summary>
    public async Task<List<DnsRecord>> GetRecordsAsync(string sessionId, string domain, CancellationToken ct = default)
    {
        var url = $"{BaseUrl()}?auth={Uri.EscapeDataString(sessionId)}&out=xml"
                  + $"&func=domain.record&elid={Uri.EscapeDataString(domain)}";
        var xml = await GetXmlAsync(url, ct);

        var err = xml.Root?.Element("error");
        if (err != null)
            throw new IspApiException(err.Element("msg")?.Value ?? "Ошибка получения записей");

        var list = new List<DnsRecord>();
        foreach (var el in xml.Root?.Elements("elem") ?? Array.Empty<XElement>())
        {
            var rtype = el.Element("rtype")?.Value ?? el.Element("type")?.Value ?? "";
            var rawName = el.Element("name")?.Value ?? "@";
            var name = NormalizeNameFromApi(rawName, domain);
            var value = el.Element("value")?.Value ?? el.Element("rkey")?.Value ?? "";
            int.TryParse(el.Element("ttl")?.Value, out var ttl);
            int? prio = int.TryParse(el.Element("prio")?.Value ?? el.Element("priority")?.Value, out var p) ? p : null;
            // ISPmanager кладёт MX-приоритет не в <prio>, а в <info>priority= 10</info> — парсим.
            if (prio == null)
            {
                var info = el.Element("info")?.Value ?? "";
                var m = System.Text.RegularExpressions.Regex.Match(info, @"priority\s*=\s*(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out var p2)) prio = p2;
            }
            var rkey = el.Element("rkey")?.Value;
            if (string.IsNullOrEmpty(rtype)) continue;
            list.Add(new DnsRecord(rtype, name, value, ttl, prio, rkey));
        }
        return list;
    }

    /// <summary>
    /// ISPmanager возвращает <c>name</c> как FQDN с точкой в конце: <c>www.example.ru.</c>
    /// или <c>example.ru.</c> для корневого. Приводим к короткому виду:
    /// корневой → <c>@</c>, sub → <c>www</c>. Это делает и UX предсказуемым, и позволяет
    /// UpsertRecord собирать FQDN обратно без риска двойной точки.
    /// </summary>
    private static string NormalizeNameFromApi(string raw, string domain)
    {
        var n = (raw ?? "").Trim().TrimEnd('.');
        var d = domain.Trim().TrimEnd('.');
        if (string.IsNullOrEmpty(n)) return "@";
        if (n.Equals(d, StringComparison.OrdinalIgnoreCase)) return "@";
        if (n.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
            return n[..^(d.Length + 1)];
        return n;
    }

    /// <summary>
    /// Строит валидный FQDN для отправки в ISPmanager так, чтобы никогда не
    /// получалось двойной точки в конце (ISP отбивает <c>Invalid domain name '…..'</c>).
    /// Правила: <c>@</c>/пусто → <c>{domain}.</c>; уже FQDN с точкой → как есть;
    /// уже FQDN без точки — добавляем точку; короткое имя → <c>{name}.{domain}.</c>
    /// </summary>
    private static string BuildRecordName(string name, string domain)
    {
        var d = domain.Trim().TrimEnd('.');
        var n = (name ?? "").Trim();
        if (string.IsNullOrEmpty(n) || n == "@") return $"{d}.";
        if (n.EndsWith(".")) return n; // уже FQDN, не трогаем
        if (n.Equals(d, StringComparison.OrdinalIgnoreCase)
            || n.EndsWith("." + d, StringComparison.OrdinalIgnoreCase))
            return $"{n}.";
        return $"{n}.{d}.";
    }

    /// <summary>
    /// Добавляет домен в панель. Автоматически обрабатывает три случая:
    /// 1. Обычный success — <c>&lt;ok/&gt;</c>
    /// 2. <c>&lt;doc func="confirm"&gt;</c> — надо повторить с sok=yes
    /// 3. <c>&lt;error type="referer_confirm"&gt;</c> — XSRF защита ISPmanager,
    ///    требуется Referer заголовок + повтор через GET с sok=yes.
    /// Referer добавляется ко ВСЕМ мутирующим запросам (панель zomro так требует).
    /// </summary>
    public async Task<bool> AddDomainAsync(string sessionId, string domain, string dtype = "master",
        string? ip = null, CancellationToken ct = default)
    {
        var url = BaseUrl();
        var form = new Dictionary<string, string>
        {
            ["out"] = "xml",
            ["func"] = "domain.edit",
            ["sok"] = "ok",
            ["name"] = domain,
            ["plid"] = "new",
            ["dtype"] = dtype,
        };
        if (!string.IsNullOrEmpty(ip)) form["ip"] = ip;

        var xml = await PostFormAsync(url, form, sessionId, ct);

        // Case: <doc func="confirm"> — обычное подтверждение через GET с sok=yes
        var docFunc = xml.Root?.Attribute("func")?.Value;
        if (docFunc == "confirm")
        {
            var confirmUrl = $"{url}?{FormEncoded(form)}&sok=yes&auth={Uri.EscapeDataString(sessionId)}";
            var xml2 = await GetXmlAsync(confirmUrl, ct);
            var err2 = xml2.Root?.Element("error");
            if (err2 != null) throw new IspApiException(FormatIspError(err2));
            return xml2.Root?.Element("ok") != null;
        }

        // Case: <error type="referer_confirm"> — XSRF защита, retry с sok=yes
        var errEl = xml.Root?.Element("error");
        if (errEl?.Attribute("type")?.Value == "referer_confirm")
        {
            form["sok"] = "yes";
            var xml3 = await PostFormAsync(url, form, sessionId, ct);
            var err3 = xml3.Root?.Element("error");
            if (err3 != null) throw new IspApiException(FormatIspError(err3));
            return xml3.Root?.Element("ok") != null;
        }

        if (errEl != null)
            throw new IspApiException(FormatIspError(errEl));

        return xml.Root?.Element("ok") != null;
    }

    /// <summary>
    /// POST form-data с Cookie сессии + Referer (обязателен для мутаций в ISPmanager).
    /// Ошибки HTTP не бросаются — возвращаем XML тело в любом случае.
    /// </summary>
    private async Task<XDocument> PostFormAsync(string url, Dictionary<string, string> form, string sessionId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        req.Headers.Add("Cookie", $"dnsmgrses5={sessionId}");
        // Referer нужен для обхода XSRF (`referer_confirm`) — панель проверяет
        // что запрос пришёл из её же интерфейса.
        req.Headers.Referrer = new Uri(url);
        using var resp = await _http.SendAsync(req, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        return XDocument.Parse(body);
    }

    /// <summary>
    /// Разворачивает ошибку панели: собирает msg + type + object + val для диагностики.
    /// ISPmanager часто пишет реальную причину в атрибутах, а не в msg.
    /// </summary>
    private static string FormatIspError(XElement errorEl, string? rawBody = null)
    {
        var msg = errorEl.Element("msg")?.Value?.Trim();
        var type = errorEl.Attribute("type")?.Value;
        var obj = errorEl.Attribute("object")?.Value;
        var val = errorEl.Attribute("val")?.Value;
        var parts = new System.Collections.Generic.List<string>();
        if (!string.IsNullOrEmpty(msg)) parts.Add(msg);
        if (!string.IsNullOrEmpty(type)) parts.Add($"type={type}");
        if (!string.IsNullOrEmpty(obj)) parts.Add($"поле={obj}");
        if (!string.IsNullOrEmpty(val)) parts.Add($"значение={val}");
        var result = parts.Count > 0 ? string.Join(" · ", parts) : "Ошибка API";
        // Debug: если ничего конкретного не удалось извлечь — добавляем raw для диагностики
        if (parts.Count == 0 && !string.IsNullOrEmpty(rawBody))
            result += "\n\n" + rawBody.Substring(0, Math.Min(500, rawBody.Length));
        return result;
    }

    /// <summary>
    /// Добавляет/редактирует DNS-запись. Если <paramref name="rkey"/> пуст — создаётся новая,
    /// иначе редактируется существующая (rkey = уникальный ключ записи из ISPmanager).
    /// Value для A/AAAA идёт в поле `ip`, для MX — в `domain`, для остальных — в `value`.
    /// </summary>
    public async Task<bool> UpsertRecordAsync(string sessionId, string domain, string rtype,
        string name, string value, int? ttl = null, int? priority = null,
        string? rkey = null, CancellationToken ct = default)
    {
        rtype = rtype.ToLowerInvariant();
        var url = BaseUrl();
        var form = new Dictionary<string, string>
        {
            ["out"] = "xml",
            ["func"] = "domain.record.edit",
            ["sok"] = "ok",
            ["elid"] = rkey ?? "",
            ["plid"] = domain,
            ["name"] = BuildRecordName(name, domain),
            ["rtype"] = rtype,
            ["ttl"] = (ttl?.ToString()) ?? "3600",
        };

        // Value идёт в разное поле в зависимости от типа записи (так делает ISPmanager UI)
        if (rtype == "a" || rtype == "aaaa") form["ip"] = value;
        else if (rtype == "mx") { form["domain"] = value; form["priority"] = (priority?.ToString()) ?? "10"; }
        else form["value"] = value;

        var xml = await PostFormAsync(url, form, sessionId, ct);
        return await FinalizeMutationAsync(xml, url, form, sessionId, ct);
    }

    /// <summary>Удаляет DNS-запись по её rkey.</summary>
    public async Task<bool> DeleteRecordAsync(string sessionId, string domain, string rkey, CancellationToken ct = default)
    {
        var url = BaseUrl();
        var form = new Dictionary<string, string>
        {
            ["out"] = "xml",
            ["func"] = "domain.record.delete",
            ["elid"] = rkey,
            ["plid"] = domain,
        };
        var xml = await PostFormAsync(url, form, sessionId, ct);
        return await FinalizeMutationAsync(xml, url, form, sessionId, ct);
    }

    /// <summary>
    /// Обрабатывает confirm/referer_confirm/error после мутирующего запроса.
    /// Возвращает true при success, бросает <see cref="IspApiException"/> при ошибке.
    /// </summary>
    private async Task<bool> FinalizeMutationAsync(XDocument xml, string url,
        Dictionary<string, string> form, string sessionId, CancellationToken ct)
    {
        var docFunc = xml.Root?.Attribute("func")?.Value;
        if (docFunc == "confirm")
        {
            var confirmUrl = $"{url}?{FormEncoded(form)}&sok=yes&auth={Uri.EscapeDataString(sessionId)}";
            var xml2 = await GetXmlAsync(confirmUrl, ct);
            var err2 = xml2.Root?.Element("error");
            if (err2 != null) throw new IspApiException(FormatIspError(err2));
            return xml2.Root?.Element("ok") != null;
        }

        var errEl = xml.Root?.Element("error");
        if (errEl?.Attribute("type")?.Value == "referer_confirm")
        {
            form["sok"] = "yes";
            var xml3 = await PostFormAsync(url, form, sessionId, ct);
            var err3 = xml3.Root?.Element("error");
            if (err3 != null) throw new IspApiException(FormatIspError(err3));
            return xml3.Root?.Element("ok") != null;
        }

        if (errEl != null) throw new IspApiException(FormatIspError(errEl));
        return xml.Root?.Element("ok") != null;
    }

    /// <summary>Удаляет домен. Так же обрабатывает confirm + referer_confirm.</summary>
    public async Task<bool> DeleteDomainAsync(string sessionId, string domain, CancellationToken ct = default)
    {
        var url = BaseUrl();
        var form = new Dictionary<string, string>
        {
            ["out"] = "xml",
            ["func"] = "domain.delete",
            ["tconvert"] = "punycode",
            ["elname"] = domain,
            ["elid"] = domain,
            ["plid"] = "",
        };
        var xml = await PostFormAsync(url, form, sessionId, ct);

        var docFunc = xml.Root?.Attribute("func")?.Value;
        if (docFunc == "confirm")
        {
            var confirmUrl = $"{url}?{FormEncoded(form)}&sok=yes&auth={Uri.EscapeDataString(sessionId)}";
            var xml2 = await GetXmlAsync(confirmUrl, ct);
            var err2 = xml2.Root?.Element("error");
            if (err2 != null) throw new IspApiException(FormatIspError(err2));
            return xml2.Root?.Element("ok") != null;
        }

        var errEl = xml.Root?.Element("error");
        if (errEl?.Attribute("type")?.Value == "referer_confirm")
        {
            form["sok"] = "yes";
            var xml3 = await PostFormAsync(url, form, sessionId, ct);
            var err3 = xml3.Root?.Element("error");
            if (err3 != null) throw new IspApiException(FormatIspError(err3));
            return xml3.Root?.Element("ok") != null;
        }

        if (errEl != null)
            throw new IspApiException(FormatIspError(errEl));
        return xml.Root?.Element("ok") != null;
    }

    // ── helpers ──────────────────────────────────────────────────────

    private async Task<XDocument> GetXmlAsync(string url, CancellationToken ct)
    {
        var body = await _http.GetStringAsync(url, ct);
        return XDocument.Parse(body);
    }

    private static string FormEncoded(Dictionary<string, string> form)
    {
        var parts = new List<string>(form.Count);
        foreach (var (k, v) in form)
            parts.Add($"{Uri.EscapeDataString(k)}={Uri.EscapeDataString(v)}");
        return string.Join("&", parts);
    }

    public void Dispose() => _http.Dispose();
}

public class IspApiException : Exception
{
    public IspApiException(string message) : base(message) { }
}
