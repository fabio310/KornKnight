namespace ChessBot.Engine.Types;

/// <summary>
/// Represents a single square on the chess board using 0-63 indexing (0x64 / Mailbox).
/// Square 0 is A1 (bottom-left from White's perspective), square 63 is H8 (top-right).
/// This design allows for future optimization (e.g., 0x88 or Bitboard) without breaking the interface.
/// </summary>
public readonly struct Square : IEquatable<Square>, IComparable<Square>
{
    private const int MaxIndex = 63;

    /// <summary>
    /// The internal index of the square (0-63).
    /// </summary>
    private readonly int _index;

    /// <summary>
    /// Creates a Square from a 0-63 index.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">Thrown if index is outside 0-63.</exception>
    public Square(int index)
    {
        if (index < 0 || index > MaxIndex)
            throw new ArgumentOutOfRangeException(nameof(index), $"Square index must be between 0 and 63, got {index}.");
        _index = index;
    }

    /// <summary>
    /// Creates a Square from file (0-7, A-H) and rank (0-7, 1-8).
    /// </summary>
    public Square(int file, int rank)
    {
        if (file < 0 || file > 7)
            throw new ArgumentOutOfRangeException(nameof(file), "File must be between 0 and 7.");
        if (rank < 0 || rank > 7)
            throw new ArgumentOutOfRangeException(nameof(rank), "Rank must be between 0 and 7.");
        _index = rank * 8 + file;
    }

    /// <summary>
    /// Creates a Square from a 0-63 index WITHOUT checking it.
    ///
    /// For call sites that derive an index from something that cannot be out of range — a
    /// six-bit field unpacked from a transposition entry, a bitboard bit position, an offset
    /// already tested against the board edge. The validating constructor is for input that
    /// came from outside the engine: FEN, algebraic notation, a UI click.
    ///
    /// The check is not free where it sits. A Square is constructed several times per move and
    /// a move is constructed tens of times per node, and the branch the check compiles to is on
    /// the hot path of move generation. It is worth paying exactly where a bad index is
    /// possible, which is nowhere inside the generator.
    /// </summary>
    public static Square FromIndexUnsafe(int index) => new(index, skipValidation: true);

    /// <summary>
    /// Creates a Square from a file and rank in 0-7 WITHOUT checking either.
    ///
    /// This is the shape move generation actually uses, and every call site there sits directly
    /// under the bound test that proves it: the loops step a file/rank offset and test
    /// <c>f &gt;= 0 &amp;&amp; f &lt; 8 &amp;&amp; r &gt;= 0 &amp;&amp; r &lt; 8</c> before constructing
    /// anything. The validating constructor then re-tested exactly what the loop had just
    /// established, once per square of every ray of every slider of every node.
    ///
    /// Perft is what makes this safe to do rather than merely plausible. An index that escaped
    /// its range here would not throw — it would silently name the wrong square — and that is
    /// precisely the failure a full-depth perft catches, because it changes a node count.
    /// </summary>
    public static Square FromFileRankUnsafe(int file, int rank) =>
        new((rank << 3) + file, skipValidation: true);

    /// <summary>
    /// The unchecked construction path. The bool is never read; it exists only to give this
    /// constructor a signature the validating one does not have.
    /// </summary>
    private Square(int index, bool skipValidation) => _index = index;

    /// <summary>
    /// The zero-based file index (0-7, where 0=A, 7=H).
    ///
    /// A mask rather than a remainder, and a shift rather than a division below. They are the
    /// same thing only because the index is known non-negative: C# rounds integer division
    /// toward zero and takes the sign of the dividend for the remainder, so the JIT has to emit
    /// a sign correction for <c>% 8</c> and <c>/ 8</c> that <c>&amp; 7</c> and <c>&gt;&gt; 3</c>
    /// do not need. The constructor is what guarantees non-negative.
    /// </summary>
    public int File => _index & 7;

    /// <summary>
    /// The zero-based rank index (0-7, where 0=1st rank, 7=8th rank).
    /// </summary>
    public int Rank => _index >> 3;

    /// <summary>
    /// The internal index (0-63).
    /// </summary>
    public int Index => _index;

    /// <summary>
    /// The file letter (A-H).
    /// </summary>
    public char FileLetter => (char)('a' + File);

    /// <summary>
    /// The rank number (1-8).
    /// </summary>
    public int RankNumber => Rank + 1;

    /// <summary>
    /// Creates a Square from algebraic notation (e.g., "e4", "h1").
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if notation is invalid.</exception>
    public static Square FromAlgebraic(string notation)
    {
        if (string.IsNullOrEmpty(notation) || notation.Length != 2)
            throw new ArgumentException($"Invalid square notation: '{notation}'. Expected format: 'a1' to 'h8'.");

        char fileChar = char.ToLower(notation[0]);
        char rankChar = notation[1];

        if (fileChar < 'a' || fileChar > 'h')
            throw new ArgumentException($"Invalid file: '{fileChar}'. Expected 'a' to 'h'.");
        if (rankChar < '1' || rankChar > '8')
            throw new ArgumentException($"Invalid rank: '{rankChar}'. Expected '1' to '8'.");

        int file = fileChar - 'a';
        int rank = rankChar - '1';

        return new Square(file, rank);
    }

    /// <summary>
    /// Returns the algebraic notation of this square (e.g., "e4").
    /// </summary>
    public override string ToString() => $"{FileLetter}{RankNumber}";

    /// <summary>
    /// Returns the algebraic notation in uppercase format for some scenarios.
    /// </summary>
    public string ToUppercaseAlgebraic() => $"{char.ToUpper(FileLetter)}{RankNumber}";

    /// <summary>
    /// Returns all squares on the board as an enumerable (0-63 in order).
    /// </summary>
    public static IEnumerable<Square> AllSquares
    {
        get
        {
            for (int i = 0; i <= MaxIndex; i++)
                yield return new Square(i);
        }
    }

    public override bool Equals(object? obj) => obj is Square square && Equals(square);

    public bool Equals(Square other) => _index == other._index;

    public int CompareTo(Square other) => _index.CompareTo(other._index);

    public override int GetHashCode() => _index.GetHashCode();

    public static bool operator ==(Square left, Square right) => left.Equals(right);
    public static bool operator !=(Square left, Square right) => !left.Equals(right);
    public static bool operator <(Square left, Square right) => left._index < right._index;
    public static bool operator <=(Square left, Square right) => left._index <= right._index;
    public static bool operator >(Square left, Square right) => left._index > right._index;
    public static bool operator >=(Square left, Square right) => left._index >= right._index;
}
