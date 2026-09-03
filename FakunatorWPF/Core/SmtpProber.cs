using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core;

public class SmtpProber
{
    // Дефолты для HELO/MAIL FROM. HELO авто-определяется через reverse DNS прокси
    // (как делают нормальные валидаторы — Gmail/Yahoo не блочат, IP совпадает с PTR).
    // MAIL FROM "<>" — null sender (RFC 5321), самый совместимый для RCPT-only.
    private const string FallbackHeloDomain = "verifier-mx.com";
    private const string DefaultMailFrom = "<>";

    // Юзер-настройки. Пусто = авто.
    private readonly string _userHelo;
    private readonly string _userMailFrom;

    // Кэш rDNS per proxy host → hostname. Один lookup на прокси за весь прогон.
    private static readonly ConcurrentDictionary<string, string> _rdnsCache =
        new(StringComparer.OrdinalIgnoreCase);

    public SmtpProber(string? heloDomain = null, string? mailFromAddress = null)
    {
        _userHelo = string.IsNullOrWhiteSpace(heloDomain) ? "" : heloDomain.Trim();
        _userMailFrom = string.IsNullOrWhiteSpace(mailFromAddress) ? "" : mailFromAddress.Trim();
    }

    /// <summary>
    /// Возвращает HELO-строку для данного прокси. Если юзер задал — возвращает её.
    /// Иначе делает reverse DNS lookup IP прокси и возвращает hostname (например,
    /// "v771311.hosted-by-vdsina.com"). Результат кэшируется per proxy host.
    ///
    /// ВАЖНО: Dns.GetHostEntryAsync на Windows игнорирует CancellationToken и может
    /// висеть на системном syscall. Поэтому используем Task.WhenAny с жёстким таймаутом
    /// (не связанным с outer ct), чтобы зависший DNS не утаскивал весь probe-timeout.
    /// </summary>
    private async Task<string> ResolveHeloAsync(ProxySpec proxy, CancellationToken ct)
    {
        if (!string.IsNullOrEmpty(_userHelo)) return _userHelo;

        // Direct-режим: HELO fallback (реальный домен с MX), rDNS домашнего IP скорее всего
        // "broadband-XX.isp.ru" — Gmail такое сразу дропает. verifier-mx.com безопаснее.
        if (proxy.IsDirect) return FallbackHeloDomain;

        if (_rdnsCache.TryGetValue(proxy.Host, out var cached)) return cached;

        // proxy.Host уже hostname — не делаем DNS, юзаем как HELO
        if (!IPAddress.TryParse(proxy.Host, out _))
        {
            _rdnsCache[proxy.Host] = proxy.Host;
            return proxy.Host;
        }

        // Reverse DNS с hard timeout 2с (Task.WhenAny, не зависит от внутреннего CT API)
        try
        {
            var dnsTask = Dns.GetHostEntryAsync(proxy.Host);
            var timeoutTask = Task.Delay(2000, ct);
            var winner = await Task.WhenAny(dnsTask, timeoutTask);

            if (winner == dnsTask)
            {
                var entry = await dnsTask;
                if (!string.IsNullOrEmpty(entry.HostName)
                    && entry.HostName.Contains('.')
                    && entry.HostName != proxy.Host)
                {
                    _rdnsCache[proxy.Host] = entry.HostName;
                    return entry.HostName;
                }
            }
            // else: timeout сработал — забываем про dnsTask, fallback ниже
        }
        catch { }

        _rdnsCache[proxy.Host] = FallbackHeloDomain;
        return FallbackHeloDomain;
    }

    /// <summary>Возвращает MAIL FROM строку (с угловыми скобками).</summary>
    private string BuildMailFrom(string helo)
    {
        if (string.IsNullOrEmpty(_userMailFrom)) return DefaultMailFrom;

        // Юзер указал. Можно с скобками или без.
        var s = _userMailFrom;
        return s.StartsWith('<') && s.EndsWith('>') ? s : $"<{s}>";
    }

