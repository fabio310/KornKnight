namespace ChessBot.Engine.Search;

using ChessBot.Engine.Types;

/// <summary>
/// Configuration for an engine search operation.
/// Specifies the constraints under which the engine will analyze a position.
/// </summary>
public class SearchSettings
{
    /// <summary>
    /// Maximum search depth (plies). If null, no depth limit.
    /// </summary>
    public int? MaxDepth { get; set; }

    /// <summary>
    /// Maximum time allowed for search in milliseconds. If null, no time limit.
    /// </summary>
    public int? MaxTimeMs { get; set; }

    /// <summary>
    /// Minimum number of nodes to search. If null, defaults to 300,000.
    /// </summary>
    /// <remarks>
    /// Declared but never read by the search — kept only so existing callers still compile.
    /// Use <see cref="MaxNodes"/> for an enforced node budget.
    /// </remarks>
    public long? MinNodeTarget { get; set; }

    /// <summary>
    /// Hard node budget. When set, the search stops once this many nodes (main + quiescence)
    /// have been visited, making the result reproducible: unlike a time limit, the same
    /// position and settings always produce the same move, score and node count. That is what
    /// makes a paired A/B comparison of search changes possible without timing noise.
    /// </summary>
    public long? MaxNodes { get; set; }

    /// <summary>
    /// If true, the search will perform iterative deepening (search at depths 1, 2, ..., until time/depth limit).
    /// </summary>
    public bool UseIterativeDeepening { get; set; } = true;

    /// <summary>
    /// If true, the search will output debug/telemetry information.
    /// </summary>
    public bool Verbose { get; set; }

    // ── Heuristic toggles ────────────────────────────────────────────────────
    // All default to true, so normal play is unaffected. Turning them off yields a
    // plain alpha-beta search, whose score must equal an unpruned minimax of the same
    // depth — that equivalence is the correctness gate for the search. Null-move,
    // LMR and futility pruning are deliberately unsound heuristics: they trade exact
    // scores for depth, so they must be excluded from that comparison.

    /// <summary>Null-move pruning.</summary>
    public bool UseNullMove { get; set; } = true;

    /// <summary>Late move reductions.</summary>
    public bool UseLmr { get; set; } = true;

    /// <summary>Futility pruning near the horizon.</summary>
    public bool UseFutility { get; set; } = true;

    /// <summary>Transposition table probes and cutoffs (stores still happen).</summary>
    public bool UseTranspositionTable { get; set; } = true;

    /// <summary>Quiescence search at the horizon; when false, the horizon returns a static eval.</summary>
    public bool UseQuiescence { get; set; } = true;

    /// <summary>Aspiration windows; when false, every iteration uses a full window.</summary>
    public bool UseAspiration { get; set; } = true;

    /// <summary>
    /// When true, if an iterative-deepening pass is cancelled mid-iteration but has already
    /// found a root move that beats the previous (completed) iteration's score, that partial
    /// result replaces the last completed iteration's move. When false, the search always
    /// falls back to the last fully completed iteration, ignoring any partial-iteration root
    /// move regardless of its score. Kept as a setting (rather than always-on) because the
    /// partial score and the completed-iteration score come from different depths and not
    /// every root move was searched at the partial depth, so the comparison is not always
    /// sound — this flag exists to allow paired A/B testing of the behavior.
    ///
    /// Defaults to false: this is a strength-affecting heuristic that has not yet been
    /// validated by a controlled A/B comparison, so it must be explicitly opted into rather
    /// than silently changing search behavior for every caller.
    /// </summary>
    public bool UsePartialRootResult { get; set; } = false;

    /// <summary>
    /// Optional override of the LMR schedule's base term (R = LmrBaseOverride + ln(depth)·ln(moveCount) / LmrDivisorOverride).
    /// Null = use the engine's built-in default (0.75). Exists solely to allow controlled
    /// A/B comparison of LMR schedules without recompiling; never set by normal callers.
    /// </summary>
    public double? LmrBaseOverride { get; set; }

