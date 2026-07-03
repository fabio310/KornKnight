using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Board;
using ChessBot.Engine.Types;

namespace ChessBot.Tests;

public class FenParsingTests
{
    [Fact]
    public void LoadFromFen_StartingPosition()
    {
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        var board = engine.GetBoardSnapshot();

        // Verify piece placement
        Assert.Equal(PieceType.Rook, board.GetPiece(Square.FromAlgebraic("a1")).Type);
        Assert.Equal(Color.White, board.GetPiece(Square.FromAlgebraic("a1")).Color);

        Assert.Equal(PieceType.Pawn, board.GetPiece(Square.FromAlgebraic("e2")).Type);
        Assert.Equal(Color.White, board.GetPiece(Square.FromAlgebraic("e2")).Color);

        Assert.Equal(PieceType.King, board.GetPiece(Square.FromAlgebraic("e8")).Type);
        Assert.Equal(Color.Black, board.GetPiece(Square.FromAlgebraic("e8")).Color);

        // Verify game state
        Assert.Equal(Color.White, board.State.ActiveColor);
        Assert.Equal(CastlingRights.FromFenString("KQkq"), board.State.CastlingRights);
        Assert.Equal(0, board.State.HalfmoveClock);
        Assert.Equal(1, board.State.FullmoveNumber);
    }

    [Fact]
    public void ExportToFen_StartingPosition()
    {
        var engine = new ChessEngine();
        var fen = engine.ExportFen();

        Assert.Equal("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", fen);
    }

    [Fact]
    public void LoadFromFen_NoWhiteCastling()
    {
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w kq - 0 1");

        var board = engine.GetBoardSnapshot();
        Assert.False(board.State.CastlingRights.WhiteKingSide);
        Assert.False(board.State.CastlingRights.WhiteQueenSide);
        Assert.True(board.State.CastlingRights.BlackKingSide);
        Assert.True(board.State.CastlingRights.BlackQueenSide);
    }

    [Fact]
    public void LoadFromFen_NoCastling()
    {
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w - - 0 1");

        var board = engine.GetBoardSnapshot();
        Assert.True(board.State.CastlingRights.IsEmpty);
    }

    [Fact]
    public void LoadFromFen_WithEnPassant()
    {
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1");

        var board = engine.GetBoardSnapshot();
        Assert.Equal(Square.FromAlgebraic("e3"), board.State.EnPassantTarget);
    }

    [Fact]
    public void LoadFromFen_WithHalfmoveAndFullmove()
    {
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 5 10");

        var board = engine.GetBoardSnapshot();
        Assert.Equal(5, board.State.HalfmoveClock);
        Assert.Equal(10, board.State.FullmoveNumber);
    }

    [Fact]
    public void LoadFromFen_CustomPosition()
    {
        var engine = new ChessEngine();
        engine.LoadFen("r1bqkb1r/pppp1ppp/2n2n2/1B2p3/4P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 0 4");

        var board = engine.GetBoardSnapshot();

        // Verify some key pieces
        Assert.Equal(PieceType.Knight, board.GetPiece(Square.FromAlgebraic("c6")).Type);
        Assert.Equal(Color.Black, board.GetPiece(Square.FromAlgebraic("c6")).Color);

        Assert.Equal(PieceType.Bishop, board.GetPiece(Square.FromAlgebraic("b5")).Type);
        Assert.Equal(Color.White, board.GetPiece(Square.FromAlgebraic("b5")).Color);

        Assert.Equal(Color.White, board.State.ActiveColor);
    }

    [Fact]
    public void LoadFromFen_InvalidFenThrows()
    {
        var engine = new ChessEngine();

        Assert.Throws<ArgumentException>(() =>
            engine.LoadFen("invalid fen string"));
    }

    [Fact]
    public void LoadFromFen_MissingPartsThrows()
    {
        var engine = new ChessEngine();

        Assert.Throws<ArgumentException>(() =>
            engine.LoadFen("rnbqkbnr/pppppppp/8/8 w"));
    }
}
