namespace ChessBot.Engine.Types;

/// <summary>
/// Represents a chess move with full context: source square, destination square, move type, and promotion piece.
/// Implemented as a readonly struct for zero-allocation efficiency.
/// </summary>
public readonly struct Move : IEquatable<Move>
{
    /// <summary>
    /// The source square (where the piece moves from).
    /// </summary>
    public Square From { get; }

    /// <summary>
    /// The destination square (where the piece moves to).
    /// </summary>
    public Square To { get; }

    /// <summary>
    /// The type of the move (capture, castling, promotion, etc.).
    /// </summary>
    public MoveType MoveType { get; }

    /// <summary>
    /// The piece type to promote to (if IsPromotion is true). Otherwise PieceType.None.
    /// </summary>
    public PieceType PromotionType { get; }

    /// <summary>
    /// Creates a Move from source and destination squares, with an optional move type and promotion piece.
    /// </summary>
    public Move(Square from, Square to, MoveType moveType = MoveType.Quiet, PieceType promotionType = PieceType.None)
    {
        From = from;
        To = to;
        MoveType = moveType;
        PromotionType = promotionType;
    }

    /// <summary>
    /// Creates a Move from algebraic notation (e.g., "e2e4", "e7e8q" for promotion).
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if notation is invalid.</exception>
    public static Move FromAlgebraic(string notation)
    {
        if (string.IsNullOrEmpty(notation) || notation.Length < 4)
            throw new ArgumentException($"Invalid move notation: '{notation}'. Expected format: 'e2e4' or 'e7e8q'.");

        Square from = Square.FromAlgebraic(notation.Substring(0, 2));
        Square to = Square.FromAlgebraic(notation.Substring(2, 2));

        PieceType promotionType = PieceType.None;
        if (notation.Length == 5)
        {
            promotionType = notation[4] switch
            {
                'n' or 'N' => PieceType.Knight,
                'b' or 'B' => PieceType.Bishop,
                'r' or 'R' => PieceType.Rook,
                'q' or 'Q' => PieceType.Queen,
                _ => throw new ArgumentException($"Invalid promotion piece: '{notation[4]}'.")
            };
        }

        return new Move(from, to, MoveType.Quiet, promotionType);
    }

    /// <summary>
    /// Returns true if this move involves a pawn promotion.
    /// </summary>
    public bool IsPromotion => PromotionType != PieceType.None;

    /// <summary>
    /// Returns the algebraic notation of this move (e.g., "e2e4", "e7e8q").
    /// </summary>
    public override string ToString()
    {
        string notation = $"{From}{To}";
        if (IsPromotion)
        {
            notation += PromotionType switch
            {
                PieceType.Knight => "n",
                PieceType.Bishop => "b",
                PieceType.Rook => "r",
                PieceType.Queen => "q",
                _ => ""
            };
        }
        return notation;
    }

    /// <summary>
    /// Returns a more detailed representation of the move including its type.
    /// </summary>
    public string ToDetailedString()
    {
        string baseNotation = $"{From}{To}";

        string moveTypeName = MoveType switch
        {
            MoveType.Quiet => "",
            MoveType.Capture => " (capture)",
            MoveType.DoublePawnPush => " (pawn push)",
            MoveType.Castling => " (castle)",
            MoveType.EnPassant => " (en passant)",
            MoveType.Promotion => $" (promote to {PromotionType})",
            _ => ""
        };

        return baseNotation + moveTypeName;
    }

    public override bool Equals(object? obj) => obj is Move move && Equals(move);

    public bool Equals(Move other) =>
        From.Equals(other.From) &&
        To.Equals(other.To) &&
        MoveType == other.MoveType &&
        PromotionType == other.PromotionType;

    public override int GetHashCode() =>
        HashCode.Combine(From, To, MoveType, PromotionType);

    public static bool operator ==(Move left, Move right) => left.Equals(right);
    public static bool operator !=(Move left, Move right) => !left.Equals(right);
}
