namespace ChessBot.Uci;

using ChessBot.Engine.Types;

/// <summary>
/// Conversion between engine <see cref="Move"/> values and UCI long algebraic notation
/// ("e2e4", "e7e8q").
///
/// Parsing is deliberately defined as a lookup in the legal move list rather than as a
/// decoding of the four characters. The notation carries no move type: "e1g1" is a king step
/// or a castle, "e5d6" is a quiet move or an en passant capture, and only the position knows
/// which. Matching against generated legal moves recovers the correct <see cref="MoveType"/>
/// and, as a side effect, makes it impossible for the protocol layer to hand the board a move
/// the generator never produced.
/// </summary>
public static class UciMoveNotation
{
    /// <summary>
    /// UCI's "no move" token, used for a bestmove in a position that has none (mate or
    /// stalemate), where the protocol still requires a bestmove line.
    /// </summary>
    public const string NullMove = "0000";

    /// <summary>
    /// Renders a move in UCI long algebraic notation. Castling is written as the king's own
    /// two-square step (e1g1), which is what non-Chess960 UCI expects and what the generator
    /// already stores.
    ///
    /// Written out here rather than delegating to <see cref="Move.ToString"/>: that override
    /// serves human-readable output and may change, while this is a wire format that must not.
    /// </summary>
    public static string Format(Move move)
    {
        string text = $"{move.From.FileLetter}{move.From.RankNumber}{move.To.FileLetter}{move.To.RankNumber}";

        return move.PromotionType switch
        {
            PieceType.Knight => text + "n",
            PieceType.Bishop => text + "b",
            PieceType.Rook   => text + "r",
            PieceType.Queen  => text + "q",
            _                => text,
        };
    }

    /// <summary>
    /// Resolves a UCI move token against the legal moves of the position it applies to.
    /// Returns false for anything that is not a legal move in that position — malformed
    /// tokens, well-formed but illegal moves, and promotions to the wrong piece alike.
    /// </summary>
    public static bool TryParse(string? token, IReadOnlyList<Move> legalMoves, out Move move)
    {
        move = default;

        if (!TryParseSquares(token, out Square from, out Square to, out PieceType promotion))
            return false;

        for (int i = 0; i < legalMoves.Count; i++)
        {
            Move candidate = legalMoves[i];
            if (candidate.From == from && candidate.To == to && candidate.PromotionType == promotion)
            {
                move = candidate;
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Decodes the notation itself, without reference to a position. Split out so that a
    /// syntactically invalid token can be distinguished from a merely illegal one, and so the
    /// round-trip tests can exercise the decoding on its own.
    /// </summary>
    public static bool TryParseSquares(string? token, out Square from, out Square to, out PieceType promotion)
    {
        from      = default;
        to        = default;
        promotion = PieceType.None;

        if (token is null || (token.Length != 4 && token.Length != 5))
            return false;

        if (!TryParseSquare(token[0], token[1], out from)) return false;
        if (!TryParseSquare(token[2], token[3], out to))   return false;

        if (token.Length == 5)
        {
            promotion = char.ToLowerInvariant(token[4]) switch
            {
                'n' => PieceType.Knight,
                'b' => PieceType.Bishop,
                'r' => PieceType.Rook,
                'q' => PieceType.Queen,
                _   => PieceType.None,
            };

            if (promotion == PieceType.None) return false;
        }

        return true;
    }

    private static bool TryParseSquare(char fileChar, char rankChar, out Square square)
    {
        square = default;

        char file = char.ToLowerInvariant(fileChar);
        if (file < 'a' || file > 'h')      return false;
        if (rankChar < '1' || rankChar > '8') return false;

        square = new Square(file - 'a', rankChar - '1');
        return true;
    }
}
