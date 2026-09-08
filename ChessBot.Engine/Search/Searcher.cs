namespace ChessBot.Engine.Search;

using ChessBot.Engine.Types;
using ChessBot.Engine.Board;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Hashing;
using System.Diagnostics;

/// <summary>
/// Negamax alpha-beta with iterative deepening, transposition table, quiescence search,
/// null-move pruning (NMP), late move reductions (LMR), aspiration windows, and check extensions.
///
/// Score convention (negamax): positive = good for the side to move, negative = bad.
/// </summary>
internal class Searcher
{
    // ── Constants ─────────────────────────────────────────────────────────────
    // Mate encoding is shared with SearchScores so a protocol layer can decode mate scores
    // without duplicating it.
    private const int MATE_SCORE     = SearchScores.Mate;
    private const int MATE_THRESHOLD = SearchScores.MateThreshold;

    // Depth of the ply-indexed search stack: the PV table, PV lengths, per-ply move buffers,
    // and the last-move/null-move flags are all sized by it, so it is a memory decision and
    // nothing else. It used to be aliased to SearchScores.MateDistanceLimit, which tied the
    // stack depth to the width of the mate band — two unrelated quantities — and left the
    // stack 64 plies deep. That is reachable in a real game: the check extension holds depth
    // constant across a check/evasion pair, so ply grows at roughly twice the nominal depth,
    // and a check-heavy endgame already reaches ply 62 in 25 seconds.
    internal const int MAX_PLY       = 128;
    private const int INFINITY       = MATE_SCORE + 1;

    // Null-move pruning
    private const int NMP_MIN_DEPTH  = 2;
    private const int NMP_BASE_R     = 3;   // reduction; use 2 when depth is 2-3

    // Late Move Reduction
    private const int LMR_MIN_DEPTH  = 3;
    private const int LMR_FULL_MOVES = 4;   // search first N moves at full depth before reducing
    private const int LMR_MAX_MOVES  = 64;  // move-count axis of the reduction table
    private const double LMR_BASE_DEFAULT    = 0.75;
    private const double LMR_DIVISOR_DEFAULT = 2.25;

    // Current LMR schedule in effect (rebuilt only when SearchSettings overrides differ from
    // the values the table was last built with — see Search() and BuildLmrTable above).
    private int    _lmrFullMoves;
    private double _lmrTableBase;
    private double _lmrTableDivisor;
    private bool   _useLegacyFlatLmr;
    private bool   _inAspirationRetry;
    private bool   _cancelledDuringAspirationRetry;

    // Per-iteration and per-heuristic telemetry (see SearchResult for meanings).
    private readonly List<long> _iterationNodes = new();
    private long _aspirationRetryNodes;
    private long _lmrPliesSaved;
    private readonly long[] _lmrByDepth      = new long[SearchResult.LmrBucketCount];
    private readonly long[] _lmrByMoveNumber = new long[SearchResult.LmrBucketCount];

    // Check extension budget: the largest number of check extensions one root-to-leaf line may
    // accumulate.
    internal const int MAX_EXTENSIONS = 16;

    // Delta pruning in quiescence
    private const int DELTA_MARGIN   = 200; // centipawns

    // Aspiration window starting size
    private const int ASP_WINDOW     = 50;  // centipawns

    // ── State ─────────────────────────────────────────────────────────────────
    private readonly Board               _board;
    private readonly Evaluator           _evaluator;
    private readonly TranspositionTable  _transpositionTable;
    private readonly MoveOrdering        _moveOrdering;
    private readonly ZobristHasher       _zobristHasher;

    // ── Shared hot-path objects (allocated once, reused every node) ───────────
    // Eliminates ~3 heap allocations per node that were the primary NPS bottleneck.
    private readonly MoveGenerator        _moveGen;
    private readonly CheckDetector        _checkDetector;

    // Per-ply pre-allocated move buffers: _moveBuffers[ply] is a fixed-size array reused at that
    // ply. Move generation writes into it and returns a count; recursive calls at ply+1 use a
    // different slot, so the buffer's contents remain stable for the whole loop at this ply.
    private readonly Move[][]             _moveBuffers;

    // Tracks the move played at each ply so the counter-move heuristic can be populated.
    private readonly Move[]               _lastMoveAtPly;

    private int            _nodesSearched;
    private int            _qnodesSearched;
    private bool           _cancelRequested;
    private Stopwatch      _searchTimer = null!;
    private SearchSettings _settings    = null!;

    // The caller's cancellation token, kept as a field so the node loop can observe it. Checking
    // it only between iterations (as the iterative-deepening loop does) is not enough for a UCI
    // "stop": an unbounded iteration would then run to completion before noticing, which for
    // "go infinite" means never. It is read inside the existing throttled (every 2048 nodes)
    // check, so the hot path gains nothing per node.
    private CancellationToken _ct;

    // Reused across iterations so progress reporting costs no allocation (see SearchProgress).
    private readonly SearchProgress _progress = new();

    // Diagnostics
    private int _selDepth;
    private int _ttProbes;
    private int _ttHits;
    private int _ttCutoffs;
    private int _ttStores;

    // Search-shape profiling (see SearchResult for what each one means)
    private long _evaluationCalls;
    private long _movesGenerated;
    private long _betaCutoffs;
    private long _betaCutoffsFirstMove;
    private long _nullMoveAttempts;
    private long _nullMoveCutoffs;
    private long _lmrReductions;
    private long _lmrReSearches;
    private long _futilitySkips;
    private long _pvsReSearches;
    private long _aspirationFailLow;
    private long _aspirationFailHigh;
    private long _repetitionDraws;
    private long _checkExtensions;
    private long _checkExtensionsCapped;
    private int  _maxExtensionsInLine;

