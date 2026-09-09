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

    /// <summary>
    /// Corpus-wide nodes per second (total nodes over total wall time), not the mean of
    /// per-position rates: a mean would weight a position that searched a thousand nodes the
    /// same as one that searched a million. This is the metric that says whether a change made
    /// the evaluation cheaper, as opposed to changing what the search decides.
    /// </summary>
    public double NodesPerSecond => TotalElapsedMs > 0 ? TotalNodes / (TotalElapsedMs / 1000.0) : 0;

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

/// <summary>
/// Result of playing the two configurations directly against each other.
///
/// Search-shape metrics say how a change moves the tree around; only games say whether it wins.
/// This is the harness's one strength measurement, so it reports its own uncertainty: a score
/// is not evidence of anything without the interval around it.
/// </summary>
public sealed class AbHeadToHeadResult
{
    /// <summary>Games played. Openings are played in pairs, once with each side as White.</summary>
    public int Games { get; set; }

    /// <summary>Wins for configuration B (the changed one).</summary>
    public int WinsB { get; set; }

    /// <summary>Wins for configuration A (the baseline).</summary>
    public int WinsA { get; set; }

    public int Draws { get; set; }

    /// <summary>Node budget per move, the same for both sides. 0 when the games were timed.</summary>
    public long NodesPerMove { get; set; }

    /// <summary>
    /// Time budget per move in ms, when the games were played on a clock instead of a node
    /// budget. Null for node-budget games. Timed games are not reproducible, but they are the
    /// only ones that charge a configuration for what its evaluation costs to compute.
    /// </summary>
    public int? MsPerMove { get; set; }

    /// <summary>
    /// How many games ran at once. Recorded because it qualifies a timed result: under
    /// concurrency both sides search at a reduced effective node rate, so the games are still
    /// a fair comparison but not a measurement at the machine's full speed.
    /// </summary>
    public int Concurrency { get; set; } = 1;

    /// <summary>How the per-move budget was expressed, for reports and rationales.</summary>
    public string BudgetLabel => MsPerMove is int ms
        ? $"{ms:N0} ms/move" + (Concurrency > 1 ? $", {Concurrency} games in parallel" : "")
        : $"{NodesPerMove:N0} nodes/move";

    /// <summary>How each game ended, for sanity-checking that the games were real games.</summary>
    public List<string> TerminationReasons { get; set; } = new();

    /// <summary>B's score rate from B's point of view: (wins + draws/2) / games.</summary>
    public double ScoreRateB => Games > 0 ? (WinsB + Draws / 2.0) / Games : 0;

    /// <summary>
    /// Standard error of <see cref="ScoreRateB"/>, treating each game as an independent trial
    /// scoring 1, 0.5 or 0. Reported so a 51% score over 100 games is visibly indistinguishable
    /// from 50%.
    /// </summary>
    public double ScoreRateStdError
    {
        get
        {
            if (Games <= 1) return 0;

            double mean = ScoreRateB;
            double sumSq = WinsB * Math.Pow(1 - mean, 2)
                         + Draws * Math.Pow(0.5 - mean, 2)
                         + WinsA * Math.Pow(0 - mean, 2);

            return Math.Sqrt(sumSq / (Games - 1) / Games);
        }
    }

    /// <summary>
    /// Elo difference implied by the score rate, for B relative to A. Undefined (null) at a
    /// clean sweep in either direction, where the logistic estimate is infinite.
    /// </summary>
    public double? EloDifference
    {
        get
        {
            double p = ScoreRateB;
            if (Games == 0 || p <= 0 || p >= 1) return null;
            return -400 * Math.Log10(1 / p - 1);
        }
    }

    /// <summary>
    /// Half-width of the roughly 95% confidence interval on the Elo estimate, derived from
    /// <see cref="ScoreRateStdError"/>. Null wherever the Elo estimate itself is.
    /// </summary>
    public double? EloMarginOfError
    {
        get
        {
            double p = ScoreRateB;
            double se = ScoreRateStdError;
            if (Games == 0 || se <= 0 || p <= 0 || p >= 1) return null;

            double lo = Math.Clamp(p - 1.96 * se, 1e-6, 1 - 1e-6);
            double hi = Math.Clamp(p + 1.96 * se, 1e-6, 1 - 1e-6);

            return (-400 * Math.Log10(1 / hi - 1) - -400 * Math.Log10(1 / lo - 1)) / 2;
        }
    }

