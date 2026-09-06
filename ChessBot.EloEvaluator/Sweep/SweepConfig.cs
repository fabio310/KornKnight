using System.Globalization;

namespace ChessBot.EloEvaluator.Sweep;

/// <summary>
/// Configuration for the <c>sweep</c> subcommand: play rounds against a local UCI engine
/// (Stockfish by default), raising the opponent's UCI_Elo from round to round until
/// ChessBot no longer scores above 50%.
/// </summary>
public sealed class SweepConfig
{
    public const string DefaultStockfishPath =
        @"C:\Tools\stockfish\stockfish-windows-x86-64-avx2.exe";

    // Stockfish's documented UCI_Elo range.
    public const int StockfishMinElo = 1320;
    public const int StockfishMaxElo = 3190;

    public string EnginePath   { get; private set; } = DefaultStockfishPath;
    public int    StartElo     { get; private set; } = StockfishMinElo;
    public int    EloStep      { get; private set; } = 100;
    public int    MaxElo       { get; private set; } = StockfishMaxElo;
    /// <summary>Games played per round; forced to an even number so colors stay balanced.</summary>
    public int    GamesPerRound { get; private set; } = 10;
    public int    MoveTimeMs   { get; private set; } = 1000;
    public string OutDir       { get; private set; } = "elo-sweep";
    public int    BlunderThresholdCp { get; private set; } = 200;
    /// <summary>Move-by-move engine output during the games (off by default — sweeps are long).</summary>
    public bool   Verbose      { get; private set; }
    /// <summary>Ladder stops once halving would take the step below this (Elo).</summary>
    public int    MinStep      { get; private set; } = 25;
    /// <summary>Lower bound the ladder may probe.</summary>
    public int    MinElo       { get; private set; } = StockfishMinElo;
    /// <summary>Safety bound so a ladder that never converges still terminates.</summary>
    public int    MaxRounds    { get; private set; } = 20;

    /// <summary>Games per side handed to MatchConfig (which plays 2× that many).</summary>
    public int GamesPerSide => GamesPerRound / 2;

    private SweepConfig() { }

