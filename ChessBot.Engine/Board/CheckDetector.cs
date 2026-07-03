namespace ChessBot.Engine.Board;

using ChessBot.Engine.Types;

/// <summary>
/// Handles king safety detection: check detection, square attack analysis, and pin detection.
/// All methods analyze the position without modifying board state.
/// </summary>
internal class CheckDetector
{
    private readonly Board _board;

    public CheckDetector(Board board)
    {
        _board = board;
    }

    // Precomputed direction/offset tables shared by all attack-detection helpers below.
    // These used to be re-allocated as local array literals on every single call, and
    // IsSquareAttackedBy/IsInCheck are invoked once per pseudo-legal move during legality
    // filtering plus once per search node for check detection — a major GC/allocation
    // hot-path cost. Caching them as static readonly fields makes attack detection allocation-free.
    private static readonly int[] KnightFileOffsets  = { -2, -2, -1, -1, 1, 1, 2, 2 };
    private static readonly int[] KnightRankOffsets  = { -1, 1, -2, 2, -2, 2, -1, 1 };
    private static readonly int[] KingFileOffsets    = { -1, -1, -1, 0, 0, 1, 1, 1 };
    private static readonly int[] KingRankOffsets    = { -1, 0, 1, -1, 1, -1, 0, 1 };
    private static readonly int[] DiagonalFileDirs   = { -1, -1, 1, 1 };
    private static readonly int[] DiagonalRankDirs   = { -1, 1, -1, 1 };
    private static readonly int[] OrthogonalFileDirs = { -1, 1, 0, 0 };
    private static readonly int[] OrthogonalRankDirs = { 0, 0, -1, 1 };

    /// <summary>
    /// Returns true if the specified color's king is in check.
    /// </summary>
    public bool IsInCheck(Color color)
    {
        Square kingSquare = _board.GetKingPosition(color);
        return IsSquareAttackedBy(kingSquare, color.Opposite());
    }

    /// <summary>
    /// Returns true if the specified square is attacked by the opponent (of the given defending color).
    /// </summary>
    public bool IsSquareAttackedBy(Square square, Color attackingColor)
    {
        // Check pawn attacks
        if (IsPawnAttackingSquare(square, attackingColor))
            return true;

        // Check knight attacks
        if (IsKnightAttackingSquare(square, attackingColor))
            return true;

        // Check king attacks
        if (IsKingAttackingSquare(square, attackingColor))
            return true;

        // Check sliding piece attacks (bishops, rooks, queens)
        if (IsSlidingPieceAttackingSquare(square, attackingColor))
            return true;

        return false;
    }

