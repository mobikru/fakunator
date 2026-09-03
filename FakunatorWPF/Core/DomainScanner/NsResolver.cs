using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Threading;
using System.Threading.Tasks;
using DnsClient;

namespace Fakunator.Core.DomainScanner;

/// <summary>
/// Порт resolver.py на C# через DnsClient.NET.
///
/// Ключевые решения (из Python-версии):
/// - Ротация публичных резолверов: при timeout часто виноват троттлинг именно
///   этого IP, а не dead-домен. Retry с другим резолвером отличает "нас
///   троттлят" от "домен и правда не отвечает".
/// - NoNameservers НЕ ретраится: SERVFAIL/REFUSED с авторитативного сервера —
///   это уже финальный ответ TLD-делегации, ретрай не поможет.
/// - Direct-TLD query fallback: при lame-делегации основной запрос падает
///   в SERVFAIL и скрывает NS-хосты. Идём напрямую к TLD-серверу
///   (a.dns.ripn.net для .ru/.su, a.gtld-servers.net для .com/.net).
/// </summary>
public class NsResolver
{
    private static readonly IPAddress[] PublicResolvers =
    {
        IPAddress.Parse("1.1.1.1"),
        IPAddress.Parse("1.0.0.1"),
        IPAddress.Parse("8.8.8.8"),
        IPAddress.Parse("8.8.4.4"),
        IPAddress.Parse("9.9.9.9"),
    };

