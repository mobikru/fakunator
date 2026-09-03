using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core;

/// <summary>
/// HTTP client for OpenAI and Anthropic APIs. Sends batches of emails for
/// gender + country classification and parses results.
/// </summary>
public class AiClient : IDisposable
{
    private readonly string _provider;   // "openai" or "anthropic"
    private readonly string _apiKey;
    private readonly string _model;
    private readonly HttpClient _http;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Публичный доступ к промпту для fingerprint'а в AnalyzeCache.</summary>
    public static string GetSystemPrompt() => SystemPrompt;

    private const string SystemPrompt = @"You are a name-origin classifier. For each email address, analyze the local part (before @) and the domain to determine:
1. Name: the likely first name extracted from the local part (capitalize properly, e.g. ""Valentina"", ""Gideon"")
2. Gender: M (male), F (female), N (truly ambiguous/unisex only), ? (no name found at all)
3. Country: ISO 3166-1 alpha-2 code (e.g. US, RU, DE, JP)

CRITICAL — be decisive, not cautious:
- GENDER: Return M or F whenever a name is >60% one gender in real usage. Reserve N ONLY for genuinely unisex names (Alex, Sam, Sasha, Chris, Taylor, Jordan, Casey, Jamie). Names like Shawn/Sean/Ryan → M. Sherrie/Michelle/Ashley → F. Do NOT default to N when unsure — pick the more likely one.
- COUNTRY: Return a country code whenever the name has ANY cultural origin, even for generic domains (gmail.com, yahoo.com, outlook.com). Do NOT default to ""?"". Examples:
  * Shawn/John/Michael/Jennifer → US
  * Ivan/Olga/Dmitri/Anastasia → RU
  * Hiroshi/Yuki/Takeshi/Shibuya → JP
  * Mohammed/Ahmed/Fatima → SA (or EG/MA depending on script)
  * Wei/Li/Chen/Wang → CN
  * Priya/Rajesh/Arjun → IN
  * Shayndel/Chaim/Yitzhak → IL (Jewish names)
  * Klaus/Hans/Wolfgang → DE
- The domain overrides name origin when it's country-specific: mail.ru/yandex.ru → RU regardless of name; .de/.fr/.jp → that country.

USE ""?"" ONLY when:
- Local part has no name at all (random hash, pure numbers, business name like ""sheltonbuilds"")
- Name is a real word from an unknown culture that has no plausible country

Return ONLY a JSON object with a ""results"" array, no markdown, no explanation:
{""results"": [{""email"": ""..."", ""name"": ""..."", ""gender"": ""M|F|N|?"", ""iso"": ""XX|?""}]}

One object in results per email, same order as input.";

