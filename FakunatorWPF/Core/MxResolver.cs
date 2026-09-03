using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core;

/// <summary>
/// Resolves MX records for a domain. Uses manual UDP DNS query (no NuGet required).
/// Caches results in a ConcurrentDictionary.
/// </summary>
public static class MxResolver
{
    private static readonly ConcurrentDictionary<string, string[]> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Resolve MX records for the domain. Returns MX hostnames sorted by priority.
    /// Falls back to A record if no MX found.
    /// </summary>
    public static async Task<string[]> ResolveAsync(string domain, CancellationToken ct)
    {
        if (_cache.TryGetValue(domain, out var cached))
            return cached;

        try
        {
            var mxRecords = await QueryMxViaDnsAsync(domain, ct);
            if (mxRecords.Length > 0)
            {
                _cache[domain] = mxRecords;
                return mxRecords;
            }
        }
        catch
        {
            // Fall through to A record fallback
        }

        // Fallback: check if domain has an A record
        try
        {
            var addresses = await Dns.GetHostAddressesAsync(domain, ct);
            if (addresses.Length > 0)
            {
                var result = new[] { domain };
                _cache[domain] = result;
                return result;
            }
        }
        catch
        {
            // Domain doesn't resolve at all
        }

        var empty = Array.Empty<string>();
        _cache[domain] = empty;
        return empty;
    }

    public static void ClearCache() => _cache.Clear();

    /// <summary>
    /// Manual UDP DNS MX query. This avoids needing DnsClient NuGet.
    /// </summary>
    private static async Task<string[]> QueryMxViaDnsAsync(string domain, CancellationToken ct)
    {
        // Build DNS query packet
        var queryPacket = BuildMxQuery(domain);

        using var udp = new UdpClient();
        // Use system DNS server (8.8.8.8 as fallback)
        var dnsServer = GetSystemDns() ?? "8.8.8.8";
        var endpoint = new IPEndPoint(IPAddress.Parse(dnsServer), 53);

        await udp.SendAsync(queryPacket, queryPacket.Length, endpoint);

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(5000);

        var receiveTask = udp.ReceiveAsync(timeoutCts.Token);
        var result = await receiveTask;

        return ParseMxResponse(result.Buffer, domain);
    }

    private static byte[] BuildMxQuery(string domain)
    {
        var ms = new MemoryStream();
        var rng = new Random();

        // Transaction ID
        var id = (ushort)rng.Next(0, 65536);
        ms.WriteByte((byte)(id >> 8));
        ms.WriteByte((byte)(id & 0xFF));

        // Flags: standard query, recursion desired
        ms.WriteByte(0x01);
        ms.WriteByte(0x00);

        // Questions: 1
        ms.WriteByte(0x00); ms.WriteByte(0x01);
        // Answers: 0
        ms.WriteByte(0x00); ms.WriteByte(0x00);
        // Authority: 0
        ms.WriteByte(0x00); ms.WriteByte(0x00);
        // Additional: 0
        ms.WriteByte(0x00); ms.WriteByte(0x00);

        // Question: domain name
        foreach (var label in domain.Split('.'))
        {
            var bytes = Encoding.ASCII.GetBytes(label);
            ms.WriteByte((byte)bytes.Length);
            ms.Write(bytes);
        }
        ms.WriteByte(0x00); // null terminator

        // Type: MX (15)
        ms.WriteByte(0x00); ms.WriteByte(0x0F);
        // Class: IN (1)
        ms.WriteByte(0x00); ms.WriteByte(0x01);

        return ms.ToArray();
    }

    private static string[] ParseMxResponse(byte[] data, string domain)
    {
        if (data.Length < 12) return Array.Empty<string>();

        // Answer count at offset 6-7
        int answerCount = (data[6] << 8) | data[7];
        if (answerCount == 0) return Array.Empty<string>();

        // Skip header (12 bytes) + question section
        int offset = 12;
        // Skip question name
        offset = SkipName(data, offset);
        offset += 4; // skip QTYPE + QCLASS

        // Parse answer records
        var mxList = new List<(int Priority, string Host)>();

        for (int i = 0; i < answerCount && offset < data.Length; i++)
        {
            // Skip name
            offset = SkipName(data, offset);
            if (offset + 10 > data.Length) break;

            int type = (data[offset] << 8) | data[offset + 1];
            offset += 2; // type
            offset += 2; // class
            offset += 4; // TTL
            int rdLen = (data[offset] << 8) | data[offset + 1];
            offset += 2; // rdlength

            if (type == 15 && rdLen >= 4) // MX record
            {
                int priority = (data[offset] << 8) | data[offset + 1];
                var host = ReadName(data, offset + 2);
                if (!string.IsNullOrEmpty(host))
                {
                    // Remove trailing dot
                    if (host.EndsWith('.'))
                        host = host[..^1];
                    mxList.Add((priority, host));
                }
            }

            offset += rdLen;
        }

        return mxList
            .OrderBy(m => m.Priority)
            .Select(m => m.Host)
            .ToArray();
    }

    private static int SkipName(byte[] data, int offset)
    {
        while (offset < data.Length)
        {
            int len = data[offset];
            if (len == 0) { offset++; break; }
            if ((len & 0xC0) == 0xC0) { offset += 2; break; } // pointer
            offset += 1 + len;
        }
        return offset;
    }

    private static string ReadName(byte[] data, int offset)
    {
        var parts = new List<string>();
        int maxJumps = 20; // prevent infinite loops
        int jumps = 0;

        while (offset < data.Length && jumps < maxJumps)
        {
            int len = data[offset];
            if (len == 0) break;

            if ((len & 0xC0) == 0xC0) // pointer
            {
                int ptr = ((len & 0x3F) << 8) | data[offset + 1];
                offset = ptr;
                jumps++;
                continue;
            }

            offset++;
            if (offset + len > data.Length) break;
            parts.Add(Encoding.ASCII.GetString(data, offset, len));
            offset += len;
        }

        return string.Join('.', parts);
    }

    private static string? GetSystemDns()
    {
        try
        {
            var interfaces = System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces();
            foreach (var ni in interfaces)
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                    continue;

                var props = ni.GetIPProperties();
                foreach (var dns in props.DnsAddresses)
                {
                    if (dns.AddressFamily == AddressFamily.InterNetwork)
                        return dns.ToString();
                }
            }
        }
        catch { }

        return null;
    }
}
