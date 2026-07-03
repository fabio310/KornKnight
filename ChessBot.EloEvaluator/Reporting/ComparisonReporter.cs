using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChessBot.EloEvaluator.Models;

namespace ChessBot.EloEvaluator.Reporting;

/// <summary>
/// Implements the <c>--compare-latest</c> command: loads the two most recent
/// JSON reports from the output directory and prints a side-by-side delta.
/// </summary>
public static class ComparisonReporter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true,
        Converters                  = { new JsonStringEnumConverter() }
    };

    public static void Run(string outDir)
    {
        if (!Directory.Exists(outDir))
        {
            Console.Error.WriteLine($"ERROR: Output directory not found: {Path.GetFullPath(outDir)}");
            return;
        }

        // Collect timestamped JSON reports (exclude latest.json)
        var files = Directory.GetFiles(outDir, "*.json")
            .Where(f => !Path.GetFileName(f).Equals("latest.json",
                StringComparison.OrdinalIgnoreCase))
            .OrderBy(f => f)   // RunId format is sortable by date
            .ToArray();

        if (files.Length < 2)
        {
            Console.WriteLine(
                "Need at least 2 reports to compare. " +
                "Run the evaluator twice first, then use --compare-latest.");
            return;
        }

        var prev = LoadReport(files[^2]);
        var curr = LoadReport(files[^1]);

        if (prev is null || curr is null) return;

        Console.OutputEncoding = Encoding.UTF8;
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine("  ChessBot Elo Evaluator — Comparison Report");
        Console.WriteLine("═══════════════════════════════════════════════════════════════");
        Console.WriteLine();
        Console.WriteLine($"  Previous : {prev.RunId}  ({prev.ValidGames} valid games)");
        Console.WriteLine($"  Current  : {curr.RunId}  ({curr.ValidGames} valid games)");
        Console.WriteLine();

        Console.WriteLine("── Elo ─────────────────────────────────────────────────────────");
        PrintDelta("Score rate",
            $"{prev.ScoreRate:P1}", $"{curr.ScoreRate:P1}",
            (curr.ScoreRate - prev.ScoreRate) * 100.0, " %pt",
            d => d > 0);
        PrintDelta("Elo difference",
            $"{prev.EloDiff:+0.0;-0.0}", $"{curr.EloDiff:+0.0;-0.0}",
            curr.EloDiff - prev.EloDiff, "",
            d => d > 0);
        if (curr.EstimatedElo.HasValue && prev.EstimatedElo.HasValue)
            PrintDelta("Est. Elo",
                $"{prev.EstimatedElo:F0}", $"{curr.EstimatedElo:F0}",
                curr.EstimatedElo.Value - prev.EstimatedElo.Value, "",
                d => d > 0);
        PrintDelta("95% CI",
            $"±{prev.ConfidenceInterval95:F0}", $"±{curr.ConfidenceInterval95:F0}",
            prev.ConfidenceInterval95 - curr.ConfidenceInterval95, "",
            d => d > 0);   // smaller CI = better
        Console.WriteLine();

        Console.WriteLine("── Reliability ─────────────────────────────────────────────────");
        Console.WriteLine($"  Previous : {prev.ReliabilityNote}");
        Console.WriteLine($"  Current  : {curr.ReliabilityNote}");
        Console.WriteLine();

        Console.WriteLine("── Engine Performance ───────────────────────────────────────────");
        PrintDelta("Avg depth",
            $"{prev.EnginePerf.AvgDepth:F1}", $"{curr.EnginePerf.AvgDepth:F1}",
            curr.EnginePerf.AvgDepth - prev.EnginePerf.AvgDepth, "",
            d => d > 0);
        PrintDelta("Avg NPS",
            $"{prev.EnginePerf.AvgNps:N0}", $"{curr.EnginePerf.AvgNps:N0}",
            curr.EnginePerf.AvgNps - prev.EnginePerf.AvgNps, "",
            d => d > 0);
        PrintDelta("Blunder rate /10",
            $"{prev.BlunderStats.BlunderRate:F2}", $"{curr.BlunderStats.BlunderRate:F2}",
            curr.BlunderStats.BlunderRate - prev.BlunderStats.BlunderRate, "",
            d => d < 0);   // fewer blunders = better
        Console.WriteLine();

        Console.WriteLine("── Games ───────────────────────────────────────────────────────");
        Console.WriteLine($"  Previous : {prev.Wins}W / {prev.Draws}D / {prev.Losses}L");
        Console.WriteLine($"  Current  : {curr.Wins}W / {curr.Draws}D / {curr.Losses}L");
    }

    private static void PrintDelta(
        string metric, string prevStr, string currStr,
        double delta, string unit, Func<double, bool> isBetter)
    {
        string sign  = delta > 0 ? "+" : string.Empty;
        string arrow = Math.Abs(delta) < 0.01 ? "→"
                     : isBetter(delta)         ? "↑"
                                               : "↓";
        Console.WriteLine(
            $"  {metric,-22}: {prevStr,-12} → {currStr,-12} " +
            $"({sign}{delta:F2}{unit}) {arrow}");
    }

    private static EloReport? LoadReport(string path)
    {
        try
        {
            return JsonSerializer.Deserialize<EloReport>(
                File.ReadAllText(path, Encoding.UTF8), JsonOpts);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"ERROR loading {path}: {ex.Message}");
            return null;
        }
    }
}
