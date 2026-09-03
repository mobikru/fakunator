using System.Collections.Generic;

namespace Fakunator.Core;

public record SnapshotBatch
{
    public int Processed { get; init; }
    public int Total { get; init; }
    public Dictionary<string, int> Counts { get; init; } = new();
    public Dictionary<string, int> Providers { get; init; } = new();
    public double Elapsed { get; init; }
    public double Speed { get; init; }
    public double ThroughputPoint { get; init; }
    public List<(int LineNo, string Email, string Category, string Note)> FreshFeed { get; init; } = new();
}
