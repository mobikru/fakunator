using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core.AmsApi;

/// <summary>
/// JSON-RPC 2.0 клиент AMS Enterprise 2.x.
/// Все запросы — POST на <c>http(s)://{host}/api/v1</c>, api-key передаётся в
/// <c>params.apiKey</c>. Текстовые поля (тела писем и т.п.) сервер ждёт в base64 —
/// см. <see cref="ToBase64"/>/<see cref="FromBase64"/>.
/// </summary>
public class AmsApiClient : IDisposable
{
    private readonly HttpClient _http;
    private readonly string _apiKey;
    private readonly string _endpoint;
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    public AmsApiClient(string host, string apiKey, bool useHttps = false, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(host)) throw new ArgumentException("host is empty", nameof(host));
        if (string.IsNullOrWhiteSpace(apiKey)) throw new ArgumentException("apiKey is empty", nameof(apiKey));

        _apiKey = apiKey.Trim();
        var scheme = useHttps ? "https" : "http";
        // Юзер может вставить host с/без схемы — нормализуем.
        var clean = host.Trim();
        if (clean.StartsWith("http://", StringComparison.OrdinalIgnoreCase)) clean = clean[7..];
        else if (clean.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) clean = clean[8..];
        clean = clean.TrimEnd('/');
        _endpoint = $"{scheme}://{clean}/api/v1";

        _http = new HttpClient { Timeout = timeout ?? TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Fakunator-AMS/2.0");
    }

    // ── Ядро JSON-RPC ────────────────────────────────────────────────

    /// <summary>Отправляет JSON-RPC вызов и десериализует result в <typeparamref name="T"/>.</summary>
    public async Task<T?> CallAsync<T>(string method, IDictionary<string, object?>? extraParams = null,
        CancellationToken ct = default)
    {
        var pars = new Dictionary<string, object?> { ["apiKey"] = _apiKey };
        if (extraParams != null)
            foreach (var kv in extraParams) pars[kv.Key] = kv.Value;

        var req = new
        {
            jsonrpc = "2.0",
            method,
            @params = pars,
            id = Guid.NewGuid().ToString("N"),
        };
        var json = JsonSerializer.Serialize(req, JsonOpts);
        using var content = new StringContent(json, Encoding.UTF8, "application/json");
        using var resp = await _http.PostAsync(_endpoint, content, ct);
        var body = await resp.Content.ReadAsStringAsync(ct);
        if (!resp.IsSuccessStatusCode)
            throw new AmsApiException($"HTTP {(int)resp.StatusCode} {resp.ReasonPhrase}: {Trunc(body, 300)}");

        var envelope = JsonSerializer.Deserialize<AmsRpcResponse<T>>(body, JsonOpts);
        if (envelope == null) throw new AmsApiException("empty response");
        if (envelope.Error != null)
            throw new AmsApiException($"AMS: {envelope.Error.Message} (code {envelope.Error.Code})");
        return envelope.Result;
    }

    /// <summary>Пинг: планировщик рассылок жив? Возвращает bool.</summary>
    public Task<bool> IsSchedulerRunningAsync(CancellationToken ct = default) =>
        CallAsync<bool>("isSchedulerRunning", null, ct);

