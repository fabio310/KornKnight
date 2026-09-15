namespace ChessBot.Engine.Board;

using ChessBot.Engine.Types;
using System.Collections.Generic;

/// <summary>
/// Generates fully legal moves for the side to move in a single pass, without a separate
/// pseudo-legal generation + per-move make/unmake legality filter. Once per node it computes the
/// king square, the set of checkers, the pinned pieces with their pin rays, and the squares the
/// enemy attacks; every move is then produced already knowing it is legal:
/// <list type="bullet">
/// <item>Double check =&gt; only king moves.</item>
/// <item>Single check =&gt; non-king moves must land on the capture/block mask.</item>
/// <item>Pinned pieces =&gt; restricted to their pin ray (a pinned knight cannot move at all).</item>
/// <item>King moves =&gt; the target must not be attacked by the enemy (king x-rayed out so it
/// cannot retreat along a checking ray).</item>
/// <item>Castling =&gt; both the transit and target squares must be empty and unattacked.</item>
/// <item>En passant =&gt; the pin/check masks plus a special horizontal discovered-check test.</item>
/// </list>
/// </summary>
internal class MoveGenerator
{
    /// <summary>
    /// Upper bound on legal moves in any reachable chess position (the proven maximum is 218).
    /// Callers of the zero-allocation Move[] APIs below must supply buffers at least this long.
    /// </summary>
    public const int MaxMoves = 256;

    private readonly Board _board;

    // Scratch buffer backing the List<Move>-based convenience API. Hot-path search code should
    // use the Move[]+count overloads instead; this exists only for external/test callers
    // (e.g. ChessEngine.GetLegalMoves()) that need a List<Move>.
    private readonly Move[] _legalMovesScratch = new Move[MaxMoves];

    // Pre-allocated buffers for fast piece iteration — avoids IEnumerable heap allocations.
    // A side can have at most 16 pieces (1 king, 8 pawns, and up to 7 others after promotion).
    private readonly (Square sq, Piece p)[] _friendlyPieces = new (Square, Piece)[16];
    private readonly (Square sq, Piece p)[] _enemyPieces    = new (Square, Piece)[16];

    // ── Per-node attack data (recomputed once by ComputeAttackData) ─────────────────────────────
    // The active/enemy colors and the active king's square for the current generation pass.
    private Color  _us;
    private Color  _them;
    private Square _kingSquare;

    // Bitboard (square index 0-63 => bit) of every square attacked by the enemy, computed with the
    // friendly king treated as transparent so a checking slider still covers the square behind the
    // king (which the king therefore may not step onto).
    private ulong _enemyAttacks;

    // Number of pieces giving check to the active king (capped at 2) and the set of squares that
    // resolve a single check (squares between the king and a sliding checker plus the checker's own
    // square; just the checker's square for a pawn/knight). When not in check the mask is all-ones;
    // in double check it is unused because only the king may move.
    private int   _checkerCount;
    private ulong _checkMask;

    // Bitboard of pinned friendly pieces plus, for each pinned square, the ray it may move along
    // (from the king exclusive to the pinning slider inclusive). _pinRay[i] is only meaningful when
    // bit i of _pinned is set, so the array never needs clearing between nodes.
    private ulong _pinned;
    private readonly ulong[] _pinRay = new ulong[64];

    // Destination for the current generation pass and the running count of moves written.
    private Move[] _moves = null!;
    private int    _moveCount;

    // Precomputed direction/offset tables shared by the generators below. Caching them as static
    // readonly fields (instead of re-allocating array literals per call) keeps generation
    // allocation-free on the search hot path.
    private static readonly int[] KnightFileOffsets = { -2, -2, -1, -1, 1, 1, 2, 2 };
    private static readonly int[] KnightRankOffsets = { -1, 1, -2, 2, -2, 2, -1, 1 };
    private static readonly int[] KingFileOffsets   = { -1, -1, -1, 0, 0, 1, 1, 1 };
    private static readonly int[] KingRankOffsets   = { -1, 0, 1, -1, 1, -1, 0, 1 };

    // Sliding directions as (file, rank) deltas. Indices 0-3 are diagonal (bishop/queen), indices
    // 4-7 are orthogonal (rook/queen); the split lets checker/pin detection know which enemy slider
    // types are relevant for a given ray.
    private static readonly int[] SlideFileDirs = { -1, -1, 1, 1, -1, 1, 0, 0 };
    private static readonly int[] SlideRankDirs = { -1, 1, -1, 1, 0, 0, -1, 1 };
    private const int DiagonalDirStart   = 0;
    private const int OrthogonalDirStart = 4;

