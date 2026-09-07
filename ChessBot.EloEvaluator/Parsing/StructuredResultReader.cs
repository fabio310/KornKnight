using ChessBot.EloEvaluator.Models;
using ChessBot.MatchRunner;

namespace ChessBot.EloEvaluator.Parsing;

/// <summary>
/// Reads games from a run's <c>match_result.json</c>.
///
/// This is the preferred source: it is the format the match runner writes deliberately, it
/// states how many games were requested and how many were actually recorded, and it cannot be
/// confused with another artifact in the same directory. Text parsing remains as a fallback for
/// directories produced before the structured document existed, or written by an older build.
/// </summary>
public static class StructuredResultReader
{
    public const string FileName = "match_result.json";

    /// <summary>True when the directory contains a structured result this build can read.</summary>
    public static bool Exists(string dir) => File.Exists(Path.Combine(dir, FileName));

    /// <summary>
    /// Loads the structured result if present and readable. Returns null when it is absent or
    /// declares a newer schema than this build understands — the caller then falls back to the
    /// text parsers rather than risking a misread document.
    /// </summary>
    public static MatchResultDocument? TryRead(string dir, out string? reason)
    {
        reason = null;
        string path = Path.Combine(dir, FileName);
        if (!File.Exists(path))
        {
            reason = $"{FileName} not present";
            return null;
        }

        try
        {
            return MatchResultWriter.Read(path);
        }
        catch (Exception ex)
        {
            reason = ex.Message;
            return null;
        }
    }

    /// <summary>
    /// Projects the structured document into the evaluator's <see cref="ParsedGame"/> shape.
    /// Only ChessBot's own moves carry search statistics, so the per-game aggregates are taken
    /// from those; opponent moves contribute ply counts only.
    /// </summary>
    public static List<ParsedGame> ToParsedGames(MatchResultDocument doc, string sourcePath)
    {
        var games = new List<ParsedGame>();

        foreach (var g in doc.Games)
        {
            var cbMoves = g.Moves.Where(m => m.IsChessBotMove).ToList();

            var parsed = new ParsedGame
            {
                SourceFile = sourcePath,
                SourceType = "json",
            };

            parsed.GameNumber    = g.GameNumber;
            parsed.ChessBotColor = g.ChessBotIsWhite ? "White" : "Black";
            parsed.Outcome       = g.Outcome switch
            {
                nameof(GameOutcome.ChessBotWin)  => GameOutcomeKind.Win,
                nameof(GameOutcome.ChessBotLoss) => GameOutcomeKind.Loss,
                nameof(GameOutcome.Draw)         => GameOutcomeKind.Draw,
                nameof(GameOutcome.Aborted)      => GameOutcomeKind.Aborted,
                _                                => GameOutcomeKind.Unknown,
            };
            parsed.ResultTag = parsed.Outcome switch
            {
                GameOutcomeKind.Win  => g.ChessBotIsWhite ? "1-0" : "0-1",
                GameOutcomeKind.Loss => g.ChessBotIsWhite ? "0-1" : "1-0",
                GameOutcomeKind.Draw => "1/2-1/2",
                _                    => "*",
            };

            parsed.TerminationReason = g.TerminationReason;
            parsed.OpponentName      = doc.OpponentName;
            parsed.InitialFen        = string.IsNullOrWhiteSpace(g.InitialFen)
                ? parsed.InitialFen : g.InitialFen;

            parsed.TotalPlies    = g.Moves.Count;
            parsed.ChessBotPlies = cbMoves.Count;
            parsed.OpponentPlies = g.Moves.Count - cbMoves.Count;

            if (cbMoves.Count > 0)
            {
                parsed.CbAvgDepth        = cbMoves.Average(m => m.Depth);
                parsed.CbAvgSelDepth     = cbMoves.Average(m => m.SelDepth);
                parsed.CbAvgNps          = cbMoves.Average(m => (double)m.Nps);
                parsed.CbPeakNps         = cbMoves.Max(m => (double)m.Nps);
                parsed.CbAvgNodesPerMove = cbMoves.Average(m => (double)m.Nodes);
                parsed.CbTotalNodes      = cbMoves.Sum(m => m.Nodes);
            }

            games.Add(parsed);
        }

        return games;
    }
}
