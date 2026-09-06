namespace ChessBot.MatchRunner;

using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using ChessBot.Engine;
using ChessBot.Engine.Search;

/// <summary>
/// A named search configuration under comparison in an A/B run.
/// </summary>
public sealed class AbConfig
{
    public required string Name { get; init; }
    public required Func<SearchSettings> Build { get; init; }

    /// <summary>Human-readable description of what this configuration changes.</summary>
    public string Description { get; init; } = string.Empty;
}

/// <summary>
/// Per-position result of running one config against one FEN.
/// </summary>
public sealed class AbPositionResult
{
    public required string Fen           { get; init; }
    public required string BestMove      { get; init; }
    public required int    Evaluation    { get; init; }

    /// <summary>Last fully completed iterative-deepening depth.</summary>
    public required int    DepthAchieved { get; init; }
    /// <summary>Depth of the iteration that was cancelled, or 0 if none was.</summary>
    public required int    PartialDepth  { get; init; }
    /// <summary>True when the reported move came from the cancelled iteration.</summary>
    public required bool   UsedPartialRootResult { get; init; }
    public required int    RootMovesCompleted { get; init; }
    public required int    RootMoveCount      { get; init; }
    public required double RootCoveragePercent{ get; init; }
    public required bool   PartialScoreIsExact{ get; init; }
    public required bool   IsUnsearchedFallbackMove { get; init; }

    public required long   TotalNodes    { get; init; }
    public required long   MainNodes     { get; init; }
    public required long   QNodes        { get; init; }
    public required double QNodeShare    { get; init; }
    public required long   ElapsedMs     { get; init; }

    /// <summary>True when the search stopped because it hit the node budget.</summary>
    public required bool   NodeBudgetReached { get; init; }

    public required long   BetaCutoffs          { get; init; }
    public required long   BetaCutoffsFirstMove { get; init; }
    public required long   LmrReductions        { get; init; }
    public required long   LmrReSearches        { get; init; }
    public required long   LmrPliesSaved        { get; init; }
    public required long   AspirationRetries    { get; init; }
    public required long   AspirationRetryNodes { get; init; }
    public required double IterationNodeRatio   { get; init; }
}

/// <summary>
/// Aggregate result of running one named config over a whole FEN corpus.
/// </summary>
public sealed class AbConfigResult
{
    public required string Name { get; init; }
    public required string Description { get; init; }
    /// <summary>The settings actually used, rendered field by field.</summary>
    public required Dictionary<string, string> EffectiveSettings { get; init; }
    public required List<AbPositionResult> Positions { get; init; }

    public long   TotalNodes     => Positions.Sum(p => p.TotalNodes);
    public long   TotalMainNodes => Positions.Sum(p => p.MainNodes);
    public long   TotalQNodes    => Positions.Sum(p => p.QNodes);
    public double QNodeShare     => TotalNodes > 0 ? (double)TotalQNodes / TotalNodes : 0;
    public long   TotalElapsedMs => Positions.Sum(p => p.ElapsedMs);

    public double AvgCompletedDepth => Positions.Count > 0 ? Positions.Average(p => p.DepthAchieved) : 0;
    public double AvgPartialDepth   => Positions.Count > 0 ? Positions.Average(p => p.PartialDepth) : 0;
    public int    PositionsWithCancelledIteration => Positions.Count(p => p.PartialDepth > 0);
    public int    PartialSelectionCount => Positions.Count(p => p.UsedPartialRootResult);
    public int    NodeBudgetReachedCount => Positions.Count(p => p.NodeBudgetReached);
    public int    UnsearchedFallbackCount => Positions.Count(p => p.IsUnsearchedFallbackMove);
    public double AvgRootCoveragePercent =>
        Positions.Where(p => p.PartialDepth > 0).Select(p => p.RootCoveragePercent).DefaultIfEmpty(0).Average();

    public long   TotalBetaCutoffs          => Positions.Sum(p => p.BetaCutoffs);
    public long   TotalBetaCutoffsFirstMove => Positions.Sum(p => p.BetaCutoffsFirstMove);
    /// <summary>Ratio-of-sums across the whole corpus (not an average of per-position ratios).</summary>
    public double FirstMoveCutoffRate => TotalBetaCutoffs > 0 ? (double)TotalBetaCutoffsFirstMove / TotalBetaCutoffs : 0;

