using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core.Postmaster;

/// <summary>
/// Клиент Postmaster API (mail.ru). Все методы требуют access_token —
/// его выдаёт <see cref="MailRuOAuthClient"/> из refresh_token.
/// Endpoints согласно help.mail.ru/developers/postmaster/api/:
/// - GET /ext-api/reg-list/         — список верифицированных доменов
/// - GET /ext-api/troubles-list/    — проблемы SPF/DKIM/DMARC
/// - GET /ext-api/stat-list/        — статистика по домену
/// Rate limit: 10 req/min per domain per account.
/// </summary>
public class PostmasterApiClient : IDisposable
{
    private const string BaseUrl = "https://postmaster.mail.ru";
    private readonly HttpClient _http;
    private readonly string _accessToken;

    public PostmasterApiClient(string accessToken)
    {
        _accessToken = accessToken;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Fakunator/2.0 (Windows)");
    }

    /// <summary>
    /// Список верифицированных доменов аккаунта.
    /// Реальный формат ответа: <c>{ok: true, domains: [...]}</c>.
    /// </summary>
    public async Task<List<PostmasterDomain>> RegListAsync(CancellationToken ct = default)
    {
        var body = await GetAsync("/ext-api/reg-list/", ct);
        var list = new List<PostmasterDomain>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var domains = FindArray(doc.RootElement, "domains");
            if (domains.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in domains.EnumerateArray())
                {
                    // Домен может быть строкой (простой список) или объектом
                    string name = d.ValueKind == JsonValueKind.String
                        ? (d.GetString() ?? "")
                        : (TryStr(d, "domain") ?? TryStr(d, "name") ?? "");
                    if (string.IsNullOrEmpty(name)) continue;

                    var verified = d.ValueKind == JsonValueKind.Object
                        ? (TryBool(d, "verified") ?? true)
                        : true;
                    var status = d.ValueKind == JsonValueKind.Object
                        ? (TryStr(d, "status") ?? "")
                        : "";
                    var addedAt = d.ValueKind == JsonValueKind.Object
                        ? (TryStr(d, "added_at") ?? "")
                        : "";
                    list.Add(new PostmasterDomain(name, verified, status, addedAt));
                }
            }
        }
        catch (JsonException) { /* ignore parse issues */ }
        return list;
    }

    /// <summary>Ищем массив по ключу и в root, и внутри "data" (в зависимости от формата).</summary>
    private static JsonElement FindArray(JsonElement root, string key)
    {
        if (root.TryGetProperty(key, out var a) && a.ValueKind == JsonValueKind.Array) return a;
        if (root.TryGetProperty("data", out var data)
            && data.TryGetProperty(key, out var a2) && a2.ValueKind == JsonValueKind.Array) return a2;
        return default;
    }

    /// <summary>
    /// Проблемы SPF/DKIM/DMARC для доменов аккаунта. Опционально можно
    /// отфильтровать по домену.
    /// </summary>
    public async Task<List<PostmasterTrouble>> TroublesListAsync(string? domain = null, CancellationToken ct = default)
    {
        var q = string.IsNullOrEmpty(domain) ? "" : $"?domain={Uri.EscapeDataString(domain)}";
        var body = await GetAsync($"/ext-api/troubles-list/{q}", ct);
        var list = new List<PostmasterTrouble>();
        // Реальный формат: {domains: [{domain, errors: [{msg, code}, ...]}], ok: true}
        try
        {
            using var doc = JsonDocument.Parse(body);
            var arr = FindArray(doc.RootElement, "domains");
            if (arr.ValueKind != JsonValueKind.Array)
                arr = FindArray(doc.RootElement, "troubles");

            if (arr.ValueKind == JsonValueKind.Array)
            {
                foreach (var d in arr.EnumerateArray())
                {
                    if (d.ValueKind != JsonValueKind.Object) continue;
                    var dName = TryStr(d, "domain") ?? "";

                    // Вложенный errors[] со структурой {msg, code}.
                    // code < 0 — реальная проблема (SPF not found и т.п.), code == 0 —
                    // info-статус (типа "DKIM statistics not available"), не проблема.
                    if (d.TryGetProperty("errors", out var errors) && errors.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var e in errors.EnumerateArray())
                        {
                            var msg = TryStr(e, "msg") ?? TryStr(e, "message") ?? "";
                            int code = 0;
                            if (e.TryGetProperty("code", out var c) && c.ValueKind == JsonValueKind.Number)
                                c.TryGetInt32(out code);
                            if (code == 0) continue; // info-статус — пропускаем
                            var type = ExtractTroubleType(msg);
                            list.Add(new PostmasterTrouble(dName, type, msg));
                        }
                    }
                    else
                    {
                        // Fallback на плоский формат
                        var type = TryStr(d, "type") ?? "";
                        var desc = TryStr(d, "description") ?? TryStr(d, "message") ?? "";
                        if (!string.IsNullOrEmpty(type) || !string.IsNullOrEmpty(desc))
                            list.Add(new PostmasterTrouble(dName, type, desc));
                    }
                }
            }
        }
        catch (JsonException) { }
        return list;
    }

    /// <summary>Извлекает тип проблемы из текста msg — например «SPF not found.» → «SPF».</summary>
    private static string ExtractTroubleType(string msg)
    {
        if (msg.Contains("SPF", StringComparison.OrdinalIgnoreCase)) return "SPF";
        if (msg.Contains("DKIM", StringComparison.OrdinalIgnoreCase)) return "DKIM";
        if (msg.Contains("DMARC", StringComparison.OrdinalIgnoreCase)) return "DMARC";
        if (msg.Contains("MX", StringComparison.OrdinalIgnoreCase)) return "MX";
        return "info";
    }

    /// <summary>
    /// Статистика за период по домену.
    /// Формат ответа: <c>{ok: true, stat: {...агрегированные метрики...}}</c>.
    /// Метрики: sent, delivered, spam, complaints, dkim_fail, dmarc_fail, spf_fail и т.п.
    /// </summary>
    public async Task<PostmasterStat> StatListAsync(string domain, DateTime? from = null, CancellationToken ct = default)
    {
        // Mail.ru API отвергает date_from старше 1 года ("Wrong param: date_from, older than 1 year").
        // Ставим ровно 364 дня — максимум который принимается.
        var f = (from ?? DateTime.UtcNow.AddDays(-364)).ToString("yyyy-MM-dd");
        var path = $"/ext-api/stat-list/?date_from={f}&domain={Uri.EscapeDataString(domain)}";
        var body = await GetAsync(path, ct);
        var metrics = new Dictionary<string, double>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // Формат: {data: [{domain, delivered, messages_sent, ...}], ok: true}
            if (root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in data.EnumerateArray())
                {
                    if (item.ValueKind != JsonValueKind.Object) continue;
                    foreach (var p in item.EnumerateObject())
                    {
                        // Пропускаем text-поля типа "domain"
                        if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var v))
                            metrics[p.Name] = v;
                    }
                }
            }
        }
        catch (JsonException) { }
        return new PostmasterStat(domain, metrics, body);
    }

    /// <summary>
    /// Ежедневная детализация метрик за период — то что рисует график
    /// на странице «Графики» постмастера. Каждая точка — метрики за один день.
    /// </summary>
    public async Task<List<PostmasterDaily>> StatListDetailedAsync(string domain,
        DateTime? from = null, DateTime? to = null, CancellationToken ct = default)
    {
        var f = (from ?? DateTime.UtcNow.AddDays(-364)).ToString("yyyy-MM-dd");
        var t = (to ?? DateTime.UtcNow).ToString("yyyy-MM-dd");
        var path = $"/ext-api/stat-list-detailed/?date_from={f}&date_to={t}&domain={Uri.EscapeDataString(domain)}";
        var body = await GetAsync(path, ct);
        var days = new List<PostmasterDaily>();
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            // {data: [{domain, data: [{date, delivered, ...}]}]}
            if (root.TryGetProperty("data", out var outer) && outer.ValueKind == JsonValueKind.Array)
            {
                foreach (var domObj in outer.EnumerateArray())
                {
                    if (!domObj.TryGetProperty("data", out var inner) || inner.ValueKind != JsonValueKind.Array)
                        continue;
                    foreach (var day in inner.EnumerateArray())
                    {
                        var date = TryStr(day, "date") ?? "";
                        var metrics = new Dictionary<string, double>();
                        foreach (var p in day.EnumerateObject())
                        {
                            if (p.Value.ValueKind == JsonValueKind.Number && p.Value.TryGetDouble(out var v))
                                metrics[p.Name] = v;
                        }
                        if (!string.IsNullOrEmpty(date))
                            days.Add(new PostmasterDaily(date, metrics));
                    }
                }
            }
        }
        catch (JsonException) { }
        // API возвращает от новых к старым — переворачиваем чтобы график шёл слева направо (старые слева).
        days.Reverse();
        return days;
    }

    private async Task<string> GetAsync(string path, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, BaseUrl + path);
        // Postmaster API требует кастомный заголовок «Bearer: TOKEN» (без Authorization-префикса).
        // Стандартный `Authorization: Bearer` возвращает 403 — экспериментально проверено.
        req.Headers.TryAddWithoutValidation("Bearer", _accessToken);
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            throw new PostmasterApiException($"HTTP {(int)resp.StatusCode}: {body}");
        return body;
    }

    private static string? TryStr(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool? TryBool(JsonElement el, string prop) =>
        el.TryGetProperty(prop, out var v) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean() : null;

    public void Dispose() => _http.Dispose();
}

public record PostmasterDomain(string Domain, bool Verified, string Status, string AddedAt);
public record PostmasterTrouble(string Domain, string Type, string Description);
public record PostmasterStat(string Domain, Dictionary<string, double> Metrics, string RawJson);
public record PostmasterDaily(string Date, Dictionary<string, double> Metrics);

public class PostmasterApiException : Exception
{
    public PostmasterApiException(string message) : base(message) { }
}
