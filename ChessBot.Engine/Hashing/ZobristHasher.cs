namespace ChessBot.Engine.Hashing;

using ChessBot.Engine.Types;

/// <summary>
/// Generates and manages 64-bit Zobrist hashes for chess positions.
/// Uses pseudo-random number generation to create unique fingerprints for pieces, colors, castling, and en passant.
/// </summary>
public class ZobristHasher
{
    /// <summary>
    /// Random keys for each piece type and color on each square (8x8 = 64 squares).
    /// Indexed as: _pieceKeys[color][pieceType][square.Index]
    /// </summary>
    private readonly ulong[][][] _pieceKeys;

    /// <summary>
    /// Random key for active color (White vs Black).
    /// Index 0 = White, 1 = Black.
    /// </summary>
    private readonly ulong[] _colorKeys;

    /// <summary>
    /// Random keys for castling rights (16 possible combinations).
    /// Index maps to CastlingRights.Mask.
    /// </summary>
    private readonly ulong[] _castlingKeys;

    /// <summary>
    /// Random keys for en passant targets (8 possible files, rank 2 or 5).
    /// Index maps to file (0-7), rank is implicit (3 for White capture, 4 for Black capture).
    /// </summary>
    private readonly ulong[] _enPassantKeys;

    /// <summary>
    /// Initializes Zobrist hasher with pseudo-random keys.
    /// </summary>
    public ZobristHasher()
    {
        _pieceKeys = new ulong[2][][];
        _colorKeys = new ulong[2];
        _castlingKeys = new ulong[16];
        _enPassantKeys = new ulong[8];

        GenerateKeys();
    }

    /// <summary>
    /// Generates all pseudo-random keys using a seeded random number generator.
    /// Seeds are deterministic for reproducibility.
    /// </summary>
    private void GenerateKeys()
    {
        var rng = new Random(unchecked((int)0xDEADBEEF));  // Fixed seed for deterministic hashing

        // Generate piece keys: [color][piecetype][square]
        for (int color = 0; color < 2; color++)
        {
            _pieceKeys[color] = new ulong[7][];  // 7 piece types (None, Pawn, Knight, Bishop, Rook, Queen, King)
            for (int pieceType = 0; pieceType < 7; pieceType++)
            {
                _pieceKeys[color][pieceType] = new ulong[64];
                for (int square = 0; square < 64; square++)
                {
                    _pieceKeys[color][pieceType][square] = GenerateRandomUlong(rng);
                }
            }
        }

        // Generate color keys
        for (int i = 0; i < 2; i++)
        {
            _colorKeys[i] = GenerateRandomUlong(rng);
        }

        // Generate castling keys (16 combinations)
        for (int i = 0; i < 16; i++)
        {
            _castlingKeys[i] = GenerateRandomUlong(rng);
        }

        // Generate en passant keys (8 files)
        for (int i = 0; i < 8; i++)
        {
            _enPassantKeys[i] = GenerateRandomUlong(rng);
        }
    }

    /// <summary>
    /// Generates a random 64-bit unsigned long.
    /// </summary>
    private ulong GenerateRandomUlong(Random rng)
    {
        byte[] buffer = new byte[8];
        rng.NextBytes(buffer);
        return BitConverter.ToUInt64(buffer, 0);
    }

    /// <summary>
    /// Computes the Zobrist hash for a given position.
    /// </summary>
    public ulong ComputeHash(Board.Board board)
    {
        ulong hash = 0;

        // Hash all pieces
        foreach (var (square, piece) in board.GetAllPieces())
        {
            if (!piece.IsEmpty)
            {
                hash ^= GetPieceKey(piece.Color, piece.Type, square);
            }
        }

        // Hash active color
        if (board.State.ActiveColor == Color.Black)
            hash ^= _colorKeys[1];

        // Hash castling rights
        hash ^= _castlingKeys[board.State.CastlingRights.Mask];

        // Hash en passant target
        if (board.State.EnPassantTarget.Index != 0)
        {
            hash ^= _enPassantKeys[board.State.EnPassantTarget.File];
        }

        return hash;
    }

    /// <summary>
    /// Gets the Zobrist key for a piece on a specific square.
    /// </summary>
    public ulong GetPieceKey(Color color, PieceType type, Square square)
    {
        return _pieceKeys[(int)color][(int)type][square.Index];
    }

    /// <summary>
    /// Gets the Zobrist key for color toggle.
    /// </summary>
    public ulong GetColorKey(Color color)
    {
        return _colorKeys[(int)color];
    }

    /// <summary>
    /// Gets the Zobrist key for castling rights.
    /// </summary>
    public ulong GetCastlingKey(byte castlingRightsMask)
    {
        return _castlingKeys[castlingRightsMask];
    }

    /// <summary>
    /// Gets the Zobrist key for en passant target on a specific file.
    /// </summary>
    public ulong GetEnPassantKey(int file)
    {
        if (file < 0 || file >= 8)
            return 0;
        return _enPassantKeys[file];
    }
}