    /// <summary>
    /// Optional override of the LMR schedule's divisor term. Null = use the engine's
    /// built-in default (2.25). See <see cref="LmrBaseOverride"/>.
    /// </summary>
    public double? LmrDivisorOverride { get; set; }

    /// <summary>
    /// Optional override of how many first moves at a node are searched at full depth
    /// before LMR starts reducing (built-in default: 4). See <see cref="LmrBaseOverride"/>.
    /// </summary>
    public int? LmrFullMovesOverride { get; set; }

    /// <summary>
    /// Reproduces the flat LMR schedule the engine used before the logarithmic table was
    /// introduced: reduce by 1 from the fifth move onward, by 2 past the eighth, regardless
    /// of depth. Exists so the current schedule can be A/B compared against the exact
    /// implementation it replaced, rather than against another logarithmic parameterisation.
    /// When true, <see cref="LmrBaseOverride"/> and <see cref="LmrDivisorOverride"/> are ignored.
    /// </summary>
    public bool UseLegacyFlatLmr { get; set; }

    /// <summary>
    /// Check extension (search one ply deeper when in check). Sound, but it changes the
    /// shape of a fixed-depth tree, so it must be off when comparing against a fixed-depth
    /// minimax reference.
    /// </summary>
    public bool UseCheckExtension { get; set; } = true;

    /// <summary>
    /// The evaluator's hanging-piece term: a penalty of half its value for every attacked,
    /// undefended non-pawn piece.
    ///
    /// Kept switchable because the term is doubtful on two counts. Quiescence search already
    /// resolves hanging material, so the penalty double-counts what the search finds anyway,
    /// and it does so in the static evaluation — which null-move and futility pruning compare
    /// directly against beta, so the noise propagates into pruning decisions rather than
    /// staying in the leaf score. It is also the most expensive term in the program: up to two
    /// ray-based <c>IsSquareAttackedBy</c> calls per non-pawn piece, at every node, since the
    /// static evaluation runs throughout the tree and not only at leaves.
    ///
    /// Defaults to true (current behaviour); the default only changes if a measurement says so.
    /// </summary>
    public bool UseThreatEval { get; set; } = true;

    /// <summary>
    /// Derive the opening-development term's weight from the material on the board instead of
    /// from the move number.
    ///
    /// The move-number form makes the evaluation depend on something the position does not
    /// contain. The Zobrist hash carries no move number, so transposition entries hold scores
    /// that were only valid at the move number they were stored at; inside a tree that crosses
    /// move 20 the score improves by up to 100 cp purely because plies elapsed, which pays the
    /// engine to shuffle rather than develop; and the same position reached by a longer route
    /// evaluates differently from itself.
    ///
    /// The phase form scales the term by the 24-point material phase, so it fades out smoothly
    /// and depends only on the position.
    ///
    /// Defaults to false (current behaviour); the default only changes if a measurement says so.
    /// </summary>
    public bool UseGamePhaseDevelopment { get; set; }

    /// <summary>
    /// Blend separate midgame and endgame material values and piece-square tables on the game
    /// phase, instead of scoring the whole game from one set.
    ///
    /// One table set has to describe two different games at once. A pawn on the sixth rank is a
    /// small positional plus in the opening and nearly decisive in a pawn endgame; a knight is
    /// worth more than a rook's difference in a closed middlegame and less once the board opens.
    /// A single set splits those differences and is wrong at both ends.
    ///
    /// The midgame set is exactly the table the engine already used, so at full phase a tapered
    /// evaluation reproduces the untapered score to the centipawn, and any measured difference
    /// comes only from positions where material has actually left the board.
    ///
    /// Defaults to false (current behaviour); the default only changes if a measurement says so.
    /// </summary>
    public bool UseTaperedEval { get; set; }