    public long   TotalLmrReductions => Positions.Sum(p => p.LmrReductions);
    public long   TotalLmrReSearches => Positions.Sum(p => p.LmrReSearches);
    public long   TotalLmrPliesSaved => Positions.Sum(p => p.LmrPliesSaved);
    /// <summary>Fraction of LMR-reduced searches that needed a full-depth re-search.</summary>
    public double LmrReSearchRate => TotalLmrReductions > 0 ? (double)TotalLmrReSearches / TotalLmrReductions : 0;

    public long   TotalAspirationRetries    => Positions.Sum(p => p.AspirationRetries);
    public long   TotalAspirationRetryNodes => Positions.Sum(p => p.AspirationRetryNodes);
    /// <summary>Share of all nodes spent re-searching after an aspiration window failed.</summary>
    public double AspirationRetryNodeShare =>
        TotalNodes > 0 ? (double)TotalAspirationRetryNodes / TotalNodes : 0;
}

/// <summary>Verdict for one compared behaviour.</summary>
public enum AbVerdict { Keep, Revert, Inconclusive }

/// <summary>Comparison of one position where the two configs chose different moves.</summary>
public sealed class AbDisagreement
{
    public required string Fen        { get; init; }
    public required string MoveA      { get; init; }
    public required string MoveB      { get; init; }
    public required int    EvalA      { get; init; }
    public required int    EvalB      { get; init; }
    /// <summary>Reference-engine score for A's move, side-to-move relative. Null when unavailable.</summary>
    public int?   ReferenceScoreA { get; set; }
    public int?   ReferenceScoreB { get; set; }
    /// <summary>Positive = B's move is better by this many centipawns according to the reference.</summary>
    public int?   ReferenceDeltaBMinusA { get; set; }
}

/// <summary>Complete machine-readable A/B report.</summary>
public sealed class AbReport
{
    public int    SchemaVersion { get; set; } = 1;
    public string RunId         { get; set; } = string.Empty;
    public string GeneratedUtc  { get; set; } = string.Empty;
    public string Mode          { get; set; } = string.Empty;

    public int  MaxDepth   { get; set; }
    public long NodeBudget { get; set; }
    public int  CorpusSize { get; set; }
    public string CorpusSource { get; set; } = string.Empty;

    public AbConfigResult? ConfigA { get; set; }
    public AbConfigResult? ConfigB { get; set; }

    public int    SameMoveCount { get; set; }
    public double MoveAgreementRate { get; set; }
    public int    SameEvaluationCount { get; set; }
    public double EvaluationAgreementRate { get; set; }
    public List<AbDisagreement> Disagreements { get; set; } = new();

