namespace ChessBot.MatchRunner;

/// <summary>
/// Entry point for ChessBot.MatchRunner.
/// Usage: ChessBot.MatchRunner --engine &lt;path&gt; [--time &lt;ms&gt;] [--games &lt;n&gt;] [--pgn-dir &lt;dir&gt;] [--quiet]
/// </summary>
internal class Program
{
    static async Task<int> Main(string[] args)
    {
        Console.WriteLine("=== ChessBot Match Runner ===");

        var cfg = MatchConfig.Parse(args);

        if (string.IsNullOrWhiteSpace(cfg.ExternalEnginePath))
        {
            Console.WriteLine("Usage: ChessBot.MatchRunner --engine <path> [--time <ms>] [--games <n>] [--pgn-dir <dir>] [--quiet]");
            Console.WriteLine("  --engine  Path to external UCI engine executable");
            Console.WriteLine("  --time    Milliseconds per move (default: 1000)");
            Console.WriteLine("  --games   Games per side (default: 1)");
            Console.WriteLine("  --pgn-dir PGN output directory (default: pgns)");
            Console.WriteLine("  --blunder Blunder threshold in centipawns (default: 200)");
            Console.WriteLine("  --quiet   Suppress move-by-move output");
            Console.WriteLine();
            Console.WriteLine("No --engine path supplied. Exiting.");
            return 1;
        }

        if (!File.Exists(cfg.ExternalEnginePath))
        {
            Console.Error.WriteLine($"ERROR: Engine not found: {cfg.ExternalEnginePath}");
            return 2;
        }

        Directory.CreateDirectory(cfg.PgnOutputDir);

        var results = new List<GameResult>();
        int wins = 0, losses = 0, draws = 0;

        for (int game = 0; game < cfg.GamesPerSide * 2; game++)
        {
            bool chessBotIsWhite = (game % 2 == 0);
            string colorLabel = chessBotIsWhite ? "White" : "Black";
            Console.WriteLine();
            Console.WriteLine($"--- Game {game + 1}: ChessBot plays {colorLabel} ---");

            GameResult result;
            using (var uci = new UciAdapter(cfg.ExternalEnginePath))
            {
                await uci.InitializeAsync();
                var runner = new GameRunner(uci, cfg);
                result = await runner.PlayGameAsync(chessBotIsWhite, game + 1);
            }

            results.Add(result);

            switch (result.Outcome)
            {
                case GameOutcome.ChessBotWin:  wins++;   break;
                case GameOutcome.ChessBotLoss: losses++; break;
                default:                       draws++;  break;
            }

            Console.WriteLine($"Game {game + 1} result: {result.Outcome}  ({result.Moves.Count} moves)");

            var analyzer = new PositionAnalyzer(cfg);
            var analysis = analyzer.Analyze(result);

            // Always write the per-game log alongside the PGN
            string logFile = Path.ChangeExtension(result.PgnPath, ".log");
            analyzer.WriteGameLog(result, analysis, logFile);
            Console.WriteLine($"  Log written to: {logFile}");

            if (analysis.Blunders.Count > 0)
            {
                Console.WriteLine($"  Blunders found: {analysis.Blunders.Count}");
                foreach (var b in analysis.Blunders)
                    Console.WriteLine($"    Move {b.MoveNumber}: {b.Move} swing {b.SwingCp:+#;-#;0}cp — FEN: {b.Fen}");
            }
        }

        Console.WriteLine();
        Console.WriteLine($"=== Match complete: +{wins}={draws}-{losses} for ChessBot ===");

        string summaryPath = Path.Combine(cfg.PgnOutputDir, "match_summary.log");
        WriteSummary(summaryPath, results, cfg);
        Console.WriteLine($"Summary written to: {summaryPath}");

        return 0;
    }

    private static void WriteSummary(string path, List<GameResult> results, MatchConfig cfg)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("ChessBot Match Summary");
        w.WriteLine($"Engine: {cfg.ExternalEnginePath}");
        w.WriteLine($"Move time: {cfg.MoveTimeMs}ms");
        w.WriteLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine();

        int wins = 0, losses = 0, draws = 0;
        foreach (var r in results)
        {
            switch (r.Outcome)
            {
                case GameOutcome.ChessBotWin:  wins++;   break;
                case GameOutcome.ChessBotLoss: losses++; break;
                default:                       draws++;  break;
            }
            w.WriteLine($"Game {r.GameNumber} ({(r.ChessBotIsWhite ? "White" : "Black")}): {r.Outcome} in {r.Moves.Count} moves");
        }
        w.WriteLine();
        w.WriteLine($"Result: +{wins}={draws}-{losses}");
    }
}