    /// <summary>Запускает планировщик рассылок AMS. Без него <c>startMailing</c>
    /// возвращает OK, но реальная отправка не идёт. Метод <c>stopScheduler</c>
    /// в API не выставлен — остановить можно только из окна AMS Enterprise.</summary>
    public async Task<bool> RunSchedulerAsync(CancellationToken ct = default)
    {
        var res = await CallAsync<System.Text.Json.JsonElement>("runScheduler", null, ct);
        return res.ValueKind == System.Text.Json.JsonValueKind.String
            ? string.Equals(res.GetString(), "OK", StringComparison.OrdinalIgnoreCase)
            : res.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    // ── Рассылки ─────────────────────────────────────────────────────

    public Task<List<AmsMailing>?> GetMailingsAsync(CancellationToken ct = default) =>
        CallAsync<List<AmsMailing>>("getMailings", null, ct);

    public Task<List<AmsMailing>?> GetTransactionalMailingsAsync(CancellationToken ct = default) =>
        CallAsync<List<AmsMailing>>("getTransactionalMailings", null, ct);

    public Task<AmsMailing?> GetMailingAsync(int id, CancellationToken ct = default) =>
        CallAsync<AmsMailing>("getMailing", new Dictionary<string, object?> { ["id"] = id }, ct);

    /// <summary>Запускает рассылку. <paramref name="startMode"/> — <c>"continue"</c>
    /// (продолжить с текущей позиции) или <c>"restart"</c> (начать сначала).
    /// Возвращает true если сервер вернул строку "OK" или true.</summary>
    public async Task<bool> StartMailingAsync(int id, string startMode = "continue", CancellationToken ct = default)
    {
        // Сервер отвечает "OK" (строка) — читаем как JsonElement и мапим.
        var res = await CallAsync<System.Text.Json.JsonElement>("startMailing",
            new Dictionary<string, object?> { ["id"] = id, ["startMode"] = startMode }, ct);
        return res.ValueKind == System.Text.Json.JsonValueKind.String
            ? string.Equals(res.GetString(), "OK", StringComparison.OrdinalIgnoreCase)
            : res.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    public async Task<bool> StopMailingAsync(int id, CancellationToken ct = default)
    {
        var res = await CallAsync<System.Text.Json.JsonElement>("stopMailing",
            new Dictionary<string, object?> { ["id"] = id }, ct);
        return res.ValueKind == System.Text.Json.JsonValueKind.String
            ? string.Equals(res.GetString(), "OK", StringComparison.OrdinalIgnoreCase)
            : res.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    /// <summary>Изменяет одно из вложенных полей settings рассылки — sender/list/message/preset —
    /// через <c>editMailing</c> с <c>settings.{key}.id = value</c>.</summary>
    public async Task<bool> SetMailingComponentAsync(int mailingId, string key, int refId, CancellationToken ct = default)
    {
        var res = await CallAsync<System.Text.Json.JsonElement>("editMailing",
            new Dictionary<string, object?>
            {
                ["id"] = mailingId,
                ["settings"] = new Dictionary<string, object?>
                {
                    [key] = new Dictionary<string, object?> { ["id"] = refId }
                },
            }, ct);
        return res.ValueKind == System.Text.Json.JsonValueKind.String
            ? string.Equals(res.GetString(), "OK", StringComparison.OrdinalIgnoreCase)
            : res.ValueKind == System.Text.Json.JsonValueKind.True;
    }

    public Task<bool> DeleteMailingAsync(int id, CancellationToken ct = default) =>
        CallAsync<bool>("deleteMailing", new Dictionary<string, object?> { ["id"] = id }, ct);

    public Task<bool> RestartRunningMailingsAsync(CancellationToken ct = default) =>
        CallAsync<bool>("restartRunningMailings", null, ct);

    public async Task<int> AddMailingAsync(AmsMailingCreate m, CancellationToken ct = default)
    {
        var p = new Dictionary<string, object?>
        {
            ["name"] = m.Name,
            ["type"] = m.Type,
            ["senderAccountId"] = m.SenderAccountId,
            ["messageId"] = m.MessageId,
            ["deliveryPresetId"] = m.DeliveryPresetId,
            ["mailingListIds"] = m.MailingListIds,
        };
        var r = await CallAsync<AmsAddResult>("addMailing", p, ct);
        return r?.Id ?? 0;
    }

    public Task<bool> EditMailingAsync(int id, IDictionary<string, object?> fields, CancellationToken ct = default)
    {
        var p = new Dictionary<string, object?>(fields) { ["id"] = id };
        return CallAsync<bool>("editMailing", p, ct);
    }

    // ── Учётные записи отправителя ───────────────────────────────────

    public Task<List<AmsSenderAccount>?> GetSenderAccountsAsync(CancellationToken ct = default) =>
        CallAsync<List<AmsSenderAccount>>("getSenderAccounts", null, ct);

    public Task<AmsSenderAccount?> GetSenderAccountAsync(int id, CancellationToken ct = default) =>
        CallAsync<AmsSenderAccount>("getSenderAccount", new Dictionary<string, object?> { ["id"] = id }, ct);

    public Task<bool> DeleteSenderAccountAsync(int id, CancellationToken ct = default) =>
        CallAsync<bool>("deleteSenderAccount", new Dictionary<string, object?> { ["id"] = id }, ct);

    // ── Списки рассылки ──────────────────────────────────────────────

    public Task<List<AmsMailingList>?> GetMailingListsAsync(CancellationToken ct = default) =>
        CallAsync<List<AmsMailingList>>("getMailingLists", null, ct);

    public Task<int> GetMailingListSizeAsync(int id, CancellationToken ct = default) =>
        CallAsync<int>("getMailingListSize", new Dictionary<string, object?> { ["id"] = id }, ct);

    public Task<bool> ClearMailingListAsync(int id, CancellationToken ct = default) =>
        CallAsync<bool>("clearMailingList", new Dictionary<string, object?> { ["id"] = id }, ct);

    // ── Письма ───────────────────────────────────────────────────────

    public Task<List<AmsMessage>?> GetMessagesAsync(CancellationToken ct = default) =>
        CallAsync<List<AmsMessage>>("getMessages", null, ct);

    public Task<AmsMessage?> GetMessageAsync(int id, CancellationToken ct = default) =>
        CallAsync<AmsMessage>("getMessage", new Dictionary<string, object?> { ["id"] = id }, ct);

    public Task<bool> DeleteMessageAsync(int id, CancellationToken ct = default) =>
        CallAsync<bool>("deleteMessage", new Dictionary<string, object?> { ["id"] = id }, ct);

    // ── Профили отправки ─────────────────────────────────────────────

    public Task<List<AmsDeliveryPreset>?> GetDeliveryPresetsAsync(CancellationToken ct = default) =>
        CallAsync<List<AmsDeliveryPreset>>("getDeliveryPresets", null, ct);

    public Task<AmsDeliveryPreset?> GetDeliveryPresetAsync(int id, CancellationToken ct = default) =>
        CallAsync<AmsDeliveryPreset>("getDeliveryPreset", new Dictionary<string, object?> { ["id"] = id }, ct);

    public Task<bool> DeleteDeliveryPresetAsync(int id, CancellationToken ct = default) =>
        CallAsync<bool>("deleteDeliveryPreset", new Dictionary<string, object?> { ["id"] = id }, ct);

    // ── Helpers ──────────────────────────────────────────────────────

    /// <summary>Кодирует строку в base64 UTF-8 — для полей типа тела письма.</summary>
    public static string ToBase64(string s) =>
        string.IsNullOrEmpty(s) ? "" : Convert.ToBase64String(Encoding.UTF8.GetBytes(s));

    /// <summary>
    /// Раскодирует base64 обратно в строку. Пробует UTF-8, при обнаружении mojibake
    /// (replacement chars U+FFFD или паттерны cp1251→utf-8) откатывается на CP1251 —
    /// старый AMS/BSPdev часто хранит русские тексты в windows-1251.
    /// </summary>
    public static string FromBase64(string s)
    {
        if (string.IsNullOrEmpty(s)) return "";
        try
        {
            var bytes = Convert.FromBase64String(s);
            // Смотрим на исходные байты — если это валидный UTF-8 без «сломанных»
            // последовательностей, используем UTF-8.
            if (IsLikelyUtf8(bytes))
                return Encoding.UTF8.GetString(bytes);
            // Иначе — CP1251 (типичный fallback для legacy-Windows софта).
            Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(1251).GetString(bytes);
        }
        catch { return s; }
    }

    /// <summary>Быстрая проверка: похоже ли что байты — валидный UTF-8. Работает даже
    /// для чистого ASCII (там UTF-8 = CP1251 = tests true).</summary>
    private static bool IsLikelyUtf8(byte[] bytes)
    {
        try
        {
            // Encoder с throwOnInvalidBytes = true бросит если байты кривые.
            var strict = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true);
            _ = strict.GetString(bytes);
            return true;
        }
        catch { return false; }
    }

    private static string Trunc(string s, int max) =>
        string.IsNullOrEmpty(s) ? "" : (s.Length > max ? s[..max] + "…" : s);

    public void Dispose() => _http.Dispose();
}

public class AmsApiException : Exception
{
    public AmsApiException(string message) : base(message) { }
}
