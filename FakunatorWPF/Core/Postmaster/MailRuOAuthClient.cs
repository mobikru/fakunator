using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core.Postmaster;

/// <summary>
/// Клиент OAuth 2.0 для Mail.ru (Postmaster API).
/// Поддерживает Resource Owner Password Grant (логин+пароль → tokens) и
/// Refresh Token Grant (refresh_token → новый access_token).
///
/// Client ID для Postmaster API: <c>postmaster_api_client</c> — публичный,
/// client_secret не требуется.
///
/// Access token живёт 1 час, refresh token — постоянный (пока юзер не
/// сгенерирует новый в панели mail.ru).
/// </summary>
public class MailRuOAuthClient : IDisposable
{
    private const string TokenUrl = "https://o2.mail.ru/token";
    public const string PostmasterClientId = "postmaster_api_client";

    private readonly HttpClient _http;

    public MailRuOAuthClient()
    {
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        // Mail.ru OAuth отклоняет запросы без User-Agent с ошибкой
        // "invalid_request: missing user-agent header". Ставим свой.
        _http.DefaultRequestHeaders.UserAgent.ParseAdd("Fakunator/2.0 (Windows)");
    }

    /// <summary>
    /// Обмен логин+пароль на access + refresh токены.
    /// </summary>
    public async Task<OAuthTokens> LoginWithPasswordAsync(string username, string password,
        string clientId = PostmasterClientId, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "password",
            ["client_id"] = clientId,
            ["username"] = username,
            ["password"] = password,
        };
        return await PostTokenAsync(form, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Обновление access_token по refresh_token.
    /// </summary>
    public async Task<OAuthTokens> RefreshAccessAsync(string refreshToken,
        string clientId = PostmasterClientId, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "refresh_token",
            ["client_id"] = clientId,
            ["refresh_token"] = refreshToken,
        };
        return await PostTokenAsync(form, ct).ConfigureAwait(false);
    }

    private async Task<OAuthTokens> PostTokenAsync(Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, TokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };
        using var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Mail.ru при ошибке возвращает JSON с полями error/error_description
        if (!resp.IsSuccessStatusCode)
        {
            var errMsg = TryParseError(body) ?? $"HTTP {(int)resp.StatusCode}: {body}";
            throw new MailRuAuthException(errMsg);
        }

        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;

            // Возможно ошибка в 200 OK ответе (иногда mail.ru так возвращает)
            if (root.TryGetProperty("error", out _))
            {
                var errMsg = TryParseError(body) ?? "Ошибка OAuth";
                throw new MailRuAuthException(errMsg);
            }

            var access = root.GetProperty("access_token").GetString() ?? "";
            var refresh = root.TryGetProperty("refresh_token", out var rt) ? rt.GetString() ?? "" : "";
            var expIn = root.TryGetProperty("expires_in", out var ei) && ei.TryGetInt32(out var v) ? v : 3600;
            var tokenType = root.TryGetProperty("token_type", out var tt) ? tt.GetString() ?? "Bearer" : "Bearer";

            if (string.IsNullOrEmpty(access))
                throw new MailRuAuthException("Ответ не содержит access_token");

            return new OAuthTokens(access, refresh, expIn, tokenType, DateTime.UtcNow.AddSeconds(expIn - 60));
        }
        catch (JsonException)
        {
            throw new MailRuAuthException($"Не удалось разобрать ответ: {body}");
        }
    }

    private static string? TryParseError(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var err = root.TryGetProperty("error", out var e) ? e.GetString() : null;
            var desc = root.TryGetProperty("error_description", out var d) ? d.GetString() : null;
            if (err == null && desc == null) return null;
            if (err != null && desc != null) return $"{err}: {desc}";
            return err ?? desc;
        }
        catch { return null; }
    }

    public void Dispose() => _http.Dispose();
}

/// <summary>Пара токенов OAuth + время истечения access_token.</summary>
public record OAuthTokens(string AccessToken, string RefreshToken, int ExpiresIn, string TokenType, DateTime AccessExpiresAt);

public class MailRuAuthException : Exception
{
    public MailRuAuthException(string message) : base(message) { }
}
