namespace ChessBot.Engine.Evaluation;

using ChessBot.Engine.Types;

/// <summary>
/// The standard 24-point material phase: how far a position has travelled from the opening
/// towards the endgame, measured by the pieces still on the board.
///
/// Every piece carries a weight — knight 1, bishop 1, rook 2, queen 4 — so a full starting
/// array sums to 24 and a bare king-and-pawn endgame to 0. Pawns weigh nothing: they are the
/// pieces that stay on the board while the game changes character, so counting them would make
/// the phase move in the wrong direction.
///
/// This exists so that phase-dependent evaluation terms depend on the *position* and nothing
/// else. A term gated on the move number is not a function of the position at all: the same
/// position scores differently depending on how many plies were spent reaching it, which the
/// Zobrist hash does not encode — so transposition entries carry scores that were only valid at
/// one move number, and a search crossing the gate sees the score change for no reason on the
/// board.
/// </summary>
internal static class GamePhase
{
    /// <summary>Phase of the full starting array — the most "opening" a position can be.</summary>
    public const int Max = 24;

    /// <summary>
    /// Phase weight of one piece. Kings and pawns weigh nothing; the four piece types that get
    /// traded off carry the whole scale.
    /// </summary>
    public static int WeightFor(PieceType type) => type switch
    {
        PieceType.Knight => 1,
        PieceType.Bishop => 1,
        PieceType.Rook   => 2,
        PieceType.Queen  => 4,
        _                => 0,
    };

    /// <summary>
    /// Clamps a raw phase sum to the 0..<see cref="Max"/> scale. Promotions can push the sum
    /// past 24 (three queens is phase 12 on their own), and a position more "opening" than the
    /// opening is not a meaningful thing for a term to scale by.
    /// </summary>
    public static int Clamp(int phase) => Math.Clamp(phase, 0, Max);

    /// <summary>
    /// Scales <paramref name="score"/> by how much opening is left: the full value at the
    /// starting array, nothing once the pieces are gone, and a smooth ramp between.
    ///
    /// Integer division truncates towards zero, so a score and its negation scale to exact
    /// opposites — the evaluation stays colour-symmetric, which a rounding rule that favoured
    /// one direction would silently break.
    /// </summary>
    public static int ScaleByOpening(int score, int phase) => score * Clamp(phase) / Max;
}
