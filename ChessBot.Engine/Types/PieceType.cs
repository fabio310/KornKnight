namespace ChessBot.Engine.Types;

/// <summary>
/// Represents the six piece types in chess (excluding color).
/// </summary>
public enum PieceType : byte
{
    /// <summary>No piece (empty square).</summary>
    None = 0,

    /// <summary>Pawn (value 1).</summary>
    Pawn = 1,

    /// <summary>Knight (value 3).</summary>
    Knight = 2,

    /// <summary>Bishop (value 3).</summary>
    Bishop = 3,

    /// <summary>Rook (value 5).</summary>
    Rook = 4,

    /// <summary>Queen (value 9).</summary>
    Queen = 5,

    /// <summary>King (infinite material value).</summary>
    King = 6
}

/// <summary>
/// Extension methods for PieceType.
/// </summary>
public static class PieceTypeExtensions
{
    /// <summary>
    /// Returns the standard material value in centipawns (100 = 1 pawn equivalent).
    /// </summary>
    public static int MaterialValue(this PieceType type) => type switch
    {
        PieceType.Pawn => 100,
        PieceType.Knight => 320,
        PieceType.Bishop => 330,
        PieceType.Rook => 500,
        PieceType.Queen => 900,
        PieceType.King => 0,  // King is not traded; value is infinite
        _ => 0
    };

    /// <summary>
    /// Determines if the piece type is a slider (Bishop, Rook, or Queen).
    /// Used for move generation optimization.
    /// </summary>
    public static bool IsSlider(this PieceType type) =>
        type is PieceType.Bishop or PieceType.Rook or PieceType.Queen;

    /// <summary>
    /// Returns true for piece types that can be promoted (only Pawn).
    /// </summary>
    public static bool CanPromote(this PieceType type) => type == PieceType.Pawn;

    /// <summary>
    /// Returns true if the piece type is a major piece (Rook or Queen).
    /// </summary>
    public static bool IsMajor(this PieceType type) =>
        type is PieceType.Rook or PieceType.Queen;

    /// <summary>
    /// Returns true if the piece type is a minor piece (Knight or Bishop).
    /// </summary>
    public static bool IsMinor(this PieceType type) =>
        type is PieceType.Knight or PieceType.Bishop;
}