    public MoveGenerator(Board board)
    {
        _board = board;
    }

    /// <summary>
    /// Generates all legal moves for the side whose turn it is to move.
    /// </summary>
    public List<Move> GenerateLegalMoves()
    {
        var output = new List<Move>();
        GenerateLegalMovesInto(output);
        return output;
    }

    /// <summary>
    /// Generates all legal moves into a caller-supplied list (which is cleared first).
    /// Convenience wrapper over the zero-allocation Move[] API below, kept for external callers
    /// and tests (e.g. ChessEngine.GetLegalMoves()). Search hot-path code should call
    /// GenerateLegalMovesInto(Move[], out int) directly instead.
    /// </summary>
    public void GenerateLegalMovesInto(List<Move> output)
    {
        GenerateLegalMovesInto(_legalMovesScratch, out int count);

        output.Clear();
        for (int i = 0; i < count; i++)
            output.Add(_legalMovesScratch[i]);
    }

    /// <summary>
    /// Generates all legal moves into a caller-supplied fixed-size buffer (zero heap allocation).
    /// The buffer must be at least <see cref="MaxMoves"/> long. This is the hot-path API used by
    /// the search's root move handling, negamax, and quiescence (when in check).
    /// </summary>
    public void GenerateLegalMovesInto(Move[] buffer, out int count)
    {
        count = GenerateLegal(buffer, tacticalOnly: false);
    }

    /// <summary>
    /// Generates only tactical legal moves — captures, en passant, promotions, and
    /// promotion-captures — into a caller-supplied fixed-size buffer (zero heap allocation).
    /// Quiet moves, including castling, are never produced. Uses exactly the same legality data
    /// (checkers, pins, enemy attacks) as the full generator, so every move returned is legal.
    /// Intended for quiescence search when not in check, where quiet moves can never raise alpha
    /// above stand-pat.
    /// </summary>
    public void GenerateLegalTacticalMovesInto(Move[] buffer, out int count)
    {
        count = GenerateLegal(buffer, tacticalOnly: true);
    }

    /// <summary>
    /// Core legal move generator. Computes the per-node attack data once (see
    /// <see cref="ComputeAttackData"/>), then emits legal moves directly into
    /// <paramref name="buffer"/> and returns the count. When <paramref name="tacticalOnly"/> is
    /// true, only captures/en passant/promotions are produced (quiet moves and castling skipped).
    /// </summary>
    private int GenerateLegal(Move[] buffer, bool tacticalOnly)
    {
        _moves      = buffer;
        _moveCount  = 0;
        _us         = _board.ActiveColor;
        _them       = _us.Opposite();
        _kingSquare = _board.GetKingPosition(_us);

        ComputeAttackData();

        // King moves are always available and are the ONLY option in double check.
        GenerateKingMoves(_kingSquare, tacticalOnly);

        if (_checkerCount < 2)
        {
            int pieceCount = _board.GetPiecesOf(_us, _friendlyPieces);
            for (int i = 0; i < pieceCount; i++)
            {
                var (square, piece) = _friendlyPieces[i];
                switch (piece.Type)
                {
                    case PieceType.Pawn:   GeneratePawnMoves(square, tacticalOnly); break;
                    case PieceType.Knight: GenerateKnightMoves(square, tacticalOnly); break;
                    case PieceType.Bishop: GenerateSliderMoves(square, tacticalOnly, DiagonalDirStart, 4); break;
                    case PieceType.Rook:   GenerateSliderMoves(square, tacticalOnly, OrthogonalDirStart, 4); break;
                    case PieceType.Queen:  GenerateSliderMoves(square, tacticalOnly, 0, 8); break;
                }
            }

            // Castling is a quiet king move and is never legal while in check.
            if (!tacticalOnly && _checkerCount == 0)
                GenerateCastlingMoves();
        }

        return _moveCount;
    }

    /// <summary>Appends a legal move to the current output buffer.</summary>
    private void AddMove(Move move) => _moves[_moveCount++] = move;

    /// <summary>Returns true if <paramref name="square"/> is attacked by the enemy.</summary>
    private bool IsAttacked(Square square) => ((_enemyAttacks >> square.Index) & 1UL) != 0;

    // ── Does this move give check? ───────────────────────────────────────────────────────────────

