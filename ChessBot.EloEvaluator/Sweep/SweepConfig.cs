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
    /// <summary>Optional bisection between the last passed and the first failed level.</summary>
    public bool   Refine       { get; private set; }
    /// <summary>Stop refining once the bracket is this narrow (Elo).</summary>
    public int    RefineStep   { get; private set; } = 25;

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
                case "--refine":
                    cfg.Refine = true;
                    break;
                case "--refine-step" when i + 1 < args.Length:
                    if (TryInt(args[++i], out int rs)) cfg.RefineStep = rs;
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

        if (cfg.StartElo > cfg.MaxElo)
        {
            Console.Error.WriteLine($"ERROR: --start-elo ({cfg.StartElo}) is above --max-elo ({cfg.MaxElo}).");
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
        Console.WriteLine("Plays rounds against a local UCI engine, raising its UCI_Elo each round until");
        Console.WriteLine("ChessBot's score rate drops to 50% or below. That level is ChessBot's strength.");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine($"  --stockfish <path>      UCI engine executable    (default: {DefaultStockfishPath})");
        Console.WriteLine($"  --start-elo <n>         First round's UCI_Elo    (default: {StockfishMinElo})");
        Console.WriteLine("  --elo-step <n>          Elo added per round      (default: 100)");
        Console.WriteLine($"  --max-elo <n>           Last round's UCI_Elo     (default: {StockfishMaxElo})");
        Console.WriteLine("  --games-per-round <n>   Games per round, even    (default: 10)");
        Console.WriteLine("  --time-ms <ms>          Move time per move       (default: 1000)");
        Console.WriteLine("  --out-dir <dir>         Sweep output directory   (default: elo-sweep)");
        Console.WriteLine("  --blunder <cp>          Blunder threshold        (default: 200)");
        Console.WriteLine("  --refine                Bisect between the last passed and first failed level");
        Console.WriteLine("  --refine-step <n>       Stop bisecting below this bracket width (default: 25)");
        Console.WriteLine("  --verbose / -v          Move-by-move engine output");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- sweep");
        Console.WriteLine("  dotnet run -- sweep --start-elo 1400 --elo-step 100 --games-per-round 10");
        Console.WriteLine("  dotnet run -- sweep --start-elo 1320 --games-per-round 4 --time-ms 300 --refine");
    }
}
