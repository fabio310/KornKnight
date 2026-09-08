namespace ChessBot.Engine.Search;

using ChessBot.Engine.Types;
using ChessBot.Engine.Board;
using System.Collections.Generic;

/// <summary>
/// Implements move ordering heuristics for efficient alpha-beta pruning.
/// Prioritizes moves by: TT move (999k) > winning captures (MVV-LVA + SEE, 600k) >
/// promotions (300k) > killer moves (200k) > counter-moves (150k) > history/quiet (100k),
/// with losing captures (SEE below zero) demoted to 500k — a band that only exists because
/// SEE can now tell the two apart.
/// </summary>
internal class MoveOrdering
{
    private readonly Board _board;

    /// <summary>
    /// Killer moves: best quiet moves at each depth that caused cutoffs.
    /// Indexed by ply (the search stack depth, Searcher.MAX_PLY entries).
    /// </summary>
    private readonly Move[] _killerMoves1;
    private readonly Move[] _killerMoves2;

    /// <summary>
    /// History heuristic: count successful moves for move ordering.
    /// Indexed as [fromSquare.Index][toSquare.Index].
    /// </summary>
    private readonly long[,] _history;

    /// <summary>
    /// Counter-move heuristic: best counter to opponent's last move.
    /// Indexed as [from.Index][to.Index] of the last opponent move.
    /// Stores the best counter-move.
    /// </summary>
    private readonly Move[,] _counterMoves;

    /// <summary>
    /// Pre-allocated scoring buffer for zero-allocation in-place sorting.
    /// Sized for the maximum possible legal-move count (chess max is ~218).
    /// </summary>
    private readonly int[] _moveScores = new int[256];

    // Precomputed direction/offset tables for the SEE attacker scan. These used to be allocated
    // as jagged int[][] literals (plus a LINQ .Select() enumerator) on every single call, and SEE
    // runs once per capture scored during move ordering, i.e. once per node. Flat static readonly
    // arrays make it allocation-free.
    private static readonly int[] KnightFileOffsets = { -2, -2, -1, -1, 1, 1, 2, 2 };
    private static readonly int[] KnightRankOffsets = { -1, 1, -2, 2, -2, 2, -1, 1 };

    // The eight ray directions out of a square. Indices 0-3 are diagonals, 4-7 orthogonals;
    // SeeRayAttacks relies on that split.
    private static readonly int[] RayFileDirs = { -1, -1, 1, 1, -1, 1, 0, 0 };
    private static readonly int[] RayRankDirs = { -1, 1, -1, 1, 0, 0, -1, 1 };

    public MoveOrdering(Board board)
    {
        _board = board;
        _killerMoves1 = new Move[Searcher.MAX_PLY];
        _killerMoves2 = new Move[Searcher.MAX_PLY];
        _history = new long[64, 64];
        _counterMoves = new Move[64, 64];
    }

    /// <summary>
    /// Sorts the first <paramref name="count"/> entries of a caller-supplied array in-place using
    /// a pre-allocated score buffer and insertion sort – zero heap allocations. This is the
    /// hot-path overload: root move handling, negamax, and quiescence all pass fixed-size Move[]
    /// buffers (paired with a count) rather than List&lt;Move&gt;.
    /// Best for the typical node move count of 20–35 where insertion sort beats Array.Sort overhead.
    /// </summary>
    public void OrderMoves(Move[] moves, int count, Move ttMove, Move lastOpponentMove, int depth)
    {
        // Score every move into the pre-allocated buffer (avoids List<(Move,int)> allocation)
        for (int i = 0; i < count; i++)
            _moveScores[i] = CalculateMoveScore(moves[i], ttMove, lastOpponentMove, depth);

        // Insertion sort descending by score (O(N²) but N ≤ ~35 per node, fastest for small N)
        for (int i = 1; i < count; i++)
        {
            var m = moves[i];
            int s = _moveScores[i];
            int j = i - 1;
            while (j >= 0 && _moveScores[j] < s)
            {
                moves[j + 1] = moves[j];
                _moveScores[j + 1] = _moveScores[j];
                j--;
            }
            moves[j + 1] = m;
            _moveScores[j + 1] = s;
        }
    }

    /// <summary>
    /// Overload for backward compatibility (used in quiescence search).
    /// </summary>
    public void OrderMoves(Move[] moves, int count, Move pvMove, int depth)
    {
        OrderMoves(moves, count, pvMove, default, depth);
    }

