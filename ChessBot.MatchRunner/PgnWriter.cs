namespace ChessBot.MatchRunner;

/// <summary>
/// Writes game results to PGN (Portable Game Notation) format.
/// </summary>
public static class PgnWriter
{
    /// <summary>
    /// Writes a single game to a PGN file.
    /// Move notation is in UCI format (e.g., "e2e4") wrapped in PGN move numbers.
    /// </summary>
    public static void Write(string path, GameResult result)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");

        using var w = new StreamWriter(path, append: false);
        WriteHeaders(w, result);
        w.WriteLine();
        WriteMoveText(w, result);
        w.WriteLine();
    }

    /// <summary>
    /// Appends all games in a match to a single PGN file.
    /// </summary>
    public static void AppendAll(string path, IEnumerable<GameResult> results)
    {
        foreach (var r in results)
        {
            using var w = new StreamWriter(path, append: true);
            WriteHeaders(w, r);
            w.WriteLine();
            WriteMoveText(w, r);
            w.WriteLine();
            w.WriteLine();
        }
    }

    private static void WriteHeaders(StreamWriter w, GameResult result)
    {
        string white = result.ChessBotIsWhite ? "ChessBot" : "External";
        string black = result.ChessBotIsWhite ? "External" : "ChessBot";

        string resultTag = result.Outcome switch
        {
            GameOutcome.ChessBotWin  => result.ChessBotIsWhite ? "1-0" : "0-1",
            GameOutcome.ChessBotLoss => result.ChessBotIsWhite ? "0-1" : "1-0",
            GameOutcome.Draw         => "1/2-1/2",
            _                        => "*"
        };

        w.WriteLine($"[Event \"ChessBot Match\"]");
        w.WriteLine($"[Site \"Local\"]");
        w.WriteLine($"[Date \"{DateTime.Now:yyyy.MM.dd}\"]");
        w.WriteLine($"[Round \"{result.GameNumber}\"]");
        w.WriteLine($"[White \"{white}\"]");
        w.WriteLine($"[Black \"{black}\"]");
        w.WriteLine($"[Result \"{resultTag}\"]");
        w.WriteLine($"[FEN \"{result.InitialFen}\"]");
        if (!string.IsNullOrEmpty(result.TerminationReason))
            w.WriteLine($"[Termination \"{result.TerminationReason}\"]");
    }

    private static void WriteMoveText(StreamWriter w, GameResult result)
    {
        var sb = new System.Text.StringBuilder();
        int colWidth = 0;

        foreach (var m in result.Moves)
        {
            if (m.IsWhiteMove)
            {
                string token = $"{m.MoveNumber}. {m.UciMove} ";
                sb.Append(token);
                colWidth += token.Length;
            }
            else
            {
                string token = $"{m.UciMove} ";
                sb.Append(token);
                colWidth += token.Length;
            }

            if (colWidth > 70)
            {
                sb.Append('\n');
                colWidth = 0;
            }
        }

        // Game termination marker
        string resultMarker = result.Outcome switch
        {
            GameOutcome.ChessBotWin  => result.ChessBotIsWhite ? "1-0" : "0-1",
            GameOutcome.ChessBotLoss => result.ChessBotIsWhite ? "0-1" : "1-0",
            GameOutcome.Draw         => "1/2-1/2",
            _                        => "*"
        };
        sb.Append(resultMarker);

        w.WriteLine(sb.ToString());
    }
}
