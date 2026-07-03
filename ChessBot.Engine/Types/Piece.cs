namespace ChessBot.Engine.Types;

/// <summary>
/// Represents a single piece on the board: a combination of color and piece type.
/// Implemented as a readonly struct for zero-allocation efficiency.
/// </summary>
public readonly struct Piece : IEquatable<Piece>
{
    /// <summary>
    /// The color of the piece.
    /// </summary>
    public Color Color { get; }

    /// <summary>
    /// The type of the piece.
    /// </summary>
    public PieceType Type { get; }

    /// <summary>
    /// Creates a new Piece with the specified color and type.
    /// </summary>
    public Piece(Color color, PieceType type)
    {
        Color = color;
        Type = type;
    }

    /// <summary>
    /// Creates an empty piece (no color, no type).
    /// </summary>
    public static Piece Empty => new(Color.White, PieceType.None);

    /// <summary>
    /// Returns true if this piece represents an empty square.
    /// </summary>
    public bool IsEmpty => Type == PieceType.None;

    /// <summary>
    /// Returns the material value of this piece in centipawns.
    /// </summary>
    public int MaterialValue => Type.MaterialValue();

    /// <summary>
    /// Returns a human-readable representation of the piece (e.g., "White King", "Black Pawn").
    /// </summary>
    public override string ToString() =>
        IsEmpty ? "Empty" : $"{Color} {Type}";

    /// <summary>
    /// Returns the FEN character for this piece.
    /// </summary>
    public char ToFenChar() => Type switch
    {
        PieceType.Pawn => Color == Color.White ? 'P' : 'p',
        PieceType.Knight => Color == Color.White ? 'N' : 'n',
        PieceType.Bishop => Color == Color.White ? 'B' : 'b',
        PieceType.Rook => Color == Color.White ? 'R' : 'r',
        PieceType.Queen => Color == Color.White ? 'Q' : 'q',
        PieceType.King => Color == Color.White ? 'K' : 'k',
        _ => ' '
    };

    /// <summary>
    /// Creates a Piece from a FEN character.
    /// </summary>
    public static Piece FromFenChar(char c) => c switch
    {
        'P' => new(Color.White, PieceType.Pawn),
        'p' => new(Color.Black, PieceType.Pawn),
        'N' => new(Color.White, PieceType.Knight),
        'n' => new(Color.Black, PieceType.Knight),
        'B' => new(Color.White, PieceType.Bishop),
        'b' => new(Color.Black, PieceType.Bishop),
        'R' => new(Color.White, PieceType.Rook),
        'r' => new(Color.Black, PieceType.Rook),
        'Q' => new(Color.White, PieceType.Queen),
        'q' => new(Color.Black, PieceType.Queen),
        'K' => new(Color.White, PieceType.King),
        'k' => new(Color.Black, PieceType.King),
        _ => Empty
    };

    public override bool Equals(object? obj) => obj is Piece piece && Equals(piece);

    public bool Equals(Piece other) =>
        Color == other.Color && Type == other.Type;

    public override int GetHashCode() =>
        HashCode.Combine(Color, Type);

    public static bool operator ==(Piece left, Piece right) => left.Equals(right);
    public static bool operator !=(Piece left, Piece right) => !left.Equals(right);
}
