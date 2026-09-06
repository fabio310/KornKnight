namespace ChessBot.EloEvaluator.Sweep;

/// <summary>
/// Result of one sweep round: N games against the opponent capped at <see cref="StockfishElo"/>.
/// </summary>
public sealed class SweepRoundResult
{
    public int  RoundNumber   { get; set; }
    public int  StockfishElo  { get; set; }
    /// <summary>True when this round came from the optional bisection refinement.</summary>
    public bool IsRefinement  { get; set; }

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

    /// <summary>Why the round ended the sweep — empty while the sweep continues.</summary>
    public string StopReason { get; set; } = string.Empty;

    public string RoundDir { get; set; } = string.Empty;

    // ── Extras from the per-round report pipeline (null when parsing found nothing) ──
    public double? AvgDepth { get; set; }
    public double? AvgNps   { get; set; }
    public int     Blunders { get; set; }

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
    public int    EloStep       { get; set; }
    public int    MaxElo        { get; set; }
    public int    GamesPerRound { get; set; }
    public int    MoveTimeMs    { get; set; }
    public string OutDir        { get; set; } = string.Empty;

    public List<SweepRoundResult> Rounds { get; set; } = [];

    // ── Verdict ──────────────────────────────────────────────────────────────
    /// <summary>Lowest opponent Elo at which ChessBot stopped scoring above 50%.</summary>
    public int? EstimatedElo { get; set; }
    /// <summary>Highest opponent Elo ChessBot still beat, if any.</summary>
    public int? HighestPassedElo { get; set; }
    /// <summary>Performance Elo of the deciding round (its level + its EloDiff).</summary>
    public double? PerformanceElo { get; set; }
    /// <summary>True when the sweep ran out of levels without ChessBot ever failing.</summary>
    public bool ReachedMaxElo { get; set; }
    /// <summary>True when ChessBot already failed the very first round.</summary>
    public bool FailedAtStart { get; set; }

    public string Verdict { get; set; } = string.Empty;
    public string ReliabilityNote { get; set; } = string.Empty;

    public double DurationSeconds { get; set; }
}
