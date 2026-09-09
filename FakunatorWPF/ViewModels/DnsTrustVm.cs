using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Fakunator.Core.Server;

namespace Fakunator.ViewModels;

/// <summary>
/// ViewModel вкладки DNS & Trust checker. Три группы проверок + список рекомендаций.
/// Запуск: <c>await RefreshAsync(ip, domain)</c> — обновляет все rows параллельно.
/// </summary>
public sealed class DnsTrustVm : INotifyPropertyChanged
{
    public ObservableCollection<CheckRow> Required { get; } = new();
    public ObservableCollection<CheckRow> Blacklist { get; } = new();
    public ObservableCollection<TrustHint> Boosters { get; } = new();

    private bool _isChecking;
    public bool IsChecking { get => _isChecking; set { _isChecking = value; OnChanged(nameof(IsChecking)); OnChanged(nameof(BusyText)); } }
    public string BusyText => _isChecking ? "Проверяю…" : "Проверить всё";

    public DnsTrustVm()
    {
        // Trust boosters — статические рекомендации (не auto-check, требуют ручной настройки).
        Boosters.Add(new TrustHint("PTR / rDNS через хостера",
            "IP → mail.<domain>. Без PTR Gmail/Yahoo вернут 550 no PTR. Ставится в панели VPS-хостера, не на сервере.",
            "must"));
        Boosters.Add(new TrustHint("HELO/myhostname = mail.<domain>",
            "Postfix должен представляться тем же именем, что и PTR. Проверь /etc/postfix/main.cf → myhostname.",
            "must"));
        Boosters.Add(new TrustHint("DMARC policy p=none → quarantine",
            "Стартовать с p=none (мониторинг), через месяц ужесточить до quarantine. Добавляй rua= для отчётов.",
            "must"));
        Boosters.Add(new TrustHint("abuse@ / postmaster@ ящики",
            "RFC 2142 обязывает. Gmail и Mail.ru проверяют их существование в скоринге репутации.",
            "must"));
        Boosters.Add(new TrustHint("DKIM 2048-bit (не 1024)",
            "Google/Mail.ru предпочитают 2048. Перегенерь: opendkim-genkey -b 2048 -s default -d <domain>.",
            "boost"));
        Boosters.Add(new TrustHint("DKIM ротация каждые 90 дней",
            "Selector s1/s2/s3… — свежий ключ = свежая репутация. Cron-задача, старый ключ храни ещё месяц (для верификации в пути).",
            "boost"));
        Boosters.Add(new TrustHint("MTA-STS",
            "TXT _mta-sts.<domain> + policy на https://mta-sts.<domain>/.well-known/mta-sts.txt. Устраняет STARTTLS-downgrade, читается как «серьёзный оператор».",
            "boost"));
        Boosters.Add(new TrustHint("TLS-RPT",
            "TXT _smtp._tls.<domain>: v=TLSRPTv1; rua=mailto:tls-reports@<domain>. Входит в скоринг Gmail Postmaster.",
            "boost"));
        Boosters.Add(new TrustHint("Postfix: TLS 1.2+ only",
            "smtpd_tls_protocols = >=TLSv1.2 + smtpd_tls_mandatory_ciphers = high. Отключить SSLv3/TLS1.0/1.1.",
            "boost"));
        Boosters.Add(new TrustHint("List-Unsubscribe заголовки",
            "Gmail с фев 2024 требует List-Unsubscribe + List-Unsubscribe-Post: List-Unsubscribe=One-Click для отправителей >5000/день.",
            "boost"));
    }