    /// <summary>
    /// Parses the arguments that follow the <c>sweep</c> subcommand.
    /// Returns null and prints the problem when the arguments are not usable.
    /// </summary>
    public static SweepConfig? Parse(string[] args)
    {
        var cfg = new SweepConfig();

        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--stockfish" when i + 1 < args.Length:
                case "--engine" when i + 1 < args.Length:
                    cfg.EnginePath = args[++i];
                    break;
                case "--start-elo" when i + 1 < args.Length:
                    if (TryInt(args[++i], out int se)) cfg.StartElo = se;
                    break;
                case "--elo-step" when i + 1 < args.Length:
                    if (TryInt(args[++i], out int st)) cfg.EloStep = st;
                    break;
                case "--max-elo" when i + 1 < args.Length:
                    if (TryInt(args[++i], out int me)) cfg.MaxElo = me;
                    break;
                case "--games-per-round" when i + 1 < args.Length:
                    if (TryInt(args[++i], out int gp)) cfg.GamesPerRound = gp;
                    break;
                case "--time-ms" when i + 1 < args.Length:
                    if (TryInt(args[++i], out int ms)) cfg.MoveTimeMs = ms;
                    break;
                case "--out-dir" when i + 1 < args.Length:
                case "--out" when i + 1 < args.Length:
                    cfg.OutDir = args[++i];
                    break;
                case "--blunder" when i + 1 < args.Length:
                    if (TryInt(args[++i], out int b)) cfg.BlunderThresholdCp = b;
                    break;
                case "--min-elo" when i + 1 < args.Length:
                    if (TryInt(args[++i], out int mn)) cfg.MinElo = mn;
                    break;
                case "--max-rounds" when i + 1 < args.Length:
                    if (TryInt(args[++i], out int mr)) cfg.MaxRounds = mr;
                    break;
                // The ladder always refines now; --refine is accepted and ignored, and
                // --refine-step is kept as an alias so older command lines still work.
                case "--refine":
                    break;
                case "--min-step" when i + 1 < args.Length:
                case "--refine-step" when i + 1 < args.Length:
                    if (TryInt(args[++i], out int rs)) cfg.MinStep = rs;
                    break;
                case "--verbose":
                case "-v":
                    cfg.Verbose = true;
                    break;
            }
        }

        // ── Validation ────────────────────────────────────────────────────────
        if (!File.Exists(cfg.EnginePath))
        {
            Console.Error.WriteLine($"ERROR: UCI engine not found: {cfg.EnginePath}");
            Console.Error.WriteLine("       Pass a different path with --stockfish <path>.");
            return null;
        }

        if (cfg.GamesPerRound < 2)
        {
            Console.Error.WriteLine("ERROR: --games-per-round must be at least 2.");
            return null;
        }

        if (cfg.GamesPerRound % 2 != 0)
        {
            cfg.GamesPerRound--;
            Console.WriteLine($"NOTE: --games-per-round must be even (colors are alternated); " +
                              $"using {cfg.GamesPerRound}.");
        }

        if (cfg.EloStep < 1)
        {
            Console.Error.WriteLine("ERROR: --elo-step must be at least 1.");
            return null;
        }

        if (cfg.MoveTimeMs < 1)
        {
            Console.Error.WriteLine("ERROR: --time-ms must be at least 1.");
            return null;
        }

        if (cfg.MinElo > cfg.MaxElo)
        {
            Console.Error.WriteLine($"ERROR: --min-elo ({cfg.MinElo}) is above --max-elo ({cfg.MaxElo}).");
            return null;
        }

        if (cfg.StartElo < cfg.MinElo || cfg.StartElo > cfg.MaxElo)
        {
            int clamped = Math.Clamp(cfg.StartElo, cfg.MinElo, cfg.MaxElo);
            Console.WriteLine($"NOTE: --start-elo {cfg.StartElo} is outside {cfg.MinElo}–{cfg.MaxElo}; " +
                              $"starting at {clamped}.");
            cfg.StartElo = clamped;
        }

        if (cfg.MinStep < 1)
        {
            Console.Error.WriteLine("ERROR: --min-step must be at least 1.");
            return null;
        }

        if (cfg.MinStep > cfg.EloStep)
        {
            Console.Error.WriteLine($"ERROR: --min-step ({cfg.MinStep}) must not exceed " +
                                    $"--elo-step ({cfg.EloStep}) — the step only ever shrinks.");
            return null;
        }

        if (cfg.MaxRounds < 1)
        {
            Console.Error.WriteLine("ERROR: --max-rounds must be at least 1.");
            return null;
        }

        if (cfg.StartElo < StockfishMinElo || cfg.MaxElo > StockfishMaxElo)
            Console.WriteLine($"NOTE: Stockfish's UCI_Elo range is {StockfishMinElo}–{StockfishMaxElo}; " +
                              $"values outside it are clamped by the engine itself.");

        return cfg;
    }

    private static bool TryInt(string s, out int value)
        => int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);

    public static void PrintUsage()
    {
        Console.WriteLine("Usage: ChessBot.EloEvaluator sweep [options]");
        Console.WriteLine();
        Console.WriteLine("Binary-searches ChessBot's playing strength against a local UCI engine.");
        Console.WriteLine("Win a round → climb by the step. Lose → drop by the step. Every direction");
        Console.WriteLine("change halves the step, so the levels converge on the crossover point:");
        Console.WriteLine();
        Console.WriteLine("  1320 win → 1420 win → 1520 loss → step 50 → 1470 win → step 25 → 1495 …");
        Console.WriteLine();
        Console.WriteLine("It stops once the bracket is narrower than --min-step and reports the");
        Console.WriteLine("midpoint of the highest level beaten and the lowest level not beaten.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine($"  --stockfish <path>      UCI engine executable    (default: {DefaultStockfishPath})");
        Console.WriteLine($"  --start-elo <n>         Where the ladder starts  (default: {StockfishMinElo})");
        Console.WriteLine("  --elo-step <n>          Initial step size        (default: 100)");
        Console.WriteLine("  --min-step <n>          Stop below this step     (default: 25)");
        Console.WriteLine($"  --min-elo <n>           Lower bound              (default: {StockfishMinElo})");
        Console.WriteLine($"  --max-elo <n>           Upper bound              (default: {StockfishMaxElo})");
        Console.WriteLine("  --games-per-round <n>   Games per round, even    (default: 10)");
        Console.WriteLine("  --time-ms <ms>          Move time per move       (default: 1000)");
        Console.WriteLine("  --max-rounds <n>        Safety bound on rounds   (default: 20)");
        Console.WriteLine("  --out-dir <dir>         Sweep output directory   (default: elo-sweep)");
        Console.WriteLine("  --blunder <cp>          Blunder threshold        (default: 200)");
        Console.WriteLine("  --verbose / -v          Move-by-move engine output");
        Console.WriteLine();
        Console.WriteLine("  (--refine is accepted and ignored — the ladder always refines.");
        Console.WriteLine("   --refine-step is an alias for --min-step.)");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- sweep");
        Console.WriteLine("  dotnet run -- sweep --start-elo 1800 --elo-step 200 --games-per-round 20");
        Console.WriteLine("  dotnet run -- sweep --start-elo 1600 --games-per-round 4 --time-ms 300 --min-step 50");
    }
}
