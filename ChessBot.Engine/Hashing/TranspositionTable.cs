namespace ChessBot.Engine.Hashing;

using System.Threading;
using ChessBot.Engine.Types;

/// <summary>
/// A fixed-size hash table of previously-searched positions, safe to share between threads
/// without a lock.
///
/// Every entry is two 64-bit words: the payload, and the position's key XORed with that payload.
/// A reader recomputes the XOR and keeps the entry only if it comes back to the key it asked
/// about. That does not PREVENT a torn read — two threads writing the same slot can leave one
/// word from each — it DETECTS one, which is the weaker guarantee that is actually affordable.
/// A torn entry fails the check and is treated as a miss, and a miss costs a re-search of a
/// subtree that was going to be searched anyway.
///
/// A lock would be the alternative, and it would be a lock taken once per node on a table probed
/// by every thread. The check here costs one XOR and one comparison, which the probe was already
/// paying in spirit: the old code compared a stored hash against the one it asked for, and this
/// compares the same thing through the payload.
///
/// The scheme is Hyatt's. It rests on aligned 64-bit reads and writes being atomic in themselves,
/// which .NET guarantees for a native-word-sized field on a 64-bit runtime, so the two words can
/// each be trusted individually even when the pair cannot.
/// </summary>
internal class TranspositionTable
{
    /// <summary>
    /// Flags for transposition table entries, indicating the type of score stored.
    /// </summary>
    public enum ScoreFlag : byte
    {
        /// <summary>Score is exact (from a fully-searched position).</summary>
        Exact = 0,

        /// <summary>Score is a lower bound (alpha cutoff; actual score might be higher).</summary>
        LowerBound = 1,

        /// <summary>Score is an upper bound (beta cutoff; actual score might be lower).</summary>
        UpperBound = 2
    }

    /// <summary>
    /// One slot: the payload, and the position key XORed with it.
    ///
    /// Sixteen bytes exactly, where the field-per-value form was thirty-two after padding. That
    /// is not the reason for the change but it is the measurable part of it — twice as many
    /// entries fit in the same memory, and two slots share a cache line.
    /// </summary>
    private struct TTEntry
    {
        public ulong KeyXorData;
        public ulong Data;
    }

    // ── Payload layout, 64 bits ───────────────────────────────────────────────
    //
    //   0..5    from square          6 bits
    //   6..11   to square            6 bits
    //   12..19  move type            8 bits (a [Flags] byte)
    //   20..22  promotion piece      3 bits (PieceType 0-6)
    //   23..31  depth                9 bits (1..511; the search never stores depth 0 or less)
    //   32..49  score               18 bits, biased so it can be stored unsigned
    //   50..51  score flag           2 bits
    //   52..59  age                  8 bits
    //   63      occupied             1 bit
    //
    // Bit 63 is what makes "is this slot in use" a question about the payload rather than about
    // the key. An unused slot is two zero words; a used one always has this bit set, whatever
    // else it holds. Without it an entry whose key happened to equal its payload would read as
    // empty, which is astronomically unlikely and exactly the kind of thing that shows up once a
    // year in a long run.

    private const int FromShift      = 0;
    private const int ToShift        = 6;
    private const int MoveTypeShift  = 12;
    private const int PromotionShift = 20;
    private const int DepthShift     = 23;
    private const int ScoreShift     = 32;
    private const int FlagShift      = 50;
    private const int AgeShift       = 52;
    private const ulong OccupiedBit  = 1UL << 63;

    private const ulong SquareMask    = 0x3F;
    private const ulong MoveTypeMask  = 0xFF;
    private const ulong PromotionMask = 0x7;
    private const ulong DepthMask     = 0x1FF;
    private const ulong ScoreMask     = 0x3FFFF;
    private const ulong FlagMask      = 0x3;
    private const ulong AgeMask       = 0xFF;

    /// <summary>
    /// Added to a score before it is packed so the 18-bit field can hold it unsigned. The widest
    /// score the search ever stores is a mate adjusted by ply — 100,000 + 127 — so the ±131,072
    /// this allows has room to spare.
    /// </summary>
    private const int ScoreBias = 131_072;