    /// <summary>
    /// Invoked once per *completed* iterative-deepening iteration — never per node — so a
    /// protocol layer (UCI "info depth ...") can report progress without the search having to
    /// know that a protocol exists. Null by default: no callback, no cost, and the search tree
    /// is identical either way, since this is telemetry rather than a heuristic.
    ///
    /// The argument is a single instance reused for every iteration, so its contents (the PV
    /// list included) are only valid for the duration of the call; a consumer that needs to
    /// keep them must copy them. Reuse is what keeps progress reporting allocation-free.
    /// </summary>
    public Action<SearchProgress>? OnIterationComplete { get; set; }

    public SearchSettings()
    {
        MinNodeTarget ??= 300_000;
    }

    /// <summary>
    /// A settings instance with every unsound heuristic disabled — plain alpha-beta with
    /// quiescence off. Used by the correctness gate that compares against minimax.
    /// </summary>
    public static SearchSettings PlainAlphaBeta(int depth) => new()
    {
        MaxDepth              = depth,
        UseNullMove           = false,
        UseLmr                = false,
        UseFutility           = false,
        UseTranspositionTable = false,
        UseQuiescence         = false,
        UseAspiration         = false,
        UseCheckExtension     = false,
    };
}

/// <summary>
/// Mate-score encoding, exposed because anything that renders a search score has to know
/// where the mate band starts: the search returns ply-relative mate scores
/// (<c>±(Mate - plies)</c>), and a protocol layer must turn those back into a distance
/// ("score mate 3") rather than printing a 100,000 centipawn evaluation.
/// </summary>
public static class SearchScores
{
    /// <summary>Score of a mate delivered at ply 0. Deeper mates score less, by one per ply.</summary>
    public const int Mate = 100_000;

    /// <summary>
    /// Width of the mate band: scores whose magnitude is within this much of <see cref="Mate"/>
    /// are mates rather than evaluations.
    ///
    /// This is not the search's stack depth — that is <c>Searcher.MAX_PLY</c>, and the two were
    /// wrongly aliased. The only relationship between them is a lower bound: a mate found at the
    /// deepest reachable ply scores <c>Mate - ply</c>, so the band must be at least as wide as
    /// the stack or such a score would decode as a centipawn evaluation. The value below leaves
    /// generous headroom above the stack, and no real evaluation comes within 99,000 centipawns
    /// of it.
    /// </summary>
    public const int MateDistanceLimit = 256;

    /// <summary>Lowest magnitude that still denotes a mate rather than a centipawn evaluation.</summary>
    public const int MateThreshold = Mate - MateDistanceLimit;

    /// <summary>True when <paramref name="score"/> encodes a forced mate for one side.</summary>
    public static bool IsMateScore(int score) => Math.Abs(score) >= MateThreshold;

    /// <summary>
    /// Converts a mate score into a signed distance in *moves* (not plies), the unit UCI's
    /// "score mate" uses: positive when the side to move mates, negative when it is mated.
    /// A mate in 3 plies is reported as 2 moves, matching the convention that the mating move
    /// itself completes the move pair.
    /// </summary>
    public static int MateDistanceInMoves(int score)
    {
        int plies = Mate - Math.Abs(score);
        int moves = (plies + 1) / 2;
        return score > 0 ? moves : -moves;
    }
}

/// <summary>
/// A snapshot of one completed iterative-deepening iteration, handed to
/// <see cref="SearchSettings.OnIterationComplete"/>.
///
/// The searcher reuses one instance for the whole search, so the values are only valid inside
/// the callback — that is deliberate: progress reporting must not add an allocation per
/// iteration to a search that is otherwise allocation-free.
/// </summary>
public sealed class SearchProgress
{
    /// <summary>Depth of the iteration that just completed.</summary>
    public int Depth { get; internal set; }

    /// <summary>Deepest ply reached including quiescence, over the search so far.</summary>
    public int SelDepth { get; internal set; }

    /// <summary>
    /// Score in the negamax convention (positive = good for the side to move). Mate scores are
    /// ply-relative; use <see cref="SearchScores"/> to decode them.
    /// </summary>
    public int Score { get; internal set; }

    /// <summary>Total nodes (main + quiescence) visited when this iteration finished.</summary>
    public long Nodes { get; internal set; }

    /// <summary>Elapsed search time in milliseconds when this iteration finished.</summary>
    public long ElapsedMs { get; internal set; }

