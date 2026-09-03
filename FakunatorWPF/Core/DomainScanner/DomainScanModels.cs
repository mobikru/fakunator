using System.Collections.Generic;

namespace Fakunator.Core.DomainScanner;

/// <summary>
/// Результат DNS-проверки одного домена (передаётся из воркера в БД и UI).
/// </summary>
public record DomainRecord(
    string Domain,
    string Zone,
    string NsProvider,
    List<string> NsHosts,
    string Status,      // ok / lame_delegation / nxdomain / no_ns / timeout / error
    bool HasA,
    string AStatus);

/// <summary>
/// Snapshot для прогресс-репорта в UI (~10 Hz).
/// </summary>
public record DomainScanSnapshot
{
    public bool Running { get; init; }
    public string CurrentZone { get; init; } = "";
    public List<string> Zones { get; init; } = new();
    public int TotalFetched { get; init; }
    public int TotalProcessed { get; init; }
    public int TotalSaved { get; init; }
    public int Errors { get; init; }
    public Dictionary<string, int> ByStatus { get; init; } = new();
    public double Elapsed { get; init; }
    public double Speed { get; init; }
    public List<DomainRecord> FreshFeed { get; init; } = new();

    /// <summary>Всего доменов в БД сейчас (для UI-плитки «всего в базе»).</summary>
    public int TotalInDb { get; init; }

    /// <summary>Топ NS-провайдеров: (provider, count). Для sidebar-списка.</summary>
    public List<(string Provider, int Count)> TopProviders { get; init; } = new();
}
