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
    /// <summary>
    /// Everything one game produces. Collected per game rather than written into shared state,
    /// so games can run concurrently and still be aggregated in a fixed order.
    /// </summary>
    private sealed class GameRunOutcome
    {
        public required GameResult Result { get; init; }
        public required string OpponentName { get; init; }
        public required List<string> ArtifactFiles { get; init; }
        public required List<DisagreementDto> Disagreements { get; init; }

        /// <summary>Null when no reference engine was configured.</summary>
        public List<MoveLossAnalyzer.MoveLossRecord>? LossRecords { get; init; }
        public ReferenceEngineDto? ReferenceEngine { get; init; }

        /// <summary>
        /// The game's console output, buffered rather than written as it happens: concurrent
        /// games would otherwise interleave their lines into an unreadable transcript.
        /// </summary>
        public required string ConsoleOutput { get; init; }
    }

    /// <summary>
    /// Plays one game and runs the per-game analysis. Touches no shared state: its opponent
    /// process, its engine and its output files all belong to this game alone.
    /// </summary>
    private static async Task<GameRunOutcome> PlayAndAnalyzeGameAsync(
        int gameIndex, MatchConfig cfg, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();

        var log = new System.Text.StringBuilder();
        var artifactFiles = new List<string>();

        bool chessBotIsWhite = (gameIndex % 2 == 0);
        string colorLabel = chessBotIsWhite ? "White" : "Black";
        log.AppendLine();
        log.AppendLine($"--- Game {gameIndex + 1}: ChessBot plays {colorLabel} ---");

        GameResult result;
        string opponentName;
        using (var uci = new UciAdapter(cfg.ExternalEnginePath))
        {
            await uci.InitializeAsync(ct);
            await cfg.ApplyEngineOptionsAsync(uci, ct);
            opponentName = uci.EngineName;

            var runner = new GameRunner(uci, cfg);
            result = await runner.PlayGameAsync(chessBotIsWhite, gameIndex + 1, ct);
        }

        log.AppendLine($"Game {gameIndex + 1} result: {result.Outcome}  ({result.Moves.Count} moves)");

        var analyzer = new PositionAnalyzer(cfg);
        var analysis = analyzer.Analyze(result);

        // Always write the per-game log alongside the PGN
        string logFile = Path.ChangeExtension(result.PgnPath, ".log");
        analyzer.WriteGameLog(result, analysis, logFile);
        log.AppendLine($"  Log written to: {logFile}");

        if (analysis.CrossEngineEvaluationDisagreements.Count > 0)
        {
            log.AppendLine($"  Cross-engine eval disagreements found: {analysis.CrossEngineEvaluationDisagreements.Count}");
            foreach (var b in analysis.CrossEngineEvaluationDisagreements)
                log.AppendLine($"    Move {b.MoveNumber}: {b.Move} swing {b.SwingCp:+#;-#;0}cp — FEN: {b.Fen}");
        }

        // ── Real move-loss analysis (outside the timed game) ────────────────────
        // Runs after the game has fully completed, using a separate reference-engine
        // process (normally Stockfish), so it can never affect move-time budgets.
        List<MoveLossAnalyzer.MoveLossRecord>? lossRecords = null;
        ReferenceEngineDto? referenceEngine = null;

        if (!string.IsNullOrWhiteSpace(cfg.ReferenceEnginePath))
        {
            using var refEngine = new UciAdapter(cfg.ReferenceEnginePath);
            await refEngine.InitializeAsync(ct);

            foreach (var opt in cfg.ReferenceEngineOptions)
                await refEngine.SetOptionAsync(opt.Name, opt.Value);
            if (cfg.ReferenceEngineOptions.Count > 0)
                await refEngine.SyncAsync(ct: ct);

            lossRecords = await MoveLossAnalyzer.AnalyzeGameAsync(
                result, refEngine, cfg.ReferenceEngineDepth, cfg.MoveLossMaxRetries, ct);

            string lossPath = Path.ChangeExtension(result.PgnPath, ".moveloss.log");
            MoveLossAnalyzer.WriteReport(
                result, lossRecords, lossPath,
                referenceEngineName: refEngine.EngineName,
                referenceEngineDepth: cfg.ReferenceEngineDepth,
                referenceEngineOptions: cfg.ReferenceEngineOptions);
            log.AppendLine($"  Move-loss report written to: {lossPath}");
            artifactFiles.Add(lossPath);

            // Recorded from the engine that actually ran: identity, the options set for it, and
            // the options it reported (including the defaults it was left at).
            referenceEngine = new ReferenceEngineDto
            {
                Path            = cfg.ReferenceEnginePath,
                Name            = refEngine.EngineName,
                Depth           = cfg.ReferenceEngineDepth,
                Options         = cfg.ReferenceEngineOptions.ToDictionary(o => o.Name, o => o.Value),
                ReportedOptions = ParseReportedOptions(refEngine.HandshakeLines),
                Networks        = refEngine.HandshakeLines
                    .Where(l => l.Contains("NNUE", StringComparison.OrdinalIgnoreCase) ||
                                l.Contains("network", StringComparison.OrdinalIgnoreCase))
                    .ToList(),
            };
        }

        return new GameRunOutcome
        {
            Result        = result,
            OpponentName  = opponentName,
            ArtifactFiles = artifactFiles,
            LossRecords   = lossRecords,
            ReferenceEngine = referenceEngine,
            ConsoleOutput = log.ToString(),
            Disagreements = analysis.CrossEngineEvaluationDisagreements
                .Select(d => new DisagreementDto
                {
                    MoveNumber  = d.MoveNumber,
                    IsWhiteMove = d.IsWhiteMove,
                    Move        = d.Move,
                    Fen         = d.Fen,
                    ScoreBefore = d.ScoreBefore,
                    ScoreAfter  = d.ScoreAfter,
                    SwingCp     = d.SwingCp,
                })
                .ToList(),
        };
    }

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

        var allLossRecords       = new List<MoveLossAnalyzer.MoveLossRecord>();
        var perGameLoss          = new Dictionary<int, List<MoveLossAnalyzer.MoveLossRecord>>();
        var perGameDisagreements = new Dictionary<int, List<DisagreementDto>>();
        ReferenceEngineDto? referenceEngineInfo = null;

        // ── Play the games ────────────────────────────────────────────────────
        // Games are independent — each spawns its own opponent process and its own ChessEngine —
        // so they are played concurrently when cfg.Concurrency allows it. Everything a game
        // produces is collected per game and folded in game order afterwards, so the summary,
        // the result document and the manifest are identical whatever order the games finish in.
        int concurrency = Math.Max(1, cfg.Concurrency);
        var completed = new GameRunOutcome?[cfg.TotalGames];

        if (concurrency > 1)
            Console.WriteLine($"Playing {cfg.TotalGames} games, {concurrency} at a time. " +
                              $"Concurrent games share the CPU, so each engine searches fewer nodes " +
                              $"per move than it would alone (--concurrency 1 for full speed).");

        var consoleLock = new object();

        using (var gate = new SemaphoreSlim(concurrency))
        {
            var running = new List<Task>(cfg.TotalGames);

            for (int game = 0; game < cfg.TotalGames; game++)
            {
                int index = game;
                running.Add(Task.Run(async () =>
                {
                    await gate.WaitAsync(ct);
                    try
                    {
                        var played = await PlayAndAnalyzeGameAsync(index, cfg, ct);
                        completed[index] = played;

                        // Printed as each game finishes rather than at the end, so a long match
                        // shows progress. Whole blocks are written under the lock, so concurrent
                        // games never interleave their lines.
                        lock (consoleLock) Console.Write(played.ConsoleOutput);
                    }
                    finally
                    {
                        gate.Release();
                    }
                }, ct));
            }

            await Task.WhenAll(running);
        }

        foreach (var game in completed)
        {
            if (game is null) continue;   // only reachable if the run was cancelled

            outcome.Games.Add(game.Result);
            outcome.OpponentName = game.OpponentName;

            switch (game.Result.Outcome)
            {
                case GameOutcome.ChessBotWin:  outcome.Wins++;   break;
                case GameOutcome.ChessBotLoss: outcome.Losses++; break;
                default:                       outcome.Draws++;  break;
            }

            artifactFiles.AddRange(game.ArtifactFiles);
            perGameDisagreements[game.Result.GameNumber] = game.Disagreements;

            if (game.LossRecords is not null)
            {
                allLossRecords.AddRange(game.LossRecords);
                perGameLoss[game.Result.GameNumber] = game.LossRecords;
            }

            referenceEngineInfo ??= game.ReferenceEngine;
        }

        string summaryPath = Path.Combine(cfg.PgnOutputDir, "match_summary.log");
        WriteSummary(summaryPath, outcome, cfg);
        outcome.SummaryPath = summaryPath;
        artifactFiles.Add(summaryPath);

        // ── Versioned machine-readable result document ───────────────────────────
        // Built as the authoritative record of the run: configuration, both game counts,
        // engine identities, disagreements and move-loss results, so a consumer never has to
        // scrape the human-readable log for anything.
        string resultJsonPath = Path.Combine(cfg.PgnOutputDir, "match_result.json");

        var document = MatchResultDocument.From(outcome);
        document.RequestedGames       = cfg.TotalGames;
        document.ColorImbalance       = cfg.ColorImbalance;
        document.UsePartialRootResult = cfg.UsePartialRootResult;
        document.EffectiveConfig      = cfg.Describe();
        document.ReferenceEngine      = referenceEngineInfo;
        document.Opponent = new OpponentEngineDto
        {
            Path         = cfg.ExternalEnginePath,
            Name         = outcome.OpponentName,
            LimitedToElo = cfg.EngineElo,
            Options      = cfg.EngineOptions.ToDictionary(o => o.Name, o => o.Value),
        };

        foreach (var g in document.Games)
        {
            if (perGameDisagreements.TryGetValue(g.GameNumber, out var d)) g.Disagreements = d;
            if (perGameLoss.TryGetValue(g.GameNumber, out var loss))
            {
                g.MoveLoss          = loss.Select(MoveLossRecordDto.From).ToList();
                g.MoveLossAggregate = MoveLossAggregateDto.From(loss);
            }
        }

        if (allLossRecords.Count > 0)
            document.MoveLossAggregate = MoveLossAggregateDto.From(allLossRecords);

        // Artifact references, relative to the output directory so they stay portable.
        document.ArtifactFiles = artifactFiles
            .Append(resultJsonPath)
            .Append(Path.Combine(cfg.PgnOutputDir, "run_manifest.json"))
            .Concat(outcome.Games.SelectMany(g => string.IsNullOrWhiteSpace(g.PgnPath)
                ? Array.Empty<string>()
                : new[] { g.PgnPath, Path.ChangeExtension(g.PgnPath, ".log") }))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(p => Path.GetFileName(p))
            .OrderBy(p => p, StringComparer.Ordinal)
            .ToList();

        MatchResultWriter.Write(document, resultJsonPath);
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

    /// <summary>
    /// Turns the engine's "option name X type spin default 1 min 1 max 1024" handshake lines
    /// into name → default pairs, so a run records the options it left at their defaults rather
    /// than only the ones it set explicitly.
    /// </summary>
    private static Dictionary<string, string> ParseReportedOptions(IReadOnlyList<string> handshakeLines)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in handshakeLines)
        {
            if (!line.StartsWith("option name ", StringComparison.OrdinalIgnoreCase)) continue;

            int typeIdx = line.IndexOf(" type ", StringComparison.OrdinalIgnoreCase);
            if (typeIdx < 0) continue;

            string name = line["option name ".Length..typeIdx].Trim();

            int defaultIdx = line.IndexOf(" default ", StringComparison.OrdinalIgnoreCase);
            string value = "(no default reported)";
            if (defaultIdx >= 0)
            {
                string rest = line[(defaultIdx + " default ".Length)..];
                foreach (string terminator in new[] { " min ", " max ", " var " })
                {
                    int cut = rest.IndexOf(terminator, StringComparison.OrdinalIgnoreCase);
                    if (cut >= 0) rest = rest[..cut];
                }
                value = rest.Trim();
            }

            options[name] = value;
        }

        return options;
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