    /// <summary>Static evaluation with a call counter, so evaluation cost is measurable.</summary>
    private int EvaluateStatic()
    {
        _evaluationCalls++;
        return _evaluator.EvaluateFast(_board, _settings.UseThreatEval,
                                       _settings.UseGamePhaseDevelopment, _settings.UseTaperedEval);
    }

    // ── Partial-iteration root results ────────────────────────────────────────
    // When an iterative-deepening pass runs out of time it is abandoned, but the root
    // moves it *did* finish are still valid: a partially searched depth N that has
    // examined the best few moves usually beats a completed depth N-1. These fields
    // capture the best root move of the iteration currently in progress, recorded only
    // when a root move actually raises alpha (so the score is a real improvement rather
    // than a fail-low bound). Reset at the start of every depth iteration.
    private Move   _rootPartialMove;
    private int    _rootPartialScore;
    private readonly Move[] _rootPartialPv;
    private int    _rootPartialPvLength;

    // True when _rootPartialScore is an exact value (the move was the new best and did not
    // itself trigger a beta cutoff at the root). False means the score is only a lower bound
    // (the root move raised alpha and then immediately failed high against the *current*
    // aspiration window) — the true value could be higher. Kept separate from ScoreBound
    // strings used elsewhere because this is specifically about the *partial* root candidate.
    private bool   _rootPartialIsExact;

    // Number of root moves that finished searching in the iteration currently in progress —
    // separate from the partial best-move fields above because it must be reported even when
    // no root move ever raised alpha (i.e. every root move so far failed low).
    private int    _rootMovesCompleted;
    private int    _rootMoveCount;

    /// <summary>
    /// Resets all partial-root-candidate and root-coverage state. Must be called before
    /// *every* root search attempt — including each aspiration-window retry within the same
    /// iterative-deepening depth — so that a cancellation during e.g. the fail-high re-search
    /// cannot report a candidate or coverage count left over from the fail-low attempt that
    /// preceded it in the same depth.
    /// </summary>
    private void ResetRootPartialState()
    {
        _rootPartialMove     = default;
        _rootPartialScore     = -INFINITY;
        _rootPartialPvLength = 0;
        _rootPartialIsExact  = false;
        _rootMovesCompleted  = 0;
    }

    /// <summary>Precomputed LMR reductions indexed by [depth, moveNumber]; built in the constructor.</summary>
    private readonly int[,] _lmrTable;

    // Triangular PV table: _pvTable[ply, ply..ply+len] stores the PV from ply
    private readonly Move[,] _pvTable;
    private readonly int[]   _pvLength;

    // Guard against recursive null moves. Scoped per-ply (not a single instance-wide flag)
    // so NMP only blocks two *consecutive* null moves, rather than disabling null-move
    // pruning for an entire subtree once a real move is made deeper in the tree.
    private readonly bool[] _nullMoveAtPly;

    // Quiet moves already searched at each ply, so that when one of them finally causes a cutoff
    // the others can be told they failed. Fixed per-ply buffers keep the node allocation-free; a
    // node that searches more quiet moves than this before cutting simply stops recording them,
    // which costs a few malus updates and nothing else.
    private const int MAX_QUIETS_TRACKED = 64;
    private readonly Move[][] _quietsTried;

    // Check extensions accumulated on the path from the root down to each ply. Indexed by ply
    // because the search is depth-first: a node writes its own slot before it recurses, so the
    // slot below it always holds the count for the line currently being walked.
    private readonly int[] _extensionsAtPly;

    // ── Constructor ───────────────────────────────────────────────────────────
    public Searcher(Board board, Evaluator evaluator, ZobristHasher zobristHasher)
    {
        _board              = board;
        _evaluator          = evaluator;
        _zobristHasher      = zobristHasher;
        _transpositionTable = new TranspositionTable(64); // 64 MB
        _moveOrdering       = new MoveOrdering(board);
        _pvTable            = new Move[MAX_PLY, MAX_PLY];
        _pvLength           = new int[MAX_PLY];

        _moveGen        = new MoveGenerator(board);
        _checkDetector  = new CheckDetector(board);
        _lastMoveAtPly  = new Move[MAX_PLY];
        _nullMoveAtPly  = new bool[MAX_PLY];
        _extensionsAtPly = new int[MAX_PLY];

        _quietsTried = new Move[MAX_PLY][];
        for (int i = 0; i < MAX_PLY; i++)
            _quietsTried[i] = new Move[MAX_QUIETS_TRACKED];

        _moveBuffers = new Move[MAX_PLY][];
        for (int i = 0; i < MAX_PLY; i++)
            _moveBuffers[i] = new Move[MoveGenerator.MaxMoves];

        _rootPartialPv = new Move[MAX_PLY];

        // ── Late Move Reduction table ─────────────────────────────────────────
        // R = 0.75 + ln(depth)·ln(moveNumber) / 2.25, the standard logarithmic schedule.
        //
        // The previous flat rule (reduce 1, or 2 past move 8) reduced the same amount at
        // depth 4 as at depth 12, which is where the tree is widest. Profiling the 203-position
        // regression corpus showed only 0.7% of reduced searches ever needed a full-depth
        // re-search — reductions were so timid they almost never changed an outcome, so the
        // nodes they saved were nodes that could have bought depth instead.
        _lmrTable = new int[MAX_PLY, LMR_MAX_MOVES];
        BuildLmrTable(LMR_BASE_DEFAULT, LMR_DIVISOR_DEFAULT);
        _lmrFullMoves = LMR_FULL_MOVES;
        _lmrTableBase = LMR_BASE_DEFAULT;
        _lmrTableDivisor = LMR_DIVISOR_DEFAULT;
    }

    private void BuildLmrTable(double lmrBase, double lmrDivisor)
    {
        for (int d = 1; d < MAX_PLY; d++)
            for (int m = 1; m < LMR_MAX_MOVES; m++)
                _lmrTable[d, m] = (int)(lmrBase + Math.Log(d) * Math.Log(m) / lmrDivisor);
    }