    /// <summary>
    /// Probe a single email via SOCKS5 proxy → MX:25 SMTP handshake.
    /// EHLO → MAIL FROM → RCPT TO → classify → QUIT.
    /// </summary>
    public async Task<SmtpVerdict> ProbeAsync(
        string email, string mxHost, ProxySpec proxy, int timeoutMs, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var proxyLabel = proxy.IsDirect ? "DIRECT" : $"{proxy.Host}:{proxy.Port}";
        Socket? sock = null;
        NetworkStream? stream = null;

        try
        {
            sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sock.NoDelay = true;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            // DIRECT: коннект напрямую с локального IP на MX:25 (в обход SOCKS5)
            if (proxy.IsDirect)
            {
                var mxAddresses = await Dns.GetHostAddressesAsync(mxHost, timeoutCts.Token);
                if (mxAddresses.Length == 0)
                    return MakeError(email, mxHost, proxyLabel, "MX DNS failed", sw);

                await sock.ConnectAsync(new IPEndPoint(mxAddresses[0], 25), timeoutCts.Token);
                stream = new NetworkStream(sock, ownsSocket: false);
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;
                return await DoSmtpHandshake(stream, email, mxHost, proxy, proxyLabel, timeoutCts.Token, sw);
            }

            // 1) Connect to SOCKS5 proxy
            var proxyAddresses = await Dns.GetHostAddressesAsync(proxy.Host, timeoutCts.Token);
            if (proxyAddresses.Length == 0)
                return MakeError(email, mxHost, proxyLabel, "Proxy DNS failed", sw);

            await sock.ConnectAsync(new IPEndPoint(proxyAddresses[0], proxy.Port), timeoutCts.Token);
            stream = new NetworkStream(sock, ownsSocket: false);
            stream.ReadTimeout = timeoutMs;
            stream.WriteTimeout = timeoutMs;

            // 2) SOCKS5 handshake
            bool hasAuth = !string.IsNullOrEmpty(proxy.User);
            byte[] greeting = hasAuth
                ? new byte[] { 0x05, 0x02, 0x00, 0x02 }  // NO-AUTH + USER/PASS
                : new byte[] { 0x05, 0x01, 0x00 };         // NO-AUTH only

            await stream.WriteAsync(greeting, timeoutCts.Token);
            var greetResp = await ReadExactAsync(stream, 2, timeoutCts.Token);
            if (greetResp[0] != 0x05)
                return MakeError(email, mxHost, proxyLabel, "SOCKS5 version mismatch", sw);

            if (greetResp[1] == 0x02 && hasAuth)
            {
                // Username/password authentication
                var userBytes = Encoding.ASCII.GetBytes(proxy.User!);
                var passBytes = Encoding.ASCII.GetBytes(proxy.Pass ?? "");
                var authPacket = new byte[3 + userBytes.Length + passBytes.Length];
                authPacket[0] = 0x01; // version
                authPacket[1] = (byte)userBytes.Length;
                Buffer.BlockCopy(userBytes, 0, authPacket, 2, userBytes.Length);
                authPacket[2 + userBytes.Length] = (byte)passBytes.Length;
                Buffer.BlockCopy(passBytes, 0, authPacket, 3 + userBytes.Length, passBytes.Length);

                await stream.WriteAsync(authPacket, timeoutCts.Token);
                var authResp = await ReadExactAsync(stream, 2, timeoutCts.Token);
                if (authResp[1] != 0x00)
                    return MakeError(email, mxHost, proxyLabel, "SOCKS5 auth failed", sw);
            }
            else if (greetResp[1] == 0xFF)
            {
                return MakeError(email, mxHost, proxyLabel, "SOCKS5 no acceptable method", sw);
            }

            // 3) SOCKS5 CONNECT to MX:25
            var domainBytes = Encoding.ASCII.GetBytes(mxHost);
            var connectReq = new byte[7 + domainBytes.Length];
            connectReq[0] = 0x05; // version
            connectReq[1] = 0x01; // connect
            connectReq[2] = 0x00; // reserved
            connectReq[3] = 0x03; // domain name
            connectReq[4] = (byte)domainBytes.Length;
            Buffer.BlockCopy(domainBytes, 0, connectReq, 5, domainBytes.Length);
            connectReq[5 + domainBytes.Length] = 0x00; // port high byte (25)
            connectReq[6 + domainBytes.Length] = 0x19; // port low byte (25)

            await stream.WriteAsync(connectReq, timeoutCts.Token);

            // Read connect response: minimum 10 bytes for IPv4 reply
            var connResp = await ReadExactAsync(stream, 4, timeoutCts.Token);
            if (connResp[1] != 0x00)
                return MakeError(email, mxHost, proxyLabel, $"SOCKS5 connect failed: 0x{connResp[1]:X2}", sw);

            // Skip the remaining address bytes based on ATYP
            switch (connResp[3])
            {
                case 0x01: // IPv4
                    await ReadExactAsync(stream, 6, timeoutCts.Token); // 4 + 2
                    break;
                case 0x03: // Domain
                    var lenBuf = await ReadExactAsync(stream, 1, timeoutCts.Token);
                    await ReadExactAsync(stream, lenBuf[0] + 2, timeoutCts.Token);
                    break;
                case 0x04: // IPv6
                    await ReadExactAsync(stream, 18, timeoutCts.Token); // 16 + 2
                    break;
            }

            // 4) Now we have a tunnel to MX:25 — do SMTP
            return await DoSmtpHandshake(stream, email, mxHost, proxy, proxyLabel, timeoutCts.Token, sw);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw; // propagate real cancellation
        }
        catch (OperationCanceledException)
        {
            return MakeError(email, mxHost, proxyLabel, "Timeout", sw);
        }
        catch (Exception ex)
        {
            return MakeError(email, mxHost, proxyLabel, ex.Message, sw);
        }
        finally
        {
            stream?.Dispose();
            if (sock != null)
            {
                try { sock.Shutdown(SocketShutdown.Both); } catch { }
                sock.Dispose();
            }
        }
    }

