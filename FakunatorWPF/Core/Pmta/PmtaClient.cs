using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core.Pmta;

/// <summary>
/// Клиент PowerMTA v5 Web Monitor. Взаимодействует через POST-form-urlencoded
/// с телом <c>format=json</c>. Self-signed сертификаты игнорируем — типовая
/// настройка для внутренних PMTA-нод.
/// </summary>
public class PmtaClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _baseUrl;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    public PmtaClient(PmtaPanel panel)
    {
        var scheme = panel.UseHttps ? "https" : "http";
        _baseUrl = $"{scheme}://{panel.Host}:{panel.Port}";

        var handler = new HttpClientHandler();
        if (panel.IgnoreCertErrors)
        {
            handler.ServerCertificateCustomValidationCallback =
                HttpClientHandler.DangerousAcceptAnyServerCertificateValidator;
        }
        _http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    private async Task<T?> PostAsync<T>(string endpoint, CancellationToken ct = default)
    {
        // PMTA принимает form-urlencoded с обязательным полем format=json
        var content = new StringContent("format=json", Encoding.UTF8,
            "application/x-www-form-urlencoded");
        using var resp = await _http.PostAsync(_baseUrl + endpoint, content, ct);
        resp.EnsureSuccessStatusCode();
        var body = await resp.Content.ReadAsStringAsync(ct);
        return JsonSerializer.Deserialize<T>(body, JsonOpts);
    }

    public Task<PmtaStatusResponse?> GetStatusAsync(CancellationToken ct = default) =>
        PostAsync<PmtaStatusResponse>("/status", ct);

    public Task<PmtaQueuesResponse?> GetQueuesAsync(CancellationToken ct = default) =>
        PostAsync<PmtaQueuesResponse>("/queues", ct);

    public Task<PmtaDomainsResponse?> GetDomainsAsync(CancellationToken ct = default) =>
        PostAsync<PmtaDomainsResponse>("/domains", ct);

    public Task<PmtaVmtasResponse?> GetVmtasAsync(CancellationToken ct = default) =>
        PostAsync<PmtaVmtasResponse>("/vmtas", ct);

    public Task<PmtaJobsResponse?> GetJobsAsync(CancellationToken ct = default) =>
        PostAsync<PmtaJobsResponse>("/jobs", ct);

    /// <summary>Отправка команды управления PMTA. Разбирает JSON-ответ:
    /// при <c>status=fail</c> бросает исключение с текстом <c>message</c>,
    /// иначе возвращает <c>message</c> (или <c>data</c> если пусто).</summary>
    public async Task<string> SendCommandAsync(string command, CancellationToken ct = default)
    {
        var content = new StringContent(
            $"command={Uri.EscapeDataString(command)}&format=json",
            Encoding.UTF8, "application/x-www-form-urlencoded");
        using var resp = await _http.PostAsync(_baseUrl + "/command", content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode}: {Trim(body)}");
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) ? s.GetString() : null;
            var message = root.TryGetProperty("message", out var m) ? m.GetString() ?? "" : "";
            var data = root.TryGetProperty("data", out var d) ? d.GetString() ?? "" : "";
            if (string.Equals(status, "fail", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(string.IsNullOrWhiteSpace(message) ? "PMTA fail" : message);
            var text = !string.IsNullOrWhiteSpace(message) ? message : data;
            return string.IsNullOrWhiteSpace(text) ? "OK" : text.Trim();
        }
        catch (JsonException)
        {
            return Trim(body);
        }
    }

    private static string Trim(string s) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length > 400 ? s[..400] + "…" : s);

    public void Dispose() => _http.Dispose();
}