    /// <summary>Nodes per second over the whole search so far.</summary>
    public double NodesPerSecond => ElapsedMs > 0 ? Nodes / (ElapsedMs / 1000.0) : 0;

    /// <summary>
    /// The principal variation of this iteration. Backed by a list the searcher refills every
    /// iteration; copy it if it must outlive the callback.
    /// </summary>
    public IReadOnlyList<Move> PrincipalVariation => PvBuffer;

    /// <summary>Mutable backing store, refilled by the searcher before each callback.</summary>
    internal List<Move> PvBuffer { get; } = new();
}

/// <summary>
/// The result of a search operation, containing the best move, evaluation, and telemetry.
/// </summary>
public class SearchResult
{
    /// <summary>
    /// The best move found.
    /// </summary>
    public Move BestMove { get; set; }

    /// <summary>
    /// The evaluation of the position in centipawns (positive = White advantage, negative = Black advantage).
    /// </summary>
    public int Evaluation { get; set; }

    /// <summary>
    /// The principal variation (line of best play from both sides).
    /// </summary>
    public List<Move> PrincipalVariation { get; set; } = new();

    /// <summary>
    /// The last iterative-deepening depth that ran to completion. Never a partially searched
    /// depth, even when a partial root result from a deeper, unfinished iteration was used as
    /// the reported move (see <see cref="UsedPartialRootResult"/> and <see cref="PartialDepth"/>).
    /// </summary>
    public int DepthAchieved { get; set; }

    /// <summary>
    /// The depth of the iteration that was in progress (and cancelled) when the search stopped.
    /// 0 if no iteration was cancelled mid-flight (i.e. the search ended cleanly after a fully
    /// completed iteration, or hit the root-terminal-position case).
    /// </summary>
    public int PartialDepth { get; set; }

    /// <summary>
    /// True if <see cref="BestMove"/>/<see cref="Evaluation"/>/<see cref="PrincipalVariation"/> were
    /// taken from a partially searched iteration (<see cref="PartialDepth"/>) rather than the last
    /// fully completed one (<see cref="DepthAchieved"/>). Only ever true when
    /// <see cref="SearchSettings.UsePartialRootResult"/> was enabled.
    /// </summary>
    public bool UsedPartialRootResult { get; set; }

    /// <summary>
    /// Number of root moves that finished searching in the iteration that was in progress when
    /// the search stopped (0 if no iteration was cancelled mid-flight).
    /// </summary>
    public int RootMovesCompleted { get; set; }

    /// <summary>
    /// Total number of legal root moves available. Compare with <see cref="RootMovesCompleted"/>
    /// to see how much of the partial iteration actually finished.
    /// </summary>
    public int RootMoveCount { get; set; }

    /// <summary>
    /// Percentage of root moves that finished searching in the cancelled iteration
    /// (100 * RootMovesCompleted / RootMoveCount). 0 when no iteration was cancelled mid-flight.
    /// Always belongs to the specific aspiration-window attempt that was in flight when
    /// cancellation happened, not to any earlier attempt within the same depth.
    /// </summary>
    public double RootCoveragePercent { get; set; }

    /// <summary>
    /// True if the partial score used (when <see cref="UsedPartialRootResult"/> is true) is an
    /// exact value — the candidate root move raised alpha without itself failing high against
    /// the aspiration window in effect at the time. False means the score is only a lower bound
    /// (the true value could be higher), so it should not be treated as a precise evaluation.
    /// </summary>
    public bool PartialScoreIsExact { get; set; }

    /// <summary>
    /// Total nodes visited: main-search nodes plus quiescence nodes. Identical to
    /// <see cref="TotalNodes"/>; kept under this name because existing callers and log
    /// formats use it.
    /// </summary>
    public long NodesSearched { get; set; }

    /// <summary>Main-search (non-quiescence) nodes only.</summary>
    public long MainNodes { get; set; }

    /// <summary>Quiescence nodes only. Same value as <see cref="QNodesSearched"/>.</summary>
    public long QNodes => QNodesSearched;

