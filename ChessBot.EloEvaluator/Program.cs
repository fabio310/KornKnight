using ChessBot.EloEvaluator;
using ChessBot.EloEvaluator.Analysis;
using ChessBot.EloEvaluator.Models;
using ChessBot.EloEvaluator.Parsing;
using ChessBot.EloEvaluator.Reporting;
using System.Text;

Console.OutputEncoding = Encoding.UTF8;

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

// ── Discover files ────────────────────────────────────────────────────────────
var logFiles = Directory.GetFiles(cfg.PgnDir, "*.log")
    .Where(f => !Path.GetFileName(f).Equals("match_summary.log",
        StringComparison.OrdinalIgnoreCase))
    .OrderBy(f => f)
    .ToArray();

var pgnFiles = Directory.GetFiles(cfg.PgnDir, "*.pgn")
    .OrderBy(f => f)
    .ToArray();

Console.WriteLine($"Found {logFiles.Length} game log(s) and {pgnFiles.Length} PGN file(s).");
Console.WriteLine();

// ── Parse log files ───────────────────────────────────────────────────────────
var games = new List<ParsedGame>();
var logGameKeys = new HashSet<string>();

foreach (var logFile in logFiles)
{
    if (cfg.Verbose)
        Console.WriteLine($"  [log] {Path.GetFileName(logFile)}");

    var g = LogFileParser.Parse(logFile);
    games.Add(g);

    // Build a dedup key using date + game number to avoid double-counting
    // when both a .log and a .pgn file exist for the same game
    string key = BuildGameKey(g);
    logGameKeys.Add(key);

    if (cfg.Verbose)
        Console.WriteLine($"        → game {g.GameNumber}  {g.ChessBotColor}  {g.Outcome}  " +
                          $"{g.TotalPlies} plies  depth={g.CbAvgDepth?.ToString("F1") ?? "n/a"}  " +
                          $"NPS={g.CbAvgNps?.ToString("N0") ?? "n/a"}");
}

// ── Parse PGN files (skip games already covered by a log file) ────────────────
foreach (var pgnFile in pgnFiles)
{
    if (cfg.Verbose)
        Console.WriteLine($"  [pgn] {Path.GetFileName(pgnFile)}");

    var pgnGames = PgnFileParser.ParseAll(pgnFile);
    foreach (var g in pgnGames)
    {
        string key = BuildGameKey(g);
        if (!logGameKeys.Contains(key))
        {
            games.Add(g);
            if (cfg.Verbose)
                Console.WriteLine($"        → game {g.GameNumber}  {g.ChessBotColor}  {g.Outcome}  " +
                                  $"{g.TotalPlies} plies  (PGN only — no log)");
        }
        else if (cfg.Verbose)
        {
            Console.WriteLine($"        → game {g.GameNumber} skipped (log file already parsed)");
        }
    }
}

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

// ── Helpers ───────────────────────────────────────────────────────────────────
static string BuildGameKey(ParsedGame g)
    => $"{g.Date?.ToString("yyyyMMdd") ?? "nodate"}_{g.GameNumber}";
