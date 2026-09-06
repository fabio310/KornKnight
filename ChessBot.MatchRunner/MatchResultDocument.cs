namespace ChessBot.MatchRunner;

using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Versioned, serializable per-move record for the machine-readable match result format.
/// Mirrors <see cref="MoveRecord"/> but is a plain DTO so its shape is stable across engine
/// refactors (schema changes must bump <see cref="MatchResultDocument.CurrentSchemaVersion"/>).
/// </summary>
public sealed class MoveRecordDto
{
    public int    MoveNumber     { get; set; }
    public bool   IsWhiteMove    { get; set; }
    public bool   IsChessBotMove { get; set; }
    public string UciMove        { get; set; } = string.Empty;
    public string Fen            { get; set; } = string.Empty;
    public int    Depth          { get; set; }
    public int    SelDepth       { get; set; }
    public int    ScoreCp        { get; set; }
    public int?   ScoreMate      { get; set; }
    public string ScoreBound     { get; set; } = "exact";
    public long   Nodes          { get; set; }
    public long   Nps            { get; set; }
    public int    HashFull       { get; set; }
    public long   TbHits         { get; set; }
    public long   ElapsedMs      { get; set; }
    public string Pv             { get; set; } = string.Empty;

    public static MoveRecordDto From(MoveRecord m) => new()
    {
        MoveNumber     = m.MoveNumber,
        IsWhiteMove    = m.IsWhiteMove,
        IsChessBotMove = m.IsChessBotMove,
        UciMove        = m.UciMove,
        Fen            = m.Fen,
        Depth          = m.Depth,
        SelDepth       = m.SelDepth,
        ScoreCp        = m.ScoreCp,
        ScoreMate      = m.ScoreMate,
        ScoreBound     = m.ScoreBound,
        Nodes          = m.Nodes,
        Nps            = m.Nps,
        HashFull       = m.HashFull,
        TbHits         = m.TbHits,
        ElapsedMs      = m.ElapsedMs,
        Pv             = m.Pv,
    };
}

/// <summary>
/// Versioned, serializable per-game record for the machine-readable match result format.
/// </summary>
public sealed class GameResultDto
{
    public int    GameNumber        { get; set; }
    public bool   ChessBotIsWhite   { get; set; }
    public string Outcome           { get; set; } = string.Empty;
    public string InitialFen        { get; set; } = string.Empty;
    public string? TerminationReason { get; set; }
    public string PgnPath           { get; set; } = string.Empty;
    public List<MoveRecordDto> Moves { get; set; } = new();

    public static GameResultDto From(GameResult g) => new()
    {
        GameNumber         = g.GameNumber,
        ChessBotIsWhite    = g.ChessBotIsWhite,
        Outcome            = g.Outcome.ToString(),
        InitialFen         = g.InitialFen,
        TerminationReason  = g.TerminationReason,
        PgnPath            = g.PgnPath,
        Moves              = g.Moves.Select(MoveRecordDto.From).ToList(),
    };
}

/// <summary>
/// Versioned, machine-readable snapshot of a whole match's results (all games).
/// <see cref="SchemaVersion"/> must be incremented whenever a breaking shape change is made,
/// so downstream tooling (sweeps, dashboards, external analysis scripts) can detect
/// incompatible formats instead of silently misreading fields.
/// </summary>
public sealed class MatchResultDocument
{
    /// <summary>Bump this whenever a breaking change is made to this document's shape.</summary>
    public const int CurrentSchemaVersion = 1;

    public int    SchemaVersion  { get; set; } = CurrentSchemaVersion;
    public string OpponentName   { get; set; } = "Unknown";
    public int    Wins           { get; set; }
    public int    Draws          { get; set; }
    public int    Losses         { get; set; }
    public double ScoreRate      { get; set; }
    public List<GameResultDto> Games { get; set; } = new();

    public static MatchResultDocument From(MatchOutcome outcome) => new()
    {
        OpponentName = outcome.OpponentName,
        Wins         = outcome.Wins,
        Draws        = outcome.Draws,
        Losses       = outcome.Losses,
        ScoreRate    = outcome.ScoreRate,
        Games        = outcome.Games.Select(GameResultDto.From).ToList(),
    };
}

/// <summary>
/// Reads/writes <see cref="MatchResultDocument"/> as JSON.
/// </summary>
public static class MatchResultWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    public static void Write(MatchResultDocument document, string path)
    {
        string json = JsonSerializer.Serialize(document, JsonOptions);
        File.WriteAllText(path, json);
    }

    public static void Write(MatchOutcome outcome, string path) =>
        Write(MatchResultDocument.From(outcome), path);

    public static MatchResultDocument Read(string path)
    {
        string json = File.ReadAllText(path);
        return JsonSerializer.Deserialize<MatchResultDocument>(json, JsonOptions)
            ?? throw new InvalidDataException($"Could not deserialize match result document from {path}");
    }
}
