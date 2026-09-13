namespace ChessBot.Engine.Evaluation;

using ChessBot.Engine.Types;

/// <summary>
/// The passed pawn term: a pawn with no enemy pawn ahead of it on its own file or either adjacent
/// file, scored by how far it has come and by how much of the game is left.
///
/// It is the one thing material and piece-square tables cannot express about a pawn. A PST gives
/// every pawn on the sixth rank the same bonus whether it is running unopposed or blocked by two
/// enemy pawns; those two positions are won and drawn respectively, and the engine had no way to
/// tell them apart.
///
/// Scored from pawn bitboards rather than a board scan, so the same routine serves both
/// <see cref="Evaluator.Evaluate"/> (which builds them from its own scan) and
/// <see cref="Evaluator.EvaluateFast"/> (which reads the Board's incrementally maintained pair).
/// That is what keeps the two numerically identical.
/// </summary>
internal static class PassedPawns
{
    /// <summary>
    /// Bonus by the pawn's own rank, counted from its side's first rank, on the midgame scale.
    /// Index 0 and 7 are unreachable for a pawn and stay zero.
    ///
    /// This pays for being UNOPPOSED, not for being advanced. Advancement is already in the pawn
    /// piece-square tables, which ramp to +30 on the seventh in the midgame and +90 in the
    /// endgame for every pawn regardless of what stands in front of it. The first weighting here
    /// ignored that and added another 100/190 on top, so a seventh-rank passer scored 380 cp in
    /// an endgame and the term amplified the PST's shape instead of discriminating against it.
    /// It measured -4.5 +/- 9.8 Elo over 4,000 games.
    /// </summary>
    private static readonly int[] MidgameByRank = { 0, 3, 6, 12, 20, 35, 55, 0 };

    /// <summary>
    /// The same curve on the endgame scale. Larger than the midgame one, because a runner matters
    /// more once the pieces that would blockade it are gone — but only about half what it was, for
    /// the reason given above.
    /// </summary>
    private static readonly int[] EndgameByRank = { 0, 8, 14, 24, 40, 65, 100, 0 };

    /// <summary>Extra for a passer defended by one of its own pawns — it cannot simply be taken.</summary>
    private const int ProtectedMidgame = 12;
    private const int ProtectedEndgame = 20;

    /// <summary>
    /// Extra for a passer with a friendly pawn abreast of it on an adjacent file. Two connected
    /// passers support each other up the board and are far harder to blockade than two separate
    /// ones, which is why this is counted apart from being protected.
    /// </summary>
    private const int ConnectedMidgame = 8;
    private const int ConnectedEndgame = 15;

    /// <summary>
    /// For each square, the squares an enemy pawn would have to occupy to stop a White pawn there
    /// from being passed: everything ahead of it on its own file and the two adjacent files.
    /// Built once at type load.
    /// </summary>
    private static readonly ulong[] WhiteFrontSpan = BuildFrontSpans(white: true);
    private static readonly ulong[] BlackFrontSpan = BuildFrontSpans(white: false);

    /// <summary>Squares on the file either side of each square, on the same rank.</summary>
    private static readonly ulong[] AdjacentFileSameRank = BuildAdjacentFileSameRank();

    private static ulong[] BuildFrontSpans(bool white)
    {
        var spans = new ulong[64];

        for (int square = 0; square < 64; square++)
        {
            int file = square % 8, rank = square / 8;
            ulong span = 0UL;

            for (int f = file - 1; f <= file + 1; f++)
            {
                if (f < 0 || f > 7) continue;

                if (white)
                    for (int r = rank + 1; r <= 7; r++) span |= 1UL << (r * 8 + f);
                else
                    for (int r = rank - 1; r >= 0; r--) span |= 1UL << (r * 8 + f);
            }

            spans[square] = span;
        }

        return spans;
    }

    private static ulong[] BuildAdjacentFileSameRank()
    {
        var masks = new ulong[64];

        for (int square = 0; square < 64; square++)
        {
            int file = square % 8, rank = square / 8;
            ulong mask = 0UL;

            if (file - 1 >= 0) mask |= 1UL << (rank * 8 + file - 1);
            if (file + 1 <= 7) mask |= 1UL << (rank * 8 + file + 1);

            masks[square] = mask;
        }

        return masks;
    }

    /// <summary>
    /// White-positive passed pawn score for a position, already interpolated on
    /// <paramref name="phase"/>.
    /// </summary>
    internal static int Evaluate(ulong whitePawns, ulong blackPawns, int phase)
    {
        int midgame = 0, endgame = 0;

        Accumulate(whitePawns, blackPawns, white: true, ref midgame, ref endgame);
        Accumulate(blackPawns, whitePawns, white: false, ref midgame, ref endgame);

        return PieceSquareTables.Interpolate(midgame, endgame, phase);
    }

    /// <summary>
    /// Adds one side's passers to the running midgame/endgame totals, signed so White is positive.
    /// </summary>
    private static void Accumulate(ulong ourPawns, ulong theirPawns, bool white,
                                   ref int midgame, ref int endgame)
    {
        ulong[] frontSpan = white ? WhiteFrontSpan : BlackFrontSpan;
        int sign = white ? 1 : -1;

        ulong remaining = ourPawns;
        while (remaining != 0)
        {
            int square = System.Numerics.BitOperations.TrailingZeroCount(remaining);
            remaining &= remaining - 1;

            if ((frontSpan[square] & theirPawns) != 0)
                continue;   // an enemy pawn still stands in the way

            // Rank counted from the pawn's own side, so both colours index the same curve.
            int rank = white ? square / 8 : 7 - square / 8;

            int mg = MidgameByRank[rank];
            int eg = EndgameByRank[rank];

            // Protected: one of our own pawns attacks the square it stands on, which means the
            // square behind it on an adjacent file, measured in our own direction of travel.
            int behind = white ? square - 8 : square + 8;
            if (behind >= 0 && behind < 64 && (AdjacentFileSameRank[behind] & ourPawns) != 0)
            {
                mg += ProtectedMidgame;
                eg += ProtectedEndgame;
            }

            // Connected: a friendly pawn abreast on an adjacent file.
            if ((AdjacentFileSameRank[square] & ourPawns) != 0)
            {
                mg += ConnectedMidgame;
                eg += ConnectedEndgame;
            }

            midgame += sign * mg;
            endgame += sign * eg;
        }
    }
}