    // ── Public search entry point ─────────────────────────────────────────────

    /// <summary>
    /// Discards everything learned from previous positions: the transposition table and the
    /// killer/history/counter-move tables. Needed when the engine is handed an unrelated game
    /// (UCI "ucinewgame"), where carrying scores over from the previous game's tree is at best
    /// noise and at worst a wrong cutoff from a same-hash position reached by a different path.
    /// </summary>
    internal void ClearTables()
    {
        _transpositionTable.Clear();
        _moveOrdering.Clear();
    }

    public SearchResult Search(SearchSettings settings, CancellationToken ct = default)
    {
        _settings        = settings ?? new SearchSettings();
        _ct              = ct;

        // ── Controlled A/B override of the LMR schedule (see SearchSettings.LmrBaseOverride) ──
        // UseLegacyFlatLmr reproduces the pre-table schedule exactly and takes precedence over
        // the parametric overrides, so the current schedule can be compared against the
        // implementation it actually replaced.
        _useLegacyFlatLmr = _settings.UseLegacyFlatLmr;

        double wantBase    = _settings.LmrBaseOverride    ?? LMR_BASE_DEFAULT;
        double wantDivisor = _settings.LmrDivisorOverride ?? LMR_DIVISOR_DEFAULT;
        int    wantFullMoves = _settings.LmrFullMovesOverride ?? LMR_FULL_MOVES;
        if (wantBase != _lmrTableBase || wantDivisor != _lmrTableDivisor)
        {
            BuildLmrTable(wantBase, wantDivisor);
            _lmrTableBase    = wantBase;
            _lmrTableDivisor = wantDivisor;
        }
        _lmrFullMoves = wantFullMoves;

        _iterationNodes.Clear();
        _iterationNodes.Add(0);          // index 0 unused; keeps depth == index
        _aspirationRetryNodes = 0;
        _inAspirationRetry    = false;
        _cancelledDuringAspirationRetry = false;
        _lmrPliesSaved        = 0;
        Array.Clear(_lmrByDepth, 0, _lmrByDepth.Length);
        Array.Clear(_lmrByMoveNumber, 0, _lmrByMoveNumber.Length);

        _nodesSearched   = 0;
        _qnodesSearched  = 0;
        _cancelRequested = false;
        Array.Clear(_nullMoveAtPly, 0, _nullMoveAtPly.Length);
        _searchTimer     = Stopwatch.StartNew();
        _selDepth        = 0;
        _ttProbes        = 0;
        _ttHits          = 0;
        _ttCutoffs       = 0;
        _ttStores        = 0;

        _evaluationCalls      = 0;
        _movesGenerated       = 0;
        _betaCutoffs          = 0;
        _betaCutoffsFirstMove = 0;
        _nullMoveAttempts     = 0;
        _nullMoveCutoffs      = 0;
        _lmrReductions        = 0;
        _lmrReSearches        = 0;
        _futilitySkips        = 0;
        _pvsReSearches        = 0;
        _aspirationFailLow    = 0;
        _aspirationFailHigh   = 0;
        _repetitionDraws      = 0;
        _checkExtensions      = 0;
        _checkExtensionsCapped = 0;
        _maxExtensionsInLine  = 0;

        var result      = new SearchResult();
        int prevScore   = 0;

        _moveOrdering.NewSearch();
        _transpositionTable.NewSearch();

        // ── Root terminal-position check ───────────────────────────────────
        // If the root position itself has no legal moves, it is checkmate or
        // stalemate. Without this check, iterative deepening would spin through
        // every depth re-deriving the same fixed mate/draw score while leaving
        // BestMove/IsCheckmate/IsStalemate unset (all false/default) — silently
        // wrong output for a terminal position.
        var rootMoves = _moveBuffers[0];
        _moveGen.GenerateLegalMovesInto(rootMoves, out int rootMoveCount);

        if (rootMoveCount == 0)
        {
            bool rootInCheck = _checkDetector.IsInCheck(_board.State.ActiveColor);
            _searchTimer.Stop();

            result.IsCheckmate    = rootInCheck;
            result.IsStalemate    = !rootInCheck;
            result.Evaluation     = rootInCheck ? -MATE_SCORE : 0;
            result.DepthAchieved  = 0;
            result.NodesSearched  = 0;
            result.ElapsedTimeMs  = _searchTimer.ElapsedMilliseconds;
            result.NodesPerSecond = 0;
            result.HashFull       = _transpositionTable.GetFillPermille();
            return result;
        }

        int maxDepth    = _settings.MaxDepth ?? MAX_PLY;
        int allocatedMs = _settings.MaxTimeMs ?? 10_000;

        // ── Iterative deepening ────────────────────────────────────────────
        for (int depth = 1; depth <= maxDepth; depth++)
        {
            // Hard time cap: stop if we've consumed >90% of allowed time to prevent timeout
            long timeLimit = (long)(allocatedMs * 0.9);
            if (_searchTimer.ElapsedMilliseconds > timeLimit) break;
            if (ct.IsCancellationRequested) { _cancelRequested = true; break; }

            Array.Clear(_pvLength, 0, _pvLength.Length);

            // Discard any partial root result from the previous iteration/attempt.
            ResetRootPartialState();
            _rootMoveCount = rootMoveCount;

            int score;

            if (depth <= 4 || !_settings.UseAspiration)
            {
                // Full window for early depths — aspiration windows unreliable here
                score = NegamaxSearch(0, depth, -INFINITY, INFINITY);
            }
            else
            {
                // Aspiration windows: narrow first, then widen in two steps before going infinite.
                // Three widening levels: ±50 → ±200 → ±∞ (independently for low/high failures).
                int alpha = prevScore - ASP_WINDOW;
                int beta  = prevScore + ASP_WINDOW;

                score = NegamaxSearch(0, depth, alpha, beta);

                if (!_cancelRequested && score <= alpha)
                {
                    // Fail-low: widen lower bound. A cancellation partway through *this*
                    // re-search must not report a root candidate/coverage count that was
                    // accumulated during the aborted first attempt above, so state is reset
                    // again before every retry — not just once per depth.
                    _aspirationFailLow++;
                    long retryStart = _nodesSearched + _qnodesSearched;
                    _inAspirationRetry = true;
                    ResetRootPartialState();
                    alpha = prevScore - ASP_WINDOW * 4;
                    score = NegamaxSearch(0, depth, alpha, beta);
                    if (!_cancelRequested && score <= alpha)
                    {
                        ResetRootPartialState();
                        score = NegamaxSearch(0, depth, -INFINITY, beta);
                    }
                    _inAspirationRetry = false;
                    _aspirationRetryNodes += (_nodesSearched + _qnodesSearched) - retryStart;
                }
                else if (!_cancelRequested && score >= beta)
                {
                    // Fail-high: widen upper bound. Same reasoning as the fail-low branch above.
                    _aspirationFailHigh++;
                    long retryStart = _nodesSearched + _qnodesSearched;
                    _inAspirationRetry = true;
                    ResetRootPartialState();
                    beta = prevScore + ASP_WINDOW * 4;
                    score = NegamaxSearch(0, depth, alpha, beta);
                    if (!_cancelRequested && score >= beta)
                    {
                        ResetRootPartialState();
                        score = NegamaxSearch(0, depth, alpha, INFINITY);
                    }
                    _inAspirationRetry = false;
                    _aspirationRetryNodes += (_nodesSearched + _qnodesSearched) - retryStart;
                }
            }

            if (_cancelRequested)
            {
                if (_inAspirationRetry) _cancelledDuringAspirationRetry = true;
                // The iteration was abandoned. DepthAchieved must stay at the last fully
                // completed depth — reporting this unfinished iteration's depth as "achieved"
                // would claim a depth that was never actually finished. The partial-iteration
                // depth and how many root moves it completed are reported separately. These
                // values always belong to the specific aspiration attempt that was in flight
                // when cancellation happened, since state is reset before every attempt above.
                result.PartialDepth        = depth;
                result.RootMovesCompleted  = _rootMovesCompleted;
                result.RootMoveCount       = _rootMoveCount;
                result.RootCoveragePercent = _rootMoveCount > 0
                    ? 100.0 * _rootMovesCompleted / _rootMoveCount : 0;

                // If enabled, and the iteration already found a root move that beats the
                // previous (completed) iteration's score, that move can replace the completed
                // iteration's answer. A fail-low re-search cannot get here: its scores never
                // exceed prevScore. This is a heuristic substitution — the partial score and
                // the completed score come from different depths and not every root move was
                // searched at the partial depth, so it must remain independently switchable.
                if (_settings.UsePartialRootResult &&
                    result.DepthAchieved > 0 &&
                    _rootPartialMove != default &&
                    _rootPartialScore > prevScore)
                {
                    result.BestMove              = _rootPartialMove;
                    result.Evaluation            = _rootPartialScore;
                    result.UsedPartialRootResult = true;
                    result.PartialScoreIsExact   = _rootPartialIsExact;

                    result.PrincipalVariation.Clear();
                    for (int i = 0; i < _rootPartialPvLength; i++)
                        result.PrincipalVariation.Add(_rootPartialPv[i]);
                }
                break;
            }

            prevScore            = score;
            result.Evaluation    = score;
            result.DepthAchieved = depth;
            result.NodesSearched = _nodesSearched + _qnodesSearched;

            // Cumulative node count at the moment this iteration completed. Only completed
            // iterations are recorded, so a branching estimate never mixes in a partial one.
            while (_iterationNodes.Count <= depth) _iterationNodes.Add(0);
            _iterationNodes[depth] = _nodesSearched + _qnodesSearched;

            // Extract PV from triangular table
            result.PrincipalVariation.Clear();
            for (int i = 0; i < _pvLength[0]; i++)
                result.PrincipalVariation.Add(_pvTable[0, i]);

            if (_pvLength[0] > 0)
                result.BestMove = _pvTable[0, 0];

            // Report the finished iteration before deciding whether to start another one, so a
            // search that stops on the time cap below has still published its deepest result.
            if (_settings.OnIterationComplete is { } onIterationComplete)
            {
                _progress.Depth     = depth;
                _progress.SelDepth  = Math.Max(_selDepth, depth);
                _progress.Score     = score;
                _progress.Nodes     = _nodesSearched + _qnodesSearched;
                _progress.ElapsedMs = _searchTimer.ElapsedMilliseconds;
                _progress.PvBuffer.Clear();
                for (int i = 0; i < _pvLength[0]; i++)
                    _progress.PvBuffer.Add(_pvTable[0, i]);

                onIterationComplete(_progress);
            }

            if (_settings.Verbose)
            {
                double sec = _searchTimer.Elapsed.TotalSeconds;
                double nps = sec > 0 ? (_nodesSearched + _qnodesSearched) / sec : 0;
                System.Diagnostics.Debug.WriteLine(
                    $"Depth {depth}: Score={score} Nodes={result.NodesSearched} NPS={nps:F0}");
            }

            // Check hard cap at end of iteration too
            if (_searchTimer.ElapsedMilliseconds > timeLimit) break;

            // Early exit if a forced mate is found
            if (Math.Abs(score) >= MATE_THRESHOLD) break;
        }

        // Extremely small node/time budgets can expire before even depth 1 completes and
        // before UsePartialRootResult ever finds a root move that raises alpha, leaving
        // BestMove at its default value. A legal move must still be returned — fall back to
        // the first move produced by move ordering (root move 0) and flag this explicitly so
        // callers never mistake it for an evaluated result.
        if (result.BestMove == default)
        {
            result.BestMove                = rootMoves[0];
            result.IsUnsearchedFallbackMove = true;
        }

        _searchTimer.Stop();

        // Count every node visited, including those in an iteration that was abandoned on
        // time. Assigning this only on completed iterations understated both the node count
        // and NPS (which divides by the *full* elapsed time) whenever the search was cut off
        // mid-iteration — i.e. on almost every timed move.
        result.NodesSearched  = _nodesSearched + _qnodesSearched;
        result.MainNodes      = _nodesSearched;
        result.ElapsedTimeMs  = _searchTimer.ElapsedMilliseconds;
        result.NodesPerSecond = result.ElapsedTimeMs > 0
            ? result.NodesSearched / (result.ElapsedTimeMs / 1000.0) : 0;
        result.QNodesSearched = _qnodesSearched;

        result.IterationNodes       = new List<long>(_iterationNodes);
        result.LastIterationNodes   = _iterationNodes.Count >= 3
            ? _iterationNodes[^1] - _iterationNodes[^2]
            : (_iterationNodes.Count == 2 ? _iterationNodes[^1] : 0);
        result.AspirationRetryNodes = _aspirationRetryNodes;
        result.CancelledDuringAspirationRetry = _cancelledDuringAspirationRetry;
        result.LmrPliesSaved        = _lmrPliesSaved;
        result.LmrReductionsByDepth      = (long[])_lmrByDepth.Clone();
        result.LmrReductionsByMoveNumber = (long[])_lmrByMoveNumber.Clone();
        result.SelDepth       = _selDepth;
        result.TTProbes       = _ttProbes;
        result.TTHits         = _ttHits;
        result.TTCutoffs      = _ttCutoffs;
        result.TTStores       = _ttStores;

        result.EvaluationCalls       = _evaluationCalls;
        result.MovesGenerated        = _movesGenerated;
        result.BetaCutoffs           = _betaCutoffs;
        result.BetaCutoffsFirstMove  = _betaCutoffsFirstMove;
        result.NullMoveAttempts      = _nullMoveAttempts;
        result.NullMoveCutoffs       = _nullMoveCutoffs;
        result.LmrReductions         = _lmrReductions;
        result.LmrReSearches         = _lmrReSearches;
        result.FutilitySkips         = _futilitySkips;
        result.PvsReSearches         = _pvsReSearches;
        result.AspirationFailLow     = _aspirationFailLow;
        result.AspirationFailHigh    = _aspirationFailHigh;
        result.RepetitionDraws       = _repetitionDraws;
        result.CheckExtensions       = _checkExtensions;
        result.CheckExtensionsCapped = _checkExtensionsCapped;
        result.MaxCheckExtensionsInLine = _maxExtensionsInLine;
        result.HashFull       = _transpositionTable.GetFillPermille();

        return result;
    }

