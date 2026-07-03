namespace ChessBot.Engine.Types;

/// <summary>
/// Represents the side-to-move in chess. Fundamental for rule enforcement and evaluation.
/// </summary>
public enum Color : byte
{
    /// <summary>White pieces move first.</summary>
    White = 0,

    /// <summary>Black pieces move second.</summary>
    Black = 1
}

/// <summary>
/// Extension methods for the Color enum.
/// </summary>
public static class ColorExtensions
{
    /// <summary>
    /// Returns the opposite color (White returns Black, Black returns White).
    /// </summary>
    public static Color Opposite(this Color color) => color == Color.White ? Color.Black : Color.White;

    /// <summary>
    /// Returns 1 for White, -1 for Black. Used in evaluation calculations.
    /// </summary>
    public static int Sign(this Color color) => color == Color.White ? 1 : -1;

    /// <summary>
    /// Returns the numeric direction for pawn advancement: +1 for White (towards rank 8), -1 for Black (towards rank 1).
    /// </summary>
    public static int PawnDirection(this Color color) => color == Color.White ? 1 : -1;
}
