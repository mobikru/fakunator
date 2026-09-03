using System.Collections.Generic;

namespace Fakunator.Core.DomainScanner;

/// <summary>
/// Порт ns_providers.py из Python-версии. Сопоставление NS-хостов с
/// известными провайдерами. Порядок паттернов важен: специфичное раньше общего.
/// Fallback — берётся последняя часть FQDN (registrar-like), например
/// "ns1.somehost.io" → "somehost.io".
/// </summary>
public static class NsProviderDetector
{
    private static readonly (string Pattern, string Name)[] Patterns =
    {
        ("cloudflare.com", "Cloudflare"),
        ("awsdns", "AWS Route 53"),
        ("domaincontrol.com", "GoDaddy"),
        ("reg.ru", "REG.RU"),
        ("nic.ru", "RU-CENTER (Nic.ru)"),
        ("yandex.net", "Yandex"),
        ("dnspod.com", "DNSPod"),
        ("ns.beget", "Beget"),
        ("beget.com", "Beget"),
        ("sedoparking.com", "Sedo Parking"),
        ("parkingcrew.net", "ParkingCrew"),
        ("above.com", "Above.com"),
        ("registrar-servers.com", "Namecheap"),
        ("namecheaphosting.com", "Namecheap"),
        ("dns.ovh.net", "OVH"),
        ("digitalocean.com", "DigitalOcean"),
        ("azure-dns", "Azure DNS"),
        ("domains.google", "Google Domains"),
        ("google.com", "Google"),
        ("hostgator.com", "HostGator"),
        ("bluehost.com", "Bluehost"),
        ("timeweb.ru", "Timeweb"),
        ("firstvds.ru", "FirstVDS"),
        ("selectel.ru", "Selectel"),
        ("ns1.com", "NS1"),
        ("worldnic.com", "Network Solutions"),
        ("dnsowl.com", "DNS Made Easy"),
        ("dnsmadeeasy.com", "DNS Made Easy"),
        ("ultradns", "UltraDNS (Neustar)"),
        ("tildadns.com", "tildadns.com"),
        ("sprinthost.ru", "sprinthost.ru"),
        ("jino.ru", "jino.ru"),
        ("1dedic.ru", "1dedic.ru"),
    };

    public static string Detect(List<string> nsList)
    {
        if (nsList == null || nsList.Count == 0)
            return "Unknown";

        var joined = string.Join(" ", nsList).ToLowerInvariant();
        foreach (var (pattern, name) in Patterns)
        {
            if (joined.Contains(pattern))
                return name;
        }

        // Fallback: registrar-like домен из первого NS (последние 2 части)
        var parts = nsList[0].ToLowerInvariant().Trim('.').Split('.');
        if (parts.Length >= 2)
            return $"{parts[^2]}.{parts[^1]}";

        return "Unknown";
    }
}
