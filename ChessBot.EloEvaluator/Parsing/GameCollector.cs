using ChessBot.EloEvaluator.Models;

namespace ChessBot.EloEvaluator.Parsing;

/// <summary>
/// Discovers and parses all games in a MatchRunner output directory:
/// every .log file (excluding match_summary.log) plus every .pgn file that has no
/// matching .log, so a game recorded in both formats is only counted once.
/// </summary>
public static class GameCollector
{
    /// <param name="dir">MatchRunner output directory.</param>
    /// <param name="verbose">Print one line per parsed file.</param>
    /// <param name="announceCounts">Print the "Found N game log(s)…" line.</param>
    public static List<ParsedGame> Collect(string dir, bool verbose = false, bool announceCounts = true)
    {
        var games = new List<ParsedGame>();
        if (!Directory.Exists(dir))
            return games;

        var logFiles = Directory.GetFiles(dir, "*.log")
            .Where(f => !Path.GetFileName(f).Equals("match_summary.log",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)
            .ToArray();

        var pgnFiles = Directory.GetFiles(dir, "*.pgn")
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
