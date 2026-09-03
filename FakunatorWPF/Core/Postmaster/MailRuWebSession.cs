using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core.Postmaster;

/// <summary>
/// Web-сессия постмастера mail.ru для действий которых нет в публичном OAuth API:
/// добавление домена, получение строки верификации, запуск проверки TXT-записи.
/// Всё это делается через веб-формы (Django + CSRF), поэтому нужны username+password.
///
/// Flow:
///  1. GET  account.mail.ru/login       — получить начальные cookies
///  2. POST auth.mail.ru/cgi-bin/auth   — залогиниться, получить Mpop/act/mrcu/...
///  3. GET  postmaster.mail.ru/add      — получить cookie csrftoken + HTML с csrfmiddlewaretoken
///  4. POST postmaster.mail.ru/add/     — форма {name=DOMAIN, csrfmiddlewaretoken=T} → 302 → /DOMAIN/verify/
///  5. GET  /DOMAIN/verify/             — парсим mailru-verification:HEX
///  6. POST /DOMAIN/verify/             — запуск проверки TXT-записи (после того как её создали в DNS)
/// </summary>
public class MailRuWebSession : IDisposable
{
    private readonly HttpClient _http;
    private readonly HttpClientHandler _handler;
    private bool _loggedIn;

    public string Username { get; }

    public MailRuWebSession(string username)
    {
        Username = username;
        _handler = new HttpClientHandler
        {
            UseCookies = true,
            CookieContainer = new CookieContainer(),
            AllowAutoRedirect = true,
        };
        _http = new HttpClient(_handler) { Timeout = TimeSpan.FromSeconds(30) };
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/131.0.0.0 Safari/537.36");
        _http.DefaultRequestHeaders.AcceptLanguage.ParseAdd("ru-RU,ru;q=0.9,en;q=0.8");
    }

    /// <summary>
    /// Логинится в аккаунт пользователя и открывает сессию постмастера.
    /// Проверяет что Mpop cookie получен. Возвращает true если успех.
    /// </summary>
    public async Task LoginAsync(string password, CancellationToken ct = default)
    {
        // Шаг 1: базовые cookies с login-страницы
        await _http.GetAsync("https://account.mail.ru/login", ct).ConfigureAwait(false);

        // Шаг 2: классическая форма аутентификации mail.ru
        var loginLocal = Username.Contains('@') ? Username.Split('@')[0] : Username;
        var domain = Username.Contains('@') ? Username.Split('@')[1] : "mail.ru";
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["Login"] = loginLocal,
            ["Password"] = password,
            ["Domain"] = domain,
            ["saveauth"] = "1",
            ["FailPage"] = "",
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://auth.mail.ru/cgi-bin/auth") { Content = form };
        req.Headers.Referrer = new Uri("https://account.mail.ru/login");
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // Проверка: должно быть Mpop cookie на mail.ru
        bool hasMpop = false;
        foreach (Cookie c in _handler.CookieContainer.GetCookies(new Uri("https://mail.ru")))
            if (c.Name == "Mpop") hasMpop = true;

        if (!hasMpop)
        {
            // Извлекаем title если есть — там может быть текст ошибки
            var t = Regex.Match(body, @"<title>(.*?)</title>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
            var titleText = t.Success ? t.Groups[1].Value.Trim() : "";
            throw new MailRuWebException(
                $"Не удалось войти в mail.ru как {Username}. " +
                (string.IsNullOrEmpty(titleText) ? "Проверь пароль." : $"Ответ: {titleText}"));
        }

        // Шаг 3: активируем сессию постмастера — csrftoken придёт с этой страницы
        await _http.GetAsync("https://postmaster.mail.ru/add", ct).ConfigureAwait(false);
        _loggedIn = true;
    }

    /// <summary>
    /// Добавляет домен в постмастер и возвращает строку верификации TXT-записи.
    /// Формат возврата — hex-значение которое нужно вписать в TXT как «mailru-verification: HEX».
    /// </summary>
    public async Task<string> AddDomainAndGetTxtAsync(string domainName, CancellationToken ct = default)
    {
        if (!_loggedIn) throw new InvalidOperationException("Session not logged in — вызови LoginAsync первым.");

        // Обновляем HTML формы и получаем csrfmiddlewaretoken
        var addPage = await _http.GetStringAsync("https://postmaster.mail.ru/add", ct).ConfigureAwait(false);
        var token = ExtractFormToken(addPage);
        if (string.IsNullOrEmpty(token))
            throw new MailRuWebException("Не нашёл токен формы на странице /add — возможно, mail.ru поменял разметку.");

        // POST на добавление домена
        var form = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["name"] = domainName,
            ["csrfmiddlewaretoken"] = token,
        });
        using var req = new HttpRequestMessage(HttpMethod.Post, "https://postmaster.mail.ru/add/") { Content = form };
        req.Headers.Referrer = new Uri("https://postmaster.mail.ru/add/");
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        // После редиректа URL должен быть /DOMAIN/verify/ — на этой странице строка верификации
        var finalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? "";
        if (!finalUrl.Contains("/verify", StringComparison.OrdinalIgnoreCase))
        {
            // Может, домен уже есть — тогда пробуем сразу GET /DOMAIN/verify/
            var verifyUrl = $"https://postmaster.mail.ru/{domainName}/verify/";
            body = await _http.GetStringAsync(verifyUrl, ct).ConfigureAwait(false);
        }