    /// <summary>Main + quiescence nodes. Same value as <see cref="NodesSearched"/>.</summary>
    public long TotalNodes => MainNodes + QNodesSearched;

    /// <summary>Share of total nodes spent in quiescence (0-1).</summary>
    public double QNodeShare => TotalNodes > 0 ? (double)QNodesSearched / TotalNodes : 0;

    /// <summary>
    /// Nodes per second (performance metric).
    /// </summary>
    public double NodesPerSecond { get; set; }

    /// <summary>
    /// Time taken for the search in milliseconds.
    /// </summary>
    public long ElapsedTimeMs { get; set; }

    /// <summary>
    /// True when the node/time budget was too small to complete even the first iterative-
    /// deepening iteration (depth 1), so <see cref="BestMove"/> is a legal but otherwise
    /// unevaluated root move (the first move produced by move ordering) rather than the
    /// result of any search. Callers/reporting must classify this explicitly rather than
    /// silently presenting it as a depth-1 result.
    /// </summary>
    public bool IsUnsearchedFallbackMove { get; set; }

    /// <summary>
    /// Returns true if the search found a checkmate.
    /// </summary>
    public bool IsCheckmate { get; set; }

    /// <summary>
    /// Returns true if the position is a stalemate.
    /// </summary>
    public bool IsStalemate { get; set; }

    // ── Extended diagnostics ─────────────────────────────────────────────────

    /// <summary>
    /// Quiescence nodes searched. These ARE included in <see cref="NodesSearched"/>
    /// (which is main + quiescence); use <see cref="MainNodes"/> for the main-search count.
    /// </summary>
    public long QNodesSearched { get; set; }

    /// <summary>Maximum quiescence/selective depth reached.</summary>
    public int SelDepth { get; set; }

    /// <summary>Number of transposition-table probe attempts.</summary>
    public int TTProbes { get; set; }

    /// <summary>Number of TT probes that found a matching entry.</summary>
    public int TTHits { get; set; }

    /// <summary>Number of TT hits that caused an immediate cutoff.</summary>
    public int TTCutoffs { get; set; }

    /// <summary>Number of entries written to the TT.</summary>
    public int TTStores { get; set; }

    /// <summary>
    /// TT fill in per-mille (0–1000). -1 means not reported.
    /// Used by the match runner to log "Avg TT fill".
    /// </summary>
    public int HashFull { get; set; } = -1;

    // ── Search-shape profiling ───────────────────────────────────────────────
    // These separate raw execution speed from tree quality: a search can be fast and
    // still reach little depth if ordering is poor, and the cutoff figures are what
    // distinguish the two.

    /// <summary>Static evaluation calls (main search and quiescence).</summary>
    public long EvaluationCalls { get; set; }

    /// <summary>Moves produced by move generation across the whole search.</summary>
    public long MovesGenerated { get; set; }

    /// <summary>Nodes where a move raised alpha to beta (fail-high).</summary>
    public long BetaCutoffs { get; set; }

    /// <summary>Fail-highs produced by the first move tried — the move-ordering quality metric.</summary>
    public long BetaCutoffsFirstMove { get; set; }

    /// <summary>Fraction of fail-highs delivered by the first ordered move (0-1).</summary>
    public double FirstMoveCutoffRate =>
        BetaCutoffs > 0 ? (double)BetaCutoffsFirstMove / BetaCutoffs : 0;

    /// <summary>
    /// Node counts of each completed iterative-deepening iteration, indexed by depth
    /// (<c>IterationNodes[d]</c> = total nodes the search had visited when depth <c>d</c>
    /// finished). Index 0 is unused. Only populated for completed iterations; a cancelled
    /// iteration contributes nothing here even though its nodes count towards
    /// <see cref="NodesSearched"/>.
    /// </summary>
    public List<long> IterationNodes { get; set; } = new();

    /// <summary>
    /// Nodes spent by the last completed iteration alone (its cumulative count minus the
    /// previous iteration's). This is the quantity an effective-branching-factor estimate
    /// needs; the cumulative total is not.
    /// </summary>
    public long LastIterationNodes { get; set; }

