using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

namespace Fakunator.Core.DomainScanner;

/// <summary>
/// Клиент для domains-monitor.com. Стримит список доменов зоны построчно
/// (миллионы записей — bulk-download в память недопустим, работаем через
/// IAsyncEnumerable + HttpCompletionOption.ResponseHeadersRead).
///
/// Порт dm_client.py.
/// </summary>
public class DmApiClient : IDisposable
{
    private const string ApiBase = "https://domains-monitor.com/api/v1";
    private readonly string _token;
    private readonly HttpClient _http;

    public DmApiClient(string token)
    {
        _token = token;
        _http = new HttpClient { Timeout = TimeSpan.FromMinutes(30) };
    }

    /// <summary>
    /// Стрим доменных имён одной зоны. Формат "text" — plain построчно,
    /// формат "zip" — распаковываем на лету (для большой зоны типа .com).
    /// </summary>
    public async IAsyncEnumerable<string> StreamDomainsAsync(
        string zone, string fmt = "text",
        [EnumeratorCancellation] CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(_token))
            throw new InvalidOperationException("DM_API_TOKEN не задан (Настройки → Domain scanner)");

        var url = $"{ApiBase}/{_token}/get/{zone}/list/{fmt}/";

        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        var contentType = resp.Content.Headers.ContentType?.MediaType ?? "";
        bool isZip = fmt == "zip" || contentType.Contains("zip");

        await using var netStream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);

        if (isZip)
        {
            // Буферизируем в память (для маленьких зон типа .ru ~ 5M записей = ~50MB gzip).
            // Для действительно огромных (.com > 150M) юзер может использовать fmt=text.
            using var ms = new MemoryStream();
            await netStream.CopyToAsync(ms, ct).ConfigureAwait(false);
            ms.Position = 0;
            using var zip = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var entry in zip.Entries)
            {
                await using var es = entry.Open();
                using var reader = new StreamReader(es);
                string? line;
                while ((line = await reader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
                {
                    var d = line.Trim().ToLowerInvariant();
                    if (d.Length > 0) yield return d;
                }
            }
            yield break;
        }

        using var textReader = new StreamReader(netStream);
        string? textLine;
        while ((textLine = await textReader.ReadLineAsync(ct).ConfigureAwait(false)) != null)
        {
            var d = textLine.Trim().ToLowerInvariant();
            if (d.Length > 0) yield return d;
        }
    }

    public void Dispose() => _http.Dispose();
}