    /// <summary>Bytes per entry, which is what the requested size in megabytes is divided by.</summary>
    private const int BytesPerEntry = 16;

    private readonly TTEntry[] _table;
    private readonly int _size;

    /// <summary>
    /// <see cref="_size"/> - 1, which is the slot index because the entry count is a power of
    /// two. See the constructor for why it is one.
    /// </summary>
    private readonly ulong _indexMask;

    private byte _currentAge;

    /// <summary>
    /// Creates a transposition table of approximately <paramref name="sizeInMB"/> megabytes.
    ///
    /// The entry count is rounded DOWN to a power of two, which is what lets the slot index be
    /// a mask rather than a remainder. Every node computed <c>hash % _size</c> twice — once to
    /// probe and once to store — and a 64-bit remainder by a value the JIT cannot see is a
    /// hardware division, tens of cycles with no pipelining to hide it, on the hottest path in
    /// the engine.
    ///
    /// Rounding down rather than up because the size is a budget the host set: a host that asks
    /// for 100 MB has said what it is willing to give, and taking 128 is not a rounding error.
    /// It costs entries only for sizes that are not already powers of two, and the sizes that
    /// matter are: 64 MB, the default, is 4,194,304 entries exactly, so the table is unchanged
    /// and so is every index in it. That is what makes this change bit-identical rather than
    /// merely equivalent — see the commit that introduced it.
    ///
    /// The alternative was Lemire's multiply-high mapping, which keeps an arbitrary entry count
    /// and costs one multiply. It is not used, and the reason is not its speed: it maps a hash
    /// through its HIGH bits where the remainder uses its low ones, so it lands positions in
    /// different slots, collides different pairs of positions, and therefore changes the search
    /// tree. That makes it a change to be justified by playing strength rather than by a node
    /// rate, which is a different kind of commit; masking is free of that because it is the
    /// same arithmetic the remainder was already doing.
    /// </summary>
    public TranspositionTable(int sizeInMB = 32)
    {
        _size       = EntriesFor(sizeInMB);
        _indexMask  = (ulong)(_size - 1);
        _table      = new TTEntry[_size];
        _currentAge = 0;
    }

    /// <summary>
    /// How many entries a table of <paramref name="sizeInMB"/> megabytes holds: the largest
    /// power of two that fits in the budget.
    ///
    /// Separate from the constructor so it can be asserted on without allocating the table —
    /// checking the 4,096 MB case otherwise means allocating four gigabytes to read one
    /// integer back out of it.
    /// </summary>
    internal static int EntriesFor(int sizeInMB)
    {
        // In long, because the product overflows int at 2,048 MB and wraps negative at 4,096 —
        // which the old code then clamped to a single entry, so asking for the largest table
        // the engine allows would have produced the smallest one that exists. Harmless while
        // the only caller passed 64; a live bug the moment the size became a UCI option.
        long requested = Math.Max(1L, ((long)sizeInMB * 1024 * 1024) / BytesPerEntry);

        // Round down to a power of two. RoundUpToPowerOf2 returns its argument unchanged when
        // it is already one, so the halving below only fires when it really did round up.
        ulong rounded = System.Numerics.BitOperations.RoundUpToPowerOf2((ulong)requested);
        if (rounded > (ulong)requested) rounded >>= 1;

        return (int)Math.Max(1UL, rounded);
    }

    /// <summary>
    /// The slot a position belongs in.
    ///
    /// A mask, because the entry count is a power of two — see the constructor. This replaced
    /// <c>(int)(hash % (ulong)_size)</c>, which was a 64-bit hardware division executed twice
    /// per node.
    /// </summary>
    private int SlotOf(ulong hash) => (int)(hash & _indexMask);

