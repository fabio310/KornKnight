using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ChessBot.EloEvaluator.Sweep;

/// <summary>
/// Writes the sweep summary in the same shape as <see cref="Reporting.ReportWriter"/>:
/// a human-readable .txt, a machine-readable .json, and a stable "latest" copy.
/// </summary>
public sealed class SweepReportWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented          = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly string _outDir;

    public SweepReportWriter(string outDir) => _outDir = outDir;

    /// <summary>Returns the paths of the files written.</summary>
    public List<string> Write(SweepResult result)
    {
        Directory.CreateDirectory(_outDir);
        var paths = new List<string>();

        string txtPath        = Path.Combine(_outDir, $"sweep_{result.RunId}.txt");
        string jsonPath       = Path.Combine(_outDir, $"sweep_{result.RunId}.json");
        string latestTxtPath  = Path.Combine(_outDir, "sweep_latest.txt");
        string latestJsonPath = Path.Combine(_outDir, "sweep_latest.json");

        using (var w = new StreamWriter(txtPath, false, Encoding.UTF8))
            BuildTxt(w, result);
        File.Copy(txtPath, latestTxtPath, overwrite: true);
        paths.Add(txtPath);

        string json = JsonSerializer.Serialize(result, JsonOpts);
        File.WriteAllText(jsonPath, json, Encoding.UTF8);
        File.WriteAllText(latestJsonPath, json, Encoding.UTF8);
        paths.Add(jsonPath);

        return paths;
    }

    /// <summary>Prints the same summary to the console.</summary>
    public static void PrintToConsole(SweepResult result) => BuildTxt(Console.Out, result);

    private static void BuildTxt(TextWriter w, SweepResult r)
    {
        w.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        w.WriteLine("║        ChessBot Elo Evaluator — Strength Sweep Report         ║");
        w.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        w.WriteLine();
        w.WriteLine($"Run ID        : {r.RunId}");
        w.WriteLine($"Timestamp     : {r.Timestamp:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine($"Opponent      : {r.OpponentName}");
        w.WriteLine($"Engine path   : {r.EnginePath}");
        w.WriteLine($"Elo range     : {r.StartElo} → {r.MaxElo}  (step {r.EloStep})");
        w.WriteLine($"Games/round   : {r.GamesPerRound}");
        w.WriteLine($"Move time     : {r.MoveTimeMs}ms");
        w.WriteLine($"Output dir    : {r.OutDir}");
        w.WriteLine($"Total runtime : {FormatDuration(r.DurationSeconds)}");
        w.WriteLine();

        // ── Rounds ────────────────────────────────────────────────────────────
        w.WriteLine("── Rounds ───────────────────────────────────────────────────────");
        w.WriteLine($"  {"#",-3} {"SF Elo",-7} {"Record",-10} {"Score",-7} {"ΔElo",-7} " +
                    $"{"Perf",-7} {"±CI",-6} {"LOS",-6} {"Depth",-6} {"Blndrs",-7} {"Time",-8} Verdict");
        w.WriteLine(new string('-', 104));

        foreach (var round in r.Rounds)
        {
            string depth   = round.AvgDepth?.ToString("F1") ?? "n/a";
            string perf    = round.EstimatedElo?.ToString("F0") ?? "n/a";
            string verdict = round.Passed ? "passed" : $"failed ({round.StopReason})";
            if (round.IsRefinement) verdict += " [refine]";

            w.WriteLine(
                $"  {round.RoundNumber,-3} {round.StockfishElo,-7} {round.Record,-10} " +
                $"{round.ScoreRate,-7:P0} {round.EloDiff,-7:+0;-0;0} {perf,-7} " +
                $"{round.ConfidenceInterval95,-6:F0} {round.LOS,-6:P0} {depth,-6} " +
                $"{round.Blunders,-7} {FormatDuration(round.DurationSeconds),-8} {verdict}");
        }
        w.WriteLine();

        // ── Verdict ───────────────────────────────────────────────────────────
        w.WriteLine("── Estimated Strength ───────────────────────────────────────────");
        if (r.EstimatedElo.HasValue)
            w.WriteLine($"  ChessBot's estimated strength ≈ Stockfish Elo {r.EstimatedElo.Value} (UCI_Elo)");
        else
            w.WriteLine($"  ChessBot's estimated strength ≥ Stockfish Elo {r.HighestPassedElo ?? r.MaxElo} (UCI_Elo)");

        if (r.HighestPassedElo.HasValue)
            w.WriteLine($"  Highest level beaten          : Stockfish Elo {r.HighestPassedElo.Value}");
        else
            w.WriteLine("  Highest level beaten          : none — failed the first level");

        if (r.PerformanceElo.HasValue)
            w.WriteLine($"  Deciding round performance Elo: {r.PerformanceElo.Value:F0}");
        w.WriteLine($"  Confidence                    : {r.ReliabilityNote}");
        w.WriteLine();
        w.WriteLine("  " + r.Verdict);
        w.WriteLine();

        // ── Per-round output dirs ─────────────────────────────────────────────
        w.WriteLine("── Round Output Directories ─────────────────────────────────────");
        foreach (var round in r.Rounds)
            w.WriteLine($"  Elo {round.StockfishElo,-5} → {round.RoundDir}");
        w.WriteLine();
    }

    private static string FormatDuration(double seconds)
        => seconds >= 60
            ? $"{(int)(seconds / 60)}m{(int)(seconds % 60):00}s"
            : $"{seconds:F0}s";
}
