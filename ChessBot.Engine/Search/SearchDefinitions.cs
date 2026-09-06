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
    /// </summary>
    public bool UsePartialRootResult { get; set; } = true;

    /// <summary>
    /// Check extension (search one ply deeper when in check). Sound, but it changes the
    /// shape of a fixed-depth tree, so it must be off when comparing against a fixed-depth
    /// minimax reference.
    /// </summary>
    public bool UseCheckExtension { get; set; } = true;

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
    /// Total nodes evaluated during the search.
    /// </summary>
    public long NodesSearched { get; set; }

    /// <summary>
    /// Nodes per second (performance metric).
    /// </summary>
    public double NodesPerSecond { get; set; }

    /// <summary>
    /// Time taken for the search in milliseconds.
    /// </summary>
    public long ElapsedTimeMs { get; set; }

    /// <summary>
    /// Returns true if the search found a checkmate.
    /// </summary>
    public bool IsCheckmate { get; set; }

    /// <summary>
    /// Returns true if the position is a stalemate.
    /// </summary>
    public bool IsStalemate { get; set; }

    // ── Extended diagnostics ─────────────────────────────────────────────────

    /// <summary>Quiescence nodes searched (not counted in NodesSearched).</summary>
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

    /// <summary>Effective branching factor: nodes^(1/depth) over the completed depth.</summary>
    public double EffectiveBranchingFactor =>
        DepthAchieved > 0 && NodesSearched > 0
            ? Math.Pow(NodesSearched, 1.0 / DepthAchieved)
            : 0;

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
