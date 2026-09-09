namespace ChessBot.Engine.Search;

using ChessBot.Engine.Types;
using ChessBot.Engine.Board;
using System.Collections.Generic;

/// <summary>
/// Implements move ordering heuristics for efficient alpha-beta pruning.
/// Prioritizes moves by: TT move (999k) > winning tactical moves — captures and promotions
/// alike (600k) > losing tactical moves (500k) > killer moves (200k/190k) > counter-moves
/// (150k) > quiet moves ranked by history (100k ± history). Which of the two tactical bands a
/// move lands in is decided by <see cref="SeeGe"/>; its rank within the band, by MVV-LVA.
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
    /// History heuristic: a signed record of how well a quiet move has done at producing beta
    /// cutoffs, relative to how often it was tried and failed to. Indexed as [from.Index, to.Index].
    /// </summary>
    private readonly int[,] _history;

    /// <summary>
    /// The magnitude a history entry approaches but never reaches. Updates move an entry toward
    /// this ceiling by a fraction of the distance still to go, so a move that keeps cutting keeps
    /// separating itself from one that cuts less often, and no number of hits can pin two entries
    /// at the same value.
    ///
    /// The previous update added depth² outright and the read clamped at 10,000, which needs about
    /// seventy cutoffs on one from/to pair at depth 12 before that pair stops ranking. Measured
    /// against the old rule, that was rarer than it sounds — within a single search, 0 of 124 used
    /// slots reached the clamp at 300k nodes, 3 of 314 at 2M and 17 of 604 at 10M — because the
    /// table was cleared before every move, so nothing accumulated across a game. It is
    /// <see cref="NewSearch"/> keeping the table between moves that makes an unbounded accumulator
    /// untenable, and gravity is what makes keeping it safe.
    /// </summary>
    internal const int HistoryMax = 16384;

    /// <summary>
    /// Ceiling on a single update's weight. Depth² alone reaches 16,384 at depth 128 and would
    /// swamp the gravity term in one hit at the deepest plies.
    /// </summary>
    private const int HistoryBonusMax = 1200;

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
        _history = new int[64, 64];
        _counterMoves = new Move[64, 64];
    }

    /// <summary>
    /// Sorts the first <paramref name="count"/> entries of a caller-supplied array in-place using
    /// a pre-allocated score buffer and insertion sort – zero heap allocations. This is the
    /// hot-path overload: root move handling, negamax, and quiescence all pass fixed-size Move[]
    /// buffers (paired with a count) rather than List&lt;Move&gt;.
    /// Best for the typical node move count of 20–35 where insertion sort beats Array.Sort overhead.
    ///
    /// <paramref name="ply"/> is distance from the root, NOT remaining depth: it selects the
    /// killer slots for this ply. See <see cref="RecordKillerMove"/> for why the distinction
    /// matters.
    /// </summary>
    public void OrderMoves(Move[] moves, int count, Move ttMove, Move lastOpponentMove, int ply)
    {
        // Score every move into the pre-allocated buffer (avoids List<(Move,int)> allocation)
        for (int i = 0; i < count; i++)
            _moveScores[i] = CalculateMoveScore(moves[i], ttMove, lastOpponentMove, ply);

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
    public void OrderMoves(Move[] moves, int count, Move pvMove, int ply)
    {
        OrderMoves(moves, count, pvMove, default, ply);
    }

    /// <summary>
    /// Calculates a score for move ordering; higher is searched earlier. See the class summary
    /// for the bands.
    /// </summary>
    private int CalculateMoveScore(Move move, Move ttMove, Move lastOpponentMove, int ply)
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
            // swing, and among equal swings the cheapest piece that achieves it. Two array
            // lookups, and within a band it already gets the order nearly right.
            int mvvScore = swing * 10 - attacker;

            // The band — winning tactical move or losing one — is the one thing MVV-LVA cannot
            // supply, and it is the only thing the exchange is asked for here. Scoring the full
            // exchange bought a number that was then only ever compared against zero, at a
            // measured 17.6% of the engine's node rate; asking for the sign instead lets a third
            // of those calls answer without looking at the board at all. The rest of that 17.6%
            // is the ray scan itself and needs an attackers-to-square bitboard, not a cheaper
            // question — see the note on SeeGe.
            return SeeGe(move, 0)
                ? 600000 + mvvScore
                : 500000 + mvvScore;
        }

        // Killer moves: moves that caused cutoffs at this ply
        if (ply < Searcher.MAX_PLY)
        {
            if (move == _killerMoves1[ply])
                return 200000;
            if (move == _killerMoves2[ply])
                return 190000;
        }

        // Counter-move heuristic: good response to opponent's last move
        if (lastOpponentMove != default && lastOpponentMove.From.Index < 64 && lastOpponentMove.To.Index < 64)
        {
            if (move == _counterMoves[lastOpponentMove.From.Index, lastOpponentMove.To.Index])
                return 150000;
        }

        // History: the signed cutoff record of this quiet move. Negative for a move that has been
        // tried and failed often, so it sorts below one that has never been seen at all.
        return 100000 + _history[move.From.Index, move.To.Index];
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
    /// Records a killer move (a move that caused a cutoff at a given ply).
    /// Replaces the second killer with the first, and promotes the new move to first.
    ///
    /// <paramref name="ply"/> is distance from the root, NOT remaining depth. Killers are
    /// siblings: the point is that a move which cut at this ply is likely to cut in the other
    /// branches at this ply. Remaining depth would pool moves from unrelated parts of the tree
    /// and index the table by a number that shrinks as the search descends, which is the
    /// opposite of what the heuristic needs. The parameter was called "depth" while every call
    /// site passed ply — correct behaviour, one rename away from a silent regression.
    /// </summary>
    public void RecordKillerMove(Move move, int ply)
    {
        if (ply >= Searcher.MAX_PLY)
            return;

        if (move != _killerMoves1[ply])
        {
            _killerMoves2[ply] = _killerMoves1[ply];
            _killerMoves1[ply] = move;
        }
    }

    /// <summary>
    /// Rewards a quiet move that produced a beta cutoff, weighted by the depth it did so at:
    /// a cutoff eight plies from the horizon is worth far more evidence than one at the horizon.
    /// </summary>
    public void RecordHistoryMove(Move move, int depth) => UpdateHistory(move, HistoryBonus(depth));

    /// <summary>
    /// Penalises a quiet move that was searched ahead of the move that actually cut and failed to
    /// cut itself. Without this the table only ever learns which moves are good and never which
    /// are merely tried often — two moves with the same number of cutoffs are indistinguishable
    /// even when one of them was searched ten times as often to get them.
    /// </summary>
    public void RecordHistoryFailure(Move move, int depth) => UpdateHistory(move, -HistoryBonus(depth));

    /// <summary>The current history value of a move. Signed; 0 for a move never seen.</summary>
    internal int HistoryScore(Move move) => _history[move.From.Index, move.To.Index];

    private static int HistoryBonus(int depth) => Math.Min(depth * depth, HistoryBonusMax);

    /// <summary>
    /// The gravity update: move the entry toward ±<see cref="HistoryMax"/> by the bonus, less the
    /// share of the bonus already accounted for by how far the entry has come. An entry near the
    /// ceiling barely moves; one near zero moves by almost the whole bonus. That is what keeps
    /// heavily rewarded entries ordered against each other instead of piled on a clamp.
    /// </summary>
    private void UpdateHistory(Move move, int bonus)
    {
        ref int entry = ref _history[move.From.Index, move.To.Index];
        entry += bonus - entry * Math.Abs(bonus) / HistoryMax;
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
    /// The usual "max(-gain[d-1], gain[d]) is negative" early exit is deliberately omitted here:
    /// it preserves the sign but not the value, and this routine exists precisely for the callers
    /// that need the number itself — a pruning margin, or a test asserting what an exchange is
    /// actually worth. Callers that only need the sign should use <see cref="SeeGe"/>, which is
    /// the early-exiting form and is what move ordering runs.
    /// </summary>
    internal int StaticExchangeEvaluation(Move captureMove)
    {
        Square to   = captureMove.To;
        Color  side = _board.State.ActiveColor;

        ulong vacated = SeeVacatedSquares(captureMove, out int onSquare);
        SeePrimeRays(to, vacated);

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
    /// Whether the exchange starting with <paramref name="captureMove"/> nets the side to move at
    /// least <paramref name="threshold"/> centipawns — the same swap-off as
    /// <see cref="StaticExchangeEvaluation"/>, answering only whether the balance clears a bound.
    ///
    /// That weaker question is much cheaper. The balance is carried negamax-style, one side's
    /// running total at a time, and the loop stops the moment the side to move can no longer
    /// change the verdict by continuing: capturing costs it the piece it just put on the square,
    /// so once even winning the next piece outright leaves it short, nothing deeper can rescue it.
    /// The two constant-time pre-checks alone settle most captures without a single attacker scan —
    /// a capture that stays ahead even if the piece is taken for free is good whatever follows, and
    /// one that falls short even unanswered is bad whatever follows.
    ///
    /// <paramref name="threshold"/> is what the caller demands of the exchange, so
    /// <c>SeeGe(m, 0)</c> asks "is this capture not losing material".
    /// </summary>
    internal bool SeeGe(Move captureMove, int threshold)
    {
        Square to   = captureMove.To;
        Color  side = _board.State.ActiveColor;

        // What the mover banks immediately. If that alone falls short of the threshold, so does
        // every continuation — the opponent's reply can only take material back.
        int balance = MaterialSwing(_board, captureMove) - threshold;
        if (balance < 0) return false;

        ulong vacated = SeeVacatedSquares(captureMove, out int onSquare);

        // Now assume the worst: the piece just placed on the target square is lost for nothing.
        // Still clearing the threshold means no defence matters, so the answer is settled before a
        // single square has been looked at. This is the check that pays for the whole routine: at
        // a threshold of zero it covers every capture whose attacker is worth no more than what it
        // takes, and those never prime a ray. Measured over a 60-position search, 37% of calls end
        // here; the other 63% spend an average of 19 ray steps priming before they can start.
        balance = onSquare - balance;
        if (balance <= 0) return true;

        SeePrimeRays(to, vacated);

        // The verdict as it stands, flipped by each capture that is actually worth making. It
        // ends up 1 exactly when the side that started the exchange comes out at or above the
        // threshold, which is why the loop can stop at any point and still answer correctly.
        int verdict = 1;
        side = side.Opposite();

        while (true)
        {
            int value = SeeLeastValuableAttacker(to, side, vacated, out int rayIndex, out Square square);
            if (value == int.MaxValue) break;

            // A king may only take the last defender; capturing into a still-attacked square is
            // illegal, so the exchange stops before this capture rather than after it.
            if (value == SeeKingValue &&
                SeeLeastValuableAttacker(to, side.Opposite(), vacated, out _, out _) != int.MaxValue)
                break;

            verdict ^= 1;

            // Flip the balance into the new side-to-move's frame: it wins the piece standing on
            // the square and stands to lose the one it captures with. If that leaves it short even
            // before the reply, it would rather not capture at all, so the exchange ends here.
            balance = value - balance;
            if (balance < verdict) break;

            vacated |= 1UL << square.Index;
            if (rayIndex >= 0) SeeAdvanceRay(rayIndex, to, vacated);

            side = side.Opposite();
        }

        return verdict != 0;
    }

    /// <summary>
    /// Marks the squares the capture itself empties and reports the value of what the mover leaves
    /// standing on the target square for the opponent to capture. Constant time, and deliberately
    /// separate from <see cref="SeePrimeRays"/>: <see cref="SeeGe"/> settles most captures on these
    /// two numbers alone, and priming the rays is the expensive half.
    /// </summary>
    private ulong SeeVacatedSquares(Move captureMove, out int onSquare)
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
        onSquare = moverType == PieceType.King ? SeeKingValue : moverType.MaterialValue();

        return vacated;
    }

    /// <summary>
    /// Primes the eight ray cursors out of the target square, so each one stands on the first
    /// piece that could capture along it. Eight outward scans of up to seven squares — the part
    /// of an exchange that costs real time, and the reason both entry points defer it as long as
    /// they can.
    /// </summary>
    private void SeePrimeRays(Square to, ulong vacated)
    {
        for (int d = 0; d < 8; d++)
        {
            _seeRayStep[d] = 0;
            SeeAdvanceRay(d, to, vacated);
        }
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
    /// Ages the tables at the start of each search within one game. History is halved rather than
    /// discarded: the position about to be searched is two plies from the one just searched, so
    /// most of what the previous search learned about which quiet moves cut is still true — but
    /// only most of it, and halving lets fresh evidence overtake stale evidence within a few
    /// cutoffs instead of competing with a full-strength record of a position that no longer
    /// exists. Killers and counter-moves are kept outright for the same reason.
    ///
    /// This replaces a <see cref="Clear"/> in the per-move setup, which threw away everything the
    /// previous move's search had learned. <see cref="Clear"/> is now only for an unrelated game.
    /// </summary>
    public void NewSearch()
    {
        for (int i = 0; i < 64; i++)
            for (int j = 0; j < 64; j++)
                _history[i, j] /= 2;
    }

    /// <summary>
    /// Clears all ordering heuristics. For an unrelated game (UCI "ucinewgame"), where nothing
    /// learned about the previous game's tree applies.
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
