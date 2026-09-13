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
        string white = result.ChessBotIsWhite ? result.MeasuredPlayerName : result.OpponentPlayerName;
        string black = result.ChessBotIsWhite ? result.OpponentPlayerName : result.MeasuredPlayerName;

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
        w.WriteLine($"[Opening \"{result.OpeningName}\"]");

        // SetUp must accompany FEN whenever the game did not start from the initial position, or
        // a reader is entitled to ignore the FEN tag and replay the moves from the wrong board.
        if (result.InitialFen != OpeningBook.StartFen)
            w.WriteLine("[SetUp \"1\"]");
        w.WriteLine($"[FEN \"{result.InitialFen}\"]");

        if (!string.IsNullOrEmpty(result.TerminationReason))
            w.WriteLine($"[Termination \"{result.TerminationReason}\"]");
    }

    private static void WriteMoveText(StreamWriter w, GameResult result)
    {
        var sb = new System.Text.StringBuilder();
        int colWidth = 0;
        bool first = true;

        foreach (var m in result.Moves)
        {
            // A game started from a book position can open on Black's move, which PGN writes as
            // "12... Nf6". Without the number the movetext would read as White's move.
            string token = m.IsWhiteMove   ? $"{m.MoveNumber}. {m.UciMove} "
                         : first           ? $"{m.MoveNumber}... {m.UciMove} "
                         :                   $"{m.UciMove} ";
            sb.Append(token);
            colWidth += token.Length;
            first = false;

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