    // ── Negamax with alpha-beta ───────────────────────────────────────────────

    internal int NegamaxSearch(int ply, int depth, int alpha, int beta)
    {
        // Stack bound first, before anything ply-indexed. Every array below is sized MAX_PLY,
        // so this has to be the first statement in the function: it used to sit seventy lines
        // down, under the draw checks, which made it unreachable — the _pvLength write on the
        // next line threw before the guard could return.
        if (ply >= MAX_PLY) return EvaluateStatic();

        _nodesSearched++;
        _pvLength[ply] = 0;

        // Cancellation check (throttled to avoid stopwatch overhead on every node)
        if (_cancelRequested) return 0;
        // Node budget is checked exactly, not every 2048 nodes, so a fixed-node search is
        // reproducible down to the node count.
        if (_settings.MaxNodes is long cap && _nodesSearched + _qnodesSearched > cap)
        {
            _cancelRequested = true;
            return 0;
        }

        // Clock and caller cancellation share the same throttle: both are external stop
        // conditions that only need to be noticed promptly, not exactly.
        if ((_nodesSearched & 2047) == 0 &&
            (_ct.IsCancellationRequested ||
             _searchTimer.ElapsedMilliseconds > (_settings.MaxTimeMs ?? 10_000)))
        {
            _cancelRequested = true;
            return 0;
        }

        bool pvNode = beta - alpha > 1;

        // ── Transposition table lookup ─────────────────────────────────────
        // Use the board's incremental hash (O(1)) instead of recomputing from scratch (O(32)).
        ulong hash  = _board.ZobristHash;
        Move ttMove = default;

        // Always retrieve TT best move for ordering, even when the entry depth is too low for
        // a score cutoff. A shallow hit still provides an excellent first move to try.
        if (_settings.UseTranspositionTable)
        {
            _ttProbes++;
            ttMove = _transpositionTable.LookupBestMoveOnly(hash);
        }

        var ttEntry = _settings.UseTranspositionTable
            ? _transpositionTable.Lookup(hash, depth)
            : null;
        if (ttEntry.HasValue)
        {
            var (ttScore, ttFlag, ttBest, _) = ttEntry.Value;
            if (ttBest != default) ttMove = ttBest; // depth-qualified move is more reliable
            _ttHits++;

            // Correct bound flag logic: only use cutoff if bound is applicable at this alpha-beta window
            if (!pvNode)
            {
                // Mate-distance fix: adjust score based on ply for proper mate evaluation
                int adjustedScore = AdjustMateScore(ttScore, ply);

                if (ttFlag == TranspositionTable.ScoreFlag.Exact)      { _ttCutoffs++; return adjustedScore; }
                if (ttFlag == TranspositionTable.ScoreFlag.LowerBound) 
                {
                    alpha = Math.Max(alpha, adjustedScore);
                    if (alpha >= beta) { _ttCutoffs++; return adjustedScore; }
                }
                if (ttFlag == TranspositionTable.ScoreFlag.UpperBound) 
                {
                    beta  = Math.Min(beta,  adjustedScore);
                    if (alpha >= beta) { _ttCutoffs++; return adjustedScore; }
                }
            }
        }

        // ── Draw ──────────────────────────────────────────────────────────
        if (_board.State.IsFiftyMoveRuleDraw) return 0;
        if (IsDrawByRepetition()) { _repetitionDraws++; return 0; }

        // ── Check detection ───────────────────────────────────────────────
        bool inCheck = _checkDetector.IsInCheck(_board.State.ActiveColor);

        // Check extension: add 1 ply when in check to avoid missing short tactics, up to a fixed
        // budget per root-to-leaf line. Without the budget the extension gives back exactly the
        // ply it costs, so remaining depth never falls along a check/evasion sequence and the
        // line's length is bounded only by the search stack. The budget is per line rather than
        // per search because a global one would be exhausted by the first check-heavy subtree
        // and leave the rest of the tree unextended.
        int  extensionsSoFar = ply > 0 ? _extensionsAtPly[ply - 1] : 0;
        bool wantExtend      = inCheck && _settings.UseCheckExtension;
        bool extend          = wantExtend && extensionsSoFar < MAX_EXTENSIONS;
        if (extend)          { depth++; _checkExtensions++; }
        else if (wantExtend) { _checkExtensionsCapped++; }

        // Every node records its running total before recursing, so a child reads its parent's
        // slot rather than having a counter threaded through six recursive call sites.
        int extensions = extensionsSoFar + (extend ? 1 : 0);
        _extensionsAtPly[ply] = extensions;
        if (extensions > _maxExtensionsInLine) _maxExtensionsInLine = extensions;

        if (depth <= 0)
            return _settings.UseQuiescence
                ? QuiescenceSearch(ply, alpha, beta)
                : EvaluateStatic();

        // ── Null-move pruning ─────────────────────────────────────────────
        // Conditions: not in check, not a PV node, not already a null-move, sufficient depth,
        // and not in a likely zugzwang (we must have non-pawn material).
        int staticEval = EvaluateStatic();

        // ── Futility pruning setup ────────────────────────────────────────
        // At depth 1-2, outside check / PV positions, quiet moves that cannot
        // raise alpha even with a generous margin are skipped safely.
        bool futilityActive = _settings.UseFutility && !inCheck && !pvNode && depth <= 2;
        int  futilityMargin = depth == 1 ? 200 : 450;

        if (_settings.UseNullMove
            && !inCheck && !pvNode && !_nullMoveAtPly[ply] && depth >= NMP_MIN_DEPTH
            && staticEval >= beta && HasNonPawnMaterial(_board.State.ActiveColor))
        {
            int R = depth >= 4 ? NMP_BASE_R : 2;
            _nullMoveAttempts++;

            _board.MakeNullMove();
            _nullMoveAtPly[ply + 1] = true;
            int nullScore = -NegamaxSearch(ply + 1, depth - 1 - R, -beta, -beta + 1);
            _nullMoveAtPly[ply + 1] = false;
            _board.UndoNullMove();

            if (_cancelRequested) return 0;

            if (nullScore >= beta)
            {
                _nullMoveCutoffs++;
                return beta; // Null-move cutoff
            }
        }

        // ── Generate and order moves (zero allocation) ────────────────────
        var moves = _moveBuffers[ply];
        _moveGen.GenerateLegalMovesInto(moves, out int legalCount);
        _movesGenerated += legalCount;

        if (legalCount == 0)
            return inCheck ? -MATE_SCORE + ply : 0; // Checkmate or stalemate

        // Counter-move heuristic: pass the move made by the opponent at the previous ply
        Move lastOpponentMove = ply > 0 ? _lastMoveAtPly[ply - 1] : default;
        _moveOrdering.OrderMoves(moves, legalCount, ttMove, lastOpponentMove, ply);

        int bestScore  = -INFINITY;
        var bestMove   = moves[0];
        var ttFlag2    = TranspositionTable.ScoreFlag.UpperBound;
        int moveCount  = 0;
        int quietCount = 0;   // quiet moves actually searched at this node, for the history malus

        for (int moveIndex = 0; moveIndex < legalCount; moveIndex++)
        {
            var move = moves[moveIndex];
            moveCount++;

            // En passant is a capture (MoveTypeExtensions.IsCapture covers it), so it is never
            // treated as a quiet move for futility pruning / LMR / killer / history purposes.
            bool isCapture   = move.MoveType.IsCapture();
            bool isPromotion = (move.MoveType & MoveType.Promotion) != 0;
            bool isQuiet     = !isCapture && !isPromotion;

            // ── Futility pruning ──────────────────────────────────────────
            if (futilityActive && isQuiet && moveCount > 1 && staticEval + futilityMargin <= alpha)
            {
                _futilitySkips++;
                continue;
            }

            // ── Late Move Reductions ──────────────────────────────────────
            int reduction = 0;
            if (_settings.UseLmr
                && !inCheck && depth >= LMR_MIN_DEPTH && moveCount > _lmrFullMoves && isQuiet)
            {
                if (_useLegacyFlatLmr)
                {
                    // The pre-table schedule, reproduced exactly: depth-independent, and with
                    // no PV-node relief. Kept verbatim so an A/B run compares against the real
                    // previous behaviour rather than a re-parameterised version of the new one.
                    reduction = moveCount > 8 ? 2 : 1;
                    reduction = Math.Clamp(reduction, 0, depth - 2);
                }
                else
                {
                    reduction = _lmrTable[Math.Min(depth, MAX_PLY - 1),
                                          Math.Min(moveCount, LMR_MAX_MOVES - 1)];

                    // PV nodes carry the principal variation; reduce them one ply less so the
                    // main line keeps its accuracy while the rest of the tree is cut harder.
                    if (pvNode) reduction--;

                    // Leave at least one real ply below the reduction: the null-window probe has
                    // to be a search, not a jump straight into quiescence, or the full-depth
                    // re-search that recovers accuracy is never triggered.
                    reduction = Math.Clamp(reduction, 0, depth - 2);
                }

                if (reduction > 0)
                {
                    _lmrReductions++;
                    _lmrPliesSaved += reduction;
                    _lmrByDepth[Math.Min(depth, SearchResult.LmrBucketCount - 1)]++;
                    _lmrByMoveNumber[Math.Min(moveCount, SearchResult.LmrBucketCount - 1)]++;
                }
            }

            // Recorded only for moves that are actually searched, so a futility-pruned move is
            // never blamed for failing to cut a search it never had.
            if (isQuiet && quietCount < MAX_QUIETS_TRACKED)
                _quietsTried[ply][quietCount++] = move;

            // Record for counter-move heuristic (child nodes read _lastMoveAtPly[ply])
            _lastMoveAtPly[ply] = move;
            _board.MakeMove(move);

            int score;
            if (moveCount == 1)
            {
                // First (best) move: always search at full depth with full window
                score = -NegamaxSearch(ply + 1, depth - 1, -beta, -alpha);
            }
            else if (reduction > 0)
            {
                // LMR: reduced-depth null-window search
                score = -NegamaxSearch(ply + 1, depth - 1 - reduction, -alpha - 1, -alpha);
                // If it raised alpha, re-search at full depth
                if (!_cancelRequested && score > alpha)
                {
                    _lmrReSearches++;
                    score = -NegamaxSearch(ply + 1, depth - 1, -beta, -alpha);
                }
            }
            else
            {
                // Null-window search (Principal Variation Search)
                score = -NegamaxSearch(ply + 1, depth - 1, -alpha - 1, -alpha);
                // Re-search with full window if this might be a better move in PV
                if (!_cancelRequested && score > alpha && score < beta)
                {
                    _pvsReSearches++;
                    score = -NegamaxSearch(ply + 1, depth - 1, -beta, -alpha);
                }
            }

            _board.UndoMove();

            if (_cancelRequested) return 0;

            // A root move has now fully completed its search at this depth.
            if (ply == 0) _rootMovesCompleted = moveCount;

            if (score > bestScore)
            {
                bestScore = score;
                bestMove  = move;

                // Update triangular PV table
                _pvTable[ply, ply] = move;
                int childLen = ply + 1 < MAX_PLY ? _pvLength[ply + 1] : 0;
                for (int i = 0; i < childLen; i++)
                    _pvTable[ply, ply + 1 + i] = _pvTable[ply + 1, ply + 1 + i];
                _pvLength[ply] = 1 + childLen;

                if (score > alpha)
                {
                    alpha   = score;
                    ttFlag2 = TranspositionTable.ScoreFlag.Exact;

                    // Root move that raised alpha: remember it so the iteration is still
                    // worth something if we run out of time before finishing it.
                    if (ply == 0)
                    {
                        _rootPartialMove     = move;
                        _rootPartialScore    = score;
                        _rootPartialPvLength = _pvLength[0];
                        _rootPartialIsExact  = true;
                        for (int i = 0; i < _rootPartialPvLength; i++)
                            _rootPartialPv[i] = _pvTable[0, i];
                    }

                    if (alpha >= beta)
                    {
                        _betaCutoffs++;
                        if (moveCount == 1) _betaCutoffsFirstMove++;

                        if (isQuiet)
                        {
                            _moveOrdering.RecordKillerMove(move, ply);
                            _moveOrdering.RecordHistoryMove(move, depth);

                            // The quiet moves searched ahead of this one were ordered above it and
                            // did not cut, so they were ranked too highly. Saying so is what stops
                            // the table from only ever learning which moves are good.
                            //
                            // Not at a node in check: there the quiet moves are forced evasions,
                            // and which evasion cuts depends entirely on where the check came
                            // from, which a from/to table cannot represent. Blaming the others
                            // writes noise into the squares a king most often flees to — measured
                            // at -1.4 ply on check-heavy positions with no corpus gain to show
                            // for it.
                            if (!inCheck)
                            {
                                for (int q = 0; q < quietCount; q++)
                                    if (_quietsTried[ply][q] != move)
                                        _moveOrdering.RecordHistoryFailure(_quietsTried[ply][q], depth);
                            }

                            // Counter-move: remember this quiet move as the best response to
                            // the opponent's last move (feeds the counter-move heuristic)
                            if (ply > 0)
                                _moveOrdering.RecordCounterMove(_lastMoveAtPly[ply - 1], move);
                        }
                        ttFlag2 = TranspositionTable.ScoreFlag.LowerBound;

                        // The root move that just triggered a beta cutoff is only known to be
                        // *at least* this good against the current aspiration window — the
                        // window was too narrow to prove an exact value, so the partial
                        // candidate captured above must be flagged as a bound, not exact.
                        if (ply == 0) _rootPartialIsExact = false;
                        break;
                    }
                }
            }
        }

        // Adjust mate scores before storing (ply-relative) so they're consistent across tree depth
        int scoreToStore = bestScore;
        if (bestScore > MATE_THRESHOLD)  // Mate in N for us
            scoreToStore = bestScore + ply;
        else if (bestScore < -MATE_THRESHOLD)  // Mate in N for opponent
            scoreToStore = bestScore - ply;

        _transpositionTable.Store(hash, depth, scoreToStore, ttFlag2, bestMove);
        _ttStores++;
        return bestScore;
    }