    // Scratch state describing the position AFTER the move GivesCheck is being asked about, so the
    // ray walks below can read it without the board having been touched. Fields rather than
    // parameters because they are threaded through five helpers; a MoveGenerator belongs to one
    // searcher and the search is single-threaded, which is the same assumption _moves already makes.
    private ulong  _afterVacated;      // squares the move empties
    private Square _afterArrivalSquare; private Piece _afterArrival;   // the piece that lands
    private Square _afterRookSquare;    private Piece _afterRook;      // the castling rook, if any
    private bool   _afterHasRook;

    /// <summary>
    /// Whether <paramref name="move"/> — a legal move for the side to move — leaves the enemy king
    /// in check. Answers without touching the board.
    ///
    /// What it replaces is MakeMove + IsInCheck + UndoMove, paid once per move a pruning rule wants
    /// to drop. IsInCheck alone walks all eight rays out of the king plus three pattern tests, and
    /// make/unmake maintains the hash, the piece lists, the pawn and rook bitboards and the
    /// castling rights on the way in and undoes all of it on the way out. This asks two questions
    /// instead, each of which walks at most one ray: does the arriving piece see the enemy king
    /// from where it lands, and does the square it left open a line that something else already
    /// stood on.
    ///
    /// The second question is the same ray scan <see cref="DetectSlidingCheckersAndPins"/> runs for
    /// our own pins, run from the other king. It is not cached per node: this is called from inside
    /// a move loop whose recursive children generate moves through the same MoveGenerator instance,
    /// so anything cached here would be a sibling's data by the second call.
    /// </summary>
    public bool GivesCheck(Move move)
    {
        Color  us    = _board.State.ActiveColor;
        Square enemyKing = _board.GetKingPosition(us.Opposite());

        Piece     mover     = _board.GetPiece(move.From);
        bool      promotion = (move.MoveType & MoveType.Promotion) != 0;
        PieceType arriving  = promotion ? move.PromotionType : mover.Type;
        bool      castling  = (move.MoveType & MoveType.Castling) != 0;

        _afterVacated       = 1UL << move.From.Index;
        _afterArrivalSquare = move.To;
        _afterArrival       = new Piece(us, arriving);
        _afterHasRook       = castling;

        // En passant empties a second square: the captured pawn stands beside the destination,
        // not on it. That square is as capable of having been blocking a line as the origin is.
        if ((move.MoveType & MoveType.EnPassant) != 0)
            _afterVacated |= 1UL << new Square(move.To.File, move.From.Rank).Index;

        if (castling)
        {
            bool kingSide = move.To.File > move.From.File;
            _afterVacated     |= 1UL << new Square(kingSide ? 7 : 0, move.From.Rank).Index;
            _afterRookSquare   = new Square(kingSide ? 5 : 3, move.From.Rank);
            _afterRook         = new Piece(us, PieceType.Rook);
        }

        // 1) Direct check. A castling king never gives one; its rook is the piece that can, from
        //    the square it lands on rather than from the king's destination.
        if (castling)
        {
            if (Sees(PieceType.Rook, us, _afterRookSquare, enemyKing)) return true;
        }
        else if (Sees(arriving, us, move.To, enemyKing))
        {
            return true;
        }

        // 2) Discovered check. Every square the move empties is a candidate: the origin, the pawn
        //    an en passant capture removes, and the rook's origin when castling.
        ulong candidates = _afterVacated;
        while (candidates != 0)
        {
            int index = System.Numerics.BitOperations.TrailingZeroCount(candidates);
            candidates &= candidates - 1;

            if (OpensALineTo(new Square(index), enemyKing, us)) return true;
        }

        return false;
    }

    /// <summary>
    /// The piece standing on <paramref name="square"/> after the move being judged. Arrivals win
    /// over departures; the two never name the same square in standard chess.
    /// </summary>
    private Piece PieceAfterMove(Square square)
    {
        if (square == _afterArrivalSquare) return _afterArrival;
        if (_afterHasRook && square == _afterRookSquare) return _afterRook;

        return ((_afterVacated >> square.Index) & 1UL) != 0 ? Piece.Empty : _board.GetPiece(square);
    }

