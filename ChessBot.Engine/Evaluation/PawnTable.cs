namespace ChessBot.Engine.Evaluation;

using System.Runtime.CompilerServices;

/// <summary>
/// Cache of the pawn-only evaluation terms — pawn structure (doubled, isolated) and passed pawns —
/// keyed by the two pawn bitboards.
///
/// Both terms depend on nothing but where the pawns stand, and pawns move rarely: across a search
/// the same few thousand pawn structures are evaluated millions of times. The terms are kept here
/// RAW, midgame and endgame separately, and interpolated on the caller's phase, because that is
/// how they were combined before — two separate interpolations, each truncating — and caching one
/// already-blended pair would change scores by the rounding.
///
/// Entries store both bitboards whole, so a hit is exact: there is no hash collision to be wrong
/// about. An all-zero entry needs no valid flag either — it matches only the pawnless position,
/// whose four terms really are zero.
///
/// One table per <see cref="Evaluator"/>, so per searcher: nothing shared between threads.
/// </summary>
internal sealed class PawnTable
{
    /// <summary>
    /// 4,096 entries x 32 bytes = 128 KB, small enough to stay in a core's L2 alongside the
    /// search's own working set. The pawn structures one search visits number in the low
    /// thousands, so a larger table would mostly hold nothing.
    /// </summary>
    private const int Bits    = 12;
    private const int Entries = 1 << Bits;

    internal struct Entry
    {
        public ulong WhitePawns, BlackPawns;
        public int   StructureMg, StructureEg;
        public int   PassedMg, PassedEg;
    }

    private readonly Entry[] _entries = new Entry[Entries];

    // The weight vector the cached terms were computed under. A different active vector makes
    // every entry wrong, so the table is emptied the first time it is probed under a new one.
    private int[] _computedUnder = EvalParams.V;

    internal long Hits, Misses;

    /// <summary>
    /// The entry for these pawns: a hit if <paramref name="hit"/>, otherwise the slot to fill,
    /// already carrying the two bitboards.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    internal ref Entry Probe(ulong whitePawns, ulong blackPawns, out bool hit)
    {
        if (!ReferenceEquals(_computedUnder, EvalParams.V)) Reset();

        ulong mixed = whitePawns * 0x9E3779B97F4A7C15UL
                    ^ ulong.RotateLeft(blackPawns * 0xC2B2AE3D27D4EB4FUL, 31);
        ref Entry entry = ref _entries[(int)(mixed >> (64 - Bits))];

        hit = entry.WhitePawns == whitePawns && entry.BlackPawns == blackPawns;
        if (hit) { Hits++; return ref entry; }

        Misses++;
        entry.WhitePawns = whitePawns;
        entry.BlackPawns = blackPawns;
        return ref entry;
    }

    private void Reset()
    {
        Array.Clear(_entries);
        _computedUnder = EvalParams.V;
    }
}
