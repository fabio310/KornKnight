namespace ChessBot.Engine.Types;

/// <summary>
/// A chess move: source square, destination square, move type, and promotion piece, packed into
/// a single 32-bit word.
///
/// ── Layout, 23 of 32 bits ────────────────────────────────────────────────────
///
///   0..5    from square        6 bits
///   6..11   to square          6 bits
///   12..19  move type          8 bits (a [Flags] byte)
///   20..22  promotion piece    3 bits (PieceType 0-6)
///
/// The obvious form — two Squares wrapping an int, plus two byte enums — measured twelve bytes
/// after padding, and the cost of that is multiplicative rather than additive. The search holds
/// MAX_PLY x MaxMoves of them in its per-ply move buffers (128 x 256), a 128 x 128 triangular PV
/// table, and a [2, 64, 64] counter-move table. Those are 512 KB, 192 KB and 96 KB at twelve
/// bytes, and the move buffers in particular are walked start to end at every node.
///
/// Equality is the other half of it. It used to be four field comparisons and a
/// HashCode.Combine; it is now one integer comparison. That is not a micro-detail of the type —
/// move ordering compares moves against the TT move, the two killers and the counter-move for
/// every move at every node, so this comparison runs tens of times per node.
///
/// Every accessor below is computed, so nothing outside this file had to change: call sites
/// still read move.From, move.To, move.MoveType and move.PromotionType.
///
/// Why uint and not ushort. From and to need twelve bits and would leave four, which is one
/// short of MoveType's eight — it is a [Flags] byte with Check and Checkmate at bits 5 and 6 —
/// plus three for the promotion piece. Squeezing those into four bits means re-encoding
/// MoveType as a dense enum, which would change what every call site reads. Not worth it: the
/// padding to four bytes is free in every array that matters, since nothing here is packed
/// tighter than its own alignment.
/// </summary>
public readonly struct Move : IEquatable<Move>
{
    private const int FromShift      = 0;
    private const int ToShift        = 6;
    private const int MoveTypeShift  = 12;
    private const int PromotionShift = 20;

    private const uint SquareMask    = 0x3F;
    private const uint MoveTypeMask  = 0xFF;
    private const uint PromotionMask = 0x7;

    /// <summary>
    /// The whole move. Zero is the default value, and it decodes to a1-a1 quiet with no
    /// promotion — which is what <c>default(Move)</c> decoded to before the packing, so the
    /// search's "no move yet" checks (<c>move != default</c>, <c>bestMove = default</c>) mean
    /// exactly what they meant.
    /// </summary>
    private readonly uint _packed;

    /// <summary>
    /// The source square (where the piece moves from).
    /// </summary>
    public Square From => Square.FromIndexUnsafe((int)((_packed >> FromShift) & SquareMask));

    /// <summary>
    /// The destination square (where the piece moves to).
    /// </summary>
    public Square To => Square.FromIndexUnsafe((int)((_packed >> ToShift) & SquareMask));

    /// <summary>
    /// The type of the move (capture, castling, promotion, etc.).
    /// </summary>
    public MoveType MoveType => (MoveType)(byte)((_packed >> MoveTypeShift) & MoveTypeMask);

    /// <summary>
    /// The piece type to promote to (if IsPromotion is true). Otherwise PieceType.None.
    /// </summary>
    public PieceType PromotionType =>
        (PieceType)(byte)((_packed >> PromotionShift) & PromotionMask);

    /// <summary>
    /// Creates a Move from source and destination squares, with an optional move type and
    /// promotion piece.
    /// </summary>
    public Move(Square from, Square to, MoveType moveType = MoveType.Quiet,
                PieceType promotionType = PieceType.None)
    {
        // No masking on the way in. A Square is 0-63 by construction, MoveType is a byte, and
        // PieceType tops out at King = 6, so every field already fits its width. Masking here
        // would cost four ANDs per move constructed to defend against values the type system
        // does not allow.
        _packed = ((uint)from.Index          << FromShift)
                | ((uint)to.Index            << ToShift)
                | ((uint)(byte)moveType      << MoveTypeShift)
                | ((uint)(byte)promotionType << PromotionShift);
    }

    /// <summary>
    /// Creates a Move from algebraic notation (e.g., "e2e4", "e7e8q" for promotion).
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if notation is invalid.</exception>
    public static Move FromAlgebraic(string notation)
    {
        if (string.IsNullOrEmpty(notation) || notation.Length < 4)
            throw new ArgumentException($"Invalid move notation: '{notation}'. Expected format: 'e2e4' or 'e7e8q'.");

        Square from = Square.FromAlgebraic(notation.Substring(0, 2));
        Square to = Square.FromAlgebraic(notation.Substring(2, 2));

        PieceType promotionType = PieceType.None;
        if (notation.Length == 5)
        {
            promotionType = notation[4] switch
            {
                'n' or 'N' => PieceType.Knight,
                'b' or 'B' => PieceType.Bishop,
                'r' or 'R' => PieceType.Rook,
                'q' or 'Q' => PieceType.Queen,
                _ => throw new ArgumentException($"Invalid promotion piece: '{notation[4]}'.")
            };
        }

        return new Move(from, to, MoveType.Quiet, promotionType);
    }

    /// <summary>
    /// Returns true if this move involves a pawn promotion.
    /// </summary>
    public bool IsPromotion => PromotionType != PieceType.None;

    /// <summary>
    /// Returns the algebraic notation of this move (e.g., "e2e4", "e7e8q").
    /// </summary>
    public override string ToString()
    {
        string notation = $"{From}{To}";
        if (IsPromotion)
        {
            notation += PromotionType switch
            {
                PieceType.Knight => "n",
                PieceType.Bishop => "b",
                PieceType.Rook => "r",
                PieceType.Queen => "q",
                _ => ""
            };
        }
        return notation;
    }

    /// <summary>
    /// Returns a more detailed representation of the move including its type.
    /// </summary>
    public string ToDetailedString()
    {
        string baseNotation = $"{From}{To}";

        string moveTypeName = MoveType switch
        {
            MoveType.Quiet => "",
            MoveType.Capture => " (capture)",
            MoveType.DoublePawnPush => " (pawn push)",
            MoveType.Castling => " (castle)",
            MoveType.EnPassant => " (en passant)",
            MoveType.Promotion => $" (promote to {PromotionType})",
            _ => ""
        };

        return baseNotation + moveTypeName;
    }

    public override bool Equals(object? obj) => obj is Move move && Equals(move);

    /// <summary>
    /// One integer comparison. This ran as four field comparisons before, once per move per
    /// comparison, and move ordering makes several of those per move at every node.
    /// </summary>
    public bool Equals(Move other) => _packed == other._packed;

    public override int GetHashCode() => (int)_packed;

    public static bool operator ==(Move left, Move right) => left._packed == right._packed;
    public static bool operator !=(Move left, Move right) => left._packed != right._packed;
}
