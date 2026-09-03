namespace Fakunator.Core;

/// <summary>
/// Single analysis result for one email address.
/// </summary>
/// <param name="Email">Full email address.</param>
/// <param name="Gender">M, F, N, or ?</param>
/// <param name="Iso">ISO 3166-1 alpha-2 country code, or ?</param>
/// <param name="Source">db, db+morph, ai, ai+ai2, unknown</param>
/// <param name="MatchTier">1 = exact, 2 = variant/prefix, 0 = none</param>
public record AnalysisResult(
    string Email,
    string Gender,
    string Iso,
    string Source,
    int MatchTier = 0)
{
    /// <summary>Extracted local-part name token used for the lookup.</summary>
    public string NameToken { get; init; } = "";

    /// <summary>Semicolon-joined signals/notes from the pipeline.</summary>
    public string Signals { get; init; } = "";
}