    /// <summary>
    /// Whether a piece of <paramref name="type"/> and <paramref name="color"/> standing on
    /// <paramref name="from"/> attacks <paramref name="target"/> in the position after the move.
    /// </summary>
    private bool Sees(PieceType type, Color color, Square from, Square target)
    {
        int fileDelta = target.File - from.File;
        int rankDelta = target.Rank - from.Rank;

        switch (type)
        {
            case PieceType.Pawn:
                return rankDelta == color.PawnDirection() && (fileDelta == 1 || fileDelta == -1);

            case PieceType.Knight:
                int af = Math.Abs(fileDelta), ar = Math.Abs(rankDelta);
                return (af == 1 && ar == 2) || (af == 2 && ar == 1);

            case PieceType.King:
                return Math.Abs(fileDelta) <= 1 && Math.Abs(rankDelta) <= 1
                       && (fileDelta != 0 || rankDelta != 0);

            case PieceType.Bishop:
                return Math.Abs(fileDelta) == Math.Abs(rankDelta) && fileDelta != 0
                       && PathIsClear(from, target);

            case PieceType.Rook:
                return (fileDelta == 0) != (rankDelta == 0) && PathIsClear(from, target);

            case PieceType.Queen:
                bool onALine = (Math.Abs(fileDelta) == Math.Abs(rankDelta) && fileDelta != 0)
                            || ((fileDelta == 0) != (rankDelta == 0));
                return onALine && PathIsClear(from, target);

            default:
                return false;
        }
    }

    /// <summary>
    /// Whether every square strictly between two aligned squares is empty after the move. The
    /// caller has already established that they are aligned.
    /// </summary>
    private bool PathIsClear(Square from, Square target)
    {
        int df = Math.Sign(target.File - from.File);
        int dr = Math.Sign(target.Rank - from.Rank);

        int f = from.File + df, r = from.Rank + dr;
        while (f != target.File || r != target.Rank)
        {
            if (!PieceAfterMove(new Square(f, r)).IsEmpty) return false;
            f += df;
            r += dr;
        }

        return true;
    }

    /// <summary>
    /// Whether a square the move empties lies on a clear line out of the enemy king with one of our
    /// sliders of the matching kind standing behind it — that is, whether emptying it discovers a
    /// check.
    ///
    /// Walking outward from the king rather than inward from the slider is what makes this one ray
    /// instead of eight: the direction is fixed by the two squares, so there is only ever one line
    /// to look along.
    /// </summary>
    private bool OpensALineTo(Square vacated, Square enemyKing, Color us)
    {
        int fileDelta = vacated.File - enemyKing.File;
        int rankDelta = vacated.Rank - enemyKing.Rank;

        bool diagonal   = Math.Abs(fileDelta) == Math.Abs(rankDelta) && fileDelta != 0;
        bool orthogonal = (fileDelta == 0) != (rankDelta == 0);
        if (!diagonal && !orthogonal) return false;

        int df = Math.Sign(fileDelta);
        int dr = Math.Sign(rankDelta);

        // From the king up to and including the vacated square: all of it must be empty afterwards.
        // "Including" matters — a move can land back on the line it left, and castling empties a
        // square the rook then refills.
        int f = enemyKing.File + df, r = enemyKing.Rank + dr;
        while (true)
        {
            if (!PieceAfterMove(new Square(f, r)).IsEmpty) return false;
            if (f == vacated.File && r == vacated.Rank) break;
            f += df;
            r += dr;
        }

        // Beyond it, the first piece decides.
        f += df;
        r += dr;
        while (f >= 0 && f < 8 && r >= 0 && r < 8)
        {
            Piece piece = PieceAfterMove(new Square(f, r));
            if (!piece.IsEmpty)
            {
                if (piece.Color != us) return false;

                return diagonal
                    ? piece.Type is PieceType.Bishop or PieceType.Queen
                    : piece.Type is PieceType.Rook   or PieceType.Queen;
            }

            f += df;
            r += dr;
        }

        return false;
    }

    // ── Per-node attack / check / pin analysis ───────────────────────────────────────────────────

    /// <summary>
    /// Computes, once per node, everything the generators need to emit only legal moves:
    /// the enemy attack map (with the friendly king x-rayed out), the checker count and the
    /// single-check resolution mask, and the pinned pieces together with their pin rays.
    /// </summary>
    private void ComputeAttackData()
    {
        _enemyAttacks = 0UL;
        _checkMask    = 0UL;
        _pinned       = 0UL;
        _checkerCount = 0;

        // 1) Enemy attack map (king transparent) — drives king-move and castling legality.
        int enemyCount = _board.GetPiecesOf(_them, _enemyPieces);
        for (int i = 0; i < enemyCount; i++)
        {
            var (square, piece) = _enemyPieces[i];
            switch (piece.Type)
            {
                case PieceType.Pawn:   AddPawnAttacks(square);   break;
                case PieceType.Knight: AddKnightAttacks(square); break;
                case PieceType.King:   AddKingAttacks(square);   break;
                case PieceType.Bishop: AddSliderAttacks(square, DiagonalDirStart, 4); break;
                case PieceType.Rook:   AddSliderAttacks(square, OrthogonalDirStart, 4); break;
                case PieceType.Queen:  AddSliderAttacks(square, 0, 8); break;
            }
        }

        // 2) Non-sliding checkers (pawns, knights) — each contributes only its own square.
        DetectPawnAndKnightCheckers();

        // 3) Sliding checkers and pins via the 8 rays radiating from the king.
        DetectSlidingCheckersAndPins();

        // No checkers => every square trivially "resolves" check, so allow all targets.
        if (_checkerCount == 0)
            _checkMask = ~0UL;
    }

