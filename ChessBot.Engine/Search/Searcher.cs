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
    private const int MATE_SCORE     = 100_000;
    private const int MAX_PLY        = 64;
    private const int INFINITY       = MATE_SCORE + 1;

    // Null-move pruning
    private const int NMP_MIN_DEPTH  = 2;
    private const int NMP_BASE_R     = 3;   // reduction; use 2 when depth is 2-3

    // Late Move Reduction
    private const int LMR_MIN_DEPTH  = 3;
    private const int LMR_FULL_MOVES = 4;   // search first N moves at full depth before reducing

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

    // Per-ply pre-allocated move lists: _moveLists[ply] is cleared and reused at that ply.
    // Safe because the search is single-threaded and each ply level uses its own slot.
    private readonly List<Move>[]         _moveLists;

    // Tracks the move played at each ply so the counter-move heuristic can be populated.
    private readonly Move[]               _lastMoveAtPly;

    private int            _nodesSearched;
    private int            _qnodesSearched;
    private bool           _cancelRequested;
    private Stopwatch      _searchTimer = null!;
    private SearchSettings _settings    = null!;

    // Diagnostics
    private int _selDepth;
    private int _ttProbes;
    private int _ttHits;
    private int _ttCutoffs;
    private int _ttStores;

    // Triangular PV table: _pvTable[ply, ply..ply+len] stores the PV from ply
    private readonly Move[,] _pvTable;
    private readonly int[]   _pvLength;

    // Guard against recursive null moves. Scoped per-ply (not a single instance-wide flag)
    // so NMP only blocks two *consecutive* null moves, rather than disabling null-move
    // pruning for an entire subtree once a real move is made deeper in the tree.
    private readonly bool[] _nullMoveAtPly;

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

        _moveLists = new List<Move>[MAX_PLY];
        for (int i = 0; i < MAX_PLY; i++)
            _moveLists[i] = new List<Move>(64);
    }

    // ── Public search entry point ─────────────────────────────────────────────

    public SearchResult Search(SearchSettings settings, CancellationToken ct = default)
    {
        _settings        = settings ?? new SearchSettings();
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

        var result      = new SearchResult();
        int prevScore   = 0;

        _moveOrdering.Clear();
        _transpositionTable.NewSearch();

        // ── Root terminal-position check ───────────────────────────────────
        // If the root position itself has no legal moves, it is checkmate or
        // stalemate. Without this check, iterative deepening would spin through
        // every depth re-deriving the same fixed mate/draw score while leaving
        // BestMove/IsCheckmate/IsStalemate unset (all false/default) — silently
        // wrong output for a terminal position.
        var rootMoves = _moveLists[0];
        _moveGen.GenerateLegalMovesInto(rootMoves);

        if (rootMoves.Count == 0)
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

            int score;

            if (depth <= 4)
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
                    // Fail-low: widen lower bound
                    alpha = prevScore - ASP_WINDOW * 4;
                    score = NegamaxSearch(0, depth, alpha, beta);
                    if (!_cancelRequested && score <= alpha)
                        score = NegamaxSearch(0, depth, -INFINITY, beta);
                }
                else if (!_cancelRequested && score >= beta)
                {
                    // Fail-high: widen upper bound
                    beta = prevScore + ASP_WINDOW * 4;
                    score = NegamaxSearch(0, depth, alpha, beta);
                    if (!_cancelRequested && score >= beta)
                        score = NegamaxSearch(0, depth, alpha, INFINITY);
                }
            }

            if (_cancelRequested) break;

            prevScore            = score;
            result.Evaluation    = score;
            result.DepthAchieved = depth;
            result.NodesSearched = _nodesSearched + _qnodesSearched;

            // Extract PV from triangular table
            result.PrincipalVariation.Clear();
            for (int i = 0; i < _pvLength[0]; i++)
                result.PrincipalVariation.Add(_pvTable[0, i]);

            if (_pvLength[0] > 0)
                result.BestMove = _pvTable[0, 0];

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
            if (Math.Abs(score) >= MATE_SCORE - MAX_PLY) break;
        }

        _searchTimer.Stop();
        result.ElapsedTimeMs  = _searchTimer.ElapsedMilliseconds;
        result.NodesPerSecond = result.ElapsedTimeMs > 0
            ? result.NodesSearched / (result.ElapsedTimeMs / 1000.0) : 0;
        result.QNodesSearched = _qnodesSearched;
        result.SelDepth       = _selDepth;
        result.TTProbes       = _ttProbes;
        result.TTHits         = _ttHits;
        result.TTCutoffs      = _ttCutoffs;
        result.TTStores       = _ttStores;
        result.HashFull       = _transpositionTable.GetFillPermille();

        return result;
    }

    // ── Negamax with alpha-beta ───────────────────────────────────────────────

    private int NegamaxSearch(int ply, int depth, int alpha, int beta)
    {
        _nodesSearched++;
        _pvLength[ply] = 0;

        // Cancellation check (throttled to avoid stopwatch overhead on every node)
        if (_cancelRequested) return 0;
        if ((_nodesSearched & 2047) == 0 &&
            _searchTimer.ElapsedMilliseconds > (_settings.MaxTimeMs ?? 10_000))
        {
            _cancelRequested = true;
            return 0;
        }

        bool pvNode = beta - alpha > 1;

        // ── Transposition table lookup ─────────────────────────────────────
        // Use the board's incremental hash (O(1)) instead of recomputing from scratch (O(32)).
        ulong hash  = _board.ZobristHash;
        _ttProbes++;

        // Always retrieve TT best move for ordering, even when the entry depth is too low for
        // a score cutoff. A shallow hit still provides an excellent first move to try.
        Move ttMove = _transpositionTable.LookupBestMoveOnly(hash);

        var ttEntry = _transpositionTable.Lookup(hash, depth);
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

        // ── Draw / horizon ────────────────────────────────────────────────
        if (_board.State.IsFiftyMoveRuleDraw || IsDrawByRepetition()) return 0;
        if (ply >= MAX_PLY) return _evaluator.Evaluate(_board);

        // ── Check detection ───────────────────────────────────────────────
        bool inCheck = _checkDetector.IsInCheck(_board.State.ActiveColor);

        // Check extension: add 1 ply when in check to avoid missing short tactics
        if (inCheck) depth++;

        if (depth <= 0)
            return QuiescenceSearch(ply, alpha, beta);

        // ── Null-move pruning ─────────────────────────────────────────────
        // Conditions: not in check, not a PV node, not already a null-move, sufficient depth,
        // and not in a likely zugzwang (we must have non-pawn material).
        int staticEval = _evaluator.Evaluate(_board);

        // ── Futility pruning setup ────────────────────────────────────────
        // At depth 1-2, outside check / PV positions, quiet moves that cannot
        // raise alpha even with a generous margin are skipped safely.
        bool futilityActive = !inCheck && !pvNode && depth <= 2;
        int  futilityMargin = depth == 1 ? 200 : 450;

        if (!inCheck && !pvNode && !_nullMoveAtPly[ply] && depth >= NMP_MIN_DEPTH
            && staticEval >= beta && HasNonPawnMaterial(_board.State.ActiveColor))
        {
            int R = depth >= 4 ? NMP_BASE_R : 2;

            _board.MakeNullMove();
            _nullMoveAtPly[ply + 1] = true;
            int nullScore = -NegamaxSearch(ply + 1, depth - 1 - R, -beta, -beta + 1);
            _nullMoveAtPly[ply + 1] = false;
            _board.UndoNullMove();

            if (_cancelRequested) return 0;

            if (nullScore >= beta)
                return beta; // Null-move cutoff
        }

        // ── Generate and order moves (zero allocation) ────────────────────
        var moves = _moveLists[ply];
        _moveGen.GenerateLegalMovesInto(moves);

        if (moves.Count == 0)
            return inCheck ? -MATE_SCORE + ply : 0; // Checkmate or stalemate

        // Counter-move heuristic: pass the move made by the opponent at the previous ply
        Move lastOpponentMove = ply > 0 ? _lastMoveAtPly[ply - 1] : default;
        _moveOrdering.OrderMoves(moves, ttMove, lastOpponentMove, ply);

        int bestScore = -INFINITY;
        var bestMove  = moves[0];
        var ttFlag2   = TranspositionTable.ScoreFlag.UpperBound;
        int moveCount = 0;

        foreach (var move in moves)
        {
            moveCount++;

            bool isCapture   = (move.MoveType & MoveType.Capture)   != 0;
            bool isPromotion = (move.MoveType & MoveType.Promotion) != 0;
            bool isQuiet     = !isCapture && !isPromotion;

            // ── Futility pruning ──────────────────────────────────────────
            if (futilityActive && isQuiet && moveCount > 1 && staticEval + futilityMargin <= alpha)
                continue;

            // ── Late Move Reductions ──────────────────────────────────────
            int reduction = 0;
            if (!inCheck && depth >= LMR_MIN_DEPTH && moveCount > LMR_FULL_MOVES && isQuiet)
            {
                reduction = 1;
                if (moveCount > 8) reduction = 2;
            }

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
                    score = -NegamaxSearch(ply + 1, depth - 1, -beta, -alpha);
            }
            else
            {
                // Null-window search (Principal Variation Search)
                score = -NegamaxSearch(ply + 1, depth - 1, -alpha - 1, -alpha);
                // Re-search with full window if this might be a better move in PV
                if (!_cancelRequested && score > alpha && score < beta)
                    score = -NegamaxSearch(ply + 1, depth - 1, -beta, -alpha);
            }

            _board.UndoMove();

            if (_cancelRequested) return 0;

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

                    if (alpha >= beta)
                    {
                        if (isQuiet)
                        {
                            _moveOrdering.RecordKillerMove(move, ply);
                            _moveOrdering.RecordHistoryMove(move, depth);
                            // Counter-move: remember this quiet move as the best response to
                            // the opponent's last move (feeds the counter-move heuristic)
                            if (ply > 0)
                                _moveOrdering.RecordCounterMove(_lastMoveAtPly[ply - 1], move);
                        }
                        ttFlag2 = TranspositionTable.ScoreFlag.LowerBound;
                        break;
                    }
                }
            }
        }

        // Adjust mate scores before storing (ply-relative) so they're consistent across tree depth
        int scoreToStore = bestScore;
        if (bestScore > MATE_SCORE - MAX_PLY)  // Mate in N for us
            scoreToStore = bestScore + ply;
        else if (bestScore < -MATE_SCORE + MAX_PLY)  // Mate in N for opponent
            scoreToStore = bestScore - ply;

        _transpositionTable.Store(hash, depth, scoreToStore, ttFlag2, bestMove);
        _ttStores++;
        return bestScore;
    }

    // ── Quiescence search ─────────────────────────────────────────────────────

    private int QuiescenceSearch(int ply, int alpha, int beta)
    {
        _qnodesSearched++;
        if (ply > _selDepth) _selDepth = ply;

        if (_cancelRequested) return 0;

        // Check detection (cached instance – no allocation)
        bool inCheck = _checkDetector.IsInCheck(_board.State.ActiveColor);

        // Stand-pat evaluation (only valid when not in check)
        int standPat = _evaluator.Evaluate(_board);

        if (!inCheck)
        {
            if (standPat >= beta)  return beta;
            if (standPat > alpha)  alpha = standPat;
        }

        if (ply >= MAX_PLY - 1) return standPat;

        // Generate all legal moves into the pre-allocated per-ply buffer (no allocation)
        var allMoves = _moveLists[ply];
        _moveGen.GenerateLegalMovesInto(allMoves);

        if (allMoves.Count == 0)
            return inCheck ? -MATE_SCORE + ply : 0;

        // Order moves in-place (no allocation) so captures/promotions sort first
        _moveOrdering.OrderMoves(allMoves, default, ply);

        // Quick early-exit when not in check and no tactical moves exist
        if (!inCheck)
        {
            bool hasTactical = false;
            foreach (var m in allMoves)
                if ((m.MoveType & MoveType.Capture) != 0 || (m.MoveType & MoveType.Promotion) != 0)
                { hasTactical = true; break; }
            if (!hasTactical) return standPat;
        }

        foreach (var move in allMoves)
        {
            bool isTactical = (move.MoveType & MoveType.Capture)   != 0
                           || (move.MoveType & MoveType.Promotion) != 0;

            // When not in check only search tactical moves; skip quiet moves without allocation
            if (!inCheck && !isTactical) continue;

            // Delta pruning
            if (!inCheck && (move.MoveType & MoveType.Capture) != 0)
            {
                Piece victim = _board.GetPiece(move.To);
                if ((move.MoveType & MoveType.EnPassant) != 0)
                    victim = new Piece(_board.State.ActiveColor.Opposite(), PieceType.Pawn);

                int gain = victim.Type.MaterialValue();
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
        if (ttScore > MATE_SCORE - MAX_PLY)  // Mate in N for us
            return ttScore - currentPly;
        if (ttScore < -MATE_SCORE + MAX_PLY)  // Mate in N for opponent
            return ttScore + currentPly;
        return ttScore;
    }

    /// <summary>
    /// Detects a draw by threefold repetition via the position hash history.
    /// Uses indexed List access (no ToArray() allocation) and iterates newest→oldest,
    /// stopping at the first irreversible move (capture) to bound the search.
    /// Zobrist hashes encode the active color, so only same-color-to-move positions
    /// can ever match, making the color check implicit.
    /// </summary>
    private bool IsDrawByRepetition()
    {
        var history = _board.History; // List<T> – O(1) indexed access
        int count   = history.Count;
        if (count < 4) return false;

        ulong currentHash     = _board.ZobristHash;
        int   repetitionCount = 0;

        // Iterate from newest entry (count-1) to oldest (0).
        // history[i].hash is the hash BEFORE the i-th move was made; only same-color
        // positions (every 2 entries apart) can produce a hash match.
        for (int i = count - 1; i >= 0; i--)
        {
            var (_, capturedPiece, _, hashBeforeMove) = history[i];

            if (hashBeforeMove == currentHash)
            {
                repetitionCount++;
                if (repetitionCount >= 2) // 3 identical positions (current + 2 in history)
                    return true;
            }

            // A capture is irreversible: positions before it cannot repeat the current one
            if (capturedPiece.Type != PieceType.None)
                break;
        }

        return false;
    }
}
