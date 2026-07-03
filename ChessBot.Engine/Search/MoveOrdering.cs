namespace ChessBot.Engine.Search;

using ChessBot.Engine.Types;
using ChessBot.Engine.Board;
using System.Collections.Generic;

/// <summary>
/// Implements move ordering heuristics for efficient alpha-beta pruning.
/// Prioritizes moves by: TT move (999k+) > PV move (1M) > Good captures (MVV-LVA+SEE, 500k) > 
/// Killer moves (200k) > History/Counter moves (100k) > Quiet moves.
/// </summary>
internal class MoveOrdering
{
    private readonly Board _board;

    /// <summary>
    /// Killer moves: best quiet moves at each depth that caused cutoffs.
    /// Indexed by depth (max 64 plies).
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

    // Precomputed direction/offset tables for CheckKnightDefense/CheckSlidingDefense.
    // These used to be allocated as jagged int[][] literals (plus a LINQ .Select() enumerator)
    // on every single call — and these SEE helpers run once per capture scored during move
    // ordering, i.e. once per node. Flat static readonly arrays make this allocation-free.
    private static readonly int[] KnightFileOffsets = { -2, -2, -1, -1, 1, 1, 2, 2 };
    private static readonly int[] KnightRankOffsets = { -1, 1, -2, 2, -2, 2, -1, 1 };
    private static readonly int[] SlideFileOffsets  = { -1, -1, 1, 1, -1, 1, 0, 0 };
    private static readonly int[] SlideRankOffsets  = { -1, 1, -1, 1, 0, 0, -1, 1 };