    /// <summary>Marks the two squares an enemy pawn on <paramref name="from"/> attacks.</summary>
    private void AddPawnAttacks(Square from)
    {
        int rank = from.Rank + _them.PawnDirection();
        if (rank < 0 || rank > 7)
            return;

        if (from.File - 1 >= 0) _enemyAttacks |= 1UL << new Square(from.File - 1, rank).Index;
        if (from.File + 1 <= 7) _enemyAttacks |= 1UL << new Square(from.File + 1, rank).Index;
    }

    /// <summary>Marks the squares an enemy knight on <paramref name="from"/> attacks.</summary>
    private void AddKnightAttacks(Square from)
    {
        for (int i = 0; i < 8; i++)
        {
            int f = from.File + KnightFileOffsets[i];
            int r = from.Rank + KnightRankOffsets[i];
            if (f >= 0 && f < 8 && r >= 0 && r < 8)
                _enemyAttacks |= 1UL << new Square(f, r).Index;
        }
    }

    /// <summary>Marks the squares an enemy king on <paramref name="from"/> attacks.</summary>
    private void AddKingAttacks(Square from)
    {
        for (int i = 0; i < 8; i++)
        {
            int f = from.File + KingFileOffsets[i];
            int r = from.Rank + KingRankOffsets[i];
            if (f >= 0 && f < 8 && r >= 0 && r < 8)
                _enemyAttacks |= 1UL << new Square(f, r).Index;
        }
    }

    /// <summary>
    /// Marks the squares an enemy sliding piece on <paramref name="from"/> attacks along the given
    /// directions, treating the friendly king as transparent so the square behind the king is also
    /// covered (preventing an illegal king retreat along the checking ray).
    /// </summary>
    private void AddSliderAttacks(Square from, int dirStart, int dirCount)
    {
        for (int d = dirStart; d < dirStart + dirCount; d++)
        {
            int df = SlideFileDirs[d];
            int dr = SlideRankDirs[d];
            int f = from.File + df;
            int r = from.Rank + dr;

            while (f >= 0 && f < 8 && r >= 0 && r < 8)
            {
                Square square = new Square(f, r);
                _enemyAttacks |= 1UL << square.Index;

                Piece piece = _board.GetPiece(square);
                if (!piece.IsEmpty)
                {
                    // The friendly king does not block the ray, so it cannot step back along it.
                    if (!(piece.Type == PieceType.King && piece.Color == _us))
                        break;
                }

                f += df;
                r += dr;
            }
        }
    }

    /// <summary>Detects enemy pawn and knight checkers (each contributes only its own square).</summary>
    private void DetectPawnAndKnightCheckers()
    {
        int kf = _kingSquare.File;
        int kr = _kingSquare.Rank;

        // Enemy pawn checkers: a pawn attacks the king from the squares diagonally in front of the
        // king relative to the enemy's advance direction.
        int pawnRank = kr - _them.PawnDirection();
        if (pawnRank >= 0 && pawnRank <= 7)
        {
            for (int df = -1; df <= 1; df += 2)
            {
                int pf = kf + df;
                if (pf < 0 || pf > 7)
                    continue;

                Square square = new Square(pf, pawnRank);
                Piece piece = _board.GetPiece(square);
                if (piece.Color == _them && piece.Type == PieceType.Pawn)
                    AddChecker(1UL << square.Index);
            }
        }

        // Enemy knight checkers.
        for (int i = 0; i < 8; i++)
        {
            int f = kf + KnightFileOffsets[i];
            int r = kr + KnightRankOffsets[i];
            if (f < 0 || f > 7 || r < 0 || r > 7)
                continue;

            Square square = new Square(f, r);
            Piece piece = _board.GetPiece(square);
            if (piece.Color == _them && piece.Type == PieceType.Knight)
                AddChecker(1UL << square.Index);
        }
    }

