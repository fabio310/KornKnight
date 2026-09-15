namespace ChessBot.Engine.Evaluation;

/// <summary>
/// What a rook is standing on: an open file, a half-open file, or the seventh rank.
///
/// This is the term with the most room in the whole evaluation, because the rook tables are the
/// flattest in the set. The midgame rook table is +10 on every square of ranks 2–7 and +5 or 0 on
/// the first and last, a spread of 10; the endgame table is +5 everywhere except the seventh rank,
/// where it is +10. So the tables say almost nothing about where a rook belongs, and the thing
/// they leave out is the one thing that decides it: whether the file in front of it is clear.
///
/// The standing lesson applies to the seventh-rank bonus and is subtracted there: the endgame
/// table already pays +5 for that rank, so the term adds the remainder rather than the whole
/// classical value.
///
/// Scored from bitboards for the same reason <see cref="PassedPawns"/> is — so the full scan in
/// <see cref="Evaluator.Evaluate"/> and the incremental read in <see cref="Evaluator.EvaluateFast"/>
/// run identical code and cannot drift apart.
/// </summary>
internal static class RookFiles
{
    /// <summary>
    /// A file with no pawn of either colour on it. A rook there sees the whole board, which is
    /// worth most while there are still pieces to attack down it.
    /// </summary>
    private const int OpenMidgame = 20;
    private const int OpenEndgame = 10;

    /// <summary>
    /// A file with none of our own pawns but at least one of theirs: the rook bears down on a
    /// pawn that cannot be defended by another pawn on that file.
    /// </summary>
    private const int HalfOpenMidgame = 10;
    private const int HalfOpenEndgame = 5;

    /// <summary>
    /// The seventh rank, counted from the rook's own side. Worth more in an endgame, where it cuts
    /// the enemy king off and eats the pawns that can no longer be defended by pieces — but the
    /// endgame table already pays +5 for that rank, so this is the remainder, not the whole value.
    /// </summary>
    private const int SeventhMidgame = 15;
    private const int SeventhEndgame = 20;

    /// <summary>One bit per square of each file, for asking whether a file holds any pawn.</summary>
    private static readonly ulong[] FileMask = BuildFileMasks();

    private static ulong[] BuildFileMasks()
    {
        var masks = new ulong[8];
        for (int file = 0; file < 8; file++)
            for (int rank = 0; rank < 8; rank++)
                masks[file] |= 1UL << (rank * 8 + file);
        return masks;
    }

    /// <summary>
    /// White-positive rook-placement score, already interpolated on <paramref name="phase"/>.
    /// </summary>
    internal static int Evaluate(ulong whiteRooks, ulong blackRooks,
                                 ulong whitePawns, ulong blackPawns, int phase)
    {
        int midgame = 0, endgame = 0;

        Accumulate(whiteRooks, whitePawns, blackPawns, white: true, ref midgame, ref endgame);
        Accumulate(blackRooks, blackPawns, whitePawns, white: false, ref midgame, ref endgame);

        return PieceSquareTables.Interpolate(midgame, endgame, phase);
    }

    /// <summary>
    /// Adds one side's rooks to the running midgame/endgame totals, signed so White is positive.
    /// </summary>
    private static void Accumulate(ulong ourRooks, ulong ourPawns, ulong theirPawns, bool white,
                                   ref int midgame, ref int endgame)
    {
        int sign = white ? 1 : -1;

        ulong remaining = ourRooks;
        while (remaining != 0)
        {
            int square = System.Numerics.BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;

            ulong file = FileMask[square % 8];

            int mg = 0, eg = 0;

            if ((file & ourPawns) == 0)
            {
                // Open if neither side has a pawn there; half-open if only theirs.
                bool open = (file & theirPawns) == 0;
                mg += open ? OpenMidgame : HalfOpenMidgame;
                eg += open ? OpenEndgame : HalfOpenEndgame;
            }

            // Rank counted from the rook's own side, so both colours index the same rule.
            int rank = white ? square / 8 : 7 - square / 8;
            if (rank == 6)
            {
                mg += SeventhMidgame;
                eg += SeventhEndgame;
            }

            midgame += sign * mg;
            endgame += sign * eg;
        }
    }
}