    /// <summary>
    /// Calculates a score for move ordering.
    /// Higher score = earlier in search (higher priority).
    /// Score ranges: TT/PV (999k+), Captures (500k), Killers (200k), History (100k), Quiet (0-100k).
    /// </summary>
    private int CalculateMoveScore(Move move, Move ttMove, Move lastOpponentMove, int depth)
    {
        // TT move: absolute highest priority (moves first)
        if (move == ttMove && ttMove != default)
            return 999999;

        // Tactical moves — captures, en passant and promotions — share one band, because a
        // promotion is a material swing like any other and belongs to be compared against
        // captures rather than filed below them. A queen promotion used to score a flat 304,000
        // against a capture band whose floor is 500,000, so a move worth eight pawns was searched
        // behind every capture including ones that hang a queen; and because the capture test
        // returned first, exd8=Q never reached the promotion branch at all and was scored as a
        // plain capture of whatever stood on d8.
        if (move.MoveType.IsTactical())
        {
            int swing    = MaterialSwing(_board, move);
            int attacker = _board.GetPiece(move.From).Type.MaterialValue();

            // MVV-LVA, generalised from "the victim" to the whole swing: prefer the largest
            // swing, and among equal swings the cheapest piece that achieves it.
            int mvvScore = swing * 10 - attacker;
            int seeScore = StaticExchangeEvaluation(move);

            // Winning tactical moves (SEE >= 0) score above losing ones.
            return seeScore >= 0
                ? 600000 + mvvScore + seeScore
                : 500000 + mvvScore + seeScore;
        }

        // Killer moves: moves that caused cutoffs at this depth
        if (depth < Searcher.MAX_PLY)
        {
            if (move == _killerMoves1[depth])
                return 200000;
            if (move == _killerMoves2[depth])
                return 190000;
        }

        // Counter-move heuristic: good response to opponent's last move
        if (lastOpponentMove != default && lastOpponentMove.From.Index < 64 && lastOpponentMove.To.Index < 64)
        {
            if (move == _counterMoves[lastOpponentMove.From.Index, lastOpponentMove.To.Index])
                return 150000;
        }

        // History moves: quiet moves with successful history
        int historyScore = (int)Math.Min(10000, _history[move.From.Index, move.To.Index]);
        return 100000 + historyScore;
    }

    /// <summary>
    /// The immediate material swing of a move: what the mover banks before the opponent replies.
    /// That is the captured piece — a pawn for en passant, whose victim does not stand on the
    /// target square — plus, for a promotion, the difference between the new piece and the pawn
    /// that became it. Quiet moves swing nothing.
    ///
    /// One definition, shared by move ordering, SEE and quiescence delta pruning. Those three
    /// disagreeing is what let quiescence discard at 320 centipawns a capture-promotion that
    /// ordering had just scored at 1,120.
    /// </summary>
    internal static int MaterialSwing(Board board, Move move)
    {
        int swing = (move.MoveType & MoveType.EnPassant) != 0
            ? PieceType.Pawn.MaterialValue()
            : board.GetPiece(move.To).Type.MaterialValue();

        if ((move.MoveType & MoveType.Promotion) != 0)
            swing += move.PromotionType.MaterialValue() - PieceType.Pawn.MaterialValue();

        return swing;
    }

    /// <summary>
    /// Records a killer move (a move that caused a cutoff at a given depth).
    /// Replaces the second killer with the first, and promotes the new move to first.
    /// </summary>
    public void RecordKillerMove(Move move, int depth)
    {
        if (depth >= Searcher.MAX_PLY)
            return;

        if (move != _killerMoves1[depth])
        {
            _killerMoves2[depth] = _killerMoves1[depth];
            _killerMoves1[depth] = move;
        }
    }

    /// <summary>
    /// Records successful move for history heuristic.
    /// Called when a move causes a cutoff.
    /// </summary>
    public void RecordHistoryMove(Move move, int depth)
    {
        // Increment history score (depth-squared as bonus weight)
        _history[move.From.Index, move.To.Index] += (long)depth * depth;
    }

    /// <summary>
    /// Records a counter-move: a good move in response to opponent's last move.
    /// </summary>
    public void RecordCounterMove(Move lastOpponentMove, Move counterMove)
    {
        if (lastOpponentMove.From.Index < 64 && lastOpponentMove.To.Index < 64)
            _counterMoves[lastOpponentMove.From.Index, lastOpponentMove.To.Index] = counterMove;
    }

