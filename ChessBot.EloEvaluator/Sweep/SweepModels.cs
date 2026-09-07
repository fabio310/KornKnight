namespace ChessBot.EloEvaluator.Sweep;

/// <summary>
/// Result of one sweep round: N games against the opponent capped at <see cref="StockfishElo"/>.
/// </summary>
public sealed class SweepRoundResult
{
    public int  RoundNumber   { get; set; }
    public int  StockfishElo  { get; set; }

    public int Games  { get; set; }
    public int Wins   { get; set; }
    public int Draws  { get; set; }
    public int Losses { get; set; }

    public double  ScoreRate            { get; set; }
    public double  EloDiff              { get; set; }
    /// <summary>Performance Elo = StockfishElo + EloDiff.</summary>
    public double? EstimatedElo         { get; set; }
    public double  ConfidenceInterval95 { get; set; }
    public double  LOS                  { get; set; }
    public string  ReliabilityNote      { get; set; } = string.Empty;

    /// <summary>True while ChessBot still scores above 50% and wins at least one game.</summary>
    public bool Passed { get; set; }

    /// <summary>Step size in force for this round (halves on every direction change).</summary>
    public int Step { get; set; }

    /// <summary>Elo the ladder moved to after this round; null when the sweep stopped here.</summary>
    public int? NextElo { get; set; }

    /// <summary>Why the round ended the sweep — empty while the sweep continues.</summary>
    public string StopReason { get; set; } = string.Empty;

    public string RoundDir { get; set; } = string.Empty;

    // ── Extras from the per-round report pipeline (null when parsing found nothing) ──
    public double? AvgDepth { get; set; }
    public double? AvgNps   { get; set; }
    /// <summary>Cross-engine evaluation disagreements in this round. Diagnostic only.</summary>
    public int     Disagreements { get; set; }

    public double DurationSeconds { get; set; }

    /// <summary>"+6=2-2" style record.</summary>
    public string Record => $"+{Wins}={Draws}-{Losses}";
}

/// <summary>
/// Full result of a strength sweep.
/// </summary>
public sealed class SweepResult
{
    public string   RunId     { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }

    // ── Echo of the configuration ────────────────────────────────────────────
    public string EnginePath    { get; set; } = string.Empty;
    public string OpponentName  { get; set; } = "Unknown";
    public int    StartElo      { get; set; }
    /// <summary>Initial step size; halves on each direction change.</summary>
    public int    EloStep       { get; set; }
    public int    MinStep       { get; set; }
    public int    MaxElo        { get; set; }
    public int    MinElo        { get; set; }
    public int    GamesPerRound { get; set; }
    public int    MoveTimeMs    { get; set; }
    public string OutDir        { get; set; } = string.Empty;

    public List<SweepRoundResult> Rounds { get; set; } = [];

    // ── Verdict ──────────────────────────────────────────────────────────────
    /// <summary>
    /// Midpoint of the final bracket — the ladder's answer for ChessBot's strength.
    /// Null when the ladder never bracketed it (ran into the engine's Elo floor or ceiling).
    /// </summary>
    public int? EstimatedElo { get; set; }
    /// <summary>Highest opponent Elo ChessBot beat (lower edge of the bracket).</summary>
    public int? HighestPassedElo { get; set; }
    /// <summary>Lowest opponent Elo ChessBot failed to beat (upper edge of the bracket).</summary>
    public int? LowestFailedElo { get; set; }
    /// <summary>Width of the final bracket in Elo — the precision of the estimate.</summary>
    public int? BracketWidth { get; set; }
    /// <summary>Step size the ladder had narrowed to when it stopped.</summary>
    public int FinalStep { get; set; }
    /// <summary>Performance Elo of the deciding round (its level + its EloDiff).</summary>
    public double? PerformanceElo { get; set; }
    /// <summary>True when the ladder ran into the engine's maximum Elo without failing.</summary>
    public bool ReachedMaxElo { get; set; }
    /// <summary>True when the ladder ran into the engine's minimum Elo without ever winning.</summary>
    public bool ReachedMinElo { get; set; }
    /// <summary>Why the ladder stopped: converged, hit a bound, or ran out of rounds.</summary>
    public string StopReason { get; set; } = string.Empty;

    public string Verdict { get; set; } = string.Empty;
    public string ReliabilityNote { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }
}
