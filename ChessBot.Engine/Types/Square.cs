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
    /// The zero-based file index (0-7, where 0=A, 7=H).
    /// </summary>
    public int File => _index % 8;

    /// <summary>
    /// The zero-based rank index (0-7, where 0=1st rank, 7=8th rank).
    /// </summary>
    public int Rank => _index / 8;

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
