namespace ChessBot.Engine.Types;

/// <summary>
/// Represents castling rights for both sides. Uses bit flags to efficiently track
/// whether each side retains King-side and Queen-side castling privileges.
/// </summary>
public readonly struct CastlingRights : IEquatable<CastlingRights>
{
    /// <summary>
    /// Bit flag: White can castle King-side (0-0).
    /// </summary>
    public const byte WhiteKingSideMask = 1 << 0;  // 0001

    /// <summary>
    /// Bit flag: White can castle Queen-side (0-0-0).
    /// </summary>
    public const byte WhiteQueenSideMask = 1 << 1;  // 0010

    /// <summary>
    /// Bit flag: Black can castle King-side (0-0).
    /// </summary>
    public const byte BlackKingSideMask = 1 << 2;  // 0100

    /// <summary>
    /// Bit flag: Black can castle Queen-side (0-0-0).
    /// </summary>
    public const byte BlackQueenSideMask = 1 << 3;  // 1000

    /// <summary>
    /// All castling rights available.
    /// </summary>
    public const byte AllRightsMask = WhiteKingSideMask | WhiteQueenSideMask | BlackKingSideMask | BlackQueenSideMask;

    private readonly byte _rights;

    /// <summary>
    /// Creates castling rights from a byte bitmask.
    /// </summary>
    public CastlingRights(byte rights = AllRightsMask)
    {
        _rights = rights;
    }

    /// <summary>
    /// Creates castling rights from a FEN castling string (e.g., "KQkq", "K-", "-").
    /// </summary>
    public static CastlingRights FromFenString(string fenCastling)
    {
        if (string.IsNullOrEmpty(fenCastling) || fenCastling == "-")
            return new CastlingRights(0);

        byte rights = 0;

        if (fenCastling.Contains('K')) rights |= WhiteKingSideMask;
        if (fenCastling.Contains('Q')) rights |= WhiteQueenSideMask;
        if (fenCastling.Contains('k')) rights |= BlackKingSideMask;
        if (fenCastling.Contains('q')) rights |= BlackQueenSideMask;

        return new CastlingRights(rights);
    }

    /// <summary>
    /// White can castle King-side.
    /// </summary>
    public bool WhiteKingSide => (_rights & WhiteKingSideMask) != 0;

    /// <summary>
    /// White can castle Queen-side.
    /// </summary>
    public bool WhiteQueenSide => (_rights & WhiteQueenSideMask) != 0;

    /// <summary>
    /// Black can castle King-side.
    /// </summary>
    public bool BlackKingSide => (_rights & BlackKingSideMask) != 0;

    /// <summary>
    /// Black can castle Queen-side.
    /// </summary>
    public bool BlackQueenSide => (_rights & BlackQueenSideMask) != 0;

    /// <summary>
    /// Returns true if the specified color has any castling rights.
    /// </summary>
    public bool CanCastle(Color color) =>
        color == Color.White
            ? WhiteKingSide || WhiteQueenSide
            : BlackKingSide || BlackQueenSide;

    /// <summary>
    /// Returns castling rights for the specified color and side.
    /// </summary>
    public bool CanCastle(Color color, bool kingSide) =>
        color == Color.White
            ? (kingSide ? WhiteKingSide : WhiteQueenSide)
            : (kingSide ? BlackKingSide : BlackQueenSide);

    /// <summary>
    /// Revokes all castling rights for a specific color.
    /// </summary>
    public CastlingRights RevokeColor(Color color)
    {
        if (color == Color.White)
            return new CastlingRights((byte)(_rights & ~(WhiteKingSideMask | WhiteQueenSideMask)));
        else
            return new CastlingRights((byte)(_rights & ~(BlackKingSideMask | BlackQueenSideMask)));
    }

    /// <summary>
    /// Revokes a specific castling right.
    /// </summary>
    public CastlingRights Revoke(Color color, bool kingSide)
    {
        byte mask = color == Color.White
            ? (kingSide ? WhiteKingSideMask : WhiteQueenSideMask)
            : (kingSide ? BlackKingSideMask : BlackQueenSideMask);

        return new CastlingRights((byte)(_rights & ~mask));
    }

    /// <summary>
    /// Returns the FEN representation of castling rights (e.g., "KQkq", "-").
    /// </summary>
    public string ToFenString()
    {
        if (_rights == 0)
            return "-";

        string result = "";
        if (WhiteKingSide) result += "K";
        if (WhiteQueenSide) result += "Q";
        if (BlackKingSide) result += "k";
        if (BlackQueenSide) result += "q";

        return result;
    }

    /// <summary>
    /// Returns true if no castling rights remain.
    /// </summary>
    public bool IsEmpty => _rights == 0;

    /// <summary>
    /// Returns the raw bitmask.
    /// </summary>
    public byte Mask => _rights;

    public override string ToString() => ToFenString();

    public override bool Equals(object? obj) => obj is CastlingRights rights && Equals(rights);

    public bool Equals(CastlingRights other) => _rights == other._rights;

    public override int GetHashCode() => _rights.GetHashCode();

    public static bool operator ==(CastlingRights left, CastlingRights right) => left.Equals(right);
    public static bool operator !=(CastlingRights left, CastlingRights right) => !left.Equals(right);
}