    /// <summary>
    /// Wrapper с MX failover: пробует mxHosts по очереди. Если на одном MX SOCKS5
    /// connection refused / banner reject — переключается на следующий (alt1, alt2…).
    /// SMTP-уровневые ошибки (550/553) НЕ повод фоллбэчить — это финальный ответ сервера.
    ///
    /// <paramref name="onVerdict"/> вызывается СРАЗУ при получении каждого RCPT-ответа
    /// (стриминг). Worker может обновлять UI/счётчики не дожидаясь конца batch'а.
    /// </summary>
    public async Task<List<SmtpVerdict>> ProbeBatchWithFailoverAsync(
        List<string> emails, string[] mxHosts, ProxySpec proxy, int timeoutMs,
        CancellationToken ct, Action<SmtpVerdict>? onVerdict = null)
    {
        if (mxHosts.Length == 0)
            return ProbeBatchEmpty(emails, "", $"{proxy.Host}:{proxy.Port}", "No MX record");

        List<SmtpVerdict>? lastResults = null;
        for (int i = 0; i < mxHosts.Length; i++)
        {
            ct.ThrowIfCancellationRequested();
            // ProbeBatchAsync вызывает onVerdict ТОЛЬКО на per-RCPT уровне (не на bulk
            // SOCKS5/banner/EHLO ошибках), поэтому failover не дублирует callback'и.
            lastResults = await ProbeBatchAsync(emails, mxHosts[i], proxy, timeoutMs, ct, onVerdict);

            // Если ВСЕ результаты — connection-level error, пробуем следующий MX.
            // SMTP-уровневый отказ (250/450/550) — финальный, не фоллбэчим.
            if (!AllAreConnectionLevelErrors(lastResults)) return lastResults;
        }

        return lastResults!;
    }