        var verification = ExtractVerificationCode(body);
        if (string.IsNullOrEmpty(verification))
            throw new MailRuWebException(
                $"Домен {domainName} добавлен, но не нашёл строку mailru-verification в HTML ответа. " +
                $"URL: {finalUrl}");
        return verification;
    }

    /// <summary>
    /// Запускает проверку TXT-записи на стороне постмастера
    /// (та самая кнопка «Подтвердить» на странице /DOMAIN/verify/).
    /// Возвращает true если ответ содержит признаки успеха.
    /// </summary>
    /// <summary>
    /// Запускает DNS-верификацию (verifier=3, TXT-запись).
    ///
    /// Flow (из JS постмастера /DOMAIN/verify/):
    ///   1. POST /DOMAIN/verify/ с form {verifier: 3} + header X-CSRFToken → {status:"pending", task_id, message}
    ///   2. Ждём 10 сек (как в JS постмастера)
    ///   3. POST /DOMAIN/verify/ с form {task_id: XXX} + header X-CSRFToken → {status:"success"|"failed", message}
    /// </summary>
    public async Task<VerifyRequestResult> RequestVerifyAsync(string domainName, CancellationToken ct = default)
    {
        if (!_loggedIn) throw new InvalidOperationException("Session not logged in.");

        var verifyUrl = $"https://postmaster.mail.ru/{domainName}/verify/";

        // Открываем страницу — этим стимулируем свежий csrftoken cookie
        await _http.GetStringAsync(verifyUrl, ct).ConfigureAwait(false);

        var cookieCsrf = "";
        foreach (Cookie c in _handler.CookieContainer.GetCookies(new Uri("https://postmaster.mail.ru")))
            if (c.Name == "csrftoken") cookieCsrf = c.Value;
        if (string.IsNullOrEmpty(cookieCsrf))
            throw new MailRuWebException("Не получил csrftoken cookie с /verify/-страницы.");

        // Шаг 1: verifier=3 (DNS-проверка через TXT)
        var start = await PostVerifyAsync(verifyUrl, cookieCsrf,
            new Dictionary<string, string> { ["verifier"] = "3" }, ct).ConfigureAwait(false);

        if (!string.Equals(start.Status, "pending", StringComparison.OrdinalIgnoreCase))
        {
            // mail.ru сразу отдал финальный ответ (успех/провал) без task_id — редкий случай
            return start;
        }
        if (string.IsNullOrEmpty(start.TaskId))
            throw new MailRuWebException("mail.ru вернул pending без task_id — не могу дождаться результата.");

        // Шаг 2: ждём результат по task_id. JS mail.ru ждёт ровно 10 сек и делает 1 запрос.
        // Мы делаем polling — до 6 попыток с интервалом 10 сек, чтобы застать медленный DNS.
        for (int attempt = 0; attempt < 6; attempt++)
        {
            await Task.Delay(10000, ct).ConfigureAwait(false);
            var check = await PostVerifyAsync(verifyUrl, cookieCsrf,
                new Dictionary<string, string> { ["task_id"] = start.TaskId }, ct).ConfigureAwait(false);
            if (check.IsSuccess || check.IsFailed)
                return check;
            // если снова pending — крутимся дальше
        }
        // Не дождались за 60 сек
        return new VerifyRequestResult
        {
            HttpStatus = 200,
            FinalUrl = verifyUrl,
            Status = "pending",
            BodySnippet = $"task_id={start.TaskId} — за 60 сек mail.ru не ответил success/failed",
        };
    }

    private async Task<VerifyRequestResult> PostVerifyAsync(
        string verifyUrl, string cookieCsrf, Dictionary<string, string> data, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, verifyUrl)
        {
            Content = new FormUrlEncodedContent(data),
        };
        req.Headers.Referrer = new Uri(verifyUrl);
        req.Headers.Add("X-CSRFToken", cookieCsrf);
        req.Headers.Add("X-Requested-With", "XMLHttpRequest");
        var resp = await _http.SendAsync(req, ct).ConfigureAwait(false);
        var body = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);

        var result = new VerifyRequestResult
        {
            HttpStatus = (int)resp.StatusCode,
            FinalUrl = resp.RequestMessage?.RequestUri?.ToString() ?? verifyUrl,
            BodySnippet = body.Length > 400 ? body.Substring(0, 400) : body,
        };
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("status", out var s)) result.Status = s.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("message", out var m)) result.Message = m.GetString() ?? "";
            if (doc.RootElement.TryGetProperty("task_id", out var t))
            {
                if (t.ValueKind == System.Text.Json.JsonValueKind.String) result.TaskId = t.GetString() ?? "";
                else if (t.ValueKind == System.Text.Json.JsonValueKind.Number) result.TaskId = t.GetRawText();
            }
            if (doc.RootElement.TryGetProperty("error", out var e)) result.Error = e.GetString() ?? "";
        }
        catch { }
        return result;
    }

    /// <summary>Ищем csrfmiddlewaretoken в форме страницы Django.</summary>
    private static string ExtractFormToken(string html)
    {
        // Стандартный формат: <input type="hidden" name="csrfmiddlewaretoken" value="XXX">
        var m = Regex.Match(html,
            @"<input[^>]*name=[""']csrfmiddlewaretoken[""'][^>]*value=[""']([^""']+)[""']",
            RegexOptions.IgnoreCase);
        if (m.Success) return m.Groups[1].Value;
        // Иногда порядок атрибутов другой
        m = Regex.Match(html,
            @"<input[^>]*value=[""']([^""']+)[""'][^>]*name=[""']csrfmiddlewaretoken[""']",
            RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    /// <summary>Ищем "mailru-verification: HEX" в HTML страницы /DOMAIN/verify/.</summary>
    private static string ExtractVerificationCode(string html)
    {
        var m = Regex.Match(html, @"mailru-verification[""'\s:]+([0-9a-fA-F]{16,})", RegexOptions.IgnoreCase);
        return m.Success ? m.Groups[1].Value : "";
    }

    public void Dispose()
    {
        _http.Dispose();
        _handler.Dispose();
    }
}

public class MailRuWebException : Exception
{
    public MailRuWebException(string message) : base(message) { }
}

/// <summary>Диагностика ответа /DOMAIN/verify/ — что реально прилетело от mail.ru.</summary>
public class VerifyRequestResult
{
    public int HttpStatus { get; set; }
    public string FinalUrl { get; set; } = "";
    public string BodySnippet { get; set; } = "";
    public string Status { get; set; } = "";  // "success" | "failed" | "pending" | ""
    public string Message { get; set; } = "";
    public string TaskId { get; set; } = "";
    public string Error { get; set; } = "";
    public bool IsSuccess => string.Equals(Status, "success", StringComparison.OrdinalIgnoreCase);
    public bool IsFailed => string.Equals(Status, "failed", StringComparison.OrdinalIgnoreCase);
}
