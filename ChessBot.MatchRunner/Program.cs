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

        if (args.Contains("--ab-harness"))
            return RunAbHarness(args);

        var cfg = MatchConfig.Parse(args);

        if (string.IsNullOrWhiteSpace(cfg.ExternalEnginePath))
        {
            Console.WriteLine("Usage: ChessBot.MatchRunner --engine <path> [--time <ms>] [--games <n>] [--pgn-dir <dir>] [--quiet]");
            Console.WriteLine("  --engine             Path to external UCI engine executable");
            Console.WriteLine("  --time               Milliseconds per move (default: 1000)");
            Console.WriteLine("  --games              EXACT total games played, both colors combined (default: 2).");
            Console.WriteLine("                       Odd totals are allowed; colors alternate and the imbalance is reported.");
            Console.WriteLine("  --games-per-side     Unambiguous alternative: n games as White + n as Black");
            Console.WriteLine("  --pgn-dir            PGN output directory (default: pgns)");
            Console.WriteLine("  --disagreement-threshold  Cross-engine evaluation disagreement threshold in");
            Console.WriteLine("                       centipawns (default: 200). Diagnostic only — this is two");
            Console.WriteLine("                       engines scoring a position differently, not a measured");
            Console.WriteLine("                       centipawn loss. Legacy alias: --blunder");
            Console.WriteLine("  --engine-elo         Cap the external engine via UCI_LimitStrength + UCI_Elo");
            Console.WriteLine("                       (default: unset = full strength)");
            Console.WriteLine("  --engine-option      Extra UCI option as name=value (repeatable)");
            Console.WriteLine("  --reference-engine   Path to a reference UCI engine used for post-game same-engine");
            Console.WriteLine("                       move-loss analysis (default: unset = analysis skipped)");
            Console.WriteLine("  --reference-depth    Fixed search depth for reference-engine analysis (default: 18)");
            Console.WriteLine("  --reference-option   Reference-engine UCI option as name=value, e.g. Threads=1 (repeatable)");
            Console.WriteLine("  --moveloss-retries   Deterministic re-search attempts before an ineligible sample is excluded (default: 2)");
            Console.WriteLine("  --use-partial-root-result  Enable UsePartialRootResult in ChessBot's search (default: off)");
            Console.WriteLine("  --openings           EPD/FEN list or PGN file of start positions, each played once");
            Console.WriteLine("                       with each colour, round-robin (default: the built-in 16-opening set)");
            Console.WriteLine("  --opening-plies      Book depth taken from a PGN opening file (default: 8)");
            Console.WriteLine("  --start-position-only  Play every game from the initial position. Two");
            Console.WriteLine("                       near-deterministic engines then replay the same handful of");
            Console.WriteLine("                       games, so the run has far fewer samples than it has games.");
            Console.WriteLine("  --concurrency        Games played at the same time (default: half the physical");
            Console.WriteLine($"                       cores — {ConcurrencyPolicy.RecommendedTimedCeiling} on this machine — because these games are timed.");
            Console.WriteLine("                       There is no fixed cap; above the default you get a warning,");
            Console.WriteLine("                       because contention distorts what a timed game measures.");
            Console.WriteLine("                       Pass 1 to measure at the machine's full speed.");
            Console.WriteLine("  --no-pin             Do not pin game workers to cores or raise process priority");
            Console.WriteLine("  --quiet              Suppress move-by-move output");
            Console.WriteLine();
            Console.WriteLine("  --ab-harness         Run a controlled, no-external-engine A/B comparison instead of a match");
            Console.WriteLine("                       (see --ab-harness --help for its own options)");
            Console.WriteLine();
            Console.WriteLine("No --engine path supplied. Exiting.");
            return 1;
        }

        if (!File.Exists(cfg.ExternalEnginePath))
        {
            Console.Error.WriteLine($"ERROR: Engine not found: {cfg.ExternalEnginePath}");
            return 2;
        }

        if (!string.IsNullOrWhiteSpace(cfg.ReferenceEnginePath) && !File.Exists(cfg.ReferenceEnginePath))
        {
            Console.Error.WriteLine($"ERROR: Reference engine not found: {cfg.ReferenceEnginePath}");
            return 2;
        }

        if (cfg.EngineElo is int elo)
            Console.WriteLine($"External engine capped to UCI_Elo {elo} (UCI_LimitStrength=true)");
        foreach (var opt in cfg.EngineOptions)
            Console.WriteLine($"External engine option: {opt.Name}={opt.Value}");
        if (!string.IsNullOrWhiteSpace(cfg.ReferenceEnginePath))
            Console.WriteLine($"Reference engine: {cfg.ReferenceEnginePath} (depth {cfg.ReferenceEngineDepth})");
        Console.WriteLine($"UsePartialRootResult: {cfg.UsePartialRootResult}");
        Console.WriteLine($"Games: {cfg.TotalGames} total" +
                          (cfg.ColorImbalance > 0 ? $" (odd: {(cfg.TotalGames + 1) / 2} as White, {cfg.TotalGames / 2} as Black)" : " (evenly split between colors)"));


        var outcome = await MatchExecutor.RunAsync(cfg, commandLineArgs: args);

        Console.WriteLine();
        Console.WriteLine($"=== Match complete: +{outcome.Wins}={outcome.Draws}-{outcome.Losses} for ChessBot ===");
        Console.WriteLine($"Summary written to: {outcome.SummaryPath}");

        return 0;
    }

    /// <summary>
    /// Runs a controlled, no-external-engine A/B comparison. Supported flags:
    ///   --ab-harness             (required to enter this mode)
    ///   --ab-mode partial-root|lmr|threat-eval   (default: partial-root)
    ///   --ab-out &lt;dir&gt;           (default: ab_reports)
    ///   --ab-depth &lt;n&gt;           (default: 8)
    ///   --ab-nodes &lt;n&gt;           (default: 200000)
    ///   --ab-time &lt;ms&gt;           (time budget per position instead of a node budget)
    ///   --ab-games &lt;n&gt;           (head-to-head games between the two configurations)
    /// </summary>
    private static int RunAbHarness(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("Usage: ChessBot.MatchRunner --ab-harness [--ab-mode partial-root|lmr|threat-eval|tapered-eval] [--ab-out <dir>] [--ab-depth <n>] [--ab-nodes <n>]");
            Console.WriteLine("  --ab-mode   partial-root: baseline vs UsePartialRootResult=true (default)");
            Console.WriteLine("              lmr: legacy flat schedule vs the current logarithmic schedule");
            Console.WriteLine("              threat-eval: hanging-piece eval term on (A) vs off (B)");
            Console.WriteLine("              tapered-eval: single table set (A) vs midgame/endgame taper (B)");
            Console.WriteLine("  --ab-out    Output directory for the report (default: ab_reports)");
            Console.WriteLine("  --ab-depth  Fixed max search depth per position (default: 8)");
            Console.WriteLine("  --ab-nodes  Enforced node budget per position (default: 200000)");
            Console.WriteLine("  --ab-time   Time budget in ms per position INSTEAD of a node budget. A node");
            Console.WriteLine("              budget hides the cost of an evaluation change (same nodes, less");
            Console.WriteLine("              time); only a time budget turns a cheaper evaluation into depth.");
            Console.WriteLine("  --ab-games  Play N head-to-head games between the two configurations");
            Console.WriteLine("              (rounded up to an even number: every opening is played twice,");
            Console.WriteLine("              once with each side as White). 0 = skip (default)");
            Console.WriteLine("  --ab-game-nodes  Node budget per move in those games (default: 50000)");
            Console.WriteLine("  --ab-game-ms     Time per move in those games INSTEAD of a node budget.");
            Console.WriteLine("              Node-budget games isolate decision quality; timed games also");
            Console.WriteLine("              charge each side for what its evaluation costs to compute.");
            Console.WriteLine("  --ab-concurrency  Games played in parallel. The default follows the budget:");
            Console.WriteLine($"              a node budget takes every physical core ({MachineTopology.PhysicalCoreCount} here) because it is");
            Console.WriteLine($"              bit-identical at any concurrency; a time budget takes half ({ConcurrencyPolicy.RecommendedTimedCeiling})");
            Console.WriteLine("              because it measures the scheduler too. Colour-reversed pairs always");
            Console.WriteLine("              run together on one slot; pass 1 to measure at full speed.");
            Console.WriteLine("  --ab-no-pin  Do not pin timed-game workers to cores or raise process priority");
            Console.WriteLine("  --ab-corpus-size  Generate a deterministic corpus of N positions instead of the");
            Console.WriteLine("                    built-in 10-position smoke corpus (needed for any KEEP verdict)");
            Console.WriteLine("  --ab-corpus-seed  Seed for the generated corpus (default: 20260906)");
            Console.WriteLine("  --ab-reference-engine <path>  Adjudicate differing choices with this UCI engine.");
            Console.WriteLine("                    Required for any KEEP or REVERT verdict.");
            Console.WriteLine("  --ab-reference-depth <n>      Fixed adjudication depth (default: 16)");
            return 0;
        }

        string mode = GetArgValue(args, "--ab-mode") ?? "partial-root";
        string outDir = GetArgValue(args, "--ab-out") ?? "ab_reports";
        int depth = int.TryParse(GetArgValue(args, "--ab-depth"), out int d) ? d : 8;
        long nodes = long.TryParse(GetArgValue(args, "--ab-nodes"), out long n) ? n : 200_000;
        int corpusSize = int.TryParse(GetArgValue(args, "--ab-corpus-size"), out int cs) ? cs : 0;
        int corpusSeed = int.TryParse(GetArgValue(args, "--ab-corpus-seed"), out int seed) ? seed : 20260906;
        int? timeMs    = int.TryParse(GetArgValue(args, "--ab-time"), out int tms) ? tms : null;
        int games      = int.TryParse(GetArgValue(args, "--ab-games"), out int g) ? g : 0;
        long gameNodes = long.TryParse(GetArgValue(args, "--ab-game-nodes"), out long gn) ? gn : 50_000;
        int? gameMs    = int.TryParse(GetArgValue(args, "--ab-game-ms"), out int gms) ? gms : null;
        // The budget decides how much of the machine the games may take, because the two budgets
        // are not comparable. Node-budget games are bit-identical at any concurrency, so they run
        // on every physical core; timed games measure the scheduler too, so they stay at half.
        // Most A/B questions here are node-budget questions, which is what makes a large machine
        // worth having.
        var gameBudget = gameMs is null ? BudgetKind.Nodes : BudgetKind.Time;
        int openingCount = (games + 1) / 2;
        int concurrency = int.TryParse(GetArgValue(args, "--ab-concurrency"), out int cc)
            ? Math.Max(1, cc)
            : ConcurrencyPolicy.DefaultFor(gameBudget, openingCount);
        bool pinWorkers = !args.Contains("--ab-no-pin") && gameBudget == BudgetKind.Time;

        Directory.CreateDirectory(outDir);

        IReadOnlyList<string> corpus;
        string corpusSource;
        if (corpusSize > 0)
        {
            corpus = AbHarness.GenerateCorpus(corpusSize, corpusSeed);
            corpusSource = $"generated, seed {corpusSeed}";
        }
        else
        {
            corpus = AbHarness.DefaultCorpus;
            corpusSource = "built-in smoke corpus";
        }

        AbConfig configA;
        AbConfig configB;
        string reportStem;

        switch (mode)
        {
            case "lmr":
                // Compares the schedule the engine replaced against the one it now uses, rather
                // than two parameterisations of the new one.
                configA = new AbConfig
                {
                    Name = "legacy-flat-lmr",
                    Description = "pre-table schedule: reduce 1 from move 5, 2 past move 8, depth-independent, no PV relief",
                    Build = () => new ChessBot.Engine.Search.SearchSettings { UseLegacyFlatLmr = true },
                };
                configB = new AbConfig
                {
                    Name = "logarithmic-lmr",
                    Description = "current schedule: R = 0.75 + ln(depth)*ln(moveNumber)/2.25, one ply less at PV nodes",
                    Build = () => new ChessBot.Engine.Search.SearchSettings { UseLegacyFlatLmr = false },
                };
                reportStem = "ab_report_lmr";
                break;

            case "threat-eval":
                // A is the current behaviour, B the change under test, as in the other modes —
                // so here B is the term switched off, and a KEEP verdict means "remove it".
                configA = new AbConfig
                {
                    Name = "threat-eval-on",
                    Description = "current behaviour: penalise every attacked, undefended non-pawn piece by half its value",
                    Build = () => new ChessBot.Engine.Search.SearchSettings { UseThreatEval = true },
                };
                configB = new AbConfig
                {
                    Name = "threat-eval-off",
                    Description = "hanging-piece term removed; quiescence alone resolves hanging material",
                    Build = () => new ChessBot.Engine.Search.SearchSettings { UseThreatEval = false },
                };
                reportStem = timeMs is int ? "ab_report_threat_eval_time" : "ab_report_threat_eval";
                break;

            case "tapered-eval":
                configA = new AbConfig
                {
                    Name = "single-table-eval",
                    Description = "current behaviour: one piece-square table set and one material scale for the whole game",
                    Build = () => new ChessBot.Engine.Search.SearchSettings { UseTaperedEval = false },
                };
                configB = new AbConfig
                {
                    Name = "tapered-eval",
                    Description = "separate midgame/endgame tables and material values, interpolated on the 24-point phase",
                    Build = () => new ChessBot.Engine.Search.SearchSettings { UseTaperedEval = true },
                };
                reportStem = timeMs is int ? "ab_report_tapered_eval_time" : "ab_report_tapered_eval";
                break;

            case "partial-root":
            default:
                mode = "partial-root";
                configA = new AbConfig
                {
                    Name = "baseline",
                    Description = "always fall back to the last fully completed iteration",
                    Build = () => new ChessBot.Engine.Search.SearchSettings { UsePartialRootResult = false },
                };
                configB = new AbConfig
                {
                    Name = "partial-root",
                    Description = "use a cancelled iteration's root candidate when it beats the completed score",
                    Build = () => new ChessBot.Engine.Search.SearchSettings { UsePartialRootResult = true },
                };
                reportStem = "ab_report_partial_root";
                break;
        }

        string budgetLabel = timeMs is int t ? $"timeBudget={t:N0}ms" : $"nodeBudget={nodes:N0}";
        Console.WriteLine($"Running A/B harness: mode={mode}  depth={depth}  {budgetLabel}  corpus={corpus.Count} ({corpusSource})");

        var results = AbHarness.Run(configA, configB, corpus, maxDepth: depth, maxNodes: nodes, maxTimeMs: timeMs);
        var report = AbHarness.BuildReport(results, mode, depth, nodes, corpusSource);
        report.TimeBudgetMs = timeMs;

        if (games > 0)
        {
            // Openings are played in pairs (both colours), so the count is rounded up to even.
            var openings = AbHarness.GenerateOpeningPositions(openingCount, corpusSeed);

            string perMove = gameMs is int gm ? $"{gm} ms/move" : $"{gameNodes:N0} nodes/move";
            Console.WriteLine($"Playing {openings.Count * 2} head-to-head games at {perMove}, " +
                              $"{ConcurrencyPolicy.Describe(gameBudget, concurrency)}" +
                              (pinWorkers ? ", workers pinned to cores" : "") + "...");
            ConcurrencyPolicy.WarnIfOversubscribed(gameBudget, concurrency);

            if (pinWorkers)
                Console.WriteLine($"Process priority: {MachineTopology.RaiseProcessPriority()}");

            var h2h = AbHarness.PlayHeadToHead(
                configA, configB, openings, gameNodes,
                msPerMove: gameMs, concurrency: concurrency, pinWorkers: pinWorkers);
            AbHarness.AttachHeadToHead(report, h2h);

            Console.WriteLine($"  B scored {h2h.ScoreRateB:P1} (+{h2h.WinsB}={h2h.Draws}-{h2h.WinsA})");
        }

        // Reference adjudication of the positions where the two configurations differ. Without
        // it the verdict stays INCONCLUSIVE by design, because nothing else in this harness can
        // say which of two different moves was better.
        string? refEngine = GetArgValue(args, "--ab-reference-engine");
        int refDepth = int.TryParse(GetArgValue(args, "--ab-reference-depth"), out int rd) ? rd : 16;
        if (!string.IsNullOrWhiteSpace(refEngine))
        {
            if (!File.Exists(refEngine))
            {
                Console.Error.WriteLine($"ERROR: reference engine not found: {refEngine}");
                return 2;
            }

            Console.WriteLine($"Adjudicating {report.Disagreements.Count} disagreement(s) with " +
                              $"{refEngine} at depth {refDepth}...");
            AbHarness.AdjudicateAsync(report, refEngine, refDepth).GetAwaiter().GetResult();
        }

        string textPath = Path.Combine(outDir, reportStem + ".log");
        string jsonPath = Path.Combine(outDir, reportStem + ".json");
        AbHarness.WriteReport(report, textPath);
        AbHarness.WriteJson(report, jsonPath);

        Console.WriteLine($"A/B reports written to: {textPath}");
        Console.WriteLine($"                        {jsonPath}");
        Console.WriteLine($"Partial-root verdict: {report.PartialSelectionVerdict}  |  LMR verdict: {report.LmrVerdict}" +
                          $"  |  Threat-eval verdict: {report.ThreatEvalVerdict}");
        return 0;
    }

    private static string? GetArgValue(string[] args, string flag)
    {
        int idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
