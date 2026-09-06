namespace ChessBot.MatchRunner;

/// <summary>
/// Aggregate result of a whole match (all games of one <see cref="MatchConfig"/>).
/// </summary>
public sealed class MatchOutcome
{
    public List<GameResult> Games { get; } = new();

    public int Wins   { get; set; }
    public int Draws  { get; set; }
    public int Losses { get; set; }

    /// <summary>Number of games that produced a result (== Games.Count).</summary>
    public int N => Wins + Draws + Losses;

    /// <summary>(W + 0.5·D) / N — 0 when no games were played.</summary>
    public double ScoreRate => N == 0 ? 0.0 : (Wins + 0.5 * Draws) / N;

    /// <summary>Name reported by the external engine ("id name ...").</summary>
    public string OpponentName { get; set; } = "Unknown";

    /// <summary>Path of the written match_summary.log.</summary>
    public string SummaryPath { get; set; } = string.Empty;
}

/// <summary>
/// Runs a full match (ChessBot vs. an external UCI engine) as configured by a
/// <see cref="MatchConfig"/>: plays the games with alternating colors, writes one
/// PGN + one .log per game plus a match_summary.log into the configured output
/// directory. Shared by ChessBot.MatchRunner's CLI and ChessBot.EloEvaluator's
/// strength sweep, so the game loop exists exactly once.
/// </summary>
public static class MatchExecutor
{
    public static async Task<MatchOutcome> RunAsync(MatchConfig cfg, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cfg.PgnOutputDir);

        var outcome = new MatchOutcome();

        for (int game = 0; game < cfg.GamesPerSide * 2; game++)
        {
            ct.ThrowIfCancellationRequested();

            bool chessBotIsWhite = (game % 2 == 0);
            string colorLabel = chessBotIsWhite ? "White" : "Black";
            Console.WriteLine();
            Console.WriteLine($"--- Game {game + 1}: ChessBot plays {colorLabel} ---");

            GameResult result;
            using (var uci = new UciAdapter(cfg.ExternalEnginePath))
            {
                await uci.InitializeAsync(ct);
                await cfg.ApplyEngineOptionsAsync(uci, ct);
                outcome.OpponentName = uci.EngineName;

                var runner = new GameRunner(uci, cfg);
                result = await runner.PlayGameAsync(chessBotIsWhite, game + 1, ct);
            }

            outcome.Games.Add(result);

            switch (result.Outcome)
            {
                case GameOutcome.ChessBotWin:  outcome.Wins++;   break;
                case GameOutcome.ChessBotLoss: outcome.Losses++; break;
                default:                       outcome.Draws++;  break;
            }

            Console.WriteLine($"Game {game + 1} result: {result.Outcome}  ({result.Moves.Count} moves)");

            var analyzer = new PositionAnalyzer(cfg);
            var analysis = analyzer.Analyze(result);

            // Always write the per-game log alongside the PGN
            string logFile = Path.ChangeExtension(result.PgnPath, ".log");
            analyzer.WriteGameLog(result, analysis, logFile);
            Console.WriteLine($"  Log written to: {logFile}");

            if (analysis.CrossEngineEvaluationDisagreements.Count > 0)
            {
                Console.WriteLine($"  Cross-engine eval disagreements found: {analysis.CrossEngineEvaluationDisagreements.Count}");
                foreach (var b in analysis.CrossEngineEvaluationDisagreements)
                    Console.WriteLine($"    Move {b.MoveNumber}: {b.Move} swing {b.SwingCp:+#;-#;0}cp — FEN: {b.Fen}");
            }

            // ── Real move-loss analysis (outside the timed game) ────────────────────
            // Runs after the game has fully completed, using a separate reference-engine
            // process (normally Stockfish), so it can never affect move-time budgets.
            if (!string.IsNullOrWhiteSpace(cfg.ReferenceEnginePath))
            {
                using var refEngine = new UciAdapter(cfg.ReferenceEnginePath);
                await refEngine.InitializeAsync(ct);

                var lossRecords = await MoveLossAnalyzer.AnalyzeGameAsync(
                    result, refEngine, cfg.ReferenceEngineDepth, ct);

                string lossPath = Path.ChangeExtension(result.PgnPath, ".moveloss.log");
                MoveLossAnalyzer.WriteReport(result, lossRecords, lossPath);
                Console.WriteLine($"  Move-loss report written to: {lossPath}");
            }
        }

        string summaryPath = Path.Combine(cfg.PgnOutputDir, "match_summary.log");
        WriteSummary(summaryPath, outcome, cfg);
        outcome.SummaryPath = summaryPath;

        return outcome;
    }

    private static void WriteSummary(string path, MatchOutcome outcome, MatchConfig cfg)
    {
        using var w = new StreamWriter(path);
        w.WriteLine("ChessBot Match Summary");
        w.WriteLine($"Engine: {cfg.ExternalEnginePath}");
        w.WriteLine($"Move time: {cfg.MoveTimeMs}ms");
        if (cfg.EngineElo is int elo)
            w.WriteLine($"Engine strength: UCI_LimitStrength=true, UCI_Elo={elo}");
        foreach (var opt in cfg.EngineOptions)
            w.WriteLine($"Engine option: {opt.Name}={opt.Value}");
        w.WriteLine($"Date: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine();

        foreach (var r in outcome.Games)
            w.WriteLine($"Game {r.GameNumber} ({(r.ChessBotIsWhite ? "White" : "Black")}): " +
                        $"{r.Outcome} in {r.Moves.Count} moves");

        w.WriteLine();
        w.WriteLine($"Result: +{outcome.Wins}={outcome.Draws}-{outcome.Losses}");
    }
}
