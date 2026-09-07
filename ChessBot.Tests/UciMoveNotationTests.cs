using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Types;
using ChessBot.Uci;

namespace ChessBot.Tests;

/// <summary>
/// Covers the wire format: every legal move must survive Format → TryParse unchanged, and
/// nothing that is not a legal move may survive parsing at all. The special moves are what
/// make this non-trivial — the notation drops the move type, so parsing has to recover it
/// from the position.
/// </summary>
public class UciMoveNotationTests
{
    // Positions chosen so that between them the legal move lists contain castling (both
    // sides, both wings), en passant, promotions with and without capture, and ordinary moves.
    public static TheoryData<string> RoundTripPositions => new()
    {
        // Starting position.
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        // Kiwipete: castling both wings for White, plus a dense middlegame move list.
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        // Black to move with both castling rights available.
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R b KQkq - 0 1",
        // En passant available on d6.
        "rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 3",
        // White pawn on the seventh: quiet promotions and capture-promotions.
        "1n1n4/2P1P3/8/8/8/8/8/K6k w - - 0 1",
        // Black pawn on the second: the same for the other colour.
        "K6k/8/8/8/8/8/2p1p3/1N1N4 b - - 0 1",
    };

    [Theory]
    [MemberData(nameof(RoundTripPositions))]
    public void Format_ThenTryParse_ReturnsTheIdenticalMove(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);

        var legalMoves = engine.GetLegalMoves();
        Assert.NotEmpty(legalMoves);

        foreach (var move in legalMoves)
        {
            string text = UciMoveNotation.Format(move);

            Assert.True(UciMoveNotation.TryParse(text, legalMoves, out Move parsed),
                $"'{text}' did not parse back in position {fen}");

            // Equality includes MoveType and promotion piece, so this also proves the parser
            // recovered the special-move flags that the notation itself does not carry.
            Assert.Equal(move, parsed);
        }
    }

    [Theory]
    [MemberData(nameof(RoundTripPositions))]
    public void Format_ProducesDistinctTextForEveryLegalMove(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);

        var texts = engine.GetLegalMoves().Select(UciMoveNotation.Format).ToList();

        // Two legal moves sharing one notation would make the round trip above ambiguous:
        // the parser would return whichever came first in the list.
        Assert.Equal(texts.Count, texts.Distinct().Count());
    }

    [Fact]
    public void Format_Castling_UsesTheKingsTwoSquareStep()
    {
        var engine = new ChessEngine();
        engine.LoadFen("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");

        var castles = engine.GetLegalMoves()
                            .Where(m => m.MoveType.IsCastling())
                            .Select(UciMoveNotation.Format)
                            .OrderBy(s => s)
                            .ToArray();

        // Not "e1h1"/"e1a1": non-Chess960 UCI names the king's destination, not the rook's.
        Assert.Equal(new[] { "e1c1", "e1g1" }, castles);
    }

    [Fact]
    public void Format_EnPassant_LooksLikeAnOrdinaryPawnCapture()
    {
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/ppp1p1pp/8/3pPp2/8/8/PPPP1PPP/RNBQKBNR w KQkq d6 0 3");

        var legalMoves = engine.GetLegalMoves();
        var enPassant  = legalMoves.Single(m => (m.MoveType & MoveType.EnPassant) != 0);

        Assert.Equal("e5d6", UciMoveNotation.Format(enPassant));

        // And the parse recovers the EnPassant flag purely from the position.
        Assert.True(UciMoveNotation.TryParse("e5d6", legalMoves, out Move parsed));
        Assert.Equal(MoveType.EnPassant, parsed.MoveType & MoveType.EnPassant);
    }

    [Fact]
    public void Format_Promotion_AppendsTheLowercasePieceLetter()
    {
        var engine = new ChessEngine();
        engine.LoadFen("1n1n4/2P1P3/8/8/8/8/8/K6k w - - 0 1");

        var promotions = engine.GetLegalMoves()
                               .Where(m => m.IsPromotion && m.From == Square.FromAlgebraic("e7")
                                                         && m.To   == Square.FromAlgebraic("e8"))
                               .Select(UciMoveNotation.Format)
                               .OrderBy(s => s)
                               .ToArray();

        Assert.Equal(new[] { "e7e8b", "e7e8n", "e7e8q", "e7e8r" }, promotions);
    }

    [Fact]
    public void TryParse_PromotionToTheWrongPiece_ResolvesToThatPieceOnly()
    {
        var engine = new ChessEngine();
        engine.LoadFen("1n1n4/2P1P3/8/8/8/8/8/K6k w - - 0 1");

        var legalMoves = engine.GetLegalMoves();

        Assert.True(UciMoveNotation.TryParse("e7e8n", legalMoves, out Move knight));
        Assert.Equal(PieceType.Knight, knight.PromotionType);

        Assert.True(UciMoveNotation.TryParse("e7e8q", legalMoves, out Move queen));
        Assert.Equal(PieceType.Queen, queen.PromotionType);
    }

    [Fact]
    public void TryParse_PromotionMoveWithoutAPromotionPiece_IsRejected()
    {
        var engine = new ChessEngine();
        engine.LoadFen("1n1n4/2P1P3/8/8/8/8/8/K6k w - - 0 1");

        // The generator only ever produces promotions here, so the bare move matches nothing.
        // Accepting it would mean guessing a piece the GUI never named.
        Assert.False(UciMoveNotation.TryParse("e7e8", engine.GetLegalMoves(), out _));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("e2")]
    [InlineData("e2e")]
    [InlineData("e2e4e5")]
    [InlineData("i2i4")]      // file out of range
    [InlineData("e0e9")]      // rank out of range
    [InlineData("e7e8k")]     // promotion to king
    [InlineData("0000")]      // the null-move token is not a playable move
    public void TryParse_MalformedToken_IsRejected(string? token)
    {
        var engine = new ChessEngine();

        Assert.False(UciMoveNotation.TryParse(token, engine.GetLegalMoves(), out _));
    }

    [Fact]
    public void TryParse_WellFormedButIllegalMove_IsRejected()
    {
        var engine = new ChessEngine();

        // e2e5 is a legal-looking pawn move that no generator produces. This rejection is what
        // keeps a GUI's bad input from reaching the board.
        Assert.False(UciMoveNotation.TryParse("e2e5", engine.GetLegalMoves(), out _));
    }

    [Fact]
    public void TryParse_IsCaseInsensitive()
    {
        var engine = new ChessEngine();

        Assert.True(UciMoveNotation.TryParse("E2E4", engine.GetLegalMoves(), out Move move));
        Assert.Equal(Square.FromAlgebraic("e4"), move.To);
    }
}
