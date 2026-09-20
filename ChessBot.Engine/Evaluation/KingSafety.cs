namespace ChessBot.Engine.Evaluation;

using ChessBot.Engine.Types;

/// <summary>
/// What stands in front of the king: the pawns that shelter it, and the enemy pawns coming at it.
///
/// The evaluation had nothing about king safety at all. The king's piece-square tables say where a
/// king belongs on an empty board — the corner in the midgame, the centre in the endgame — and
/// nothing about whether the pawns that were in front of it are still there. A castled king whose
/// three shield pawns have gone scored exactly as well as one behind an intact shield, which is
/// the single largest thing this evaluation could not see.
///
/// Two terms, over the king's file and its two neighbours (the window is shifted inwards on the
/// a- and h-files so it always covers three real files):
///   shelter — how far in front of the king the nearest OWN pawn on that file is, or none at all;
///   storm   — how close the nearest ENEMY pawn on that file has come.
/// Both are read from the pawn bitboards, so the full scan and the incremental path run the same
/// code and cannot drift apart, exactly as PassedPawns and RookFiles do.
///
/// This is the half of king safety that pawns alone decide. The other half — how many pieces
/// attack the squares around the king, and what they are worth — needs per-piece attack sets,
/// which are expensive to produce from a mailbox board and nearly free once the bitboard work
/// lands. It waits for that.
///
/// The weights below are the midgame scale; the endgame ones default to 0, because a bare king in
/// an endgame wants to walk up the board and a shield it has left behind is not a weakness. The
/// taper does the rest.
/// </summary>
internal static class KingSafety
{
    /// <summary>
    /// Shelter penalty by the distance of the nearest own pawn ahead on a file: index 0 is "no
    /// pawn on this file at all", 1 is the pawn directly in front of the king, and 3 is three or
    /// more ranks away. A missing shield pawn is the worst case, which is why index 0 is the
    /// largest: the file is open onto the king.
    /// </summary>
    internal static readonly int[] DefaultShelterMidgame = { -25, 0, -10, -18 };

    /// <summary>
    /// Storm penalty by the distance of the nearest enemy pawn on a file, same indexing. A pawn
    /// two ranks away is the dangerous one — it still has a move to make and cannot simply be
    /// traded; one already in contact is usually blocked or exchanged.
    /// </summary>
    internal static readonly int[] DefaultStormMidgame = { 0, -4, -10, -6 };

    /// <summary>White-positive king-safety score, interpolated on the phase.</summary>
    internal static int Evaluate(ulong whitePawns, ulong blackPawns,
                                 Square whiteKing, Square blackKing, int phase)
    {
        Raw(whitePawns, blackPawns, whiteKing, blackKing, out int midgame, out int endgame);
        return PieceSquareTables.Interpolate(midgame, endgame, phase);
    }

    /// <summary>The same score before interpolation, midgame and endgame separately.</summary>
    internal static void Raw(ulong whitePawns, ulong blackPawns,
                             Square whiteKing, Square blackKing, out int midgame, out int endgame)
    {
        midgame = 0;
        endgame = 0;

        Accumulate(whitePawns, blackPawns, whiteKing, white: true,  ref midgame, ref endgame);
        Accumulate(blackPawns, whitePawns, blackKing, white: false, ref midgame, ref endgame);
    }

    private static void Accumulate(ulong ourPawns, ulong theirPawns, Square king, bool white,
                                   ref int midgame, ref int endgame)
    {
        int sign     = white ? 1 : -1;
        int kingFile = Math.Clamp(king.File, 1, 6);   // keep the three-file window on the board
        int kingRank = king.Rank;

        for (int file = kingFile - 1; file <= kingFile + 1; file++)
        {
            int shelter = DistanceAhead(ourPawns,   file, kingRank, white);
            int storm   = DistanceAhead(theirPawns, file, kingRank, white);

            midgame += sign * EvalParams.V[EvalParams.KingShelterMg + shelter];
            endgame += sign * EvalParams.V[EvalParams.KingShelterEg + shelter];
            midgame += sign * EvalParams.V[EvalParams.KingStormMg + storm];
            endgame += sign * EvalParams.V[EvalParams.KingStormEg + storm];
        }
    }

    /// <summary>
    /// How far ahead of the king the nearest pawn of <paramref name="pawns"/> stands on this file,
    /// counted in the king's own direction of travel: 0 for none, 1 for the square directly in
    /// front, capped at 3. "Ahead" is what matters — a pawn behind the king shelters nothing.
    /// </summary>
    private static int DistanceAhead(ulong pawns, int file, int kingRank, bool white)
    {
        ulong onFile = pawns & FileMask[file];

        for (int distance = 1; distance <= 3; distance++)
        {
            int rank = white ? kingRank + distance : kingRank - distance;
            if (rank is < 0 or > 7) break;

            if ((onFile & (1UL << (rank * 8 + file))) != 0) return distance;
        }

        return 0;
    }

    private static readonly ulong[] FileMask = BuildFileMasks();

    private static ulong[] BuildFileMasks()
    {
        var masks = new ulong[8];
        for (int file = 0; file < 8; file++)
            for (int rank = 0; rank < 8; rank++)
                masks[file] |= 1UL << (rank * 8 + file);

        return masks;
    }
}
