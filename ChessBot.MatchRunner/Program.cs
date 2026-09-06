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
            Console.WriteLine("  --engine         Path to external UCI engine executable");
            Console.WriteLine("  --time           Milliseconds per move (default: 1000)");
            Console.WriteLine("  --games          Games per side (default: 1)");
            Console.WriteLine("  --pgn-dir        PGN output directory (default: pgns)");
            Console.WriteLine("  --blunder        Blunder threshold in centipawns (default: 200)");
            Console.WriteLine("  --engine-elo     Cap the external engine via UCI_LimitStrength + UCI_Elo");
            Console.WriteLine("                   (default: unset = full strength)");
            Console.WriteLine("  --engine-option  Extra UCI option as name=value (repeatable)");
            Console.WriteLine("  --quiet          Suppress move-by-move output");
            Console.WriteLine();
            Console.WriteLine("No --engine path supplied. Exiting.");
            return 1;
        }

        if (!File.Exists(cfg.ExternalEnginePath))
        {
            Console.Error.WriteLine($"ERROR: Engine not found: {cfg.ExternalEnginePath}");
            return 2;
        }

        if (cfg.EngineElo is int elo)
            Console.WriteLine($"External engine capped to UCI_Elo {elo} (UCI_LimitStrength=true)");
        foreach (var opt in cfg.EngineOptions)
            Console.WriteLine($"External engine option: {opt.Name}={opt.Value}");

        var outcome = await MatchExecutor.RunAsync(cfg);

        Console.WriteLine();
        Console.WriteLine($"=== Match complete: +{outcome.Wins}={outcome.Draws}-{outcome.Losses} for ChessBot ===");
        Console.WriteLine($"Summary written to: {outcome.SummaryPath}");

        return 0;
    }
}
