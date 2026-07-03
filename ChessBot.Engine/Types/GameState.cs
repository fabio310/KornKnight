namespace ChessBot.Engine.Types;

/// <summary>
/// Represents the immutable game state metadata: active color, castling rights,
/// en passant target square, halfmove clock (50-move rule), and fullmove counter.
/// This is the non-board portion of the chess position and is used for FEN parsing/export
/// and for state restoration during move undo operations.
/// </summary>
public readonly struct GameState : IEquatable<GameState>
{
    /// <summary>
    /// The side whose turn it is to move.
    /// </summary>
    public Color ActiveColor { get; }

    /// <summary>
    /// The castling rights available to both sides.
    /// </summary>
    public CastlingRights CastlingRights { get; }

    /// <summary>
    /// The target square for an en passant capture (if any). Square at index 0 if not applicable.
    /// </summary>
    public Square EnPassantTarget { get; }

    /// <summary>
    /// The number of halfmoves (plies) since the last capture or pawn move. Used for the 50-move draw rule.
    /// </summary>
    public int HalfmoveClock { get; }

    /// <summary>
    /// The total number of completed moves. Increments after Black's move.
    /// </summary>
    public int FullmoveNumber { get; }

    /// <summary>
    /// Constructs a GameState with the specified parameters.
    /// </summary>
    public GameState(
        Color activeColor = Color.White,
        CastlingRights castlingRights = default,
        Square enPassantTarget = default,
        int halfmoveClock = 0,
        int fullmoveNumber = 1)
    {
        ActiveColor = activeColor;
        CastlingRights = castlingRights;
        EnPassantTarget = enPassantTarget;
        HalfmoveClock = halfmoveClock;
        FullmoveNumber = fullmoveNumber;
    }

    /// <summary>
    /// Creates a GameState from FEN notation (only the non-board portion).
    /// Example FEN: "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"
    /// This method extracts: "w KQkq - 0 1"
    /// </summary>
    public static GameState FromFenState(string fenState)
    {
        var parts = fenState.Split(' ');
        if (parts.Length < 4)
            throw new ArgumentException("FEN state string must have at least 4 parts.");

        // Part 0: Active color
        Color activeColor = parts[0].ToLower() == "w" ? Color.White : Color.Black;

        // Part 1: Castling rights
        CastlingRights castlingRights = CastlingRights.FromFenString(parts[1]);

        // Part 2: En passant target
        Square enPassantTarget = new Square(0);  // Default (no en passant)
        if (parts[2] != "-")
        {
            try
            {
                enPassantTarget = Square.FromAlgebraic(parts[2]);
            }
            catch
            {
                throw new ArgumentException($"Invalid en passant target: '{parts[2]}'.");
            }
        }

        // Part 3: Halfmove clock
        int halfmoveClock = int.TryParse(parts[3], out int hmc) ? hmc : 0;

        // Part 4: Fullmove number
        int fullmoveNumber = parts.Length > 4 && int.TryParse(parts[4], out int fm) ? fm : 1;

        return new GameState(activeColor, castlingRights, enPassantTarget, halfmoveClock, fullmoveNumber);
    }

    /// <summary>
    /// Exports this GameState to FEN notation (the non-board portion).
    /// </summary>
    public string ToFenState()
    {
        string activeColorStr = ActiveColor == Color.White ? "w" : "b";
        string castlingStr = CastlingRights.ToFenString();
        string enPassantStr = HasEnPassant ? EnPassantTarget.ToString() : "-";

        return $"{activeColorStr} {castlingStr} {enPassantStr} {HalfmoveClock} {FullmoveNumber}";
    }

    /// <summary>
    /// Returns true if the 50-move draw rule has been satisfied (100+ halfmoves without capture or pawn move).
    /// </summary>
    public bool IsFiftyMoveRuleDraw => HalfmoveClock >= 100;

    /// <summary>
    /// Returns true if no en passant capture is available this turn.
    /// </summary>
    public bool HasEnPassant => EnPassantTarget.Index != 0;

    public override string ToString() => ToFenState();

    public override bool Equals(object? obj) => obj is GameState state && Equals(state);

    public bool Equals(GameState other) =>
        ActiveColor == other.ActiveColor &&
        CastlingRights == other.CastlingRights &&
        EnPassantTarget == other.EnPassantTarget &&
        HalfmoveClock == other.HalfmoveClock &&
        FullmoveNumber == other.FullmoveNumber;

    public override int GetHashCode() =>
        HashCode.Combine(ActiveColor, CastlingRights, EnPassantTarget, HalfmoveClock, FullmoveNumber);

    public static bool operator ==(GameState left, GameState right) => left.Equals(right);
    public static bool operator !=(GameState left, GameState right) => !left.Equals(right);
}
