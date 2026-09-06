namespace ChessBot.MatchRunner;

using System.Diagnostics;
using ChessBot.Engine;
using ChessBot.Engine.Search;

/// <summary>
/// A named search configuration under comparison in an A/B run.
/// </summary>
public sealed class AbConfig
{
    public required string Name { get; init; }
    public required Func<SearchSettings> Build { get; init; }
}

/// <summary>
/// Per-position result of running one config against one FEN.
/// </summary>
public sealed class AbPositionResult
{
    public required string Fen           { get; init; }
    public required string BestMove      { get; init; }
    public required int    Evaluation    { get; init; }
    public required int    DepthAchieved { get; init; }
    public required long   Nodes         { get; init; }
    public required long   ElapsedMs     { get; init; }
    public required long   BetaCutoffs        { get; init; }
    public required long   BetaCutoffsFirstMove { get; init; }
    public required long   LmrReductions      { get; init; }
    public required long   LmrReSearches      { get; init; }
    public required double EffectiveBranchingFactor { get; init; }
    public required bool   UsedPartialRootResult    { get; init; }
}

/// <summary>
/// Aggregate result of running one named config over a whole FEN corpus.
/// </summary>
public sealed class AbConfigResult
{
    public required string Name { get; init; }
    public required List<AbPositionResult> Positions { get; init; }

    public long   TotalNodes        => Positions.Sum(p => p.Nodes);
    public long   TotalElapsedMs    => Positions.Sum(p => p.ElapsedMs);
    public double AvgDepth          => Positions.Count > 0 ? Positions.Average(p => p.DepthAchieved) : 0;
    public long   TotalBetaCutoffs           => Positions.Sum(p => p.BetaCutoffs);
    public long   TotalBetaCutoffsFirstMove  => Positions.Sum(p => p.BetaCutoffsFirstMove);
    /// <summary>Ratio-of-sums across the whole corpus (not an average of per-position ratios).</summary>
    public double FirstMoveCutoffRate => TotalBetaCutoffs > 0 ? (double)TotalBetaCutoffsFirstMove / TotalBetaCutoffs : 0;
    public long   TotalLmrReductions  => Positions.Sum(p => p.LmrReductions);
    public long   TotalLmrReSearches  => Positions.Sum(p => p.LmrReSearches);
    /// <summary>Fraction of LMR-reduced searches that needed a full-depth re-search.</summary>
    public double LmrReSearchRate => TotalLmrReductions > 0 ? (double)TotalLmrReSearches / TotalLmrReductions : 0;
    public double AvgEffectiveBranchingFactor =>
        Positions.Where(p => p.EffectiveBranchingFactor > 0).DefaultIfEmpty()
            .Average(p => p?.EffectiveBranchingFactor ?? 0);
}

/// <summary>
/// Controlled A/B harness: runs two named <see cref="SearchSettings"/> configurations against
/// the same fixed FEN corpus at a fixed node/time budget, and reports their metrics side by
/// side. This exists so strength-affecting heuristics (UsePartialRootResult, the LMR schedule)
/// can be compared under identical, reproducible conditions instead of toggled on trust.
///
/// This is deliberately NOT a live-play strength estimate: no external engine, no time
/// pressure beyond the configured per-position budget, and no adjudication of "better" —
/// it reports raw search-shape metrics and best-move agreement so a human can judge whether
/// a change is worth a full match-based Elo evaluation.
/// </summary>
public static class AbHarness
{
    /// <summary>
    /// A small, fixed, diverse corpus (opening/middlegame/endgame/tactical) used for controlled
    /// A/B comparisons. Deliberately independent from ChessBot.Tests' regression corpus to avoid
    /// coupling a benchmark tool to a test project.
    /// </summary>
    public static readonly IReadOnlyList<string> DefaultCorpus = new[]
    {
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",                          // starting position
        "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",                  // Italian opening
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",                // classic tactical/complex middlegame
        "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",                          // tactical (piece up for pawn)
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",                                          // rook endgame
        "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1",                                                    // trivial king+pawn endgame
        "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP2PPP/R2Q1RK1 w - - 0 1",            // balanced middlegame
        "rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 1 3",                      // early tactical shot (Qh4+)
        "8/8/8/8/8/8/6k1/R6K w - - 0 1",                                                      // trivial rook endgame (mate technique)
        "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1",                    // classic sharp tactical (WAC-style)
    };

    public static List<AbConfigResult> Run(
        AbConfig configA, AbConfig configB,
        IReadOnlyList<string>? corpus = null,
        int maxDepth = 8, long maxNodes = 200_000)
    {
        corpus ??= DefaultCorpus;
        return new List<AbConfigResult>
        {
            RunConfig(configA, corpus, maxDepth, maxNodes),
            RunConfig(configB, corpus, maxDepth, maxNodes),
        };
    }