    /// <summary>
    /// Scans the 8 rays out of the king to find sliding checkers (first piece along the ray is an
    /// enemy slider of the matching type) and pins (first piece is friendly, and the next piece is
    /// an enemy slider of the matching type). Records the check-resolution mask and the pin rays.
    /// </summary>
    private void DetectSlidingCheckersAndPins()
    {
        int kf = _kingSquare.File;
        int kr = _kingSquare.Rank;

        for (int d = 0; d < 8; d++)
        {
            bool diagonal = d < OrthogonalDirStart;
            int df = SlideFileDirs[d];
            int dr = SlideRankDirs[d];

            int f = kf + df;
            int r = kr + dr;
            int friendlyIdx = -1;  // square index of the first friendly piece met (pin candidate)
            ulong rayBits = 0UL;   // squares from the king (exclusive) up to the current square

            while (f >= 0 && f < 8 && r >= 0 && r < 8)
            {
                Square square = new Square(f, r);
                int idx = square.Index;
                rayBits |= 1UL << idx;

                Piece piece = _board.GetPiece(square);
                if (!piece.IsEmpty)
                {
                    if (piece.Color == _us)
                    {
                        // First friendly piece is a pin candidate; a second one rules out a pin.
                        if (friendlyIdx == -1)
                            friendlyIdx = idx;
                        else
                            break;
                    }
                    else
                    {
                        bool matches = diagonal
                            ? (piece.Type == PieceType.Bishop || piece.Type == PieceType.Queen)
                            : (piece.Type == PieceType.Rook   || piece.Type == PieceType.Queen);

                        if (matches)
                        {
                            if (friendlyIdx == -1)
                            {
                                // Direct slider check: block on any ray square or capture the checker.
                                AddChecker(rayBits);
                            }
                            else
                            {
                                // The friendly piece is pinned; it may move king-exclusive..pinner-inclusive.
                                _pinned |= 1UL << friendlyIdx;
                                _pinRay[friendlyIdx] = rayBits;
                            }
                        }

                        break; // any enemy piece blocks the ray
                    }
                }

                f += df;
                r += dr;
            }
        }
    }

    /// <summary>Records a checker and its check-resolution mask (mask kept only for single check).</summary>
    private void AddChecker(ulong resolveMask)
    {
        _checkerCount++;
        if (_checkerCount == 1)
            _checkMask = resolveMask;
    }

    // ── Per-piece legal move generation ──────────────────────────────────────────────────────────

    /// <summary>Generates legal king moves: any adjacent square not occupied by us and not attacked.</summary>
    private void GenerateKingMoves(Square from, bool tacticalOnly)
    {
        int kf = from.File;
        int kr = from.Rank;

        for (int i = 0; i < 8; i++)
        {
            int tf = kf + KingFileOffsets[i];
            int tr = kr + KingRankOffsets[i];
            if (tf < 0 || tf > 7 || tr < 0 || tr > 7)
                continue;

            Square to = new Square(tf, tr);

            // The king may not move onto an enemy-attacked square (king already x-rayed out).
            if (IsAttacked(to))
                continue;

            Piece target = _board.GetPiece(to);
            if (target.IsEmpty)
            {
                if (!tacticalOnly)
                    AddMove(new Move(from, to, MoveType.Quiet));
            }
            else if (target.Color == _them)
            {
                AddMove(new Move(from, to, MoveType.Capture));
            }
            // else: own piece — cannot move there.
        }
    }

    /// <summary>
    /// Generates legal knight moves. A pinned knight can never move (no knight move stays on a
    /// straight line); otherwise every target square must lie on the check-resolution mask.
    /// </summary>
    private void GenerateKnightMoves(Square from, bool tacticalOnly)
    {
        if (((_pinned >> from.Index) & 1UL) != 0)
            return;

        for (int i = 0; i < 8; i++)
        {
            int tf = from.File + KnightFileOffsets[i];
            int tr = from.Rank + KnightRankOffsets[i];
            if (tf < 0 || tf > 7 || tr < 0 || tr > 7)
                continue;

            Square to = new Square(tf, tr);
            if (((_checkMask >> to.Index) & 1UL) == 0)
                continue; // must resolve check

            Piece target = _board.GetPiece(to);
            if (target.IsEmpty)
            {
                if (!tacticalOnly)
                    AddMove(new Move(from, to, MoveType.Quiet));
            }
            else if (target.Color == _them)
            {
                AddMove(new Move(from, to, MoveType.Capture));
            }
        }
    }

