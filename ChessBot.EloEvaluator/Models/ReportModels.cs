using System.Text.Json.Serialization;

namespace ChessBot.EloEvaluator.Models;

public sealed class EloReport
{
    public string   RunId            { get; set; } = string.Empty;
    public DateTime Timestamp        { get; set; }
    public string   PgnDir           { get; set; } = string.Empty;
    public double?  ReferenceElo     { get; set; }

    // ── Game counts ──────────────────────────────────────────────────────────
    public int TotalGames   { get; set; }
    public int ValidGames   { get; set; }
    public int InvalidGames { get; set; }
    public int Wins         { get; set; }
    public int Draws        { get; set; }
    public int Losses       { get; set; }
    public int Aborted      { get; set; }

    // ── Elo stats ────────────────────────────────────────────────────────────
    public double  ScoreRate            { get; set; }
    public double  EloDiff              { get; set; }
    public double? EstimatedElo         { get; set; }
    public double  ConfidenceInterval95 { get; set; }
    public double  LOS                  { get; set; }
    public bool    IsReliable           { get; set; }
    public string  ReliabilityNote      { get; set; } = string.Empty;

    // ── Color breakdown ──────────────────────────────────────────────────────
    public ColorStats AsWhite { get; set; } = new();
    public ColorStats AsBlack { get; set; } = new();

    // ── Phase breakdown ──────────────────────────────────────────────────────
    public PhaseStats Opening    { get; set; } = new();
    public PhaseStats Middlegame { get; set; } = new();
    public PhaseStats Endgame    { get; set; } = new();

    // ── Engine performance ───────────────────────────────────────────────────
    public EnginePerf EnginePerf { get; set; } = new();

    // ── Cross-engine evaluation disagreement stats ─────────────────────────────
    // NOT a blunder or centipawn-loss measurement; see MoveLossAnalyzer for that.
    public CrossEngineDisagreementStats CrossEngineDisagreementStats { get; set; } = new();

    [Obsolete("Use CrossEngineDisagreementStats.")]
    public BlunderStats BlunderStats { get; set; } = new();

    // ── Per-game summaries ───────────────────────────────────────────────────
    public List<GameSummary> Games { get; set; } = [];

    // ── Validation issues ────────────────────────────────────────────────────
    public List<ValidationIssue> ValidationIssues { get; set; } = [];
}

public sealed class ColorStats
{
    public int    Games           { get; set; }
    public int    Wins            { get; set; }
    public int    Draws           { get; set; }
    public int    Losses          { get; set; }
    public double ScoreRate       { get; set; }
    public double EloDiff         { get; set; }
    public bool   IsReliable      { get; set; }
    public string ReliabilityNote { get; set; } = string.Empty;
}

public sealed class PhaseStats
{
    public string PhaseName   { get; set; } = string.Empty;
    public int    MoveCount   { get; set; }
    public double AvgDepth    { get; set; }
    public double AvgNps      { get; set; }
    public double AvgScoreCp  { get; set; }
    /// <summary>
    /// Cross-engine evaluation disagreements in this phase, re-detected from per-move score
    /// swings. NOT a blunder / centipawn-loss measurement — see MoveLossAnalyzer for that.
    /// </summary>
    public int    CrossEngineDisagreements     { get; set; }
    public double CrossEngineDisagreementRate  { get; set; }  // disagreements per 10 ChessBot moves

    [Obsolete("Use CrossEngineDisagreements.")]
    public int Blunders { get => CrossEngineDisagreements; set => CrossEngineDisagreements = value; }
    [Obsolete("Use CrossEngineDisagreementRate.")]
    public double BlunderRate { get => CrossEngineDisagreementRate; set => CrossEngineDisagreementRate = value; }
}

public sealed class EnginePerf
{
    public double AvgDepth       { get; set; }
    public double AvgNps         { get; set; }
    public double PeakNps        { get; set; }
    public double AvgNodesPerMove { get; set; }
    public double AvgTimeMsPerMove { get; set; }
    public long   TotalNodes     { get; set; }
}

/// <summary>
/// Aggregate cross-engine evaluation disagreement stats. These compare ChessBot's own pre-move
/// score against the opponent engine's post-move score from a different search — they are NOT
/// a blunder or centipawn-loss measurement. See MoveLossAnalyzer / MoveLossReport for that.
/// </summary>
public sealed class CrossEngineDisagreementStats
{
    public int    TotalDisagreements    { get; set; }
    public int    TotalChessBotMoves    { get; set; }
    public double DisagreementRate      { get; set; }  // per 10 moves
    public int    GamesWithDisagreements { get; set; }

    [Obsolete("Use TotalDisagreements.")]
    public int TotalBlunders { get => TotalDisagreements; set => TotalDisagreements = value; }
    [Obsolete("Use DisagreementRate.")]
    public double BlunderRate { get => DisagreementRate; set => DisagreementRate = value; }
    [Obsolete("Use GamesWithDisagreements.")]
    public int GamesWithBlunders { get => GamesWithDisagreements; set => GamesWithDisagreements = value; }
}

[Obsolete("Use CrossEngineDisagreementStats.")]
public sealed class BlunderStats
{
    public int    TotalBlunders       { get; set; }
    public int    TotalChessBotMoves  { get; set; }
    public double BlunderRate         { get; set; }  // per 10 moves
    public int    GamesWithBlunders   { get; set; }
}

public sealed class GameSummary
{
    public int      GameNumber    { get; set; }
    public string   SourceFile    { get; set; } = string.Empty;
    public string   ChessBotColor { get; set; } = string.Empty;
    public string   Result        { get; set; } = string.Empty;
    public string   Outcome       { get; set; } = string.Empty;
    public string?  Termination   { get; set; }
    public int      TotalPlies    { get; set; }
    public double?  AvgDepth      { get; set; }
    public double?  AvgNps        { get; set; }
    public double?  PeakNps       { get; set; }
    /// <summary>Cross-engine evaluation disagreements for this game (not a blunder count).</summary>
    public int      CrossEngineDisagreements { get; set; }
    [Obsolete("Use CrossEngineDisagreements.")]
    public int      Blunders      { get => CrossEngineDisagreements; set => CrossEngineDisagreements = value; }
    public bool     IsValid       { get; set; }
    public DateTime? Date         { get; set; }
}

public sealed class ValidationIssue
{
    public string SourceFile { get; set; } = string.Empty;
    public int    GameNumber { get; set; }
    public string Issue      { get; set; } = string.Empty;
}