    private static AbConfigResult RunConfig(
        AbConfig config, IReadOnlyList<string> corpus, int maxDepth, long maxNodes)
    {
        var results = new List<AbPositionResult>();
        foreach (var fen in corpus)
        {
            var engine = new ChessEngine();
            engine.LoadFen(fen);

            var settings = config.Build();
            settings.MaxDepth      = maxDepth;
            settings.MinNodeTarget = maxNodes;
            settings.MaxTimeMs     = int.MaxValue; // node-budget-only run: no time pressure
            settings.Verbose       = false;

            var sw = Stopwatch.StartNew();
            var result = engine.FindBestMove(settings);
            sw.Stop();

            results.Add(new AbPositionResult
            {
                Fen                       = fen,
                BestMove                  = result.BestMove.ToString() ?? string.Empty,
                Evaluation                = result.Evaluation,
                DepthAchieved             = result.DepthAchieved,
                Nodes                     = result.NodesSearched,
                ElapsedMs                 = sw.ElapsedMilliseconds,
                BetaCutoffs               = result.BetaCutoffs,
                BetaCutoffsFirstMove      = result.BetaCutoffsFirstMove,
                LmrReductions             = result.LmrReductions,
                LmrReSearches             = result.LmrReSearches,
                EffectiveBranchingFactor  = result.EffectiveBranchingFactor,
                UsedPartialRootResult     = result.UsedPartialRootResult,
            });
        }

        return new AbConfigResult { Name = config.Name, Positions = results };
    }

    /// <summary>
    /// Writes a side-by-side comparison report: aggregate metrics for both configs, plus a
    /// per-position best-move agreement table.
    /// </summary>
    public static void WriteReport(List<AbConfigResult> results, string path)
    {
        if (results.Count != 2)
            throw new ArgumentException("A/B report requires exactly two config results.", nameof(results));

        var a = results[0];
        var b = results[1];

        using var w = new StreamWriter(path);
        w.WriteLine("=== ChessBot Controlled A/B Harness Report ===");
        w.WriteLine($"Generated: {DateTime.Now:yyyy-MM-dd HH:mm:ss}");
        w.WriteLine("NOTE: fixed node-budget comparison, not a live-play Elo estimate.");
        w.WriteLine();
        w.WriteLine($"Config A: {a.Name}");
        w.WriteLine($"Config B: {b.Name}");
        w.WriteLine($"Positions: {a.Positions.Count}");
        w.WriteLine();

        w.WriteLine("=== Aggregate Metrics ===");
        WriteMetricRow(w, "Total nodes",                 a.TotalNodes,               b.TotalNodes,               "N0");
        WriteMetricRow(w, "Total elapsed (ms)",           a.TotalElapsedMs,           b.TotalElapsedMs,           "N0");
        WriteMetricRow(w, "Avg depth achieved",           a.AvgDepth,                 b.AvgDepth,                 "F2");
        WriteMetricRow(w, "First-move cutoff rate (total/total)", a.FirstMoveCutoffRate, b.FirstMoveCutoffRate, "P1");
        WriteMetricRow(w, "LMR re-search rate",           a.LmrReSearchRate,          b.LmrReSearchRate,          "P1");
        WriteMetricRow(w, "Avg effective branching factor", a.AvgEffectiveBranchingFactor, b.AvgEffectiveBranchingFactor, "F2");
        w.WriteLine();

        int agree = 0;
        w.WriteLine("=== Per-Position Comparison ===");
        for (int i = 0; i < a.Positions.Count; i++)
        {
            var pa = a.Positions[i];
            var pb = b.Positions[i];
            bool sameMove = pa.BestMove == pb.BestMove;
            if (sameMove) agree++;

            w.WriteLine();
            w.WriteLine($"Position {i + 1}: {pa.Fen}");
            w.WriteLine($"  [{a.Name}] move={pa.BestMove}  eval={pa.Evaluation:+#;-#;0}cp  depth={pa.DepthAchieved}  nodes={pa.Nodes:N0}  time={pa.ElapsedMs}ms");
            w.WriteLine($"  [{b.Name}] move={pb.BestMove}  eval={pb.Evaluation:+#;-#;0}cp  depth={pb.DepthAchieved}  nodes={pb.Nodes:N0}  time={pb.ElapsedMs}ms");
            w.WriteLine($"  Best-move agreement: {(sameMove ? "SAME" : "DIFFERENT")}");
        }

        w.WriteLine();
        w.WriteLine($"Best-move agreement: {agree}/{a.Positions.Count} ({(double)agree / a.Positions.Count:P1})");
    }

    private static void WriteMetricRow(TextWriter w, string label, double valueA, double valueB, string fmt)
    {
        string sa = valueA.ToString(fmt);
        string sb = valueB.ToString(fmt);
        w.WriteLine($"  {label,-42} A: {sa,-14} B: {sb,-14}");
    }
}
