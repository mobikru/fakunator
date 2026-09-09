using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using DnsClient;
using DnsClient.Protocol;

namespace Fakunator.Core.Server;

/// <summary>
/// Проверки DNS-записей и IP-репутации для mail-сервера.
/// Все методы async, безопасны при no-network (row.Status → "fail" + Actual = ошибка).
/// </summary>
public static class DnsTrustChecker
{
    // Используем публичные резолверы (Cloudflare + Google) — свой bind9 может кэшировать старые ответы.
    private static readonly LookupClient Dns = new(new LookupClientOptions(
        System.Net.IPAddress.Parse("1.1.1.1"),
        System.Net.IPAddress.Parse("8.8.8.8"))
    {
        Timeout = TimeSpan.FromSeconds(5),
        UseCache = false,
        Retries = 1,
    });

    // ── Публичные проверки — каждая обновляет row.Status/Actual/Hint ──

    public static async Task CheckA(CheckRow row, string name, string expectedIp)
    {
        row.Begin();
        try
        {
            var q = await Dns.QueryAsync(name, QueryType.A);
            var ips = q.Answers.ARecords().Select(a => a.Address.ToString()).ToArray();
            row.Actual = ips.Length == 0 ? "(нет записи)" : string.Join(", ", ips);
            if (ips.Length == 0) row.Fail("Запись не найдена");
            else if (ips.Contains(expectedIp)) row.Ok();
            else row.Warn($"Резолвится в {ips[0]}, ожидалось {expectedIp}");
        }
        catch (Exception ex) { row.Fail("DNS-ошибка: " + Short(ex)); }
    }

    /// <summary>Wildcard: имя вида *.domain — резолвим random-suffix.domain.</summary>
    public static Task CheckWildcard(CheckRow row, string domain, string expectedIp)
        => CheckA(row, $"fkn-test-{Guid.NewGuid().ToString("N")[..8]}.{domain}", expectedIp);

    public static async Task CheckMx(CheckRow row, string domain, string expectedHost)
    {
        row.Begin();
        try
        {
            var q = await Dns.QueryAsync(domain, QueryType.MX);
            var mx = q.Answers.MxRecords().OrderBy(m => m.Preference).ToArray();
            row.Actual = mx.Length == 0 ? "(нет записи)" : string.Join(", ", mx.Select(m => $"{m.Preference} {m.Exchange.Value.TrimEnd('.')}"));
            if (mx.Length == 0) { row.Fail("MX не задан — почта не будет приниматься"); return; }
            var host = mx[0].Exchange.Value.TrimEnd('.');
            if (string.Equals(host, expectedHost, StringComparison.OrdinalIgnoreCase)) row.Ok();
            else row.Warn($"MX = {host}, ожидалось {expectedHost}");
        }
        catch (Exception ex) { row.Fail("DNS-ошибка: " + Short(ex)); }
    }

    public static async Task CheckPtr(CheckRow row, string ip, string expectedHost)
    {
        row.Begin();
        try
        {
            var q = await Dns.QueryReverseAsync(System.Net.IPAddress.Parse(ip));
            var ptrs = q.Answers.PtrRecords().Select(p => p.PtrDomainName.Value.TrimEnd('.')).ToArray();
            row.Actual = ptrs.Length == 0 ? "(нет PTR)" : string.Join(", ", ptrs);
            if (ptrs.Length == 0) { row.Fail("PTR не задан — крупные MX (Gmail/Yahoo) отфутболят"); return; }
            if (ptrs.Any(p => string.Equals(p, expectedHost, StringComparison.OrdinalIgnoreCase))) row.Ok();
            else row.Warn($"PTR = {ptrs[0]}, желательно {expectedHost}");
        }
        catch (Exception ex) { row.Fail("Reverse-lookup ошибка: " + Short(ex)); }
    }

    public static async Task CheckTxt(CheckRow row, string name, Func<string, (bool ok, string? msg)> matcher, string expectedHint)
    {
        row.Begin();
        try
        {
            var q = await Dns.QueryAsync(name, QueryType.TXT);
            var txts = q.Answers.TxtRecords()
                .SelectMany(t => t.Text)
                .Where(s => !string.IsNullOrEmpty(s))
                .ToArray();
            if (txts.Length == 0) { row.Actual = "(нет записи)"; row.Fail($"TXT не найден. Ожидается: {expectedHint}"); return; }
            row.Actual = string.Join(" | ", txts.Select(t => t.Length > 90 ? t[..90] + "…" : t));
            foreach (var txt in txts)
            {
                var (ok, msg) = matcher(txt);
                if (ok) { row.Ok(msg); return; }
            }
            var last = matcher(txts[0]);
            row.Warn(last.msg ?? "Формат не совпадает с ожидаемым");
        }
        catch (Exception ex) { row.Fail("DNS-ошибка: " + Short(ex)); }
    }

    public static async Task CheckBlacklist(CheckRow row, string ip, string dnsblZone)
    {
        row.Begin();
        try
        {
            var reverse = string.Join(".", ip.Split('.').Reverse());
            var name = $"{reverse}.{dnsblZone}";
            var q = await Dns.QueryAsync(name, QueryType.A);
            var listed = q.Answers.ARecords().Any();
            if (!listed) { row.Actual = "clean"; row.Ok(); }
            else
            {
                var codes = string.Join(", ", q.Answers.ARecords().Select(a => a.Address.ToString()));
                row.Actual = $"listed ({codes})";
                row.Fail($"IP в чёрном списке {dnsblZone}");
            }
        }
        catch (DnsResponseException ex) when (ex.Code == DnsResponseCode.NotExistentDomain)
        {
            row.Actual = "clean"; row.Ok();
        }
        catch (Exception ex) { row.Actual = "?"; row.Warn("Проверка не удалась: " + Short(ex)); }
    }

    private static string Short(Exception ex)
    {
        var msg = ex.Message;
        var nl = msg.IndexOf('\n');
        if (nl > 0) msg = msg[..nl];
        return msg.Length > 80 ? msg[..80] + "…" : msg;
    }
}

/// <summary>Одна строка проверки. INotifyPropertyChanged для WPF-биндинга.</summary>
public sealed class CheckRow : INotifyPropertyChanged
{
    private string _status = "pending";
    private string _actual = "…";
    private string _hint = "";

    public string Name { get; init; } = "";
    public string Expected { get; init; } = "";

    public string Actual { get => _actual; set => Set(ref _actual, value); }
    public string Status { get => _status; set { Set(ref _status, value); OnChanged(nameof(Icon)); OnChanged(nameof(Color)); } }
    public string Hint { get => _hint; set => Set(ref _hint, value); }

    public string Icon => _status switch { "ok" => "✓", "warn" => "⚠", "fail" => "❌", _ => "…" };
    public string Color => _status switch { "ok" => "#22c55e", "warn" => "#f59e0b", "fail" => "#ef4444", _ => "#6b7280" };

    public void Begin() { Status = "pending"; Actual = "проверяю…"; Hint = ""; }
    public void Ok(string? hint = null) { Status = "ok"; Hint = hint ?? ""; }
    public void Warn(string hint) { Status = "warn"; Hint = hint; }
    public void Fail(string hint) { Status = "fail"; Hint = hint; }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void Set<T>(ref T f, T v, [CallerMemberName] string p = "") { if (!EqualityComparer<T>.Default.Equals(f, v)) { f = v; OnChanged(p); } }
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}
