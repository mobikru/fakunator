using System.Collections.Generic;

namespace Fakunator.Core;

public record SmtpSnapshotBatch
{
    public int Processed { get; init; }
    public int Total { get; init; }
    public Dictionary<string, int> Counts { get; init; } = new();
    public double Elapsed { get; init; }
    public double Speed { get; init; }
    public int ActiveSessions { get; init; }
    public List<SmtpVerdict> FreshFeed { get; init; } = new();
    public List<SmtpVerdict> RecentErrors { get; init; } = new();
    public List<ProxyStatsEntry> ProxyStats { get; init; } = new();
}

public record ProxyStatsEntry
{
    public string Address { get; init; } = "";
    public int Successes { get; init; }
    public int Errors { get; init; }
    public int ConsecutiveErrors { get; init; }
    public bool Disabled { get; init; }
    public string Status => Disabled ? "Disabled" : ConsecutiveErrors >= 5 ? "Failed" : ConsecutiveErrors >= 3 ? "Slow" : Successes == 0 && Errors == 0 ? "Idle" : "Healthy";
}
