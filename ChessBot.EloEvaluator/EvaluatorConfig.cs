using System.Globalization;

namespace ChessBot.EloEvaluator;

public sealed class EvaluatorConfig
{
    public string  PgnDir        { get; private set; } = "pgns";
    public string  OutDir        { get; private set; } = "elo-reports";
    public double? ReferenceElo  { get; private set; }
    public bool    CompareLatest { get; private set; }
    public bool    Verbose       { get; private set; }

    private EvaluatorConfig() { }

    /// <summary>
    /// Builds a config for evaluating one already-populated directory of .pgn/.log files
    /// (used by the sweep mode to run the normal report pipeline per round).
    /// </summary>
    public static EvaluatorConfig ForDirectory(string pgnDir, string outDir,
                                               double? referenceElo = null, bool verbose = false)
        => new()
        {
            PgnDir       = pgnDir,
            OutDir       = outDir,
            ReferenceElo = referenceElo,
            Verbose      = verbose
        };

    public static EvaluatorConfig Parse(string[] args)
    {
        var cfg = new EvaluatorConfig();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--pgn-dir" when i + 1 < args.Length:
                    cfg.PgnDir = args[++i];
                    break;
                case "--out" when i + 1 < args.Length:
                    cfg.OutDir = args[++i];
                    break;
                case "--reference-elo" when i + 1 < args.Length:
                    if (double.TryParse(args[++i], NumberStyles.Any, CultureInfo.InvariantCulture, out double elo))
                        cfg.ReferenceElo = elo;
                    break;
                case "--compare-latest":
                    cfg.CompareLatest = true;
                    break;
                case "--verbose":
                case "-v":
                    cfg.Verbose = true;
                    break;
            }
        }
        return cfg;
    }

    public static void PrintUsage()
    {
        Console.WriteLine("Usage: ChessBot.EloEvaluator [options]");
        Console.WriteLine("       ChessBot.EloEvaluator sweep [options]   (play a strength sweep vs. Stockfish;");
        Console.WriteLine("                                                run 'sweep --help' for its options)");
        Console.WriteLine();
        Console.WriteLine("Options:");
        Console.WriteLine("  --pgn-dir <dir>         Directory containing .pgn and .log files  (default: pgns)");
        Console.WriteLine("  --out <dir>             Output directory for reports              (default: elo-reports)");
        Console.WriteLine("  --reference-elo <elo>   Reference engine Elo (e.g. 3190 for SF)");
        Console.WriteLine("  --compare-latest        Compare the two most recent reports");
        Console.WriteLine("  --verbose / -v          Show per-file parsing details");
        Console.WriteLine();
        Console.WriteLine("Examples:");
        Console.WriteLine("  dotnet run -- --pgn-dir pgns --out elo-reports");
        Console.WriteLine("  dotnet run -- --pgn-dir pgns --reference-elo 3190 --out elo-reports");
        Console.WriteLine("  dotnet run -- --compare-latest --out elo-reports");
    }
}
