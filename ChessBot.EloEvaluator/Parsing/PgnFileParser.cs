using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ChessBot.EloEvaluator.Models;

namespace ChessBot.EloEvaluator.Parsing;

/// <summary>
/// Parses PGN files written by PgnWriter (UCI-notation moves).
/// A single .pgn file may contain multiple games separated by blank lines.
/// </summary>
public static class PgnFileParser
{
    // Matches a UCI move token: e2e4, g1f3, a7a8q, etc.
    private static readonly Regex UciMoveRx = new(
        @"\b[a-h][1-8][a-h][1-8][qrbn]?\b", RegexOptions.Compiled);

    // Matches a PGN tag: [TagName "Value"]
    private static readonly Regex TagRx = new(
        @"^\[(\w+)\s+""(.*)""\]\s*$", RegexOptions.Compiled);

    public static List<ParsedGame> ParseAll(string filePath)
    {
        var games = new List<ParsedGame>();

        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath, Encoding.UTF8);
        }
        catch
        {
            return games;
        }

        var tags      = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var moveText  = new StringBuilder();
        bool inMove   = false;

        void FlushGame()
        {
            if (!tags.ContainsKey("Event")) return;
            var g = BuildGame(tags, moveText.ToString(), filePath);
            games.Add(g);
            tags.Clear();
            moveText.Clear();
            inMove = false;
        }

        foreach (var line in lines)
        {
            var t = line.Trim();

            if (t.StartsWith('['))
            {
                // A new tag block starting while we were in move text means a new game
                if (inMove)
                    FlushGame();

                var m = TagRx.Match(t);
                if (m.Success)
                    tags[m.Groups[1].Value] = m.Groups[2].Value;
            }
            else if (t.Length == 0)
            {
                // Blank line between tag block and move text is normal; ignore.
                // Blank line after move text that already contains a result = game over.
                string mt = moveText.ToString();
                if (inMove && (mt.Contains("1-0") || mt.Contains("0-1") ||
                               mt.Contains("1/2-1/2") || mt.TrimEnd().EndsWith('*')))
                {
                    FlushGame();
                }
            }
            else
            {
                inMove = true;
                if (moveText.Length > 0) moveText.Append(' ');
                moveText.Append(t);

                // If the line ends with a result token the game is complete
                if (t.EndsWith("1-0")     || t.EndsWith("0-1") ||
                    t.EndsWith("1/2-1/2") || t.EndsWith('*'))
                {
                    FlushGame();
                }
            }
        }

        // Flush any trailing game that didn't end with a result line
        FlushGame();

        return games;
    }

    private static ParsedGame BuildGame(
        Dictionary<string, string> tags, string moveText, string filePath)
    {
        var game = new ParsedGame { SourceFile = filePath, SourceType = "pgn" };

        if (tags.TryGetValue("Round", out var round) &&
            int.TryParse(round, out int rn))
            game.GameNumber = rn;

        if (tags.TryGetValue("Result",      out var result))     game.ResultTag         = result;
        if (tags.TryGetValue("Termination", out var term))       game.TerminationReason = term;
        if (tags.TryGetValue("FEN",         out var fen))        game.InitialFen        = fen;

        if (tags.TryGetValue("Date", out var dateStr) &&
            DateTime.TryParseExact(dateStr, "yyyy.MM.dd",
                CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime dt))
            game.Date = dt;

        // ChessBot color
        tags.TryGetValue("White", out var white);
        tags.TryGetValue("Black", out var black);

        bool cbIsWhite = string.Equals(white, "ChessBot", StringComparison.OrdinalIgnoreCase);
        bool cbIsBlack = string.Equals(black, "ChessBot", StringComparison.OrdinalIgnoreCase);

        game.ChessBotColor = cbIsWhite ? "White" : cbIsBlack ? "Black" : "Unknown";
        game.OpponentName  = cbIsWhite ? black   : cbIsBlack ? white   : null;

        // Outcome
        game.Outcome = (game.ResultTag, game.ChessBotColor) switch
        {
            ("1-0",     "White") => GameOutcomeKind.Win,
            ("0-1",     "Black") => GameOutcomeKind.Win,
            ("0-1",     "White") => GameOutcomeKind.Loss,
            ("1-0",     "Black") => GameOutcomeKind.Loss,
            ("1/2-1/2", _)      => GameOutcomeKind.Draw,
            ("*",       _)      => GameOutcomeKind.Aborted,
            _                   => GameOutcomeKind.Unknown
        };

        // Count plies from UCI move tokens in the move text
        var uciMoves = UciMoveRx.Matches(moveText);
        game.TotalPlies = uciMoves.Count;

        // White moves: 1st, 3rd, 5th, … → ceiling(total/2)
        // Black moves: 2nd, 4th, 6th, … → floor(total/2)
        int whitePlies = (game.TotalPlies + 1) / 2;
        int blackPlies = game.TotalPlies / 2;

        game.ChessBotPlies = cbIsWhite ? whitePlies : blackPlies;
        game.OpponentPlies = game.TotalPlies - game.ChessBotPlies;

        return game;
    }
}
