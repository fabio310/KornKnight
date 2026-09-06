using ChessBot.EloEvaluator.Models;

namespace ChessBot.EloEvaluator.Parsing;

/// <summary>
/// Discovers and parses all games in a MatchRunner output directory: every per-game log plus
/// every .pgn file that has no matching log, so a game recorded in both formats is counted once.
///
/// The directory also holds files that are not games — the match summary, move-loss reports,
/// A/B reports and whatever else a run or a user leaves there. Collecting "every .log except
/// match_summary.log" fed those to the game parser, which produced additional games that were
/// then reported as invalid. Only files matching the per-game artifact naming are collected.
/// </summary>
public static class GameCollector
{
    /// <summary>
    /// Per-game logs are written as "gameN_&lt;timestamp&gt;.log" next to "gameN_&lt;timestamp&gt;.pgn".
    /// Anything else in the directory is a different kind of artifact and is not a game.
    /// The suffix group excludes files like "game1_….moveloss.log", whose stem carries an extra
    /// dotted segment.
    /// </summary>
    private static readonly System.Text.RegularExpressions.Regex GameLogPattern =
        new(@"^game\d+_[0-9]{8}_[0-9]{6}\.log$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    private static readonly System.Text.RegularExpressions.Regex GamePgnPattern =
        new(@"^game\d+_[0-9]{8}_[0-9]{6}\.pgn$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>True when the file name is a per-game log artifact (not a move-loss or summary log).</summary>
    public static bool IsGameLog(string path) => GameLogPattern.IsMatch(Path.GetFileName(path));

    /// <summary>True when the file name is a per-game PGN artifact.</summary>
    public static bool IsGamePgn(string path) => GamePgnPattern.IsMatch(Path.GetFileName(path));

    /// <param name="dir">MatchRunner output directory.</param>
    /// <param name="verbose">Print one line per parsed file.</param>
    /// <param name="announceCounts">Print the "Found N game log(s)…" line.</param>
    /// <param name="runId">
    /// When set, only artifacts belonging to this run are collected, identified by the timestamp
    /// embedded in the file name. Without it, logs left in the directory by an earlier run on the
    /// same day are indistinguishable from the current run's: deduplication by date and game
    /// number cannot tell two same-day runs apart, because both produce "game1" dated today.
    /// </param>
    /// <param name="preferStructuredResult">
    /// When true (the default), a readable match_result.json in the directory is used as the
    /// authoritative source and the text artifacts are not parsed at all. The text parsers stay
    /// as the fallback for directories written before the structured document existed.
    /// </param>
    public static List<ParsedGame> Collect(
        string dir, bool verbose = false, bool announceCounts = true, string? runId = null,
        bool preferStructuredResult = true)
    {
        var games = new List<ParsedGame>();
        if (!Directory.Exists(dir))
            return games;

        if (preferStructuredResult)
        {
            var doc = StructuredResultReader.TryRead(dir, out string? reason);
            if (doc is not null)
            {
                games = StructuredResultReader.ToParsedGames(
                    doc, Path.Combine(dir, StructuredResultReader.FileName));

                if (announceCounts)
                {
                    Console.WriteLine($"Read {games.Count} game(s) from {StructuredResultReader.FileName} " +
                                      $"(schema v{doc.SchemaVersion}" +
                                      (doc.RequestedGames > 0 && doc.RequestedGames != doc.ActualGames
                                          ? $"; NOTE: {doc.RequestedGames} game(s) requested, {doc.ActualGames} recorded"
                                          : "") + ").");
                    Console.WriteLine();
                }
                return games;
            }

            if (verbose && reason is not null)
                Console.WriteLine($"  [json] falling back to text parsing: {reason}");
        }

        var logFiles = Directory.GetFiles(dir, "*.log")
            .Where(IsGameLog)
            .Where(f => runId is null || Path.GetFileName(f).Contains(runId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToArray();

        var pgnFiles = Directory.GetFiles(dir, "*.pgn")
            .Where(IsGamePgn)
            .Where(f => runId is null || Path.GetFileName(f).Contains(runId, StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToArray();

        if (announceCounts)
        {
            Console.WriteLine($"Found {logFiles.Length} game log(s) and {pgnFiles.Length} PGN file(s).");
            Console.WriteLine();
        }

        var logGameKeys = new HashSet<string>();

        foreach (var logFile in logFiles)
        {
            if (verbose)
                Console.WriteLine($"  [log] {Path.GetFileName(logFile)}");

            var g = LogFileParser.Parse(logFile);
            games.Add(g);
            logGameKeys.Add(BuildGameKey(g));

            if (verbose)
                Console.WriteLine($"        → game {g.GameNumber}  {g.ChessBotColor}  {g.Outcome}  " +
                                  $"{g.TotalPlies} plies  depth={g.CbAvgDepth?.ToString("F1") ?? "n/a"}  " +
                                  $"NPS={g.CbAvgNps?.ToString("N0") ?? "n/a"}");
        }

        foreach (var pgnFile in pgnFiles)
        {
            if (verbose)
                Console.WriteLine($"  [pgn] {Path.GetFileName(pgnFile)}");

            foreach (var g in PgnFileParser.ParseAll(pgnFile))
            {
                if (!logGameKeys.Contains(BuildGameKey(g)))
                {
                    games.Add(g);
                    if (verbose)
                        Console.WriteLine($"        → game {g.GameNumber}  {g.ChessBotColor}  {g.Outcome}  " +
                                          $"{g.TotalPlies} plies  (PGN only — no log)");
                }
                else if (verbose)
                {
                    Console.WriteLine($"        → game {g.GameNumber} skipped (log file already parsed)");
                }
            }
        }

        return games;
    }

    /// <summary>Dedup key: a game is identified by its date and game number.</summary>
    public static string BuildGameKey(ParsedGame g)
        => $"{g.Date?.ToString("yyyyMMdd") ?? "nodate"}_{g.GameNumber}";
}
