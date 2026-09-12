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

        if (args.Contains("--ab"))
            return await AbCommand.RunAsync(args);

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
            Console.WriteLine("  --ab                 Compare two engine binaries against each other instead of");
            Console.WriteLine("                       playing a match (see --ab --help for its own options)");
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
}