    /// <summary>
    /// True when the 95% interval on the score rate excludes 0.5 — the only case in which the
    /// games alone justify calling one configuration stronger.
    /// </summary>
    public bool IsSignificant => Games > 1 && Math.Abs(ScoreRateB - 0.5) > 1.96 * ScoreRateStdError;
}

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

    /// <summary>Per-position time budget in ms when this was a time-budget run; null otherwise.</summary>
    public int? TimeBudgetMs { get; set; }

    /// <summary>Direct games between the two configurations, when the run played any.</summary>
    public AbHeadToHeadResult? HeadToHead { get; set; }
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

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AbVerdict ThreatEvalVerdict { get; set; } = AbVerdict.Inconclusive;
    public string ThreatEvalRationale { get; set; } = string.Empty;

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public AbVerdict TaperedEvalVerdict { get; set; } = AbVerdict.Inconclusive;
    public string TaperedEvalRationale { get; set; } = string.Empty;
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
        "rnbqkbnr/pppp1ppp/8/4p3/6P1/5P2/PPPPP2P/RNBQKBNR b KQkq g3 0 2",                     // early tactical shot (Qh4# is there to be found)
        "8/8/8/8/8/8/5k2/R6K w - - 0 1",                                                      // trivial rook endgame (mate technique)
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

                // Skip the first few plies: they are near-identical across lines. Skip a
                // position the walk has just checkmated or stalemated too: it loads fine and
                // searches nothing, so it would silently make the corpus one position smaller
                // than the count the run was sized for.
                if (ply >= 4 && ply % snapshotEvery == 0 && engine.GetLegalMoves().Count > 0)
                {
                    string fen = engine.ExportFen();
                    if (!fens.Contains(fen)) fens.Add(fen);
                }
            }
        }

        return fens;
    }

    /// <summary>
    /// Builds balanced game-opening positions: a seeded random walk from the starting position,
    /// keeping only positions where neither side is ahead in material and both still have their
    /// king safety intact enough to play a real game.
    ///
    /// Games cannot all start from the initial position — two deterministic configurations would
    /// play the same game every time. They also cannot start from arbitrary random-walk positions
    /// (as <see cref="GenerateCorpus"/> produces), because a start where one side is a queen up
    /// decides the game regardless of which configuration is playing, adding variance that has
    /// nothing to do with the change under test.
    /// </summary>
    public static List<string> GenerateOpeningPositions(int count, int seed = 20260907, int plies = 8)
    {
        var fens = new List<string>();
        var rng  = new Random(seed);

        int attempts = 0;
        while (fens.Count < count && attempts < count * 200)
        {
            attempts++;

            var engine = new ChessEngine();
            bool usable = true;

            for (int ply = 0; ply < plies; ply++)
            {
                var legal = engine.GetLegalMoves();
                if (legal.Count == 0) { usable = false; break; }
                engine.MakeMove(legal[rng.Next(legal.Count)]);
            }

            if (!usable) continue;
            if (engine.GetLegalMoves().Count == 0) continue;      // already terminal

            // Material must be level: an opening that hands one side a piece measures luck.
            var balance = engine.Evaluate().MaterialBalance;
            if (balance.Imbalance != 0) continue;

            string fen = engine.ExportFen();
            if (!fens.Contains(fen)) fens.Add(fen);
        }

        return fens;
    }

    /// <summary>
    /// Plays the two configurations against each other and returns the result from B's point of
    /// view. Each opening is played twice with colours reversed, so an opening that happens to
    /// favour White cannot favour one configuration.
    ///
    /// The budget kind decides what the games measure, and both readings are needed. With a node
    /// budget per move the games are reproducible and isolate decision quality: whatever the
    /// change costs to compute is given back for free. With a time budget per move they measure
    /// what actually happens over a board, where a more expensive evaluation buys its information
    /// with depth. A change that is neutral on nodes and better on time is a change worth making,
    /// and only running both distinguishes that from the reverse.
    /// </summary>
    /// <param name="concurrency">
    /// How many openings to play at once. Games are independent, so this is close to linear
    /// speed-up, and a comparison that takes an hour is a comparison that does not get run.
    /// The two games of one opening always run back to back on the same worker, so a colour-
    /// reversed pair sees the same machine conditions even on a CPU whose cores differ.
    /// Keep it at or below the physical core count, and note that timed games under
    /// concurrency measure both sides at a reduced effective node rate.
    /// </param>
    public static AbHeadToHeadResult PlayHeadToHead(
        AbConfig configA, AbConfig configB,
        IReadOnlyList<string> openings,
        long nodesPerMove = 50_000,
        int maxPlies = 300,
        CancellationToken ct = default,
        int? msPerMove = null,
        int concurrency = 1)
    {
        var result = new AbHeadToHeadResult
        {
            NodesPerMove = msPerMove is null ? nodesPerMove : 0,
            MsPerMove    = msPerMove,
            Concurrency  = Math.Max(1, concurrency),
        };

        // Indexed by [opening, swap] so the fold below is in a fixed order regardless of the
        // order in which workers finish: a result that depends on scheduling is not a result.
        var outcomes = new (int outcome, string reason)?[openings.Count, 2];

        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, concurrency) };

        Parallel.For(
            0, openings.Count, options,
            // Each worker keeps one engine per side and reuses them across its games. A fresh
            // ChessEngine allocates a 64 MB transposition table, so allocating per game would
            // spend more time in the GC than in the search.
            localInit: () => (white: new ChessEngine(), black: new ChessEngine()),
            body: (i, _, engines) =>
            {
                if (!ct.IsCancellationRequested)
                {
                    for (int swap = 0; swap < 2; swap++)
                    {
                        // swap == 0: A is White. swap == 1: B is White.
                        outcomes[i, swap] = PlayGame(
                            white: swap == 0 ? configA : configB,
                            black: swap == 0 ? configB : configA,
                            openings[i], nodesPerMove, maxPlies, ct, msPerMove,
                            engines.white, engines.black);
                    }
                }

                return engines;
            },
            localFinally: _ => { });

        for (int i = 0; i < openings.Count; i++)
        {
            for (int swap = 0; swap < 2; swap++)
            {
                if (outcomes[i, swap] is not (int outcome, string reason)) continue;

                result.Games++;
                result.TerminationReasons.Add(reason);

                if (outcome == 0) result.Draws++;
                else
                {
                    bool whiteWon = outcome > 0;
                    bool bWon     = whiteWon == (swap == 1);
                    if (bWon) result.WinsB++; else result.WinsA++;
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Plays one game. Returns +1 if White won, -1 if Black won, 0 for a draw, with the reason.
    /// Draws by repetition and the fifty-move rule are adjudicated here because nothing else
    /// would: two engines with the same evaluation shuffle forever otherwise.
    /// </summary>
    /// <remarks>
    /// Each side searches on its own <see cref="ChessEngine"/>, and therefore its own
    /// transposition table. Sharing one engine would let each configuration probe entries whose
    /// scores the *other* evaluation produced, which in a comparison whose entire subject is the
    /// evaluation would mean neither side was playing the configuration under test. The two
    /// engines are kept in step by replaying every move on both, so each also keeps the full
    /// move history its own repetition detection needs.
    /// </remarks>
    private static (int outcome, string reason) PlayGame(
        AbConfig white, AbConfig black, string openingFen,
        long nodesPerMove, int maxPlies, CancellationToken ct, int? msPerMove,
        ChessEngine whiteEngine, ChessEngine blackEngine)
    {
        // NewGame clears the tables, so a reused engine starts each game knowing nothing —
        // otherwise games in a series would not be independent.
        whiteEngine.NewGame();
        blackEngine.NewGame();
        whiteEngine.LoadFen(openingFen);
        blackEngine.LoadFen(openingFen);

        var whiteSettings = white.Build();
        var blackSettings = black.Build();

        foreach (var s in new[] { whiteSettings, blackSettings })
        {
            s.MaxNodes  = msPerMove is null ? nodesPerMove : null;
            s.MaxTimeMs = msPerMove ?? int.MaxValue;   // node budget only: reproducible games
            s.MaxDepth  = null;
            s.Verbose   = false;
        }

        var seenPositions = new Dictionary<string, int>();

        for (int ply = 0; ply < maxPlies; ply++)
        {
            if (ct.IsCancellationRequested) return (0, "cancelled");

            // White's engine doubles as the arbiter: both engines hold the same position, so
            // either can answer questions about it.
            bool whiteToMove = whiteEngine.SideToMove == ChessBot.Engine.Types.Color.White;

            var legal = whiteEngine.GetLegalMoves();
            if (legal.Count == 0)
            {
                bool inCheck = whiteEngine.GetBoardSnapshot().IsKingInCheck(whiteEngine.SideToMove);

                if (!inCheck) return (0, "stalemate");
                return (whiteToMove ? -1 : 1, "checkmate");
            }

            if (whiteEngine.GetBoardSnapshot().State.IsFiftyMoveRuleDraw)
                return (0, "fifty-move rule");

            if (whiteEngine.GetBoardSnapshot().HasInsufficientMaterial)
                return (0, "insufficient material");

            // Repetition key: the position without the move counters, which is what "the same
            // position" means for the threefold rule.
            string key = string.Join(' ', whiteEngine.ExportFen().Split(' ').Take(4));
            seenPositions[key] = seenPositions.GetValueOrDefault(key) + 1;
            if (seenPositions[key] >= 3)
                return (0, "threefold repetition");

            var mover    = whiteToMove ? whiteEngine   : blackEngine;
            var settings = whiteToMove ? whiteSettings : blackSettings;

            var search = mover.FindBestMove(settings, ct);

            // Both engines follow the game, so each keeps the move history that its own
            // repetition detection reads.
            whiteEngine.MakeMove(search.BestMove);
            blackEngine.MakeMove(search.BestMove);
        }

        return (0, "move limit");
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
        ["UseThreatEval"]         = s.UseThreatEval.ToString(),
        ["UseGamePhaseDevelopment"] = s.UseGamePhaseDevelopment.ToString(),
        ["UseTaperedEval"]        = s.UseTaperedEval.ToString(),
    };

    /// <summary>
    /// Runs both configurations over the corpus.
    ///
    /// <paramref name="maxTimeMs"/> switches the budget kind. A node budget is reproducible —
    /// the same position always yields the same move, score and node count — which is what a
    /// paired comparison of search *decisions* needs. But a node budget also hides the cost of
    /// an evaluation change completely: a cheaper evaluation searches the same nodes and simply
    /// finishes sooner. Only a time budget converts that saving into extra depth, which is the
    /// question an evaluation term has to answer. Both are therefore worth running, and each
    /// report states which it used.
    /// </summary>
    public static List<AbConfigResult> Run(
        AbConfig configA, AbConfig configB,
        IReadOnlyList<string>? corpus = null,
        int maxDepth = 8, long maxNodes = 200_000, int? maxTimeMs = null)
    {
        corpus ??= DefaultCorpus;
        return new List<AbConfigResult>
        {
            RunConfig(configA, corpus, maxDepth, maxNodes, maxTimeMs),
            RunConfig(configB, corpus, maxDepth, maxNodes, maxTimeMs),
        };
    }

    private static AbConfigResult RunConfig(
        AbConfig config, IReadOnlyList<string> corpus, int maxDepth, long maxNodes, int? maxTimeMs = null)
    {
        var results = new List<AbPositionResult>();
        Dictionary<string, string>? effective = null;

        foreach (var fen in corpus)
        {
            var engine = new ChessEngine();
            engine.LoadFen(fen);

            var settings = config.Build();
            settings.MaxDepth  = maxDepth;

            if (maxTimeMs is int timeBudget)
            {
                // Time-budget run: the node count is an outcome, not a constraint, so a cheaper
                // node rate shows up as more nodes and more depth in the same wall time.
                settings.MaxNodes  = null;
                settings.MaxTimeMs = timeBudget;
            }
            else
            {
                // MaxNodes is the enforced budget. This previously assigned MinNodeTarget, which
                // the search never reads, so the budget had no effect: every position simply ran
                // to MaxDepth without cancellation, which in turn meant the partial-root path was
                // never exercised and the report's "fixed node-budget comparison" claim was false.
                settings.MaxNodes  = maxNodes;
                settings.MaxTimeMs = int.MaxValue; // node-budget-only run: no time pressure
            }

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
                // Meaningless in a time-budget run, where no node cap was in force.
                NodeBudgetReached        = maxTimeMs is null && result.TotalNodes >= maxNodes,
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

    /// <summary>
    /// Attaches head-to-head games to a report and recomputes the verdicts, since games can
    /// settle a question that the search-shape metrics leave open.
    /// </summary>
    public static void AttachHeadToHead(AbReport report, AbHeadToHeadResult headToHead)
    {
        report.HeadToHead = headToHead;
        ApplyVerdicts(report, report.Mode, report.ConfigA!, report.ConfigB!);
    }

    /// <summary>
    /// Decides an evaluation-term mode's verdict. Both such modes ask the same question — does
    /// configuration B play better? — and only games or a reference engine can answer it, so
    /// they share one rule rather than two that could drift apart.
    /// </summary>
    private static (AbVerdict verdict, string rationale) EvaluationTermVerdict(
        AbReport r, string mode, string ownMode, bool corpusTooSmall)
    {
        if (mode != ownMode)
            return (AbVerdict.Inconclusive, $"not compared in this run (mode is not {ownMode})");

        if (r.HeadToHead is { Games: > 1 } h2h)
        {
            string outcome =
                $"head-to-head over {h2h.Games} games at {h2h.BudgetLabel}: " +
                $"B scores {h2h.ScoreRateB:P1} (+{h2h.WinsB}={h2h.Draws}-{h2h.WinsA})";

            // Games are the only evidence here that speaks to strength directly, so when they
            // are conclusive they decide, whatever the node counts did.
            if (h2h.IsSignificant)
            {
                return (h2h.ScoreRateB > 0.5 ? AbVerdict.Keep : AbVerdict.Revert,
                        $"{outcome}, which excludes 50% at 95% confidence");
            }

            return (AbVerdict.Inconclusive,
                    $"{outcome}, which does not exclude 50% at 95% confidence " +
                    $"(±{1.96 * h2h.ScoreRateStdError:P1}); not measurably strength-affecting " +
                    $"either way at this sample size");
        }

        if (r.ReferenceMeanDeltaBMinusA is double delta && !corpusTooSmall)
        {
            return (delta > 5 ? AbVerdict.Keep : delta < -5 ? AbVerdict.Revert : AbVerdict.Inconclusive,
                    $"reference engine scores B's differing choices {delta:+0.0;-0.0;0} cp relative " +
                    $"to baseline over {r.Disagreements.Count} disagreements");
        }

        return (AbVerdict.Inconclusive,
                "no head-to-head games and no reference-engine adjudication; node rate and depth " +
                "alone show what the change costs, not whether it wins");
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

        // ── Evaluation-term modes ────────────────────────────────────────────
        // Convention, as for the other modes: A is the current behaviour, B is the change under
        // test, and KEEP means "adopt B". For threat-eval B is the term switched off, so KEEP
        // there means remove it; for tapered-eval B is the new behaviour switched on.
        var (threatVerdict, threatRationale) = EvaluationTermVerdict(r, mode, "threat-eval", corpusTooSmall);
        r.ThreatEvalVerdict   = threatVerdict;
        r.ThreatEvalRationale = threatRationale;

        var (taperedVerdict, taperedRationale) = EvaluationTermVerdict(r, mode, "tapered-eval", corpusTooSmall);
        r.TaperedEvalVerdict   = taperedVerdict;
        r.TaperedEvalRationale = taperedRationale;

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

    /// <summary>
    /// Scores every position where the two configurations chose different moves with an
    /// independent reference engine, from the same pre-move position at one fixed depth, using
    /// UCI searchmoves once per candidate move. The difference between those two scores is the
    /// only thing in this harness that can say which choice was better — node counts and depth
    /// cannot — so a KEEP or REVERT verdict is gated on it.
    ///
    /// Positions the reference engine cannot score (mate scores on either side, or an engine
    /// failure) are skipped rather than folded in: a mate score is not a centipawn value, and
    /// averaging it with centipawns would produce a meaningless mean.
    /// </summary>
    public static async Task AdjudicateAsync(
        AbReport report, string referenceEnginePath, int referenceDepth, CancellationToken ct = default)
    {
        if (report.Disagreements.Count == 0)
        {
            report.ReferenceEngineName  = "(no disagreements to adjudicate)";
            report.ReferenceEngineDepth = referenceDepth;
            return;
        }

        using var engine = new UciAdapter(referenceEnginePath);
        await engine.InitializeAsync(ct);

        report.ReferenceEngineName  = engine.EngineName;
        report.ReferenceEngineDepth = referenceDepth;

        var deltas = new List<int>();

        foreach (var d in report.Disagreements)
        {
            try
            {
                var a = await engine.AnalyzeFenAsync(d.Fen, referenceDepth, d.MoveA, ct);
                var b = await engine.AnalyzeFenAsync(d.Fen, referenceDepth, d.MoveB, ct);

                if (a.ScoreMate.HasValue || b.ScoreMate.HasValue)
                    continue;   // mate scores are not centipawns

                d.ReferenceScoreA = a.ScoreCp;
                d.ReferenceScoreB = b.ScoreCp;
                d.ReferenceDeltaBMinusA = b.ScoreCp - a.ScoreCp;
                deltas.Add(d.ReferenceDeltaBMinusA.Value);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // One unscoreable position must not discard the rest of the adjudication.
                d.ReferenceScoreA = null;
                d.ReferenceScoreB = null;
            }
        }

        report.ReferenceMeanDeltaBMinusA = deltas.Count > 0 ? deltas.Average() : null;

        // Verdicts depend on the adjudication, so they are recomputed now that it exists.
        ApplyVerdicts(report, report.Mode, report.ConfigA!, report.ConfigB!);
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
        w.WriteLine(r.TimeBudgetMs is int ms
            ? $"Budget    : max depth {r.MaxDepth}, time budget {ms:N0} ms/position (enforced via SearchSettings.MaxTimeMs)"
            : $"Budget    : max depth {r.MaxDepth}, node budget {r.NodeBudget:N0} (enforced via SearchSettings.MaxNodes)");
        w.WriteLine();
        w.WriteLine(r.TimeBudgetMs is null
            ? "This is a fixed node-budget search-shape comparison, not a live-play Elo estimate."
            : "This is a fixed time-budget comparison: node counts and depth are outcomes, not constraints.");
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
        Row(w, "Nodes per second",             a.NodesPerSecond,            b.NodesPerSecond,            "N0");
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

        if (r.HeadToHead is { Games: > 0 } h)
        {
            w.WriteLine("=== Head-to-head (B against A) ===");
            w.WriteLine($"  Games            : {h.Games} ({h.BudgetLabel}, openings played with both colours)");
            w.WriteLine($"  B result         : +{h.WinsB} ={h.Draws} -{h.WinsA}");
            w.WriteLine($"  B score rate     : {h.ScoreRateB:P1} ± {1.96 * h.ScoreRateStdError:P1} (95%)");
            w.WriteLine($"  Implied Elo (B-A): {(h.EloDifference?.ToString("+0;-0;0") ?? "n/a")}" +
                        (h.EloMarginOfError is double moe ? $" ± {moe:F0}" : ""));
            w.WriteLine($"  Significant      : {(h.IsSignificant ? "yes" : "no — interval includes 50%")}");

            foreach (var group in h.TerminationReasons.GroupBy(x => x).OrderByDescending(g => g.Count()))
                w.WriteLine($"    {group.Key,-24} {group.Count()}");

            w.WriteLine();
        }

        w.WriteLine("=== Verdicts ===");
        w.WriteLine($"  Threat eval (B = off)  : {r.ThreatEvalVerdict.ToString().ToUpperInvariant()}");
        w.WriteLine($"      {r.ThreatEvalRationale}");
        w.WriteLine($"  Tapered eval (B = on)  : {r.TaperedEvalVerdict.ToString().ToUpperInvariant()}");
        w.WriteLine($"      {r.TaperedEvalRationale}");
        w.WriteLine($"  Partial root selection : {r.PartialSelectionVerdict.ToString().ToUpperInvariant()}");
        w.WriteLine($"      {r.PartialSelectionRationale}");
        w.WriteLine($"  LMR schedule           : {r.LmrVerdict.ToString().ToUpperInvariant()}");
        w.WriteLine($"      {r.LmrRationale}");
    }

    private static void Row(TextWriter w, string label, double valueA, double valueB, string fmt)
        => w.WriteLine($"  {label,-32} A: {valueA.ToString(fmt),-16} B: {valueB.ToString(fmt),-16}");
}
