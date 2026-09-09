namespace ChessBot.Engine.Types;

/// <summary>
/// Why a FEN was rejected as not describing a position reachable in a game of chess.
/// </summary>
public enum PositionViolation
{
    /// <summary>A side has no king, or more than one.</summary>
    KingCount,

    /// <summary>The two kings stand on adjacent squares.</summary>
    AdjacentKings,

    /// <summary>The side that is NOT to move is in check, so the previous move was illegal.</summary>
    SideNotToMoveInCheck,

    /// <summary>A pawn stands on the first or the eighth rank, where no pawn can ever be.</summary>
    PawnOnBackRank,
}

/// <summary>
/// Thrown when a FEN parses cleanly but describes a position the rules of chess cannot produce.
///
/// Such a position must be rejected at the boundary rather than searched: move generation and
/// make/unmake both assume their invariants (each side has exactly one king, the side to move
/// can capture nothing that is already gone) and violate them in ways that surface far from the
/// cause — a corpus FEN with adjacent kings used to fail as "Cannot make move: no piece on h1"
/// four frames inside negamax, which names neither the position nor what was wrong with it.
///
/// Derives from <see cref="ArgumentException"/> so callers that already treat a bad FEN as a bad
/// argument keep working; <see cref="Violation"/> is what a caller tests to distinguish a
/// malformed string from an impossible position.
/// </summary>
public class IllegalPositionException : ArgumentException
{
    /// <summary>Which rule the position broke.</summary>
    public PositionViolation Violation { get; }

    /// <summary>The rejected FEN, so the message alone localises the offending input.</summary>
    public string Fen { get; }

    /// <summary>
    /// Builds the exception from the rule broken, the offending FEN and a human-readable detail
    /// naming the squares involved.
    /// </summary>
    public IllegalPositionException(PositionViolation violation, string fen, string detail)
        : base($"Illegal position ({violation}): {detail}. FEN: '{fen}'.")
    {
        Violation = violation;
        Fen = fen;
    }
}
