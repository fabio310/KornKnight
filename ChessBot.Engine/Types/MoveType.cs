namespace ChessBot.Engine.Types;

/// <summary>
/// Represents the type of move being made. Used for special rule handling and UI display.
/// </summary>
[Flags]
public enum MoveType : byte
{
    /// <summary>A quiet move (no capture, no special action).</summary>
    Quiet = 0,

    /// <summary>A capture move (moving piece takes an opponent piece).</summary>
    Capture = 1 << 0,

    /// <summary>A double pawn push from the starting position.</summary>
    DoublePawnPush = 1 << 1,

    /// <summary>A castling move (King-side or Queen-side).</summary>
    Castling = 1 << 2,

    /// <summary>An en passant capture.</summary>
    EnPassant = 1 << 3,

    /// <summary>A pawn promotion (may be combined with Capture).</summary>
    Promotion = 1 << 4,

    /// <summary>Check move (optional flag, not set during generation for performance).</summary>
    Check = 1 << 5,

    /// <summary>Checkmate move (optional flag, typically set only for analysis).</summary>
    Checkmate = 1 << 6
}

/// <summary>
/// Extension methods for MoveType.
/// </summary>
public static class MoveTypeExtensions
{
    /// <summary>
    /// Returns true if the move is a capture (including en passant).
    /// </summary>
    public static bool IsCapture(this MoveType moveType) =>
        (moveType & MoveType.Capture) != 0 || (moveType & MoveType.EnPassant) != 0;

    /// <summary>
    /// Returns true if the move involves a pawn promotion.
    /// </summary>
    public static bool IsPromotion(this MoveType moveType) =>
        (moveType & MoveType.Promotion) != 0;

    /// <summary>
    /// Returns true if the move is a tactical move (capture or promotion).
    /// </summary>
    public static bool IsTactical(this MoveType moveType) =>
        IsCapture(moveType) || IsPromotion(moveType);

    /// <summary>
    /// Returns true if the move is castling.
    /// </summary>
    public static bool IsCastling(this MoveType moveType) =>
        (moveType & MoveType.Castling) != 0;

    /// <summary>
    /// Returns true if the move puts the opponent in check.
    /// </summary>
    public static bool IsCheck(this MoveType moveType) =>
        (moveType & MoveType.Check) != 0;

    /// <summary>
    /// Returns true if the move is checkmate.
    /// </summary>
    public static bool IsCheckmate(this MoveType moveType) =>
        (moveType & MoveType.Checkmate) != 0;
}