    /// <summary>
    /// Generates legal sliding-piece moves (bishop/rook/queen) along the given directions. Targets
    /// are gated by the check-resolution mask intersected with the pin ray (if the piece is pinned);
    /// the ray still walks through empty squares so blockers stop it correctly.
    /// </summary>
    private void GenerateSliderMoves(Square from, bool tacticalOnly, int dirStart, int dirCount)
    {
        ulong allowed = _checkMask;
        if (((_pinned >> from.Index) & 1UL) != 0)
            allowed &= _pinRay[from.Index];

        for (int d = dirStart; d < dirStart + dirCount; d++)
        {
            int df = SlideFileDirs[d];
            int dr = SlideRankDirs[d];
            int f = from.File + df;
            int r = from.Rank + dr;

            while (f >= 0 && f < 8 && r >= 0 && r < 8)
            {
                Square to = new Square(f, r);
                bool allow = ((allowed >> to.Index) & 1UL) != 0;
                Piece target = _board.GetPiece(to);

                if (target.IsEmpty)
                {
                    if (allow && !tacticalOnly)
                        AddMove(new Move(from, to, MoveType.Quiet));
                }
                else
                {
                    if (target.Color == _them && allow)
                        AddMove(new Move(from, to, MoveType.Capture));
                    break; // blocked by a piece of either color
                }

                f += df;
                r += dr;
            }
        }
    }

    /// <summary>
    /// Generates legal pawn moves: single/double pushes, captures, promotions, promotion-captures,
    /// and en passant. Pushes and captures must land on the check-resolution mask intersected with
    /// the pin ray; en passant additionally goes through <see cref="IsEnPassantLegal"/>.
    /// </summary>
    private void GeneratePawnMoves(Square from, bool tacticalOnly)
    {
        int dir       = _us.PawnDirection();
        int startRank = _us == Color.White ? 1 : 6;
        int promoRank = _us == Color.White ? 7 : 0;

        ulong pinMask = ((_pinned >> from.Index) & 1UL) != 0 ? _pinRay[from.Index] : ~0UL;
        ulong allowed = _checkMask & pinMask;

        int ff = from.File;
        int fr = from.Rank;

        // ── Pushes ───────────────────────────────────────────────────────────────────────────────
        int oneRank = fr + dir;
        if (oneRank >= 0 && oneRank <= 7)
        {
            Square one = new Square(ff, oneRank);
            if (_board.GetPiece(one).IsEmpty)
            {
                bool oneAllowed = ((allowed >> one.Index) & 1UL) != 0;

                if (oneRank == promoRank)
                {
                    if (oneAllowed)
                    {
                        AddMove(new Move(from, one, MoveType.Promotion, PieceType.Queen));
                        AddMove(new Move(from, one, MoveType.Promotion, PieceType.Rook));
                        AddMove(new Move(from, one, MoveType.Promotion, PieceType.Bishop));
                        AddMove(new Move(from, one, MoveType.Promotion, PieceType.Knight));
                    }
                }
                else if (!tacticalOnly && oneAllowed)
                {
                    AddMove(new Move(from, one, MoveType.Quiet));
                }

                // Double push (quiet only) — the intermediate square is already confirmed empty.
                if (!tacticalOnly && fr == startRank)
                {
                    int twoRank = fr + 2 * dir;
                    Square two = new Square(ff, twoRank);
                    if (_board.GetPiece(two).IsEmpty && ((allowed >> two.Index) & 1UL) != 0)
                        AddMove(new Move(from, two, MoveType.DoublePawnPush));
                }
            }
        }

        // ── Captures (normal, promotion-captures, en passant) ─────────────────────────────────────
        for (int df = -1; df <= 1; df += 2)
        {
            int cf = ff + df;
            int cr = fr + dir;
            if (cf < 0 || cf > 7 || cr < 0 || cr > 7)
                continue;

            Square to = new Square(cf, cr);
            Piece target = _board.GetPiece(to);

            if (!target.IsEmpty && target.Color == _them && ((allowed >> to.Index) & 1UL) != 0)
            {
                if (cr == promoRank)
                {
                    AddMove(new Move(from, to, MoveType.Capture | MoveType.Promotion, PieceType.Queen));
                    AddMove(new Move(from, to, MoveType.Capture | MoveType.Promotion, PieceType.Rook));
                    AddMove(new Move(from, to, MoveType.Capture | MoveType.Promotion, PieceType.Bishop));
                    AddMove(new Move(from, to, MoveType.Capture | MoveType.Promotion, PieceType.Knight));
                }
                else
                {
                    AddMove(new Move(from, to, MoveType.Capture));
                }
            }

            // En passant: the destination is the empty target square; the captured pawn sits beside
            // us on the moving pawn's own rank. Legality needs special handling (see below).
            if (_board.State.HasEnPassant && _board.EnPassantTarget == to)
            {
                Square captured = new Square(cf, fr);
                if (IsEnPassantLegal(from, to, captured, pinMask))
                    AddMove(new Move(from, to, MoveType.EnPassant));
            }
        }
    }