    // ── Quiescence search ─────────────────────────────────────────────────────

    internal int QuiescenceSearch(int ply, int alpha, int beta)
    {
        // Same stack bound as NegamaxSearch, and for the same reason it is the first statement:
        // _moveBuffers[ply] below is sized MAX_PLY. Quiescence is where the bound is actually
        // reached, because it has no depth counter at all — a check evasion sequence recurses
        // on ply alone until this returns.
        if (ply >= MAX_PLY) return EvaluateStatic();

        _qnodesSearched++;
        if (ply > _selDepth) _selDepth = ply;

        if (_cancelRequested) return 0;

        if (_settings.MaxNodes is long qcap && _nodesSearched + _qnodesSearched > qcap)
        {
            _cancelRequested = true;
            return 0;
        }

        // Quiescence has to honour the clock too. Without this the only time check is in
        // NegamaxSearch, so a capture-heavy qsearch entered just after the last check runs
        // unbounded — measured at 29% over the move budget in tactical positions, which is
        // a flag risk under a real clock.
        if ((_qnodesSearched & 2047) == 0 &&
            (_ct.IsCancellationRequested ||
             _searchTimer.ElapsedMilliseconds > (_settings.MaxTimeMs ?? 10_000)))
        {
            _cancelRequested = true;
            return 0;
        }

        // Check detection (cached instance – no allocation)
        bool inCheck = _checkDetector.IsInCheck(_board.State.ActiveColor);

        // Stand-pat evaluation (only valid when not in check)
        int standPat = EvaluateStatic();

        if (!inCheck)
        {
            if (standPat >= beta)  return beta;
            if (standPat > alpha)  alpha = standPat;
        }

        // Generate moves into the pre-allocated per-ply buffer (no allocation). When not in check,
        // only tactical moves (captures/en passant/promotions) are generated: quiet moves can never
        // raise alpha above stand-pat here, so skipping their generation, legality-check, and
        // ordering entirely avoids the wasted work of the old "generate all, order all, scan for
        // tactical, skip quiet" approach. When in check, stand-pat is invalid and every legal
        // evasion (quiet or not) must still be considered.
        var moves = _moveBuffers[ply];
        int count;

        if (inCheck)
        {
            _moveGen.GenerateLegalMovesInto(moves, out count);
            _movesGenerated += count;

            if (count == 0)
                return -MATE_SCORE + ply; // Checkmated — no legal evasions
        }
        else
        {
            _moveGen.GenerateLegalTacticalMovesInto(moves, out count);
            _movesGenerated += count;

            if (count == 0)
                return standPat; // No captures/promotions left to consider
        }

        // Order moves in-place (no allocation) so the best captures/promotions are tried first
        _moveOrdering.OrderMoves(moves, count, default, ply);

        for (int mi = 0; mi < count; mi++)
        {
            var move = moves[mi];

            // Delta pruning: a tactical move that cannot raise alpha even with a generous margin
            // is skipped. The gain is the full material swing — the same figure move ordering
            // scored the move by — so a capture-promotion is charged at the victim plus the
            // promotion rather than the victim alone. Charging exd8=Q at 320 instead of 1,120
            // pruned it whenever standPat + 520 fell under alpha, which is exactly the lost
            // position with a passer on the seventh that the promotion exists to rescue.
            if (!inCheck && move.MoveType.IsTactical())
            {
                int gain = MoveOrdering.MaterialSwing(_board, move);
                if (standPat + gain + DELTA_MARGIN <= alpha)
                    continue;
            }

            _board.MakeMove(move);
            int score = -QuiescenceSearch(ply + 1, -beta, -alpha);
            _board.UndoMove();

            if (_cancelRequested) return 0;

            if (score >= beta)  return beta;
            if (score > alpha)  alpha = score;
        }

        return alpha;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    // Reusable buffer for HasNonPawnMaterial — avoids the per-call heap allocation that
    // Board.GetAllPieces() incurs (it's a `yield return` iterator, so every invocation
    // allocates a new enumerator). A side has at most 16 pieces.
    private readonly (Square sq, Piece p)[] _materialCheckBuffer = new (Square, Piece)[16];

    /// <summary>Returns true if the side has at least one non-pawn, non-king piece on the board.</summary>
    private bool HasNonPawnMaterial(Color color)
    {
        int count = _board.GetPiecesOf(color, _materialCheckBuffer);
        for (int i = 0; i < count; i++)
        {
            PieceType type = _materialCheckBuffer[i].p.Type;
            if (type != PieceType.None && type != PieceType.Pawn && type != PieceType.King)
                return true;
        }
        return false;
    }

    /// <summary>
    /// Adjusts mate scores from transposition table entries based on current ply.
    /// Mate scores are ply-relative: -MATE_SCORE + ply means "mate in (MATE_SCORE - ply) / 2 moves".
    /// To ensure we prefer faster mates, we adjust the stored score to reflect how many moves
    /// we are deeper into the search tree than when it was stored.
    /// </summary>
    private int AdjustMateScore(int ttScore, int currentPly)
    {
        if (ttScore > MATE_THRESHOLD)  // Mate in N for us
            return ttScore - currentPly;
        if (ttScore < -MATE_THRESHOLD)  // Mate in N for opponent
            return ttScore + currentPly;
        return ttScore;
    }

    /// <summary>
    /// Detects a draw by threefold repetition via the position hash history.
    /// Uses indexed array access (no List<T>, no ToArray() allocation) and iterates
    /// newest→oldest, stopping at the first irreversible move (capture) to bound the search.
    /// Zobrist hashes encode the active color, so only same-color-to-move positions
    /// can ever match, making the color check implicit.
    /// </summary>
    private bool IsDrawByRepetition()
    {
        int count = _board.HistoryCount; // preallocated array – O(1) indexed access
        if (count < 4) return false;

        ulong currentHash     = _board.ZobristHash;
        int   repetitionCount = 0;

        // Iterate from newest entry (count-1) to oldest (0).
        // history[i].hash is the hash BEFORE the i-th move was made; only same-color
        // positions (every 2 entries apart) can produce a hash match.
        for (int i = count - 1; i >= 0; i--)
        {
            var entry = _board.GetHistoryEntry(i);

            if (entry.Hash == currentHash)
            {
                repetitionCount++;
                if (repetitionCount >= 2) // 3 identical positions (current + 2 in history)
                    return true;
            }

            // A capture is irreversible: positions before it cannot repeat the current one
            if (entry.CapturedPiece.Type != PieceType.None)
                break;
        }

        return false;
    }
}