    private static bool AllAreConnectionLevelErrors(List<SmtpVerdict> results)
    {
        if (results.Count == 0) return false;
        foreach (var r in results)
        {
            if (r.Verdict != Verdicts.Error) return false;
            // Connection-уровень: SOCKS5, banner, EHLO reject — нужен следующий MX.
            // MAIL FROM / RCPT TO ошибки — финальные.
            var err = r.Error ?? "";
            if (!(err.Contains("SOCKS5", StringComparison.OrdinalIgnoreCase)
                || err.Contains("Bad banner", StringComparison.OrdinalIgnoreCase)
                || err.Contains("EHLO rejected", StringComparison.OrdinalIgnoreCase)
                || err.Contains("Proxy DNS", StringComparison.OrdinalIgnoreCase)
                || err.Contains("Timeout", StringComparison.OrdinalIgnoreCase)
                || err.Contains("Connection refused", StringComparison.OrdinalIgnoreCase)
                || err.Contains("Connection closed", StringComparison.OrdinalIgnoreCase)))
                return false;
        }
        return true;
    }

    private static List<SmtpVerdict> ProbeBatchEmpty(List<string> emails, string mx, string proxy, string err)
    {
        var list = new List<SmtpVerdict>(emails.Count);
        foreach (var e in emails)
            list.Add(new SmtpVerdict(e, Verdicts.Error, mx, 0, "", proxy, err, 0));
        return list;
    }

