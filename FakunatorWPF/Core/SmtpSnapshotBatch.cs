using System.Collections.Generic;
using System.Linq;

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

    /// <summary>true, если вообще ни один источник (прокси/DIRECT) не может достучаться —
    /// у всех статус Failed/Disabled. Признак сетевой проблемы (порт 25 закрыт, прокси мертвы),
    /// а не просто "плохие" адреса — те дают SMTP-код, а не connection-level ошибку.</summary>
    public bool AllSourcesFailing =>
        Processed >= 5 && ProxyStats.Count > 0 &&
        ProxyStats.All(p => p.Status is "Failed" or "Disabled");
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
