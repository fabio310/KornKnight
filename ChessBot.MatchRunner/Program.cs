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
            Console.WriteLine("  --use-partial-root-result  Enable UsePartialRootResult in ChessBot's search (default: off)");
            Console.WriteLine("  --quiet              Suppress move-by-move output");
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
}