    /// <summary>
    /// Probe a batch of emails over a single SMTP session (RCPT TO for each).
    /// Returns list of verdicts. <paramref name="onVerdict"/> вызывается per-RCPT для
    /// стриминга в UI (только для реальных RCPT-ответов, не для bulk-ошибок batch'а).
    /// </summary>
    public async Task<List<SmtpVerdict>> ProbeBatchAsync(
        List<string> emails, string mxHost, ProxySpec proxy, int timeoutMs,
        CancellationToken ct, Action<SmtpVerdict>? onVerdict = null)
    {
        var results = new List<SmtpVerdict>(emails.Count);
        var sw = Stopwatch.StartNew();
        var proxyLabel = proxy.IsDirect ? "DIRECT" : $"{proxy.Host}:{proxy.Port}";
        Socket? sock = null;
        NetworkStream? stream = null;

        try
        {
            sock = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            sock.NoDelay = true;

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeoutCts.CancelAfter(timeoutMs);

            if (proxy.IsDirect)
            {
                // DIRECT: коннект напрямую на MX:25, без SOCKS5. Требует открытый исход. 25 порт.
                var mxAddresses = await Dns.GetHostAddressesAsync(mxHost, timeoutCts.Token);
                if (mxAddresses.Length == 0)
                {
                    foreach (var e in emails)
                        results.Add(MakeError(e, mxHost, proxyLabel, "MX DNS failed", sw));
                    return results;
                }

                await sock.ConnectAsync(new IPEndPoint(mxAddresses[0], 25), timeoutCts.Token);
                stream = new NetworkStream(sock, ownsSocket: false);
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;
            }
            else
            {
                // 1) Connect to SOCKS5 proxy
                var proxyAddresses = await Dns.GetHostAddressesAsync(proxy.Host, timeoutCts.Token);
                if (proxyAddresses.Length == 0)
                {
                    foreach (var e in emails)
                        results.Add(MakeError(e, mxHost, proxyLabel, "Proxy DNS failed", sw));
                    return results;
                }

                await sock.ConnectAsync(new IPEndPoint(proxyAddresses[0], proxy.Port), timeoutCts.Token);
                stream = new NetworkStream(sock, ownsSocket: false);
                stream.ReadTimeout = timeoutMs;
                stream.WriteTimeout = timeoutMs;

                // 2) SOCKS5 handshake
                if (!await Socks5Handshake(stream, proxy, mxHost, timeoutCts.Token))
                {
                    foreach (var e in emails)
                        results.Add(MakeError(e, mxHost, proxyLabel, "SOCKS5 handshake failed", sw));
                    return results;
                }
            }

            // 3) Read SMTP banner
            var banner = await ReadSmtpResponse(stream, timeoutCts.Token);
            if (!banner.StartsWith("220"))
            {
                foreach (var e in emails)
                    results.Add(MakeError(e, mxHost, proxyLabel, $"Bad banner: {banner}", sw));
                return results;
            }

            // 4) EHLO с динамическим HELO (rDNS прокси или юзер-настройка)
            var helo = await ResolveHeloAsync(proxy, timeoutCts.Token);
            await SendLine(stream, $"EHLO {helo}", timeoutCts.Token);
            var ehloResp = await ReadSmtpResponse(stream, timeoutCts.Token);
            if (!ehloResp.StartsWith("250"))
            {
                foreach (var e in emails)
                    results.Add(MakeError(e, mxHost, proxyLabel, $"EHLO rejected: {ehloResp}", sw));
                return results;
            }

            // 5) MAIL FROM
            await SendLine(stream, $"MAIL FROM:{BuildMailFrom(helo)}", timeoutCts.Token);
            var mailFromResp = await ReadSmtpResponse(stream, timeoutCts.Token);
            if (!mailFromResp.StartsWith("250"))
            {
                foreach (var e in emails)
                    results.Add(MakeError(e, mxHost, proxyLabel, $"MAIL FROM rejected: {mailFromResp}", sw));
                return results;
            }

            // 6) RCPT TO for each email — стримим в UI по мере прихода каждого ответа
            foreach (var email in emails)
            {
                ct.ThrowIfCancellationRequested();
                var probeSw = Stopwatch.StartNew();
                SmtpVerdict v;

                try
                {
                    await SendLine(stream, $"RCPT TO:<{email}>", timeoutCts.Token);
                    var rcptResp = await ReadSmtpResponse(stream, timeoutCts.Token);
                    var code = ParseCode(rcptResp);
                    var verdict = ClassifyCode(code);

                    v = new SmtpVerdict(
                        email, verdict, mxHost, code,
                        rcptResp.TrimEnd(), proxyLabel, "", probeSw.Elapsed.TotalMilliseconds);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    v = MakeError(email, mxHost, proxyLabel, ex.Message, probeSw);
                }

                results.Add(v);
                onVerdict?.Invoke(v);  // ← мгновенно в UI
            }

            // 7) QUIT
            try
            {
                await SendLine(stream, "QUIT", CancellationToken.None);
            }
            catch { /* best effort */ }

            return results;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Fill remaining emails with error
            int done = results.Count;
            for (int i = done; i < emails.Count; i++)
                results.Add(MakeError(emails[i], mxHost, proxyLabel, ex.Message, sw));
            return results;
        }
        finally
        {
            stream?.Dispose();
            if (sock != null)
            {
                try { sock.Shutdown(SocketShutdown.Both); } catch { }
                sock.Dispose();
            }
        }
    }

    // ── SOCKS5 handshake helper ──────────────────────────────────────