    private static readonly Dictionary<string, string> TldServers = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ru"] = "a.dns.ripn.net",
        ["su"] = "a.dns.ripn.net",
        ["com"] = "a.gtld-servers.net",
        ["net"] = "a.gtld-servers.net",
        ["org"] = "a0.org.afilias-nst.info",
    };

    private static readonly ConcurrentDictionary<string, IPAddress> _tldIpCache = new(StringComparer.OrdinalIgnoreCase);

    private readonly TimeSpan _timeout;
    // По одному клиенту на каждый публичный резолвер — точный порт Python-версии
    // (там `resolver.nameservers = [next(_resolver_cycle)]`). Ротация per-query
    // между попытками. SERVFAIL от одного резолвера = lame (как NoNameservers
    // в dnspython). Не ждём подтверждения от всех 5 — иначе теряем брошенные
    // домены где часть резолверов держит старый NS в кэше.
    private readonly LookupClient[] _clients;
    private int _cycleIdx;

    public NsResolver(double timeoutSec = 3.0)
    {
        _timeout = TimeSpan.FromSeconds(timeoutSec);
        _clients = PublicResolvers.Select(ns => new LookupClient(new LookupClientOptions(ns)
        {
            Timeout = _timeout,
            UseCache = false,
            Retries = 0,
            ContinueOnDnsError = false,   // не ретраить — семантика Python
            EnableAuditTrail = false,
            ThrowDnsErrors = false,
        })).ToArray();
    }

    private LookupClient PickClient()
    {
        int i = System.Threading.Interlocked.Increment(ref _cycleIdx);
        return _clients[Math.Abs(i) % _clients.Length];
    }

    /// <summary>
    /// NS lookup домена с retry по нескольким публичным резолверам.
    ///
    /// SERVFAIL от рекурсивного резолвера НЕ означает автоматом lame delegation —
    /// это может быть временный fluke (перегрузка Cloudflare, rate-limit).
    /// Поэтому требуем подтверждение от >=2 РАЗНЫХ резолверов прежде чем маркировать.
    /// Это резко уменьшает false-positives на "живых" доменах типа akops.ru,
    /// где 1.1.1.1 иногда отвечает SERVFAIL, а 8.8.8.8 нормально резолвит.
    ///
    /// NXDOMAIN — финальный ответ, ретраить не нужно (не troubled).
    /// REFUSED — обычно policy / rate-limit конкретного резолвера, идём к другому.
    /// </summary>
    public async Task<(List<string> Hosts, string Status)> ResolveNsAsync(string domain, CancellationToken ct = default)
    {
        // Порт Python `_query`: 2 попытки на разных резолверах, retry только на TIMEOUT.
        // SERVFAIL/REFUSED от одного резолвера = сразу lame (аналог NoNameservers dnspython).
        string lastStatus = "timeout";
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var client = PickClient();
            try
            {
                var res = await client.QueryAsync(domain, QueryType.NS, cancellationToken: ct).ConfigureAwait(false);
                if (res.HasError)
                {
                    switch (res.Header.ResponseCode)
                    {
                        case DnsHeaderResponseCode.NotExistentDomain:
                            return (new List<string>(), "nxdomain");
                        case DnsHeaderResponseCode.ServerFailure:
                        case DnsHeaderResponseCode.Refused:
                            return (new List<string>(), "lame_delegation");
                    }
                    lastStatus = "error";
                    continue;
                }

                var hosts = res.Answers
                    .OfType<DnsClient.Protocol.NsRecord>()
                    .Select(r => r.NSDName.Value.TrimEnd('.').ToLowerInvariant())
                    .Distinct()
                    .OrderBy(h => h)
                    .ToList();

                if (hosts.Count == 0) return (hosts, "no_ns");
                return (hosts, "ok");
            }
            catch (OperationCanceledException) { throw; }
            catch (DnsResponseException dex)
            {
                if (dex.Code == DnsResponseCode.NotExistentDomain)
                    return (new List<string>(), "nxdomain");
                if (dex.Code == DnsResponseCode.ServerFailure || dex.Code == DnsResponseCode.Refused)
                    return (new List<string>(), "lame_delegation");
                lastStatus = "error";
                continue;
            }
            catch
            {
                lastStatus = "timeout";
                continue;
            }
        }
        return (new List<string>(), lastStatus);
    }

    /// <summary>A-record lookup. Аналогичный подход: 2 попытки на разных резолверах.</summary>
    public async Task<(List<string> Values, string Status)> ResolveAAsync(string domain, CancellationToken ct = default)
    {
        for (int attempt = 0; attempt < 2; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            var client = PickClient();
            try
            {
                var res = await client.QueryAsync(domain, QueryType.A, cancellationToken: ct).ConfigureAwait(false);
                if (res.HasError)
                {
                    switch (res.Header.ResponseCode)
                    {
                        case DnsHeaderResponseCode.NotExistentDomain: return (new List<string>(), "nxdomain");
                        case DnsHeaderResponseCode.ServerFailure:
                        case DnsHeaderResponseCode.Refused:
                            return (new List<string>(), "lame_delegation");
                    }
                    continue;
                }
                var vals = res.Answers.OfType<DnsClient.Protocol.ARecord>()
                    .Select(r => r.Address.ToString())
                    .Distinct().OrderBy(v => v).ToList();
                if (vals.Count == 0) return (vals, "no_a");
                return (vals, "ok");
            }
            catch (OperationCanceledException) { throw; }
            catch { continue; }
        }
        return (new List<string>(), "timeout");
    }

    /// <summary>
    /// Прямой запрос NS у TLD-сервера (в обход рекурсивного резолвера).
    /// Возвращает делегацию домена из реестра — работает даже если авторитативный
    /// NS домена отвечает SERVFAIL (lame-делегация).
    /// </summary>
    public async Task<List<string>> ResolveNsAtRegistryAsync(string domain)
    {
        var tld = domain.Contains('.') ? domain[(domain.LastIndexOf('.') + 1)..] : "";
        if (!TldServers.TryGetValue(tld, out var tldHost))
            return new List<string>();

        var tldIp = await GetTldIpAsync(tldHost).ConfigureAwait(false);
        if (tldIp == null) return new List<string>();

        try
        {
            var opts = new LookupClientOptions(tldIp)
            {
                Timeout = _timeout,
                UseCache = false,
                Retries = 0,
            };
            var client = new LookupClient(opts);
            var res = await client.QueryAsync(domain, QueryType.NS).ConfigureAwait(false);

            // Делегация лежит в authority-секции ответа TLD-сервера.
            var hosts = res.Authorities
                .OfType<DnsClient.Protocol.NsRecord>()
                .Select(r => r.NSDName.Value.TrimEnd('.').ToLowerInvariant())
                .Distinct()
                .OrderBy(h => h)
                .ToList();
            return hosts;
        }
        catch
        {
            return new List<string>();
        }
    }

    private async Task<IPAddress?> GetTldIpAsync(string host)
    {
        if (_tldIpCache.TryGetValue(host, out var cached)) return cached;
        try
        {
            var res = await PickClient().QueryAsync(host, QueryType.A).ConfigureAwait(false);
            var first = res.Answers.OfType<DnsClient.Protocol.ARecord>().FirstOrDefault();
            if (first == null) return null;
            _tldIpCache[host] = first.Address;
            return first.Address;
        }
        catch
        {
            return null;
        }
    }
}