    public string? ReferenceEngineName { get; set; }
    public int?    ReferenceEngineDepth { get; set; }
    /// <summary>Mean reference-engine centipawn advantage of B's choices over A's, across disagreements.</summary>
    public double? ReferenceMeanDeltaBMinusA { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AbVerdict PartialSelectionVerdict { get; set; } = AbVerdict.Inconclusive;
    public string PartialSelectionRationale { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AbVerdict LmrVerdict { get; set; } = AbVerdict.Inconclusive;
    public string LmrRationale { get; set; } = string.Empty;
}

/// <summary>
/// Controlled A/B harness: runs two named <see cref="SearchSettings"/> configurations against
/// the same fixed FEN corpus at an enforced node budget, and reports their metrics side by
/// side. This exists so strength-affecting heuristics (UsePartialRootResult, the LMR schedule)
/// can be compared under identical, reproducible conditions instead of toggled on trust.
///
/// This is deliberately NOT a live-play strength estimate: no external engine drives the games,
/// no clock pressure beyond the node budget. Search-shape metrics alone therefore never justify
/// a KEEP verdict — see <see cref="MinCorpusForStrengthVerdict"/>.
/// </summary>
public static class AbHarness
{
    /// <summary>
    /// Below this corpus size, no amount of search-shape difference is treated as evidence of a
    /// strength change, and both verdicts stay INCONCLUSIVE. Ten diverse positions can show that
    /// a code path is exercised; they cannot support a strength claim.
    /// </summary>
    public const int MinCorpusForStrengthVerdict = 200;

    /// <summary>
    /// A small, fixed, diverse corpus (opening/middlegame/endgame/tactical). Sufficient for
    /// smoke-testing that both branches are exercised; deliberately too small for a verdict.
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

    /// <summary>
    /// Builds a larger corpus deterministically: from the starting position, walk pseudo-random
    /// legal moves with a seeded generator and snapshot the position at fixed intervals, then
    /// repeat from a fresh line. The same seed always yields the same corpus, so an A/B run is
    /// reproducible without shipping a large FEN file, and the positions are not hand-picked.
    /// </summary>
    public static List<string> GenerateCorpus(int count, int seed = 20260906, int snapshotEvery = 3, int lineLength = 60)
    {
        var fens = new List<string>();
        var rng = new Random(seed);

        while (fens.Count < count)
        {
            var engine = new ChessEngine();
            for (int ply = 0; ply < lineLength && fens.Count < count; ply++)
            {
                var legal = engine.GetLegalMoves();
                if (legal.Count == 0) break;   // terminal: start a new line

                engine.MakeMove(legal[rng.Next(legal.Count)]);

                // Skip the first few plies: they are near-identical across lines.
                if (ply >= 4 && ply % snapshotEvery == 0)
                {
                    string fen = engine.ExportFen();
                    if (!fens.Contains(fen)) fens.Add(fen);
                }
            }
        }

        return fens;
    }

    /// <summary>Renders the settings a config actually produced, so a report states them exactly.</summary>
    public static Dictionary<string, string> DescribeSettings(SearchSettings s) => new()
    {
        ["MaxDepth"]              = s.MaxDepth?.ToString() ?? "(null)",
        ["MaxNodes"]              = s.MaxNodes?.ToString() ?? "(null)",
        ["MaxTimeMs"]             = s.MaxTimeMs?.ToString() ?? "(null)",
        ["UseNullMove"]           = s.UseNullMove.ToString(),
        ["UseLmr"]                = s.UseLmr.ToString(),
        ["UseLegacyFlatLmr"]      = s.UseLegacyFlatLmr.ToString(),
        ["LmrBaseOverride"]       = s.LmrBaseOverride?.ToString() ?? "(default 0.75)",
        ["LmrDivisorOverride"]    = s.LmrDivisorOverride?.ToString() ?? "(default 2.25)",
        ["LmrFullMovesOverride"]  = s.LmrFullMovesOverride?.ToString() ?? "(default 4)",
        ["UseFutility"]           = s.UseFutility.ToString(),
        ["UseTranspositionTable"] = s.UseTranspositionTable.ToString(),
        ["UseQuiescence"]         = s.UseQuiescence.ToString(),
        ["UseAspiration"]         = s.UseAspiration.ToString(),
        ["UseCheckExtension"]     = s.UseCheckExtension.ToString(),
        ["UsePartialRootResult"]  = s.UsePartialRootResult.ToString(),
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
        Dictionary<string, string>? effective = null;

        foreach (var fen in corpus)
        {
            var engine = new ChessEngine();
            engine.LoadFen(fen);

            var settings = config.Build();
            settings.MaxDepth  = maxDepth;
            // MaxNodes is the enforced budget. This previously assigned MinNodeTarget, which the
            // search never reads, so the budget had no effect: every position simply ran to
            // MaxDepth without cancellation, which in turn meant the partial-root path was never
            // exercised and the report's "fixed node-budget comparison" claim was false.
            settings.MaxNodes  = maxNodes;
            settings.MaxTimeMs = int.MaxValue; // node-budget-only run: no time pressure
            settings.Verbose   = false;

            effective ??= DescribeSettings(settings);

            var sw = Stopwatch.StartNew();
            var result = engine.FindBestMove(settings);
            sw.Stop();

            results.Add(new AbPositionResult
            {
                Fen                      = fen,
                BestMove                 = result.BestMove.ToString() ?? string.Empty,
                Evaluation               = result.Evaluation,
                DepthAchieved            = result.DepthAchieved,
                PartialDepth             = result.PartialDepth,
                UsedPartialRootResult    = result.UsedPartialRootResult,
                RootMovesCompleted       = result.RootMovesCompleted,
                RootMoveCount            = result.RootMoveCount,
                RootCoveragePercent      = result.RootCoveragePercent,
                PartialScoreIsExact      = result.PartialScoreIsExact,
                IsUnsearchedFallbackMove = result.IsUnsearchedFallbackMove,
                TotalNodes               = result.TotalNodes,
                MainNodes                = result.MainNodes,
                QNodes                   = result.QNodes,
                QNodeShare               = result.QNodeShare,
                ElapsedMs                = sw.ElapsedMilliseconds,
                // The budget is reached when the search consumed it; the search stops within a
                // few nodes of the cap, so an exact equality test would be brittle.
                NodeBudgetReached        = result.TotalNodes >= maxNodes,
                BetaCutoffs              = result.BetaCutoffs,
                BetaCutoffsFirstMove     = result.BetaCutoffsFirstMove,
                LmrReductions            = result.LmrReductions,
                LmrReSearches            = result.LmrReSearches,
                LmrPliesSaved            = result.LmrPliesSaved,
                AspirationRetries        = result.AspirationRetries,
                AspirationRetryNodes     = result.AspirationRetryNodes,
                IterationNodeRatio       = result.IterationNodeRatio,
            });
        }

        return new AbConfigResult
        {
            Name              = config.Name,
            Description       = config.Description,
            EffectiveSettings = effective ?? new Dictionary<string, string>(),
            Positions         = results,
        };
    }

    /// <summary>
    /// Assembles the full report, including verdicts. <paramref name="mode"/> selects which of
    /// the two verdicts can be anything other than INCONCLUSIVE: a partial-root run says nothing
    /// about the LMR schedule and vice versa.
    /// </summary>
    public static AbReport BuildReport(
        List<AbConfigResult> results, string mode, int maxDepth, long nodeBudget, string corpusSource)
    {
        if (results.Count != 2)
            throw new ArgumentException("A/B report requires exactly two config results.", nameof(results));

        var a = results[0];
        var b = results[1];

        var report = new AbReport
        {
            RunId        = DateTime.UtcNow.ToString("yyyyMMdd_HHmmss"),
            GeneratedUtc = DateTime.UtcNow.ToString("o"),
            Mode         = mode,
            MaxDepth     = maxDepth,
            NodeBudget   = nodeBudget,
            CorpusSize   = a.Positions.Count,
            CorpusSource = corpusSource,
            ConfigA      = a,
            ConfigB      = b,
        };

        for (int i = 0; i < a.Positions.Count; i++)
        {
            var pa = a.Positions[i];
            var pb = b.Positions[i];

            if (pa.BestMove == pb.BestMove) report.SameMoveCount++;
            if (pa.Evaluation == pb.Evaluation) report.SameEvaluationCount++;

            if (pa.BestMove != pb.BestMove)
            {
                report.Disagreements.Add(new AbDisagreement
                {
                    Fen   = pa.Fen,
                    MoveA = pa.BestMove,
                    MoveB = pb.BestMove,
                    EvalA = pa.Evaluation,
                    EvalB = pb.Evaluation,
                });
            }
        }

        report.MoveAgreementRate = a.Positions.Count > 0
            ? (double)report.SameMoveCount / a.Positions.Count : 0;
        report.EvaluationAgreementRate = a.Positions.Count > 0
            ? (double)report.SameEvaluationCount / a.Positions.Count : 0;

        ApplyVerdicts(report, mode, a, b);
        return report;
    }

    private static void ApplyVerdicts(AbReport r, string mode, AbConfigResult a, AbConfigResult b)
    {
        bool corpusTooSmall = r.CorpusSize < MinCorpusForStrengthVerdict;
        string sizeNote =
            $"corpus of {r.CorpusSize} positions is below the {MinCorpusForStrengthVerdict} " +
            $"required before search-shape differences are treated as strength evidence";

        // ── Partial-root selection ───────────────────────────────────────────
        if (mode != "partial-root")
        {
            r.PartialSelectionVerdict  = AbVerdict.Inconclusive;
            r.PartialSelectionRationale = "not compared in this run (mode is not partial-root)";
        }
        else if (b.PartialSelectionCount == 0)
        {
            // The whole point of the comparison is that the feature actually fired.
            r.PartialSelectionVerdict  = AbVerdict.Inconclusive;
            r.PartialSelectionRationale =
                "the enabled configuration never selected a partial root result, so the feature " +
                "was not exercised; lower the node budget or raise the depth until iterations are cancelled";
        }
        else if (corpusTooSmall)
        {
            r.PartialSelectionVerdict  = AbVerdict.Inconclusive;
            r.PartialSelectionRationale =
                $"feature exercised ({b.PartialSelectionCount} partial selections over " +
                $"{b.PositionsWithCancelledIteration} cancelled iterations), but {sizeNote}";
        }
        else if (r.ReferenceMeanDeltaBMinusA is double delta)
        {
            r.PartialSelectionVerdict = delta > 5 ? AbVerdict.Keep
                                      : delta < -5 ? AbVerdict.Revert
                                      : AbVerdict.Inconclusive;
            r.PartialSelectionRationale =
                $"reference engine scores the enabled configuration's differing choices " +
                $"{delta:+0.0;-0.0;0} cp relative to baseline over {r.Disagreements.Count} disagreements";
        }
        else
        {
            r.PartialSelectionVerdict  = AbVerdict.Inconclusive;
            r.PartialSelectionRationale =
                "no reference-engine adjudication available; search-shape metrics alone cannot " +
                "establish a strength change";
        }

        // ── LMR schedule ─────────────────────────────────────────────────────
        if (mode != "lmr")
        {
            r.LmrVerdict  = AbVerdict.Inconclusive;
            r.LmrRationale = "not compared in this run (mode is not lmr)";
        }
        else if (a.TotalLmrReductions == b.TotalLmrReductions &&
                 a.TotalLmrPliesSaved == b.TotalLmrPliesSaved)
        {
            r.LmrVerdict  = AbVerdict.Inconclusive;
            r.LmrRationale =
                "the two schedules produced identical reduction counts and plies saved, so they " +
                "did not actually differ on this corpus";
        }
        else if (corpusTooSmall)
        {
            r.LmrVerdict  = AbVerdict.Inconclusive;
            r.LmrRationale =
                $"schedules differ (reductions {a.TotalLmrReductions:N0} vs {b.TotalLmrReductions:N0}, " +
                $"plies saved {a.TotalLmrPliesSaved:N0} vs {b.TotalLmrPliesSaved:N0}), but {sizeNote}";
        }
        else if (r.ReferenceMeanDeltaBMinusA is double delta)
        {
            r.LmrVerdict = delta > 5 ? AbVerdict.Keep
                         : delta < -5 ? AbVerdict.Revert
                         : AbVerdict.Inconclusive;
            r.LmrRationale =
                $"reference engine scores schedule B's differing choices {delta:+0.0;-0.0;0} cp " +
                $"relative to schedule A over {r.Disagreements.Count} disagreements";
        }
        else
        {
            r.LmrVerdict  = AbVerdict.Inconclusive;
            r.LmrRationale =
                "no reference-engine adjudication available; depth and node shape alone cannot " +
                "establish a strength change";
        }
    }

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>Writes the machine-readable report.</summary>
    public static void WriteJson(AbReport report, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(report, JsonOpts));

    /// <summary>Writes the human-readable report.</summary>
    public static void WriteReport(AbReport r, string path)
    {
        var a = r.ConfigA!;
        var b = r.ConfigB!;

        using var w = new StreamWriter(path);
        w.WriteLine("=== ChessBot Controlled A/B Harness Report ===");
        w.WriteLine($"Run ID    : {r.RunId}");
        w.WriteLine($"Generated : {r.GeneratedUtc}");
        w.WriteLine($"Mode      : {r.Mode}");
        w.WriteLine($"Corpus    : {r.CorpusSize} positions ({r.CorpusSource})");
        w.WriteLine($"Budget    : max depth {r.MaxDepth}, node budget {r.NodeBudget:N0} (enforced via SearchSettings.MaxNodes)");
        w.WriteLine();
        w.WriteLine("This is a fixed node-budget search-shape comparison, not a live-play Elo estimate.");
        w.WriteLine($"No KEEP verdict is issued below {MinCorpusForStrengthVerdict} positions or without reference-engine adjudication.");
        w.WriteLine();

        foreach (var (label, cfg) in new[] { ("A", a), ("B", b) })
        {
            w.WriteLine($"=== Config {label}: {cfg.Name} ===");
            if (!string.IsNullOrWhiteSpace(cfg.Description)) w.WriteLine($"  {cfg.Description}");
            foreach (var kv in cfg.EffectiveSettings)
                w.WriteLine($"    {kv.Key,-24} {kv.Value}");
            w.WriteLine();
        }

        w.WriteLine("=== Aggregate Metrics ===");
        Row(w, "Total nodes",                  a.TotalNodes,                b.TotalNodes,                "N0");
        Row(w, "Main nodes",                   a.TotalMainNodes,            b.TotalMainNodes,            "N0");
        Row(w, "Quiescence nodes",             a.TotalQNodes,               b.TotalQNodes,               "N0");
        Row(w, "Quiescence node share",        a.QNodeShare,                b.QNodeShare,                "P1");
        Row(w, "Total elapsed (ms)",           a.TotalElapsedMs,            b.TotalElapsedMs,            "N0");
        Row(w, "Node budget reached (count)",  a.NodeBudgetReachedCount,    b.NodeBudgetReachedCount,    "N0");
        Row(w, "Avg completed depth",          a.AvgCompletedDepth,         b.AvgCompletedDepth,         "F2");
        Row(w, "Avg partial depth",            a.AvgPartialDepth,           b.AvgPartialDepth,           "F2");
        Row(w, "Cancelled iterations (count)", a.PositionsWithCancelledIteration, b.PositionsWithCancelledIteration, "N0");
        Row(w, "Partial selections (count)",   a.PartialSelectionCount,     b.PartialSelectionCount,     "N0");
        Row(w, "Avg root coverage (cancelled)",a.AvgRootCoveragePercent,    b.AvgRootCoveragePercent,    "F1");
        Row(w, "Unsearched fallback moves",    a.UnsearchedFallbackCount,   b.UnsearchedFallbackCount,   "N0");
        Row(w, "First-move cutoff rate",       a.FirstMoveCutoffRate,       b.FirstMoveCutoffRate,       "P1");
        Row(w, "LMR reductions",               a.TotalLmrReductions,        b.TotalLmrReductions,        "N0");
        Row(w, "LMR plies saved",              a.TotalLmrPliesSaved,        b.TotalLmrPliesSaved,        "N0");
        Row(w, "LMR re-searches",              a.TotalLmrReSearches,        b.TotalLmrReSearches,        "N0");
        Row(w, "LMR re-search rate",           a.LmrReSearchRate,           b.LmrReSearchRate,           "P1");
        Row(w, "Aspiration retries",           a.TotalAspirationRetries,    b.TotalAspirationRetries,    "N0");
        Row(w, "Aspiration retry nodes",       a.TotalAspirationRetryNodes, b.TotalAspirationRetryNodes, "N0");
        Row(w, "Aspiration retry node share",  a.AspirationRetryNodeShare,  b.AspirationRetryNodeShare,  "P1");
        w.WriteLine();

        w.WriteLine("=== Agreement ===");
        w.WriteLine($"  Selected move identical : {r.SameMoveCount}/{r.CorpusSize} ({r.MoveAgreementRate:P1})");
        w.WriteLine($"  Evaluation identical    : {r.SameEvaluationCount}/{r.CorpusSize} ({r.EvaluationAgreementRate:P1})");
        w.WriteLine($"  Disagreements           : {r.Disagreements.Count}");
        if (r.ReferenceEngineName is not null)
        {
            w.WriteLine($"  Reference engine        : {r.ReferenceEngineName} at depth {r.ReferenceEngineDepth}");
            w.WriteLine($"  Mean reference delta B-A: {(r.ReferenceMeanDeltaBMinusA?.ToString("+0.0;-0.0;0") ?? "n/a")} cp");
        }
        else
        {
            w.WriteLine("  Reference engine        : not supplied — differing choices were not adjudicated");
        }
        w.WriteLine();

        if (r.Disagreements.Count > 0)
        {
            w.WriteLine("=== Disagreements ===");
            foreach (var d in r.Disagreements)
            {
                w.WriteLine($"  {d.Fen}");
                w.WriteLine($"    A={d.MoveA} ({d.EvalA:+#;-#;0}cp)   B={d.MoveB} ({d.EvalB:+#;-#;0}cp)" +
                            (d.ReferenceDeltaBMinusA is int rd
                                ? $"   reference: A={d.ReferenceScoreA}cp B={d.ReferenceScoreB}cp  delta {rd:+#;-#;0}cp"
                                : "   reference: n/a"));
            }
            w.WriteLine();
        }

        w.WriteLine("=== Verdicts ===");
        w.WriteLine($"  Partial root selection : {r.PartialSelectionVerdict.ToString().ToUpperInvariant()}");
        w.WriteLine($"      {r.PartialSelectionRationale}");
        w.WriteLine($"  LMR schedule           : {r.LmrVerdict.ToString().ToUpperInvariant()}");
        w.WriteLine($"      {r.LmrRationale}");
    }

    private static void Row(TextWriter w, string label, double valueA, double valueB, string fmt)
        => w.WriteLine($"  {label,-32} A: {valueA.ToString(fmt),-16} B: {valueB.ToString(fmt),-16}");
}