    private static async Task<bool> Socks5Handshake(
        NetworkStream stream, ProxySpec proxy, string mxHost, CancellationToken ct)
    {
        bool hasAuth = !string.IsNullOrEmpty(proxy.User);
        byte[] greeting = hasAuth
            ? new byte[] { 0x05, 0x02, 0x00, 0x02 }
            : new byte[] { 0x05, 0x01, 0x00 };

        await stream.WriteAsync(greeting, ct);
        var greetResp = await ReadExactAsync(stream, 2, ct);
        if (greetResp[0] != 0x05) return false;

        if (greetResp[1] == 0x02 && hasAuth)
        {
            var userBytes = Encoding.ASCII.GetBytes(proxy.User!);
            var passBytes = Encoding.ASCII.GetBytes(proxy.Pass ?? "");
            var authPacket = new byte[3 + userBytes.Length + passBytes.Length];
            authPacket[0] = 0x01;
            authPacket[1] = (byte)userBytes.Length;
            Buffer.BlockCopy(userBytes, 0, authPacket, 2, userBytes.Length);
            authPacket[2 + userBytes.Length] = (byte)passBytes.Length;
            Buffer.BlockCopy(passBytes, 0, authPacket, 3 + userBytes.Length, passBytes.Length);

            await stream.WriteAsync(authPacket, ct);
            var authResp = await ReadExactAsync(stream, 2, ct);
            if (authResp[1] != 0x00) return false;
        }
        else if (greetResp[1] == 0xFF)
        {
            return false;
        }

        // CONNECT to MX:25
        var domainBytes = Encoding.ASCII.GetBytes(mxHost);
        var req = new byte[7 + domainBytes.Length];
        req[0] = 0x05; req[1] = 0x01; req[2] = 0x00; req[3] = 0x03;
        req[4] = (byte)domainBytes.Length;
        Buffer.BlockCopy(domainBytes, 0, req, 5, domainBytes.Length);
        req[5 + domainBytes.Length] = 0x00;
        req[6 + domainBytes.Length] = 0x19;

        await stream.WriteAsync(req, ct);
        var connResp = await ReadExactAsync(stream, 4, ct);
        if (connResp[1] != 0x00) return false;

        // Drain remaining address bytes
        switch (connResp[3])
        {
            case 0x01:
                await ReadExactAsync(stream, 6, ct);
                break;
            case 0x03:
                var lenBuf = await ReadExactAsync(stream, 1, ct);
                await ReadExactAsync(stream, lenBuf[0] + 2, ct);
                break;
            case 0x04:
                await ReadExactAsync(stream, 18, ct);
                break;
        }

        return true;
    }

    // ── SMTP protocol helpers ────────────────────────────────────────

    private async Task<SmtpVerdict> DoSmtpHandshake(
        NetworkStream stream, string email, string mxHost, ProxySpec proxy, string proxyLabel,
        CancellationToken ct, Stopwatch sw)
    {
        // Read banner
        var banner = await ReadSmtpResponse(stream, ct);
        if (!banner.StartsWith("220"))
            return MakeError(email, mxHost, proxyLabel, $"Bad banner: {banner}", sw);

        // EHLO (динамический HELO = rDNS прокси или юзер-настройка)
        var helo = await ResolveHeloAsync(proxy, ct);
        await SendLine(stream, $"EHLO {helo}", ct);
        var ehloResp = await ReadSmtpResponse(stream, ct);
        if (!ehloResp.StartsWith("250"))
            return MakeError(email, mxHost, proxyLabel, $"EHLO rejected: {ehloResp}", sw);

        // MAIL FROM
        await SendLine(stream, $"MAIL FROM:{BuildMailFrom(helo)}", ct);
        var mailResp = await ReadSmtpResponse(stream, ct);
        if (!mailResp.StartsWith("250"))
            return MakeError(email, mxHost, proxyLabel, $"MAIL FROM rejected: {mailResp}", sw);

        // RCPT TO
        await SendLine(stream, $"RCPT TO:<{email}>", ct);
        var rcptResp = await ReadSmtpResponse(stream, ct);
        var code = ParseCode(rcptResp);
        var verdict = ClassifyCode(code);

        // QUIT
        try
        {
            await SendLine(stream, "QUIT", CancellationToken.None);
        }
        catch { }

        return new SmtpVerdict(
            email, verdict, mxHost, code,
            rcptResp.TrimEnd(), proxyLabel, "", sw.Elapsed.TotalMilliseconds);
    }

    private static async Task SendLine(NetworkStream stream, string line, CancellationToken ct)
    {
        var data = Encoding.ASCII.GetBytes(line + "\r\n");
        await stream.WriteAsync(data, ct);
    }