    // ── Static exchange evaluation ────────────────────────────────────────────
    //
    // Scratch state for one exchange. MoveOrdering belongs to a single Searcher and the search is
    // single-threaded, so these are reused rather than allocated per call — SEE runs once per
    // capture scored, which is once or more per node.

    /// <summary>
    /// The king's value inside an exchange. It is never actually traded, so the material value of
    /// zero that evaluation correctly uses would make a king recapture look free. A value above
    /// any real material total makes the swap algorithm treat losing it as unthinkable, and the
    /// legality rule below stops the king capturing into a still-defended square in the first place.
    /// </summary>
    private const int SeeKingValue = 10_000;

    /// <summary>Running exchange balance, one entry per capture in the sequence.</summary>
    private readonly int[] _seeGain = new int[40];

    // Per-direction cursors into the eight rays out of the target square. The front piece on a ray
    // is the only one that can capture along it; when that piece is taken the ray resumes from
    // where it stopped, which is exactly how an x-ray attacker behind a slider is revealed.
    private readonly int[]    _seeRayStep     = new int[8];
    private readonly bool[]   _seeRayHasPiece = new bool[8];
    private readonly Piece[]  _seeRayPiece    = new Piece[8];
    private readonly Square[] _seeRaySquare   = new Square[8];

    /// <summary>
    /// Static Exchange Evaluation: the material the side to move nets if both sides keep capturing
    /// on the target square, each taking with its least valuable attacker and stopping as soon as
    /// continuing would cost more than standing pat. No search, no make/unmake.
    ///
    /// This is the standard swap algorithm. Occupancy is not copied; instead a 64-bit mask of the
    /// squares vacated during the exchange is carried, and the ray scan treats those squares as
    /// empty — which is what makes x-rays fall out for free, including the very first one, since
    /// the capturing piece leaves its own square before the scan starts.
    ///
    /// The usual "max(-gain[d-1], gain[d]) is negative" early exit is deliberately omitted: it
    /// preserves the sign but not the value, and the value is what feeds capture ordering and,
    /// later, quiescence pruning margins. Exchange sequences are a handful of captures long, so
    /// the saving was not worth reporting a defended knight as a free one.
    /// </summary>
    internal int StaticExchangeEvaluation(Move captureMove)
    {
        Square to   = captureMove.To;
        Square from = captureMove.From;
        Color  side = _board.State.ActiveColor;

        bool enPassant = (captureMove.MoveType & MoveType.EnPassant) != 0;
        bool promotion = (captureMove.MoveType & MoveType.Promotion) != 0;

        // Squares whose occupant has left the board for the purposes of this exchange.
        ulong vacated = 1UL << from.Index;

        // The en-passant victim stands beside the target square, not on it, so vacating its square
        // is a separate step — and it matters, because a rank or file through it opens.
        if (enPassant)
            vacated |= 1UL << new Square(to.File, to.Rank - side.PawnDirection()).Index;

        // After a promotion it is the new piece that stands on the target square for the rest of
        // the exchange, so that is what the opponent is capturing.
        PieceType moverType = promotion ? captureMove.PromotionType : _board.GetPiece(from).Type;
        int onSquare = moverType == PieceType.King ? SeeKingValue : moverType.MaterialValue();

        for (int d = 0; d < 8; d++)
        {
            _seeRayStep[d] = 0;
            SeeAdvanceRay(d, to, vacated);
        }

        _seeGain[0] = MaterialSwing(_board, captureMove);
        int depth = 0;
        side = side.Opposite();

        while (depth < _seeGain.Length - 2)
        {
            depth++;
            _seeGain[depth] = onSquare - _seeGain[depth - 1];

            int value = SeeLeastValuableAttacker(to, side, vacated, out int rayIndex, out Square square);
            if (value == int.MaxValue) break;

            // A king may only take the last defender: capturing into a square the other side still
            // attacks is illegal, so the exchange simply stops there.
            if (value == SeeKingValue &&
                SeeLeastValuableAttacker(to, side.Opposite(), vacated, out _, out _) != int.MaxValue)
                break;

            vacated |= 1UL << square.Index;
            if (rayIndex >= 0) SeeAdvanceRay(rayIndex, to, vacated);

            onSquare = value;
            side     = side.Opposite();
        }

        // Fold the speculative sequence back: at every point the side to move takes the better of
        // capturing and standing pat. The deepest entry is never folded in — it belongs to a
        // capture that was found not to be available.
        while (--depth > 0)
            _seeGain[depth - 1] = -Math.Max(-_seeGain[depth - 1], _seeGain[depth]);

        return _seeGain[0];
    }