    /// <summary>
    /// Returns true if a pawn of the attacking color can capture on the target square.
    /// </summary>
    private bool IsPawnAttackingSquare(Square targetSquare, Color attackingColor)
    {
        int pawnDirection = attackingColor.PawnDirection();
        int pawnRank = attackingColor == Color.White ? 1 : 6;  // Starting rank
        int captureRank = targetSquare.Rank - pawnDirection;  // Rank where attacking pawn would be

        if (captureRank < 0 || captureRank > 7)
            return false;

        // Check left diagonal
        int leftFile = targetSquare.File - 1;
        if (leftFile >= 0)
        {
            Square leftSquare = new Square(leftFile, captureRank);
            Piece piece = _board.GetPiece(leftSquare);
            if (piece.Color == attackingColor && piece.Type == PieceType.Pawn)
                return true;
        }

        // Check right diagonal
        int rightFile = targetSquare.File + 1;
        if (rightFile < 8)
        {
            Square rightSquare = new Square(rightFile, captureRank);
            Piece piece = _board.GetPiece(rightSquare);
            if (piece.Color == attackingColor && piece.Type == PieceType.Pawn)
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns true if a knight of the attacking color can move to the target square.
    /// </summary>
    private bool IsKnightAttackingSquare(Square targetSquare, Color attackingColor)
    {
        // Knight moves: 8 possible L-shaped moves
        for (int i = 0; i < 8; i++)
        {
            int fromFile = targetSquare.File + KnightFileOffsets[i];
            int fromRank = targetSquare.Rank + KnightRankOffsets[i];

            if (fromFile >= 0 && fromFile < 8 && fromRank >= 0 && fromRank < 8)
            {
                Square knightSquare = new Square(fromFile, fromRank);
                Piece piece = _board.GetPiece(knightSquare);
                if (piece.Color == attackingColor && piece.Type == PieceType.Knight)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if the king of the attacking color is adjacent to the target square.
    /// </summary>
    private bool IsKingAttackingSquare(Square targetSquare, Color attackingColor)
    {
        // King moves: 8 adjacent squares
        for (int i = 0; i < 8; i++)
        {
            int fromFile = targetSquare.File + KingFileOffsets[i];
            int fromRank = targetSquare.Rank + KingRankOffsets[i];

            if (fromFile >= 0 && fromFile < 8 && fromRank >= 0 && fromRank < 8)
            {
                Square kingSquare = new Square(fromFile, fromRank);
                Piece piece = _board.GetPiece(kingSquare);
                if (piece.Color == attackingColor && piece.Type == PieceType.King)
                    return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Returns true if a bishop, rook, or queen of the attacking color can reach the target square.
    /// </summary>
    private bool IsSlidingPieceAttackingSquare(Square targetSquare, Color attackingColor)
    {
        // Diagonal directions (for bishops and queens)
        for (int i = 0; i < 4; i++)
        {
            if (IsLineAttacking(targetSquare, DiagonalFileDirs[i], DiagonalRankDirs[i], attackingColor, true))
                return true;
        }

        // Orthogonal directions (for rooks and queens)
        for (int i = 0; i < 4; i++)
        {
            if (IsLineAttacking(targetSquare, OrthogonalFileDirs[i], OrthogonalRankDirs[i], attackingColor, false))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Returns the material value of the cheapest piece of <paramref name="attackingColor"/>
    /// that attacks <paramref name="square"/>, or <see cref="int.MaxValue"/> if the square
    /// is not attacked at all.  Kings are excluded (using a king as attacker risks exposure).
    /// Pieces are checked cheapest-first so we can return early.
    /// </summary>
    public int GetMinAttackerValue(Square square, Color attackingColor)
    {
        // Pawn (100)
        if (IsPawnAttackingSquare(square, attackingColor))
            return PieceType.Pawn.MaterialValue();

        // Knight (300)
        if (IsKnightAttackingSquare(square, attackingColor))
            return PieceType.Knight.MaterialValue();

        // Diagonal sliders: bishop (300) or queen (900)
        int minDiag = FindMinDiagonalAttackerValue(square, attackingColor);

        // Straight sliders: rook (500) or queen (900)
        int minStraight = FindMinStraightAttackerValue(square, attackingColor);

        int minSlider = Math.Min(minDiag, minStraight);
        return minSlider; // int.MaxValue if no slider attacks
    }

    /// <summary>Returns the minimum material value among diagonal-sliding attackers.</summary>
    private int FindMinDiagonalAttackerValue(Square square, Color attackingColor)
    {
        int min = int.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            int file = square.File + DiagonalFileDirs[i];
            int rank = square.Rank + DiagonalRankDirs[i];

            while (file >= 0 && file < 8 && rank >= 0 && rank < 8)
            {
                Piece p = _board.GetPiece(new Square(file, rank));
                if (!p.IsEmpty)
                {
                    if (p.Color == attackingColor)
                    {
                        if (p.Type == PieceType.Bishop)
                            min = Math.Min(min, PieceType.Bishop.MaterialValue());
                        else if (p.Type == PieceType.Queen)
                            min = Math.Min(min, PieceType.Queen.MaterialValue());
                    }
                    break; // blocked regardless of color
                }
                file += DiagonalFileDirs[i];
                rank += DiagonalRankDirs[i];
            }
        }
        return min;
    }

    /// <summary>Returns the minimum material value among straight-sliding attackers.</summary>
    private int FindMinStraightAttackerValue(Square square, Color attackingColor)
    {
        int min = int.MaxValue;
        for (int i = 0; i < 4; i++)
        {
            int file = square.File + OrthogonalFileDirs[i];
            int rank = square.Rank + OrthogonalRankDirs[i];

            while (file >= 0 && file < 8 && rank >= 0 && rank < 8)
            {
                Piece p = _board.GetPiece(new Square(file, rank));
                if (!p.IsEmpty)
                {
                    if (p.Color == attackingColor)
                    {
                        if (p.Type == PieceType.Rook)
                            min = Math.Min(min, PieceType.Rook.MaterialValue());
                        else if (p.Type == PieceType.Queen)
                            min = Math.Min(min, PieceType.Queen.MaterialValue());
                    }
                    break;
                }
                file += OrthogonalFileDirs[i];
                rank += OrthogonalRankDirs[i];
            }
        }
        return min;
    }

    /// <summary>
    /// Checks if a sliding piece attacks along a specific direction.
    /// </summary>
    private bool IsLineAttacking(Square targetSquare, int fileDir, int rankDir, Color attackingColor, bool isDiagonal)
    {
        int file = targetSquare.File + fileDir;
        int rank = targetSquare.Rank + rankDir;

        while (file >= 0 && file < 8 && rank >= 0 && rank < 8)
        {
            Piece piece = _board.GetPiece(new Square(file, rank));

            if (!piece.IsEmpty)
            {
                // Found a piece; check if it's an attacking piece of the correct type
                if (piece.Color == attackingColor)
                {
                    if (isDiagonal && (piece.Type == PieceType.Bishop || piece.Type == PieceType.Queen))
                        return true;
                    if (!isDiagonal && (piece.Type == PieceType.Rook || piece.Type == PieceType.Queen))
                        return true;
                }
                break;  // Path is blocked
            }

            file += fileDir;
            rank += rankDir;
        }

        return false;
    }
}