    private static async Task<string> ReadSmtpResponse(NetworkStream stream, CancellationToken ct)
    {
        var sb = new StringBuilder(256);
        var buf = new byte[1024];

        // SMTP responses can be multi-line: "250-..." continued, "250 ..." final
        while (true)
        {
            int read = await stream.ReadAsync(buf, ct);
            if (read == 0) break;

            sb.Append(Encoding.ASCII.GetString(buf, 0, read));

            // Check if we got a complete response (final line: code + space + text + CRLF)
            var text = sb.ToString();
            if (text.EndsWith("\r\n"))
            {
                var lines = text.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                if (lines.Length > 0)
                {
                    var lastLine = lines[^1];
                    // Final line has "NNN " (3 digits + space), not "NNN-"
                    if (lastLine.Length >= 4 && lastLine[3] == ' ')
                        break;
                }
            }

            // Safety: don't accumulate forever
            if (sb.Length > 8192) break;
        }

        return sb.ToString();
    }

    private static int ParseCode(string response)
    {
        if (response.Length >= 3 && int.TryParse(response[..3], out var code))
            return code;
        return 0;
    }

    private static string ClassifyCode(int code)
    {
        return code switch
        {
            250 => Verdicts.Valid,
            550 or 551 or 552 or 553 or 554 => Verdicts.Invalid,
            450 or 451 or 452 => Verdicts.Unknown,
            _ when code >= 200 && code < 300 => Verdicts.Valid,
            _ when code >= 500 => Verdicts.Invalid,
            _ when code >= 400 => Verdicts.Unknown,
            _ => Verdicts.Error,
        };
    }

    // ── Byte helpers ─────────────────────────────────────────────────

    private static async Task<byte[]> ReadExactAsync(NetworkStream stream, int count, CancellationToken ct)
    {
        var buffer = new byte[count];
        int offset = 0;
        while (offset < count)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(offset, count - offset), ct);
            if (read == 0)
                throw new IOException("Connection closed during SOCKS5 handshake");
            offset += read;
        }
        return buffer;
    }

    private static SmtpVerdict MakeError(string email, string mx, string proxy, string error, Stopwatch sw)
    {
        return new SmtpVerdict(email, Verdicts.Error, mx, 0, "", proxy, error, sw.Elapsed.TotalMilliseconds);
    }
}

public record ProxySpec(string Host, int Port, string? User, string? Pass)
{
    /// <summary>
    /// Sentinel: direct-режим (без прокси). SmtpProber распознаёт по Host="DIRECT"
    /// и коннектится напрямую с локального IP на MX:25, минуя SOCKS5-handshake.
    /// Использовать только если у ISP открыт исходящий порт 25.
    /// </summary>
    public static readonly ProxySpec Direct = new("DIRECT", 0, null, null);

    public bool IsDirect => Host == "DIRECT";

    /// <summary>
    /// Parse "host:port:user:pass" or "host:port" format.
    /// </summary>
    public static ProxySpec? TryParse(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
            return null;

        var parts = line.Trim().Split(':');
        if (parts.Length < 2) return null;
        if (!int.TryParse(parts[1], out var port)) return null;

        var host = parts[0];
        var user = parts.Length > 2 ? parts[2] : null;
        var pass = parts.Length > 3 ? parts[3] : null;

        return new ProxySpec(host, port, user, pass);
    }
}

public record SmtpSettings
{
    public int BatchSize { get; init; } = 16;
    public int WorkersPerProxy { get; init; } = 3;
    public int TimeoutMs { get; init; } = 71000;
    public int MaxConsecErrors { get; init; } = 10;
    public int Retries { get; init; } = 3;
    public int SessionDelayMs { get; init; } = 2000;
    public int RecoverySeconds { get; init; } = 300;

    /// <summary>EHLO/HELO домен. Пусто = используется "verifier-mx.com" (реальный домен с MX).</summary>
    public string HeloDomain { get; init; } = "";

    /// <summary>MAIL FROM адрес. Пусто = "&lt;&gt;" (null sender, RFC bounce).</summary>
    public string MailFromAddress { get; init; } = "";
}