    /// <summary>
    /// Legality test for an en passant capture. The moving pawn's pin ray and the check mask are
    /// applied first, then the special horizontal case: because en passant removes two pawns from
    /// the same rank at once, an enemy rook/queen on that rank can deliver a discovered check that
    /// ordinary single-blocker pin detection cannot see.
    /// </summary>
    private bool IsEnPassantLegal(Square from, Square to, Square captured, ulong pinMask)
    {
        // The moving pawn must remain on its pin ray (if pinned).
        if (((pinMask >> to.Index) & 1UL) == 0)
            return false;

        // When in check, en passant is legal only if it captures the checking pawn (its square is
        // on the mask) or the landing square blocks the check.
        if (_checkerCount == 1 &&
            ((_checkMask >> to.Index)       & 1UL) == 0 &&
            ((_checkMask >> captured.Index) & 1UL) == 0)
            return false;

        // Special horizontal discovered-check test (only possible when the king shares the rank that
        // both the moving pawn and the captured pawn vacate).
        if (EnPassantExposesKingOnRank(from, captured))
            return false;

        return true;
    }

    /// <summary>
    /// Returns true if performing an en passant capture would expose the king to an enemy rook or
    /// queen along the king's rank once both pawns leave it. Both vacated squares are treated as
    /// empty; the landing square is on a different rank and so is irrelevant here.
    /// </summary>
    private bool EnPassantExposesKingOnRank(Square from, Square captured)
    {
        int kr = _kingSquare.Rank;
        if (from.Rank != kr)
            return false; // the two pawns are not on the king's rank

        int kf = _kingSquare.File;
        for (int df = -1; df <= 1; df += 2)
        {
            int f = kf + df;
            while (f >= 0 && f <= 7)
            {
                Square square = new Square(f, kr);
                if (square.Index == from.Index || square.Index == captured.Index)
                {
                    f += df;
                    continue; // both pawns are gone after the capture
                }

                Piece piece = _board.GetPiece(square);
                if (!piece.IsEmpty)
                {
                    if (piece.Color == _them && (piece.Type == PieceType.Rook || piece.Type == PieceType.Queen))
                        return true; // rook/queen now sees the king along the open rank
                    break;           // any other piece blocks the rank
                }

                f += df;
            }
        }

        return false;
    }

    /// <summary>
    /// Generates legal castling moves. Only called when the king is not in check. Requires the
    /// squares between king and rook to be empty and both the transit and the target square the
    /// king passes over/onto to be free of enemy attack.
    /// </summary>
    private void GenerateCastlingMoves()
    {
        int kingRank = _us == Color.White ? 0 : 7;
        Square kingSquare = new Square(4, kingRank);

        if (_board.GetPiece(kingSquare).Type != PieceType.King)
            return; // king not on its home square

        // King-side: f and g empty; neither f (transit) nor g (target) attacked.
        if (_board.CastlingRights.CanCastle(_us, true))
        {
            Piece rook = _board.GetPiece(new Square(7, kingRank));
            if (rook.Type == PieceType.Rook && rook.Color == _us)
            {
                Square f = new Square(5, kingRank);
                Square g = new Square(6, kingRank);
                if (_board.GetPiece(f).IsEmpty && _board.GetPiece(g).IsEmpty &&
                    !IsAttacked(f) && !IsAttacked(g))
                {
                    AddMove(new Move(kingSquare, g, MoveType.Castling));
                }
            }
        }

        // Queen-side: b, c and d empty; neither d (transit) nor c (target) attacked.
        if (_board.CastlingRights.CanCastle(_us, false))
        {
            Piece rook = _board.GetPiece(new Square(0, kingRank));
            if (rook.Type == PieceType.Rook && rook.Color == _us)
            {
                Square b = new Square(1, kingRank);
                Square c = new Square(2, kingRank);
                Square d = new Square(3, kingRank);
                if (_board.GetPiece(b).IsEmpty && _board.GetPiece(c).IsEmpty && _board.GetPiece(d).IsEmpty &&
                    !IsAttacked(d) && !IsAttacked(c))
                {
                    AddMove(new Move(kingSquare, c, MoveType.Castling));
                }
            }
        }
    }
}
