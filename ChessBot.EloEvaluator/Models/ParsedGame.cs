namespace ChessBot.EloEvaluator.Models;

public enum GameOutcomeKind { Win, Loss, Draw, Aborted, Unknown }

/// <summary>
/// A single move record extracted from a PositionAnalyzer log file.
/// </summary>
public sealed record ParsedMoveData(
    int    MoveNumber,
    bool   IsWhiteMove,
    bool   IsChessBotMove,
    string UciMove,
    string FenBefore,
    int    Depth,
    int    SelDepth,
    int    ScoreCp,
    int?   ScoreMate,
    long   Nodes,
    long   Nps,
    long   ElapsedMs,
    string Pv,
    string ScoreBound,
    bool   HasValidScore);

/// <summary>
/// Unified game record parsed from either a .log or .pgn file.
/// </summary>
public sealed class ParsedGame
{
    public required string SourceFile { get; init; }
    public required string SourceType { get; init; }  // "log" | "pgn"

    public int    GameNumber { get; set; }
    public bool   IsValid    { get; set; } = true;
    public List<string> ValidationErrors { get; } = [];

    // ── Result ────────────────────────────────────────────────────────────────
    public string          ResultTag     { get; set; } = "*";
    public string          ChessBotColor { get; set; } = "Unknown";  // "White" | "Black"
    public GameOutcomeKind Outcome       { get; set; } = GameOutcomeKind.Unknown;

    // ── Meta ──────────────────────────────────────────────────────────────────
    public string?   TerminationReason  { get; set; }
    public string?   OpponentName       { get; set; }
    public int       MoveTimeMs         { get; set; }
    public DateTime? Date               { get; set; }
    public string    InitialFen         { get; set; } = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    // ── Ply counts ────────────────────────────────────────────────────────────
    public int TotalPlies    { get; set; }
    public int ChessBotPlies { get; set; }
    public int OpponentPlies { get; set; }

    // ── Engine stats from the log stats table (ChessBot's column) ─────────────
    public double? CbAvgDepth        { get; set; }
    public double? CbAvgSelDepth     { get; set; }
    public double? CbAvgNps          { get; set; }
    public double? CbPeakNps         { get; set; }
    public double? CbAvgNodesPerMove { get; set; }
    public long?   CbTotalNodes      { get; set; }
    public double? CbAvgScoreCp      { get; set; }
    public double? CbAvgTimeMsPerMove { get; set; }

    // ── Blunder data ─────────────────────────────────────────────────────────
    public int BlundersDetected { get; set; }

    // ── Per-move records (log files only) ────────────────────────────────────
    public List<ParsedMoveData> Moves { get; } = [];
}
