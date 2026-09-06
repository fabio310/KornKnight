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
            Console.WriteLine("  --blunder            Cross-engine evaluation disagreement threshold in centipawns (default: 200)");
            Console.WriteLine("  --engine-elo         Cap the external engine via UCI_LimitStrength + UCI_Elo");
            Console.WriteLine("                       (default: unset = full strength)");
            Console.WriteLine("  --engine-option      Extra UCI option as name=value (repeatable)");
            Console.WriteLine("  --reference-engine   Path to a reference UCI engine used for post-game same-engine");
            Console.WriteLine("                       move-loss analysis (default: unset = analysis skipped)");
            Console.WriteLine("  --reference-depth    Fixed search depth for reference-engine analysis (default: 18)");
            Console.WriteLine("  --reference-option   Reference-engine UCI option as name=value, e.g. Threads=1 (repeatable)");
            Console.WriteLine("  --moveloss-retries   Deterministic re-search attempts before an ineligible sample is excluded (default: 2)");
            Console.WriteLine("  --use-partial-root-result  Enable UsePartialRootResult in ChessBot's search (default: off)");
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
    ///   --ab-mode partial-root|lmr   (default: partial-root)
    ///   --ab-out &lt;dir&gt;           (default: ab_reports)
    ///   --ab-depth &lt;n&gt;           (default: 8)
    ///   --ab-nodes &lt;n&gt;           (default: 200000)
    /// </summary>
    private static int RunAbHarness(string[] args)
    {
        if (args.Contains("--help"))
        {
            Console.WriteLine("Usage: ChessBot.MatchRunner --ab-harness [--ab-mode partial-root|lmr] [--ab-out <dir>] [--ab-depth <n>] [--ab-nodes <n>]");
            Console.WriteLine("  --ab-mode   partial-root: baseline vs UsePartialRootResult=true (default)");
            Console.WriteLine("              lmr: legacy flat schedule vs the current logarithmic schedule");
            Console.WriteLine("  --ab-out    Output directory for the report (default: ab_reports)");
            Console.WriteLine("  --ab-depth  Fixed max search depth per position (default: 8)");
            Console.WriteLine("  --ab-nodes  Enforced node budget per position (default: 200000)");
            Console.WriteLine("  --ab-corpus-size  Generate a deterministic corpus of N positions instead of the");
            Console.WriteLine("                    built-in 10-position smoke corpus (needed for any KEEP verdict)");
            Console.WriteLine("  --ab-corpus-seed  Seed for the generated corpus (default: 20260906)");
            return 0;
        }

        string mode = GetArgValue(args, "--ab-mode") ?? "partial-root";
        string outDir = GetArgValue(args, "--ab-out") ?? "ab_reports";
        int depth = int.TryParse(GetArgValue(args, "--ab-depth"), out int d) ? d : 8;
        long nodes = long.TryParse(GetArgValue(args, "--ab-nodes"), out long n) ? n : 200_000;
        int corpusSize = int.TryParse(GetArgValue(args, "--ab-corpus-size"), out int cs) ? cs : 0;
        int corpusSeed = int.TryParse(GetArgValue(args, "--ab-corpus-seed"), out int seed) ? seed : 20260906;

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

        Console.WriteLine($"Running A/B harness: mode={mode}  depth={depth}  nodeBudget={nodes:N0}  corpus={corpus.Count} ({corpusSource})");
        var results = AbHarness.Run(configA, configB, corpus, maxDepth: depth, maxNodes: nodes);
        var report = AbHarness.BuildReport(results, mode, depth, nodes, corpusSource);

        string textPath = Path.Combine(outDir, reportStem + ".log");
        string jsonPath = Path.Combine(outDir, reportStem + ".json");
        AbHarness.WriteReport(report, textPath);
        AbHarness.WriteJson(report, jsonPath);

        Console.WriteLine($"A/B reports written to: {textPath}");
        Console.WriteLine($"                        {jsonPath}");
        Console.WriteLine($"Partial-root verdict: {report.PartialSelectionVerdict}  |  LMR verdict: {report.LmrVerdict}");
        return 0;
    }

    private static string? GetArgValue(string[] args, string flag)
    {
        int idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
