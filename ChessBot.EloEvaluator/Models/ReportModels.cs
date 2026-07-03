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

    // ── Blunder stats ────────────────────────────────────────────────────────
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
    public int    Blunders    { get; set; }
    public double BlunderRate { get; set; }  // blunders per 10 ChessBot moves
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
    public int      Blunders      { get; set; }
    public bool     IsValid       { get; set; }
    public DateTime? Date         { get; set; }
}

public sealed class ValidationIssue
{
    public string SourceFile { get; set; } = string.Empty;
    public int    GameNumber { get; set; }
    public string Issue      { get; set; } = string.Empty;
}