    /// <summary>
    /// Stores an entry in the transposition table.
    /// Uses age-aware depth-preferred replacement: always replace stale entries from a
    /// previous search generation (they can never be relevant again), otherwise only
    /// replace if the new entry is at least as deep as the one already stored.
    /// Without the age check, a deep entry written early in the game can permanently
    /// block a completely unrelated (hash-colliding) position from ever being cached,
    /// which is why the table's effective fill/utility stayed low despite heavy reuse.
    ///
    /// The replacement decision reads the slot without verifying it, which under several threads
    /// can mean deciding from a torn entry. That is deliberate: the decision is a heuristic about
    /// which of two entries is worth more, and getting it wrong costs one entry. Verifying it
    /// would buy nothing and cost a probe.
    /// </summary>
    public void Store(ulong hash, int depth, int score, ScoreFlag flag, Move bestMove)
    {
        ref TTEntry entry = ref _table[SlotOf(hash)];

        ulong existing = Volatile.Read(ref entry.Data);
        bool empty     = (existing & OccupiedBit) == 0;

        if (!empty
            && (byte)((existing >> AgeShift) & AgeMask) == _currentAge
            && depth < (int)((existing >> DepthShift) & DepthMask))
        {
            return;
        }

        ulong data = Pack(depth, score, flag, bestMove, _currentAge);

        // Written key-first so that a reader which sees the new key also sees data no older than
        // it. Neither order makes the pair atomic, which is the whole premise; this one merely
        // keeps the window as small as it can be.
        Volatile.Write(ref entry.KeyXorData, hash ^ data);
        Volatile.Write(ref entry.Data, data);
    }

    /// <summary>
    /// Reads a slot once and hands back everything it holds: the move, the score, the bound
    /// flag and the depth it was searched to. Returns false when the slot does not belong to
    /// this position.
    ///
    /// One probe, because a probe is a cache miss. The table is tens of megabytes of memory
    /// touched in hash order, so essentially every read of it misses to main memory, and the
    /// search used to take that miss twice per node: once through LookupBestMoveOnly to get a
    /// move for ordering, and again through Lookup to get a score. The two calls read the same
    /// sixteen bytes of the same slot — the depth test that distinguishes them is a comparison
    /// on data the first call had already fetched and thrown away.
    ///
    /// The depth rule is deliberately NOT applied here. What a caller does with a shallow entry
    /// differs by caller — move ordering will take a move from any depth, a score cutoff will
    /// not — and folding the rule in is what forced two probes in the first place. The stored
    /// depth is returned and the caller compares it.
    /// </summary>
    public bool TryProbe(ulong hash, out int score, out ScoreFlag flag, out Move bestMove,
                         out int depth)
    {
        if (!TryRead(hash, out ulong data))
        {
            score    = 0;
            flag     = ScoreFlag.Exact;
            bestMove = default;
            depth    = 0;
            return false;
        }

        score    = UnpackScore(data);
        flag     = UnpackFlag(data);
        bestMove = UnpackMove(data);
        depth    = (int)((data >> DepthShift) & DepthMask);
        return true;
    }

    /// <summary>
    /// Returns the best move stored for this hash key, regardless of depth.
    /// Used for move-ordering: even a shallow TT entry provides a good first move to try.
    /// Returns default(Move) if no entry exists for this hash.
    /// </summary>
    public Move LookupBestMoveOnly(ulong hash) =>
        TryProbe(hash, out _, out _, out Move bestMove, out _) ? bestMove : default;

    /// <summary>
    /// Retrieves an entry from the transposition table if it exists, matches the hash, and was
    /// searched at least as deep as <paramref name="depth"/>. Returns null otherwise.
    ///
    /// Expressed in terms of <see cref="TryProbe"/> rather than reading the slot itself, so
    /// there is one decode path and not two that can drift apart. Not on the search's hot path
    /// any more — the search probes directly — but it is the form the table's tests are
    /// written against, and it states the depth rule in one place.
    /// </summary>
    public (int score, ScoreFlag flag, Move bestMove, int depth)? Lookup(ulong hash, int depth) =>
        TryProbe(hash, out int score, out ScoreFlag flag, out Move bestMove, out int storedDepth)
        && storedDepth >= depth
            ? (score, flag, bestMove, storedDepth)
            : null;