    /// <summary>
    /// Effective branching factor estimated the standard way: the ratio of the last completed
    /// iteration's own node count to the previous iteration's.
    ///
    /// The earlier metric raised the *cumulative* node total — nodes from every completed
    /// iteration plus an unfinished one — to the power 1/depth and called the result an EBF.
    /// That quantity has no branching-factor meaning: iterative deepening re-searches the tree
    /// at every depth, so the cumulative total is a sum of geometrically growing terms, and an
    /// unfinished iteration contributes an arbitrary partial amount. Returns 0 when fewer than
    /// two iterations completed, rather than inventing a value.
    /// </summary>
    public double IterationNodeRatio
    {
        get
        {
            if (IterationNodes.Count < 3) return 0;   // need two completed iterations (index 0 unused)
            long last = IterationNodes[^1] - IterationNodes[^2];
            long prev = IterationNodes[^2] - IterationNodes[^3];
            return prev > 0 && last > 0 ? (double)last / prev : 0;
        }
    }

    /// <summary>Aspiration re-search attempts (fail-low plus fail-high) across the search.</summary>
    public long AspirationRetries => AspirationFailLow + AspirationFailHigh;

    /// <summary>
    /// True when the search was cancelled while an aspiration re-search was in flight, rather
    /// than during a first attempt at a depth. This is the case the root-partial state reset
    /// has to handle correctly, so it is reported explicitly instead of being inferred.
    /// </summary>
    public bool CancelledDuringAspirationRetry { get; set; }

    /// <summary>
    /// Nodes spent on aspiration re-searches — the cost of a window that was set too narrow.
    /// Measured as nodes visited inside a re-search attempt, not the whole iteration.
    /// </summary>
    public long AspirationRetryNodes { get; set; }

    /// <summary>
    /// LMR reductions bucketed by remaining depth (index = depth, capped) so a schedule can be
    /// tuned against where it actually fires instead of against a single total.
    /// </summary>
    public long[] LmrReductionsByDepth { get; set; } = new long[LmrBucketCount];

    /// <summary>LMR reductions bucketed by move number at the node (index = move number, capped).</summary>
    public long[] LmrReductionsByMoveNumber { get; set; } = new long[LmrBucketCount];

    /// <summary>Total plies actually removed by LMR (sum of reductions), not the count of reduced moves.</summary>
    public long LmrPliesSaved { get; set; }

    /// <summary>Upper bound of the LMR telemetry buckets; values at or above land in the last bucket.</summary>
    public const int LmrBucketCount = 32;

    public long NullMoveAttempts   { get; set; }
    public long NullMoveCutoffs    { get; set; }
    public long LmrReductions      { get; set; }
    public long LmrReSearches      { get; set; }
    public long FutilitySkips      { get; set; }
    public long PvsReSearches      { get; set; }
    public long AspirationFailLow  { get; set; }
    public long AspirationFailHigh { get; set; }

    /// <summary>Draw scores returned for repetition, split out because they are path-dependent.</summary>
    public long RepetitionDraws    { get; set; }
}

/// <summary>
/// The result of board evaluation (non-search static assessment).
/// </summary>
public class EvaluationResult
{
    /// <summary>
    /// The static evaluation in centipawns.
    /// </summary>
    public int Score { get; set; }

    /// <summary>
    /// Breakdown of material balance.
    /// </summary>
    public MaterialBalance MaterialBalance { get; set; } = new();
}

/// <summary>
/// Detailed breakdown of material on the board.
/// </summary>
public class MaterialBalance
{
    /// <summary>
    /// White's material count in centipawns.
    /// </summary>
    public int WhiteMaterial { get; set; }

    /// <summary>
    /// Black's material count in centipawns.
    /// </summary>
    public int BlackMaterial { get; set; }

    /// <summary>
    /// The material imbalance (White - Black) in centipawns.
    /// </summary>
    public int Imbalance => WhiteMaterial - BlackMaterial;
}
