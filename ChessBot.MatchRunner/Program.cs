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
            Console.WriteLine("  --games              TOTAL games played, both colors combined (default: 2; must be even)");
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
        Console.WriteLine($"Games: {cfg.GamesPerSide} per side ({cfg.GamesPerSide * 2} total)");


        var outcome = await MatchExecutor.RunAsync(cfg);

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
            Console.WriteLine("              lmr: baseline LMR schedule vs an alternative schedule");
            Console.WriteLine("  --ab-out    Output directory for the report (default: ab_reports)");
            Console.WriteLine("  --ab-depth  Fixed max search depth per position (default: 8)");
            Console.WriteLine("  --ab-nodes  Node budget per position (default: 200000)");
            return 0;
        }

        string mode = GetArgValue(args, "--ab-mode") ?? "partial-root";
        string outDir = GetArgValue(args, "--ab-out") ?? "ab_reports";
        int depth = int.TryParse(GetArgValue(args, "--ab-depth"), out int d) ? d : 8;
        long nodes = long.TryParse(GetArgValue(args, "--ab-nodes"), out long n) ? n : 200_000;

        Directory.CreateDirectory(outDir);

        AbConfig configA;
        AbConfig configB;
        string reportName;

        switch (mode)
        {
            case "lmr":
                configA = new AbConfig { Name = "baseline-lmr", Build = () => new ChessBot.Engine.Search.SearchSettings() };
                configB = new AbConfig
                {
                    Name = "alt-lmr",
                    Build = () => new ChessBot.Engine.Search.SearchSettings
                    {
                        LmrBaseOverride    = 1.0,
                        LmrDivisorOverride = 2.0,
                    },
                };
                reportName = "ab_report_lmr.log";
                break;

            case "partial-root":
            default:
                configA = new AbConfig { Name = "baseline", Build = () => new ChessBot.Engine.Search.SearchSettings { UsePartialRootResult = false } };
                configB = new AbConfig { Name = "partial-root", Build = () => new ChessBot.Engine.Search.SearchSettings { UsePartialRootResult = true } };
                reportName = "ab_report_partial_root.log";
                break;
        }

        Console.WriteLine($"Running A/B harness: mode={mode}  depth={depth}  nodeBudget={nodes:N0}");
        var results = AbHarness.Run(configA, configB, maxDepth: depth, maxNodes: nodes);

        string reportPath = Path.Combine(outDir, reportName);
        AbHarness.WriteReport(results, reportPath);

        Console.WriteLine($"A/B report written to: {reportPath}");
        return 0;
    }

    private static string? GetArgValue(string[] args, string flag)
    {
        int idx = Array.IndexOf(args, flag);
        return idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : null;
    }
}
