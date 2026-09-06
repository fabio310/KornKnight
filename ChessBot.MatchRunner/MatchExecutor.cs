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

    /// <summary>Identifier shared by every artifact this run produced.</summary>
    public string RunId { get; set; } = string.Empty;

    /// <summary>Path of the written run_manifest.json.</summary>
    public string ManifestPath { get; set; } = string.Empty;
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
    public static async Task<MatchOutcome> RunAsync(
        MatchConfig cfg, CancellationToken ct = default, IEnumerable<string>? commandLineArgs = null)
    {
        Directory.CreateDirectory(cfg.PgnOutputDir);

        // Source state and start time are captured before anything is written, so the manifest
        // describes the code that produced the run rather than the working tree afterwards
        // (which the run's own PGNs and JSON would otherwise make look dirty).
        string runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        var manifest = RunManifestWriter.BeginRun(
            runId,
            cfg.PgnOutputDir,
            commandLineArgs ?? Environment.GetCommandLineArgs().Skip(1),
            cfg.Describe());

        var outcome = new MatchOutcome { RunId = runId };
        var artifactFiles = new List<string>();

        for (int game = 0; game < cfg.TotalGames; game++)
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

                foreach (var opt in cfg.ReferenceEngineOptions)
                    await refEngine.SetOptionAsync(opt.Name, opt.Value);
                if (cfg.ReferenceEngineOptions.Count > 0)
                    await refEngine.SyncAsync(ct: ct);

                var lossRecords = await MoveLossAnalyzer.AnalyzeGameAsync(
                    result, refEngine, cfg.ReferenceEngineDepth, cfg.MoveLossMaxRetries, ct);

                string lossPath = Path.ChangeExtension(result.PgnPath, ".moveloss.log");
                MoveLossAnalyzer.WriteReport(
                    result, lossRecords, lossPath,
                    referenceEngineName: refEngine.EngineName,
                    referenceEngineDepth: cfg.ReferenceEngineDepth,
                    referenceEngineOptions: cfg.ReferenceEngineOptions);
                Console.WriteLine($"  Move-loss report written to: {lossPath}");
                artifactFiles.Add(lossPath);
            }
        }

        string summaryPath = Path.Combine(cfg.PgnOutputDir, "match_summary.log");
        WriteSummary(summaryPath, outcome, cfg);
        outcome.SummaryPath = summaryPath;
        artifactFiles.Add(summaryPath);

        // ── Versioned machine-readable result document ───────────────────────────
        string resultJsonPath = Path.Combine(cfg.PgnOutputDir, "match_result.json");
        MatchResultWriter.Write(outcome, resultJsonPath);
        artifactFiles.Add(resultJsonPath);

        // ── Run manifest (reproducibility metadata) ──────────────────────────────
        // The inventory lists every artifact the run produced, not just the last few: the PGNs
        // and per-game logs are the primary evidence and were previously missing entirely.
        string manifestPath = Path.Combine(cfg.PgnOutputDir, "run_manifest.json");
        foreach (var game in outcome.Games)
        {
            if (!string.IsNullOrWhiteSpace(game.PgnPath))
            {
                artifactFiles.Add(game.PgnPath);
                artifactFiles.Add(Path.ChangeExtension(game.PgnPath, ".log"));
            }
        }

        RunManifestWriter.CompleteRun(manifest, artifactFiles.Append(manifestPath));
        RunManifestWriter.Write(manifest, manifestPath);
        outcome.ManifestPath = manifestPath;

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