    /// <summary>
    /// Walks ray <paramref name="d"/> outward from the target square, skipping vacated squares,
    /// and records the first piece it meets as that ray's front. Resumes from where the previous
    /// call stopped, so consuming a front costs one continuation rather than a fresh scan: the
    /// whole exchange spends at most seven steps per direction in total.
    /// </summary>
    private void SeeAdvanceRay(int d, Square to, ulong vacated)
    {
        int df = RayFileDirs[d], dr = RayRankDirs[d];
        int step = _seeRayStep[d];

        while (true)
        {
            step++;
            int file = to.File + df * step;
            int rank = to.Rank + dr * step;

            if (file < 0 || file > 7 || rank < 0 || rank > 7)
            {
                _seeRayStep[d]     = step;
                _seeRayHasPiece[d] = false;
                return;
            }

            var square = new Square(file, rank);
            if ((vacated & (1UL << square.Index)) != 0) continue;

            Piece piece = _board.GetPiece(square);
            if (piece.IsEmpty) continue;

            _seeRayStep[d]     = step;
            _seeRayPiece[d]    = piece;
            _seeRaySquare[d]   = square;
            _seeRayHasPiece[d] = true;
            return;
        }
    }

    /// <summary>
    /// Whether the piece at the front of ray <paramref name="d"/> actually attacks along it. One
    /// that does not — a knight parked on the ray, a rook on a diagonal — blocks the direction
    /// permanently: it can never be captured on the target square, so it is never vacated either.
    /// </summary>
    private static bool SeeRayAttacks(int d, Piece piece, int step)
    {
        bool diagonal = d < 4;
        return piece.Type switch
        {
            PieceType.Queen  => true,
            PieceType.Bishop => diagonal,
            PieceType.Rook   => !diagonal,
            PieceType.King   => step == 1,
            PieceType.Pawn   => diagonal && step == 1 && RayRankDirs[d] == -piece.Color.PawnDirection(),
            _                => false,
        };
    }

    /// <summary>
    /// The material value of the cheapest remaining attacker <paramref name="side"/> has on
    /// <paramref name="to"/>, or <see cref="int.MaxValue"/> when it has none. Knights are scanned
    /// directly, because nothing can stand between a knight and its target and so they take no
    /// part in the x-ray bookkeeping.
    /// </summary>
    private int SeeLeastValuableAttacker(Square to, Color side, ulong vacated,
                                         out int rayIndex, out Square square)
    {
        int best = int.MaxValue;
        rayIndex = -1;
        square   = default;

        for (int i = 0; i < 8; i++)
        {
            int file = to.File + KnightFileOffsets[i];
            int rank = to.Rank + KnightRankOffsets[i];
            if (file < 0 || file > 7 || rank < 0 || rank > 7) continue;

            var candidate = new Square(file, rank);
            if ((vacated & (1UL << candidate.Index)) != 0) continue;

            Piece piece = _board.GetPiece(candidate);
            if (piece.Type != PieceType.Knight || piece.Color != side) continue;

            int value = PieceType.Knight.MaterialValue();
            if (value < best) { best = value; rayIndex = -1; square = candidate; }
        }

        for (int d = 0; d < 8; d++)
        {
            if (!_seeRayHasPiece[d]) continue;

            Piece piece = _seeRayPiece[d];
            if (piece.Color != side) continue;
            if (!SeeRayAttacks(d, piece, _seeRayStep[d])) continue;

            int value = piece.Type == PieceType.King ? SeeKingValue : piece.Type.MaterialValue();
            if (value < best) { best = value; rayIndex = d; square = _seeRaySquare[d]; }
        }

        return best;
    }


    /// <summary>
    /// Clears all ordering heuristics for a new search.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_killerMoves1, 0, _killerMoves1.Length);
        Array.Clear(_killerMoves2, 0, _killerMoves2.Length);

        for (int i = 0; i < 64; i++)
        {
            for (int j = 0; j < 64; j++)
            {
                _history[i, j] = 0;
                _counterMoves[i, j] = default;
            }
        }
    }
}
