using ChessBot.EloEvaluator;
using ChessBot.EloEvaluator.Analysis;
using ChessBot.EloEvaluator.Parsing;
using ChessBot.EloEvaluator.Reporting;
using ChessBot.EloEvaluator.Sweep;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

// ── sweep subcommand ─────────────────────────────────────────────────────────
if (args.Length > 0 && args[0].Equals("sweep", StringComparison.OrdinalIgnoreCase))
{
    string[] sweepArgs = args[1..];

    if (sweepArgs.Any(a => a is "--help" or "-h"))
    {
        SweepConfig.PrintUsage();
        return 0;
    }

    var sweepCfg = SweepConfig.Parse(sweepArgs);
    if (sweepCfg is null)
    {
        Console.WriteLine();
        SweepConfig.PrintUsage();
        return 1;
    }

    var sweepResult = await new SweepRunner(sweepCfg).RunAsync();

    var sweepPaths = new SweepReportWriter(sweepCfg.OutDir).Write(sweepResult);

    Console.WriteLine();
    SweepReportWriter.PrintToConsole(sweepResult);

    Console.WriteLine($"Sweep report saved to: {Path.GetFullPath(sweepCfg.OutDir)}");
    foreach (var p in sweepPaths)
        Console.WriteLine($"  {Path.GetFileName(p)}");

    return 0;
}

var cfg = EvaluatorConfig.Parse(args);

// ── --compare-latest mode ────────────────────────────────────────────────────
if (cfg.CompareLatest)
{
    ComparisonReporter.Run(cfg.OutDir);
    return 0;
}

// ── Normal evaluation mode ───────────────────────────────────────────────────
Console.WriteLine("=== ChessBot Elo Evaluator ===");
Console.WriteLine($"PGN directory : {Path.GetFullPath(cfg.PgnDir)}");
Console.WriteLine($"Output dir    : {Path.GetFullPath(cfg.OutDir)}");
if (cfg.ReferenceElo.HasValue)
    Console.WriteLine($"Reference Elo : {cfg.ReferenceElo}");
Console.WriteLine();

if (!Directory.Exists(cfg.PgnDir))
{
    Console.Error.WriteLine($"ERROR: PGN directory not found: {Path.GetFullPath(cfg.PgnDir)}");
    Console.WriteLine();
    EvaluatorConfig.PrintUsage();
    return 1;
}

Directory.CreateDirectory(cfg.OutDir);

// ── Discover and parse games (logs first, PGNs only where no log exists) ─────
var games = GameCollector.Collect(cfg.PgnDir, cfg.Verbose);

if (games.Count == 0)
{
    Console.Error.WriteLine("No games found. Check --pgn-dir path.");
    return 1;
}

Console.WriteLine($"Total games collected : {games.Count}");
Console.WriteLine();

// ── Validate games ────────────────────────────────────────────────────────────
foreach (var g in games)
    GameValidator.Validate(g);

int invalidCount = games.Count(g => !g.IsValid);
if (invalidCount > 0)
    Console.WriteLine($"NOTE: {invalidCount} game(s) failed validation and are excluded from scoring.");

// ── Build and write report ────────────────────────────────────────────────────
var report = new ReportBuilder(cfg).Build(games);
var paths  = new ReportWriter(cfg.OutDir).Write(report);

// ── Print summary to console ──────────────────────────────────────────────────
Console.WriteLine();
ReportWriter.PrintToConsole(report);

Console.WriteLine($"Reports saved to: {Path.GetFullPath(cfg.OutDir)}");
foreach (var p in paths)
    Console.WriteLine($"  {Path.GetFileName(p)}");

return 0;
