namespace ChessBot.Engine.Hashing;

using ChessBot.Engine.Types;

/// <summary>
/// A fixed-size hash table for storing previously-evaluated positions (transposition table).
/// Stores: (zobrist hash, depth, score, flag, best move).
/// Uses depth-preferred replacement strategy: newer/deeper entries overwrite shallower ones.
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
    /// Represents a single entry in the transposition table.
    /// </summary>
    private struct TTEntry
    {
        public ulong Hash;           // Zobrist hash key for verification
        public int Depth;            // Depth at which this position was evaluated
        public int Score;            // Evaluation score (in centipawns)
        public ScoreFlag Flag;       // Type of score (exact, lower bound, upper bound)
        public Move BestMove;        // Best move found at this position
        public byte Age;             // Age counter for replacement strategy
    }

    private readonly TTEntry[] _table;
    private readonly int _size;
    private byte _currentAge;

    /// <summary>
    /// Creates a transposition table with a specified number of entries.
    /// </summary>
    public TranspositionTable(int sizeInMB = 32)
    {
        // Rough estimate: each entry is ~24 bytes, so divide MB by 24 to get approximate entry count
        _size = Math.Max(1, (sizeInMB * 1024 * 1024) / 24);
        _table = new TTEntry[_size];
        _currentAge = 0;
    }

    /// <summary>
    /// Stores an entry in the transposition table.
    /// Uses age-aware depth-preferred replacement: always replace stale entries from a
    /// previous search generation (they can never be relevant again), otherwise only
    /// replace if the new entry is at least as deep as the one already stored.
    /// Without the age check, a deep entry written early in the game can permanently
    /// block a completely unrelated (hash-colliding) position from ever being cached,
    /// which is why the table's effective fill/utility stayed low despite heavy reuse.
    /// </summary>
    public void Store(ulong hash, int depth, int score, ScoreFlag flag, Move bestMove)
    {
        int index = (int)(hash % (ulong)_size);
        ref TTEntry entry = ref _table[index];

        // Replace if: slot is empty, the stored entry is from an older search (stale),
        // or the new entry is at least as deep as the one already there.
        if (entry.Hash == 0 || entry.Age != _currentAge || depth >= entry.Depth)
        {
            entry.Hash = hash;
            entry.Depth = depth;
            entry.Score = score;
            entry.Flag = flag;
            entry.BestMove = bestMove;
            entry.Age = _currentAge;
        }
    }

    /// <summary>
    /// Returns the best move stored for this hash key, regardless of depth.
    /// Used for move-ordering: even a shallow TT entry provides a good first move to try.
    /// Returns default(Move) if no entry exists for this hash.
    /// </summary>
    public Move LookupBestMoveOnly(ulong hash)
    {
        int index = (int)(hash % (ulong)_size);
        ref TTEntry entry = ref _table[index];
        return entry.Hash == hash ? entry.BestMove : default;
    }

    /// <summary>
    /// Retrieves an entry from the transposition table if it exists and matches the hash.
    /// Returns null if no matching entry is found.
    /// </summary>
    public (int score, ScoreFlag flag, Move bestMove, int depth)? Lookup(ulong hash, int depth)
    {
        int index = (int)(hash % (ulong)_size);
        TTEntry entry = _table[index];

        // Verify hash match and depth requirement
        if (entry.Hash == hash && entry.Depth >= depth)
        {
            return (entry.Score, entry.Flag, entry.BestMove, entry.Depth);
        }

        return null;
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
        {
            if (_table[i].Hash != 0)
                count++;
        }
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
            if (_table[i].Hash != 0) filled++;
        return sampleSize > 0 ? filled * 1000 / sampleSize : 0;
    }
}