    public MoveOrdering(Board board)
    {
        _board = board;
        _killerMoves1 = new Move[64];
        _killerMoves2 = new Move[64];
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

        // Captures: scored by MVV-LVA + SEE (good trades first)
        if ((move.MoveType & MoveType.Capture) != 0)
        {
            Piece victim = _board.GetPiece(move.To);
            Piece attacker = _board.GetPiece(move.From);

            // En passant: victim is always a pawn
            if ((move.MoveType & MoveType.EnPassant) != 0)
                victim = new Piece(_board.State.ActiveColor.Opposite(), PieceType.Pawn);

            int mvvScore = MVVLVAScore(victim, attacker);
            int seeScore = StaticExchangeEvaluation(move);

            // Good captures (SEE >= 0) score higher than bad captures (SEE < 0)
            if (seeScore >= 0)
                return 600000 + mvvScore + seeScore;
            else
                return 500000 + mvvScore + seeScore;  // Bad captures later
        }

        // Promotions: very high priority (30x-40x better than quiet moves)
        if ((move.MoveType & MoveType.Promotion) != 0)
        {
            int promotionBonus = move.PromotionType switch
            {
                PieceType.Queen => 4000,
                PieceType.Rook => 3000,
                PieceType.Bishop => 2000,
                PieceType.Knight => 1000,
                _ => 0
            };
            return 300000 + promotionBonus;
        }

        // Killer moves: moves that caused cutoffs at this depth
        if (depth < 64)
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
    /// MVV-LVA (Most Valuable Victim - Least Valuable Attacker) scoring for captures.
    /// Returns a score where higher = better capture.
    /// </summary>
    private int MVVLVAScore(Piece victim, Piece attacker)
    {
        int victimValue = victim.Type.MaterialValue();
        int attackerValue = attacker.Type.MaterialValue();

        // Score = (victim value * 10) - (attacker value)
        // This prioritizes capturing valuable pieces with cheap pieces
        return (victimValue * 10) - attackerValue;
    }

    /// <summary>
    /// Records a killer move (a move that caused a cutoff at a given depth).
    /// Replaces the second killer with the first, and promotes the new move to first.
    /// </summary>
    public void RecordKillerMove(Move move, int depth)
    {
        if (depth >= 64)
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

    /// <summary>
    /// Static Exchange Evaluation (SEE): estimates the value of a capture without search.
    /// Returns positive if capture wins material, negative if it loses material.
    /// This is a fast approximation useful for move ordering and pruning decisions.
    /// </summary>
    private int StaticExchangeEvaluation(Move captureMove)
    {
        Square toSquare = captureMove.To;
        Piece capturedPiece = _board.GetPiece(toSquare);
        Piece movingPiece = _board.GetPiece(captureMove.From);

        // Start with the material gain from the capture
        int gain = capturedPiece.Type.MaterialValue();

        // For speed, we use a simplified SEE: just check if it's defended/attacking.
        // Full SEE would recurse, but this is a good balance for move ordering.
        // A real SEE would be ~100 lines; we use the MVV-LVA as a reasonable heuristic.

        // Check if the captured piece is defended (rough heuristic)
        bool isDefended = IsSquareDefended(toSquare, _board.State.ActiveColor.Opposite());

        // Check if our piece is hanging after the capture
        bool ourPieceHanging = !IsSquareDefended(toSquare, _board.State.ActiveColor);

        if (isDefended && ourPieceHanging)
        {
            // Capture loses material: opponent recaptures and our piece is lost
            gain -= movingPiece.Type.MaterialValue();
        }

        return gain;
    }

    /// <summary>
    /// Simple check: is a square defended by the given color?
    /// This is a fast approximation for SEE; a full implementation would be more complex.
    /// </summary>
    private bool IsSquareDefended(Square square, Color defendingColor)
    {
        // Check all attacking pieces: pawns, knights, bishops/queens (diagonals), rooks/queens (files/ranks), king
        return CheckPawnDefense(square, defendingColor) ||
               CheckKnightDefense(square, defendingColor) ||
               CheckSlidingDefense(square, defendingColor) ||
               CheckKingDefense(square, defendingColor);
    }

    private bool CheckPawnDefense(Square square, Color defendingColor)
    {
        int pawnDir = defendingColor.PawnDirection();
        // Pawns attack diagonally (opposite of their direction)
        int attackFile1 = square.File - 1;
        int attackFile2 = square.File + 1;
        int attackRank = square.Rank - pawnDir;

        if (attackFile1 >= 0 && attackFile1 <= 7 && attackRank >= 0 && attackRank <= 7)
        {
            Piece p1 = _board.GetPiece(new Square(attackFile1, attackRank));
            if (p1.Color == defendingColor && p1.Type == PieceType.Pawn)
                return true;
        }
        if (attackFile2 >= 0 && attackFile2 <= 7 && attackRank >= 0 && attackRank <= 7)
        {
            Piece p2 = _board.GetPiece(new Square(attackFile2, attackRank));
            if (p2.Color == defendingColor && p2.Type == PieceType.Pawn)
                return true;
        }
        return false;
    }

    private bool CheckKnightDefense(Square square, Color defendingColor)
    {
        // Knight moves: 8 possible squares
        for (int i = 0; i < 8; i++)
        {
            int nf = square.File + KnightFileOffsets[i];
            int nr = square.Rank + KnightRankOffsets[i];
            if (nf >= 0 && nf <= 7 && nr >= 0 && nr <= 7)
            {
                Piece knight = _board.GetPiece(new Square(nf, nr));
                if (knight.Color == defendingColor && knight.Type == PieceType.Knight)
                    return true;
            }
        }
        return false;
    }

    private bool CheckSlidingDefense(Square square, Color defendingColor)
    {
        // Check diagonals (bishops, queens) and files/ranks (rooks, queens)
        for (int d = 0; d < 8; d++)
        {
            int df = SlideFileOffsets[d];
            int dr = SlideRankOffsets[d];

            for (int dist = 1; dist < 8; dist++)
            {
                int nf = square.File + df * dist;
                int nr = square.Rank + dr * dist;

                if (nf < 0 || nf > 7 || nr < 0 || nr > 7)
                    break;

                Piece piece = _board.GetPiece(new Square(nf, nr));
                if (piece.Type == PieceType.None)
                    continue;

                if (piece.Color != defendingColor)
                    break;

                // Check if this piece can attack along this direction
                bool isDiagonal = (df != 0 && dr != 0);
                bool isFileRank = (df == 0 || dr == 0);

                if ((isDiagonal && (piece.Type == PieceType.Bishop || piece.Type == PieceType.Queen)) ||
                    (isFileRank && (piece.Type == PieceType.Rook || piece.Type == PieceType.Queen)))
                {
                    return true;
                }
                break; // Stop at first piece found
            }
        }
        return false;
    }

    private bool CheckKingDefense(Square square, Color defendingColor)
    {
        Square kingPos = _board.GetKingPosition(defendingColor);
        return Math.Abs(kingPos.File - square.File) <= 1 && Math.Abs(kingPos.Rank - square.Rank) <= 1;
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