    /// <summary>
    /// Reads a slot and confirms it belongs to <paramref name="hash"/>.
    ///
    /// The two words are read once each into locals, and the check is made on those copies — not
    /// on the fields — so that a write landing between the check and the use cannot change what
    /// was verified. Reading the payload first and the key second pairs with the write order
    /// above.
    /// </summary>
    private bool TryRead(ulong hash, out ulong data)
    {
        ref TTEntry entry = ref _table[SlotOf(hash)];

        data = Volatile.Read(ref entry.Data);
        ulong keyXorData = Volatile.Read(ref entry.KeyXorData);

        return (data & OccupiedBit) != 0 && (keyXorData ^ data) == hash;
    }

    private static ulong Pack(int depth, int score, ScoreFlag flag, Move move, byte age)
    {
        ulong packed = OccupiedBit;

        packed |= ((ulong)move.From.Index          & SquareMask)    << FromShift;
        packed |= ((ulong)move.To.Index            & SquareMask)    << ToShift;
        packed |= ((ulong)(byte)move.MoveType      & MoveTypeMask)  << MoveTypeShift;
        packed |= ((ulong)(byte)move.PromotionType & PromotionMask) << PromotionShift;
        packed |= ((ulong)Math.Clamp(depth, 0, (int)DepthMask)      & DepthMask)  << DepthShift;
        packed |= ((ulong)(Math.Clamp(score, -ScoreBias, ScoreBias - 1) + ScoreBias)
                                                   & ScoreMask)     << ScoreShift;
        packed |= ((ulong)(byte)flag               & FlagMask)      << FlagShift;
        packed |= ((ulong)age                      & AgeMask)       << AgeShift;

        return packed;
    }

    private static Move UnpackMove(ulong data) => new(
        new Square((int)((data >> FromShift) & SquareMask)),
        new Square((int)((data >> ToShift)   & SquareMask)),
        (MoveType)(byte)((data >> MoveTypeShift)  & MoveTypeMask),
        (PieceType)(byte)((data >> PromotionShift) & PromotionMask));

    private static int UnpackScore(ulong data) =>
        (int)((data >> ScoreShift) & ScoreMask) - ScoreBias;

    private static ScoreFlag UnpackFlag(ulong data) =>
        (ScoreFlag)(byte)((data >> FlagShift) & FlagMask);

    /// <summary>
    /// Packs and unpacks one entry without touching the table, so a test can assert that the
    /// payload survives the round trip for every field. A packing bug is silent: the search reads
    /// a plausible wrong move or a score off by a power of two and simply plays worse.
    /// </summary>
    internal static (int depth, int score, ScoreFlag flag, Move move, byte age) RoundTrip(
        int depth, int score, ScoreFlag flag, Move move, byte age)
    {
        ulong data = Pack(depth, score, flag, move, age);

        return ((int)((data >> DepthShift) & DepthMask),
                UnpackScore(data),
                UnpackFlag(data),
                UnpackMove(data),
                (byte)((data >> AgeShift) & AgeMask));
    }

    /// <summary>
    /// Clears the entire transposition table.
    /// </summary>
    public void Clear()
    {
        Array.Clear(_table, 0, _table.Length);
        _currentAge = 0;
    }

    /// <summary>
    /// Increments the age counter. Called at the start of each new search.
    /// Helps with gradual replacement strategy over multiple iterations.
    /// </summary>
    public void NewSearch()
    {
        _currentAge++;
    }

    /// <summary>
    /// Returns the approximate number of entries used in the table (for statistics).
    /// </summary>
    public int GetUsedEntries()
    {
        int count = 0;
        for (int i = 0; i < _table.Length; i++)
            if ((_table[i].Data & OccupiedBit) != 0) count++;

        return count;
    }

    /// <summary>
    /// Returns the total capacity of the table.
    /// </summary>
    public int GetCapacity() => _size;

    /// <summary>
    /// Samples the first 1000 entries (or the whole table if smaller) to estimate
    /// the fill in per-mille (0–1000).  Avoids a full O(N) scan on large tables.
    /// </summary>
    public int GetFillPermille()
    {
        int sampleSize = Math.Min(_size, 1000);
        int filled = 0;
        for (int i = 0; i < sampleSize; i++)
            if ((_table[i].Data & OccupiedBit) != 0) filled++;

        return sampleSize > 0 ? filled * 1000 / sampleSize : 0;
    }
}
