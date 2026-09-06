using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChessBot.EloEvaluator.Models;

namespace ChessBot.EloEvaluator.Reporting;

/// <summary>
/// Writes an <see cref="EloReport"/> to three files in the output directory:
///   {RunId}.json  — full machine-readable report
///   {RunId}.csv   — one row per game
///   {RunId}.txt   — human-readable summary
///   latest.json   — always overwritten with the newest report
/// </summary>
public sealed class ReportWriter
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented                    = true,
        DefaultIgnoreCondition           = JsonIgnoreCondition.WhenWritingNull,
        Converters                       = { new JsonStringEnumConverter() }
    };

    private readonly string _outDir;

    public ReportWriter(string outDir) => _outDir = outDir;

    /// <summary>Returns the paths of the files written.</summary>
    public List<string> Write(EloReport report)
    {
        Directory.CreateDirectory(_outDir);
        var paths = new List<string>();

        string jsonPath   = Path.Combine(_outDir, $"{report.RunId}.json");
        string csvPath    = Path.Combine(_outDir, $"{report.RunId}.csv");
        string txtPath    = Path.Combine(_outDir, $"{report.RunId}.txt");
        string latestPath = Path.Combine(_outDir, "latest.json");

        // JSON
        string json = JsonSerializer.Serialize(report, JsonOpts);
        File.WriteAllText(jsonPath,   json, Encoding.UTF8);
        File.WriteAllText(latestPath, json, Encoding.UTF8);
        paths.Add(jsonPath);

        // CSV
        WriteCsv(csvPath, report);
        paths.Add(csvPath);

        // TXT
        WriteTxt(txtPath, report);
        paths.Add(txtPath);

        return paths;
    }

    // ── CSV ───────────────────────────────────────────────────────────────────
    private static void WriteCsv(string path, EloReport report)
    {
        using var w = new StreamWriter(path, false, Encoding.UTF8);
        w.WriteLine("GameNumber,SourceFile,ChessBotColor,Result,Outcome," +
                    "Termination,TotalPlies,AvgDepth,AvgNps,PeakNps,CrossEngineDisagreements,IsValid,Date");

        foreach (var g in report.Games)
        {
            string term = (g.Termination ?? string.Empty).Replace("\"", "'");
            w.WriteLine(string.Join(",",
                g.GameNumber,
                $"\"{g.SourceFile}\"",
                g.ChessBotColor,
                g.Result,
                g.Outcome,
                $"\"{term}\"",
                g.TotalPlies,
                g.AvgDepth?.ToString("F1")  ?? "",
                g.AvgNps?.ToString("F0")    ?? "",
                g.PeakNps?.ToString("F0")   ?? "",
                g.CrossEngineDisagreements,
                g.IsValid,
                g.Date?.ToString("yyyy-MM-dd") ?? ""));
        }
    }

    // ── TXT ───────────────────────────────────────────────────────────────────
    private static void WriteTxt(string path, EloReport report)
    {
        using var w = new StreamWriter(path, false, Encoding.UTF8);
        BuildTxt(w, report);
    }

    /// <summary>Builds the human-readable summary into any TextWriter (also used for Console).</summary>
    public static void PrintToConsole(EloReport report)
        => BuildTxt(Console.Out, report);

    private static void BuildTxt(TextWriter w, EloReport report)
    {
        w.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        w.WriteLine("║           ChessBot Elo Evaluator — Report                    ║");
        w.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        w.WriteLine();
        w.WriteLine($"Run ID    : {report.RunId}");
        w.WriteLine($"Timestamp : {report.Timestamp:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine($"PGN dir   : {report.PgnDir}");
        if (report.ReferenceElo.HasValue)
            w.WriteLine($"Ref Elo   : {report.ReferenceElo}");
        w.WriteLine();

        // ── Game counts ───────────────────────────────────────────────────────
        w.WriteLine("── Game Counts ──────────────────────────────────────────────────");
        w.WriteLine($"  Total games    : {report.TotalGames}");
        w.WriteLine($"  Valid games    : {report.ValidGames}");
        if (report.InvalidGames > 0)
            w.WriteLine($"  Invalid games  : {report.InvalidGames}  (excluded from scoring)");
        w.WriteLine($"  Wins           : {report.Wins}");
        w.WriteLine($"  Draws          : {report.Draws}");
        w.WriteLine($"  Losses         : {report.Losses}");
        if (report.Aborted > 0)
            w.WriteLine($"  Aborted        : {report.Aborted}");
        w.WriteLine();

        // ── Elo estimate ──────────────────────────────────────────────────────
        w.WriteLine("── Elo Estimate ─────────────────────────────────────────────────");
        int scoring = report.Wins + report.Draws + report.Losses;
        if (scoring == 0)
        {
            w.WriteLine("  No scoring games available.");
        }
        else
        {
            w.WriteLine($"  Score rate     : {report.ScoreRate:P1}  " +
                        $"({report.Wins}W / {report.Draws}D / {report.Losses}L)");
            w.WriteLine($"  Elo difference : {report.EloDiff:+0.0;-0.0;0}");
            if (report.EstimatedElo.HasValue)
                w.WriteLine($"  Estimated Elo  : {report.EstimatedElo.Value:F0}");
            w.WriteLine($"  95% CI         : ±{report.ConfidenceInterval95:F0} Elo");
            w.WriteLine($"  LOS            : {report.LOS:P1}");
            w.WriteLine($"  Reliable       : {(report.IsReliable ? "YES" : "NO")}");
            w.WriteLine($"  Note           : {report.ReliabilityNote}");
        }
        w.WriteLine();

        // ── Color breakdown ───────────────────────────────────────────────────
        w.WriteLine("── By Color ─────────────────────────────────────────────────────");
        WriteColorRow(w, "As White", report.AsWhite);
        WriteColorRow(w, "As Black", report.AsBlack);
        w.WriteLine();

        // ── Engine performance ────────────────────────────────────────────────
        w.WriteLine("── Engine Performance ───────────────────────────────────────────");
        var ep = report.EnginePerf;
        if (ep.AvgNps == 0 && ep.AvgDepth == 0)
        {
            w.WriteLine("  (no log data available)");
        }
        else
        {
            w.WriteLine($"  Avg depth      : {ep.AvgDepth:F1}");
            w.WriteLine($"  Avg NPS        : {ep.AvgNps:N0}");
            w.WriteLine($"  Peak NPS       : {ep.PeakNps:N0}");
            w.WriteLine($"  Avg nodes/move : {ep.AvgNodesPerMove:N0}");
            w.WriteLine($"  Avg ms/move    : {ep.AvgTimeMsPerMove:F0}");
            w.WriteLine($"  Total nodes    : {ep.TotalNodes:N0}");
        }
        w.WriteLine();

        // ── Phase stats ───────────────────────────────────────────────────────
        w.WriteLine("── Phase Stats ──────────────────────────────────────────────────");
        WritePhaseRow(w, report.Opening);
        WritePhaseRow(w, report.Middlegame);
        WritePhaseRow(w, report.Endgame);
        w.WriteLine();

        // ── Cross-engine evaluation disagreement stats ──────────────────────────
        w.WriteLine("── Cross-Engine Evaluation Disagreement Stats ──────────────────────");
        w.WriteLine("    NOTE: not a blunder / centipawn-loss measurement (see MoveLossAnalyzer output).");
        var bs = report.CrossEngineDisagreementStats;
        w.WriteLine($"  Total disagreements     : {bs.TotalDisagreements}");
        w.WriteLine($"  ChessBot moves total    : {bs.TotalChessBotMoves}");
        w.WriteLine($"  Disagreement rate       : {bs.DisagreementRate:F2} per 10 moves");
        w.WriteLine($"  Games with disagreements: {bs.GamesWithDisagreements}");
        w.WriteLine();

        // ── Validation issues ─────────────────────────────────────────────────
        if (report.ValidationIssues.Count > 0)
        {
            w.WriteLine("── Validation Issues ────────────────────────────────────────────");
            foreach (var vi in report.ValidationIssues)
                w.WriteLine($"  [{vi.SourceFile} game {vi.GameNumber}] {vi.Issue}");
            w.WriteLine();
        }

        // ── Per-game summary table ────────────────────────────────────────────
        w.WriteLine("── Per-Game Summary ─────────────────────────────────────────────");
        w.WriteLine($"  {"#",-4} {"Color",-6} {"Result",-8} {"Outcome",-14} {"Plies",-6} " +
                    $"{"Depth",-7} {"NPS",-10} {"Disagr",-7} {"Valid",-6} Date");
        w.WriteLine(new string('-', 88));

        foreach (var g in report.Games)
        {
            string dep = g.AvgDepth.HasValue ? g.AvgDepth.Value.ToString("F1") : "n/a";
            string nps = g.AvgNps.HasValue   ? g.AvgNps.Value.ToString("N0")   : "n/a";
            w.WriteLine(
                $"  {g.GameNumber,-4} {g.ChessBotColor,-6} {g.Result,-8} {g.Outcome,-14} " +
                $"{g.TotalPlies,-6} {dep,-7} {nps,-10} {g.CrossEngineDisagreements,-7} " +
                $"{(g.IsValid ? "ok" : "ERR"),-6} {g.Date?.ToString("yyyy-MM-dd") ?? ""}");
        }
        w.WriteLine();
    }

    private static void WriteColorRow(TextWriter w, string label, ColorStats cs)
    {
        if (cs.Games == 0)
        {
            w.WriteLine($"  {label,-10}: no games");
            return;
        }
        w.WriteLine(
            $"  {label,-10}: {cs.Wins}W/{cs.Draws}D/{cs.Losses}L  " +
            $"score={cs.ScoreRate:P1}  Δelo={cs.EloDiff:+0;-0;0}  " +
            $"[{cs.ReliabilityNote}]");
    }

    private static void WritePhaseRow(TextWriter w, PhaseStats ps)
    {
        if (ps.MoveCount == 0)
        {
            w.WriteLine($"  {ps.PhaseName,-12}: no data");
            return;
        }
        w.WriteLine(
            $"  {ps.PhaseName,-12}: {ps.MoveCount,4} moves  " +
            $"depth={ps.AvgDepth:F1}  NPS={ps.AvgNps:N0}  " +
            $"cross-engine disagreements={ps.CrossEngineDisagreements} ({ps.CrossEngineDisagreementRate:F2}/10)");
    }
}
