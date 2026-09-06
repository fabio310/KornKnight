namespace ChessBot.Engine.Evaluation;

using ChessBot.Engine.Types;

/// <summary>
/// Single source of truth for Piece-Square Table (PST) values, shared by the static
/// <see cref="Evaluator"/> (full scan) and <see cref="ChessBot.Engine.Board.Board"/>'s incremental
/// make/unmake eval state. Keeping one copy guarantees the incrementally maintained PST term is
/// byte-for-byte identical to a from-scratch scan, so <see cref="Evaluator.EvaluateFast"/> matches
/// <see cref="Evaluator.Evaluate"/> exactly.
///
/// Tables are from White's perspective: index [rank][file] with rank 0 = rank 1 (White's side),
/// rank 7 = rank 8 (Black's side), file 0 = a-file.
/// </summary>
internal static class PieceSquareTables
{
    // Pawn PST (pawns advance forward, prefer central files)
    private static readonly int[][] Pawn =
    {
        new[] { 0, 0, 0, 0, 0, 0, 0, 0 },
        new[] { 5, 5, 5, 10, 10, 5, 5, 5 },
        new[] { 5, 5, 10, 20, 20, 10, 5, 5 },
        new[] { 5, 5, 10, 25, 25, 10, 5, 5 },
        new[] { 5, 5, 10, 20, 20, 10, 5, 5 },
        new[] { 5, 5, 5, 10, 10, 5, 5, 5 },
        new[] { 30, 30, 30, 30, 30, 30, 30, 30 },
        new[] { 0, 0, 0, 0, 0, 0, 0, 0 }
    };

    // Knight PST (centralize knights)
    private static readonly int[][] Knight =
    {
        new[] { -50, -40, -30, -30, -30, -30, -40, -50 },
        new[] { -40, -20, 0, 5, 5, 0, -20, -40 },
        new[] { -30, 5, 10, 15, 15, 10, 5, -30 },
        new[] { -30, 5, 15, 20, 20, 15, 5, -30 },
        new[] { -30, 5, 15, 20, 20, 15, 5, -30 },
        new[] { -30, 5, 10, 15, 15, 10, 5, -30 },
        new[] { -40, -20, 0, 5, 5, 0, -20, -40 },
        new[] { -50, -40, -30, -30, -30, -30, -40, -50 }
    };

    // Bishop PST (control center and long diagonals)
    private static readonly int[][] Bishop =
    {
        new[] { -20, -10, -10, -10, -10, -10, -10, -20 },
        new[] { -10, 5, 5, 5, 5, 5, 5, -10 },
        new[] { -10, 5, 10, 10, 10, 10, 5, -10 },
        new[] { -10, 5, 10, 15, 15, 10, 5, -10 },
        new[] { -10, 5, 10, 15, 15, 10, 5, -10 },
        new[] { -10, 5, 10, 10, 10, 10, 5, -10 },
        new[] { -10, 5, 5, 5, 5, 5, 5, -10 },
        new[] { -20, -10, -10, -10, -10, -10, -10, -20 }
    };

    // Rook PST (control files, especially open files)
    private static readonly int[][] Rook =
    {
        new[] { 0, 0, 0, 5, 5, 0, 0, 0 },
        new[] { 5, 10, 10, 10, 10, 10, 10, 5 },
        new[] { 5, 10, 10, 10, 10, 10, 10, 5 },
        new[] { 5, 10, 10, 10, 10, 10, 10, 5 },
        new[] { 5, 10, 10, 10, 10, 10, 10, 5 },
        new[] { 5, 10, 10, 10, 10, 10, 10, 5 },
        new[] { 5, 10, 10, 10, 10, 10, 10, 5 },
        new[] { 0, 0, 0, 5, 5, 0, 0, 0 }
    };

    // Queen PST (centralize slightly)
    private static readonly int[][] Queen =
    {
        new[] { -20, -10, -10, -5, -5, -10, -10, -20 },
        new[] { -10, 0, 5, 5, 5, 5, 0, -10 },
        new[] { -10, 5, 5, 5, 5, 5, 5, -10 },
        new[] { -5, 5, 5, 5, 5, 5, 5, -5 },
        new[] { -5, 5, 5, 5, 5, 5, 5, -5 },
        new[] { -10, 5, 5, 5, 5, 5, 5, -10 },
        new[] { -10, 0, 5, 5, 5, 5, 0, -10 },
        new[] { -20, -10, -10, -5, -5, -10, -10, -20 }
    };

    // King PST (keep safe in opening/middlegame, centralize in endgame)
    private static readonly int[][] King =
    {
        new[] { 20, 30, 10, 0, 0, 10, 30, 20 },
        new[] { 20, 20, 0, 0, 0, 0, 20, 20 },
        new[] { -10, -20, -20, -20, -20, -20, -20, -10 },
        new[] { -20, -30, -30, -40, -40, -30, -30, -20 },
        new[] { -20, -30, -30, -40, -40, -30, -30, -20 },
        new[] { -10, -20, -20, -20, -20, -20, -20, -10 },
        new[] { 20, 20, 0, 0, 0, 0, 20, 20 },
        new[] { 20, 30, 10, 0, 0, 10, 30, 20 }
    };

    /// <summary>
    /// Returns the jagged PST for a piece type (White-perspective, [rank][file]).
    /// Pawn table is the fallback for unexpected types, matching the Evaluator's historic behavior.
    /// </summary>
    public static int[][] TableFor(PieceType type) => type switch
    {
        PieceType.Pawn => Pawn,
        PieceType.Knight => Knight,
        PieceType.Bishop => Bishop,
        PieceType.Rook => Rook,
        PieceType.Queen => Queen,
        PieceType.King => King,
        _ => Pawn
    };

    /// <summary>
    /// Returns the PST value for a piece of the given color/type on the given square.
    /// Black pieces are mirrored (rotated 180°) so the same White-perspective table applies.
    /// </summary>
    public static int Value(Color color, PieceType type, Square square)
    {
        int rank = color == Color.White ? square.Rank : 7 - square.Rank;
        int file = color == Color.White ? square.File : 7 - square.File;
        return TableFor(type)[rank][file];
    }
}