    public AiClient(string provider, string apiKey, string model)
    {
        _provider = provider.ToLowerInvariant();
        _apiKey = apiKey;
        _model = model;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(120) };
    }

    /// <summary>
    /// Send a batch of emails for AI classification.
    /// Returns list of AnalysisResult with Source="ai".
    /// Also outputs token counts via out parameters.
    /// </summary>
    public async Task<(List<AnalysisResult> Results, int InputTokens, int OutputTokens)>
        AnalyzeBatchAsync(List<string> emails, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(_apiKey))
            throw new InvalidOperationException($"API key for {_provider} is not configured.");

        var userMessage = "Classify these emails:\n" + string.Join("\n", emails);

        int retries = 0;
        const int maxRetries = 3;

        while (true)
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                return _provider switch
                {
                    "anthropic" => await CallAnthropicAsync(userMessage, emails, ct),
                    _ => await CallOpenAiAsync(userMessage, emails, ct),
                };
            }
            catch (HttpRequestException ex) when (
                ex.StatusCode == System.Net.HttpStatusCode.TooManyRequests && retries < maxRetries)
            {
                retries++;
                var delay = TimeSpan.FromSeconds(Math.Pow(2, retries));
                await Task.Delay(delay, ct);
            }
        }
    }

    // ── Structured output schema (для OpenAI json_schema и Anthropic tools) ─
    // Обёртка {results:[...]} обязательна: OpenAI не поддерживает root-массив
    // в strict-режиме; Anthropic принимает любой object.
    private static object BuildJsonSchema() => new
    {
        type = "object",
        properties = new
        {
            results = new
            {
                type = "array",
                items = new
                {
                    type = "object",
                    properties = new
                    {
                        email = new { type = "string" },
                        name = new { type = "string" },
                        gender = new { type = "string", @enum = new[] { "M", "F", "N", "?" } },
                        iso = new { type = "string" },
                    },
                    required = new[] { "email", "name", "gender", "iso" },
                    additionalProperties = false,
                },
            },
        },
        required = new[] { "results" },
        additionalProperties = false,
    };

    // ── OpenAI ──────────────────────────────────────────────────────────

    private async Task<(List<AnalysisResult>, int, int)>
        CallOpenAiAsync(string userMessage, List<string> emails, CancellationToken ct)
    {
        var body = new
        {
            model = _model,
            messages = new object[]
            {
                new { role = "system", content = SystemPrompt },
                new { role = "user", content = userMessage },
            },
            temperature = 0.1,
            max_tokens = emails.Count * 60 + 200,
            // JSON mode (не strict json_schema) — простой и совместимый со всеми
            // gpt-4o/4.1/mini моделями. Гарантирует что ответ — валидный JSON.
            // Strict json_schema давал 400 на старых gpt-4o-mini и подвешивал retry-split.
            response_format = new { type = "json_object" },
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.openai.com/v1/chat/completions");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var respJson = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(respJson);
        var root = doc.RootElement;

        var content = root
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content")
            .GetString() ?? "";

        int inputTokens = 0, outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("prompt_tokens", out var pt))
                inputTokens = pt.GetInt32();
            if (usage.TryGetProperty("completion_tokens", out var ct2))
                outputTokens = ct2.GetInt32();
        }

        var results = ParseAiResponse(content, emails);
        return (results, inputTokens, outputTokens);
    }

    // ── Anthropic ───────────────────────────────────────────────────────

    private async Task<(List<AnalysisResult>, int, int)>
        CallAnthropicAsync(string userMessage, List<string> emails, CancellationToken ct)
    {
        // Anthropic structured output = tools API с tool_choice форсированием.
        // Модель обязана вернуть вызов инструмента, аргументы приходят как чистый object.
        var body = new
        {
            model = _model,
            max_tokens = emails.Count * 60 + 200,
            system = SystemPrompt,
            messages = new object[]
            {
                new { role = "user", content = userMessage },
            },
            tools = new object[]
            {
                new
                {
                    name = "classify_emails",
                    description = "Return name/gender/country classification for each email.",
                    input_schema = BuildJsonSchema(),
                },
            },
            tool_choice = new { type = "tool", name = "classify_emails" },
        };

        var json = JsonSerializer.Serialize(body, JsonOpts);
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://api.anthropic.com/v1/messages");
        req.Content = new StringContent(json, Encoding.UTF8, "application/json");
        req.Headers.Add("x-api-key", _apiKey);
        req.Headers.Add("anthropic-version", "2023-06-01");

        using var resp = await _http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();

        var respJson = await resp.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(respJson);
        var root = doc.RootElement;

        // Ищем tool_use block в content array — там наш structured результат.
        string content = "";
        foreach (var block in root.GetProperty("content").EnumerateArray())
        {
            if (block.TryGetProperty("type", out var t) && t.GetString() == "tool_use"
                && block.TryGetProperty("input", out var input))
            {
                content = input.GetRawText();
                break;
            }
        }

        int inputTokens = 0, outputTokens = 0;
        if (root.TryGetProperty("usage", out var usage))
        {
            if (usage.TryGetProperty("input_tokens", out var it))
                inputTokens = it.GetInt32();
            if (usage.TryGetProperty("output_tokens", out var ot))
                outputTokens = ot.GetInt32();
        }

        var results = ParseAiResponse(content, emails);
        return (results, inputTokens, outputTokens);
    }

    // ── Response parsing ────────────────────────────────────────────────

    private static List<AnalysisResult> ParseAiResponse(string content, List<string> emails)
    {
        // Собираем ответы в dict по email, потом сопоставляем с исходным списком.
        // Раньше матчили по индексу — если модель пропускала/переставляла emails,
        // потерянные исходные emails просто исчезали из результатов (silent data loss).
        var byEmail = new Dictionary<string, AnalysisResult>(StringComparer.OrdinalIgnoreCase);

        content = content.Trim();
        if (content.StartsWith("```"))
        {
            var firstNl = content.IndexOf('\n');
            if (firstNl >= 0) content = content[(firstNl + 1)..];
            if (content.EndsWith("```"))
                content = content[..^3].TrimEnd();
        }

        bool parseError = false;
        try
        {
            using var doc = JsonDocument.Parse(content);
            var root = doc.RootElement;

            // Structured outputs возвращают { "results": [...] } — распаковываем.
            // Fallback: старые модели / non-structured mode могут вернуть голый array.
            JsonElement arr;
            if (root.ValueKind == JsonValueKind.Object
                && root.TryGetProperty("results", out var resultsProp)
                && resultsProp.ValueKind == JsonValueKind.Array)
            {
                arr = resultsProp;
            }
            else
            {
                arr = root;
            }

            if (arr.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var item in arr.EnumerateArray())
                {
                    var email = item.TryGetProperty("email", out var ep)
                        ? ep.GetString() ?? (i < emails.Count ? emails[i] : $"unknown_{i}")
                        : (i < emails.Count ? emails[i] : $"unknown_{i}");

                    var gender = item.TryGetProperty("gender", out var gp)
                        ? gp.GetString() ?? "?" : "?";

                    var iso = item.TryGetProperty("iso", out var ip)
                        ? ip.GetString() ?? "?" : "?";

                    var name = item.TryGetProperty("name", out var np)
                        ? np.GetString() ?? "" : "";
                    if (string.IsNullOrEmpty(name))
                        name = ExtractNameFromEmail(email);

                    byEmail[email] = new AnalysisResult(
                        email, gender.ToUpperInvariant(), iso.ToUpperInvariant(), "ai")
                        { NameToken = name };
                    i++;
                }
            }
        }
        catch
        {
            parseError = true;
        }

        // Сопоставляем: для каждого исходного email — либо ответ AI, либо stub.
        var results = new List<AnalysisResult>(emails.Count);
        foreach (var email in emails)
        {
            if (byEmail.TryGetValue(email, out var r))
            {
                results.Add(r);
            }
            else
            {
                var signal = parseError ? "parse_error" : "missing_from_response";
                results.Add(new AnalysisResult(email, "?", "?", "ai")
                    { NameToken = ExtractNameFromEmail(email), Signals = signal });
            }
        }
        return results;
    }

    private static string ExtractNameFromEmail(string email)
    {
        var at = email.IndexOf('@');
        if (at <= 0) return "";
        var local = email[..at];
        // Find longest alphabetic token ≥ 3 chars, capitalize
        string best = "";
        int start = -1;
        for (int i = 0; i <= local.Length; i++)
        {
            bool isLet = i < local.Length && char.IsLetter(local[i]);
            if (isLet && start < 0) start = i;
            else if (!isLet && start >= 0)
            {
                int len = i - start;
                if (len >= 3 && len > best.Length)
                    best = local.Substring(start, len);
                start = -1;
            }
        }
        if (best.Length == 0) return "";
        return char.ToUpper(best[0]) + best[1..].ToLower();
    }

    /// <summary>
    /// Estimate cost in USD for given token counts.
    /// </summary>
    public static double EstimateCost(string model, int inputTokens, int outputTokens)
    {
        // Pricing per million tokens
        var (inputPer1M, outputPer1M) = model.ToLowerInvariant() switch
        {
            "gpt-4o-mini" => (0.15, 0.60),
            "gpt-4o" => (2.50, 10.00),
            "gpt-4.1-mini" => (0.40, 1.60),
            "gpt-4.1-nano" => (0.10, 0.40),
            "claude-haiku-4-5" or "claude-3-5-haiku-20241022" => (0.80, 4.00),
            "claude-sonnet-4-5" or "claude-3-5-sonnet-20241022" => (3.00, 15.00),
            _ => (0.15, 0.60), // default to gpt-4o-mini pricing
        };

        return inputTokens / 1_000_000.0 * inputPer1M
             + outputTokens / 1_000_000.0 * outputPer1M;
    }

    public void Dispose()
    {
        _http.Dispose();
    }
}
