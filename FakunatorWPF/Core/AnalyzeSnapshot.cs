using System.Collections.Generic;

namespace Fakunator.Core;

/// <summary>
/// Progress snapshot emitted by AnalyzeWorker at ~5 Hz.
/// </summary>
public record AnalyzeSnapshot
{
    public int Processed { get; init; }
    public int Total { get; init; }

    /// <summary>Source tag counts: db, ai, unknown, etc.</summary>
    public Dictionary<string, int> Counts { get; init; } = new();

    public int InputTokens { get; init; }
    public int OutputTokens { get; init; }
    public double CostUsd { get; init; }
    public double Elapsed { get; init; }
    public double Speed { get; init; }

    /// <summary>Current pipeline phase: db, ai1, ai2, done.</summary>
    public string Phase { get; init; } = "init";

    /// <summary>Freshly produced results since last snapshot.</summary>
    public List<AnalysisResult> FreshFeed { get; init; } = new();

    /// <summary>Country ISO code counts (e.g. "RU" -> 42).</summary>
    public Dictionary<string, int> CountryStats { get; init; } = new();

    public int AiErrors { get; init; }
    public string LastError { get; init; } = "";
    public bool SpendCapHit { get; init; }

    /// <summary>Всего emails для Pass2 (0 если Pass2 выключен или ещё не стартовал).</summary>
    public int Pass2Total { get; init; }

    /// <summary>Сколько Pass2 уже обработал (0..Pass2Total).</summary>
    public int Pass2Processed { get; init; }
}