    public async Task RefreshAsync(string ip, string domain)
    {
        if (string.IsNullOrWhiteSpace(ip) || string.IsNullOrWhiteSpace(domain) || _isChecking) return;
        IsChecking = true;
        try
        {
            EnsureRows(ip, domain);

            var tasks = new[]
            {
                DnsTrustChecker.CheckA(Required[0], domain, ip),
                DnsTrustChecker.CheckA(Required[1], $"mail.{domain}", ip),
                DnsTrustChecker.CheckWildcard(Required[2], domain, ip),
                DnsTrustChecker.CheckMx(Required[3], domain, $"mail.{domain}"),
                DnsTrustChecker.CheckPtr(Required[4], ip, $"mail.{domain}"),
                DnsTrustChecker.CheckTxt(Required[5], domain,
                    t => t.StartsWith("v=spf1") && t.Contains(ip)
                        ? (true, "SPF содержит IP сервера")
                        : t.StartsWith("v=spf1")
                            ? (false, "SPF найден, но IP сервера отсутствует — добавь ip4:" + ip)
                            : (false, null),
                    "v=spf1 ip4:" + ip + " ~all"),
                DnsTrustChecker.CheckTxt(Required[6], $"default._domainkey.{domain}",
                    t =>
                    {
                        if (!t.StartsWith("v=DKIM1")) return (false, null);
                        var pLen = ExtractDkimKeyLen(t);
                        if (pLen == 0) return (false, "DKIM найден, но некорректен (нет p=)");
                        if (pLen < 2048) return (false, $"DKIM {pLen}-bit — рекомендуется 2048+");
                        return (true, $"DKIM ~{pLen}-bit OK");
                    },
                    "v=DKIM1; k=rsa; p=…"),
                DnsTrustChecker.CheckTxt(Required[7], $"_dmarc.{domain}",
                    t => t.StartsWith("v=DMARC1")
                        ? (true, ExtractPolicyHint(t))
                        : (false, null),
                    "v=DMARC1; p=none; rua=mailto:postmaster@" + domain),
                DnsTrustChecker.CheckBlacklist(Blacklist[0], ip, "zen.spamhaus.org"),
                DnsTrustChecker.CheckBlacklist(Blacklist[1], ip, "bl.spamcop.net"),
                DnsTrustChecker.CheckBlacklist(Blacklist[2], ip, "b.barracudacentral.org"),
                DnsTrustChecker.CheckBlacklist(Blacklist[3], ip, "dnsbl.sorbs.net"),
                DnsTrustChecker.CheckBlacklist(Blacklist[4], ip, "dnsbl-1.uceprotect.net"),
            };
            await Task.WhenAll(tasks);
        }
        finally { IsChecking = false; }
    }

    private void EnsureRows(string ip, string domain)
    {
        if (Required.Count == 0)
        {
            Required.Add(new CheckRow { Name = "A  @",                Expected = ip });
            Required.Add(new CheckRow { Name = "A  mail",             Expected = ip });
            Required.Add(new CheckRow { Name = "A  *.wildcard",       Expected = ip });
            Required.Add(new CheckRow { Name = "MX @",                Expected = $"10 mail.{domain}" });
            Required.Add(new CheckRow { Name = "PTR (rDNS)",          Expected = $"mail.{domain}" });
            Required.Add(new CheckRow { Name = "TXT SPF",             Expected = "v=spf1 ip4:… ~all" });
            Required.Add(new CheckRow { Name = "TXT DKIM (default)",  Expected = "v=DKIM1; k=rsa; p=…" });
            Required.Add(new CheckRow { Name = "TXT DMARC",           Expected = "v=DMARC1; p=none; rua=…" });
        }
        if (Blacklist.Count == 0)
        {
            Blacklist.Add(new CheckRow { Name = "Spamhaus ZEN",   Expected = "zen.spamhaus.org" });
            Blacklist.Add(new CheckRow { Name = "Spamcop",        Expected = "bl.spamcop.net" });
            Blacklist.Add(new CheckRow { Name = "Barracuda BRBL", Expected = "b.barracudacentral.org" });
            Blacklist.Add(new CheckRow { Name = "SORBS",          Expected = "dnsbl.sorbs.net" });
            Blacklist.Add(new CheckRow { Name = "UCEPROTECT L1",  Expected = "dnsbl-1.uceprotect.net" });
        }
    }

    private static int ExtractDkimKeyLen(string txt)
    {
        var idx = txt.IndexOf("p=", System.StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return 0;
        var p = txt[(idx + 2)..].Trim();
        var semi = p.IndexOf(';'); if (semi > 0) p = p[..semi];
        p = p.Replace(" ", "").Replace("\t", "");
        if (p.Length == 0) return 0;
        // Base64-длина в чарах ~ ключ_бит / 6. 1024-bit RSA public key ~216 chars, 2048 ~392, 4096 ~736.
        return p.Length switch
        {
            < 250 => 1024,
            < 450 => 2048,
            _ => 4096,
        };
    }

    private static string ExtractPolicyHint(string txt)
    {
        var pIdx = txt.IndexOf("p=", System.StringComparison.OrdinalIgnoreCase);
        if (pIdx < 0) return "DMARC найден";
        var p = txt[(pIdx + 2)..].Trim();
        var semi = p.IndexOf(';'); if (semi > 0) p = p[..semi];
        return p.Trim().ToLowerInvariant() switch
        {
            "none" => "policy=none — только мониторинг, спокойно повышай до quarantine",
            "quarantine" => "policy=quarantine — среднее ужесточение, хорошо",
            "reject" => "policy=reject — максимум, отлично",
            _ => "policy=" + p,
        };
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged(string p) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(p));
}

public sealed class TrustHint
{
    public string Title { get; }
    public string Body { get; }
    /// <summary>"must" (красный badge) или "boost" (жёлтый).</summary>
    public string Kind { get; }
    public string Badge => Kind == "must" ? "MUST" : "BOOST";
    public string BadgeColor => Kind == "must" ? "#ef4444" : "#f59e0b";
    public TrustHint(string title, string body, string kind) { Title = title; Body = body; Kind = kind; }
}
