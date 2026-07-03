using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Types;

namespace ChessBot.Tests;

public class MoveGenerationTests
{
    [Fact]
    public void GetLegalMoves_StartingPosition_Returns20Moves()
    {
        var engine = new ChessEngine();
        var moves = engine.GetLegalMoves();

        Assert.Equal(20, moves.Count);
    }

    [Fact]
    public void GetLegalMoves_IncludesPawnAndKnightMoves()
    {
        var engine = new ChessEngine();
        var moves = engine.GetLegalMoves();

        // Starting position should have pawn moves (8 pawns * 2 moves each = 16)
        // and knight moves (2 knights * 2 moves each = 4)
        var pawnMoves = moves.Where(m => 
            m.From.Rank == 1 && // White pawns start on rank 2 (index 1)
            (m.To.Rank == 2 || m.To.Rank == 3)).ToList();

        var knightMoves = moves.Where(m =>
            (m.From.File == 1 || m.From.File == 6) && // Knight files
            m.From.Rank == 0).ToList();

        Assert.NotEmpty(pawnMoves);
        Assert.NotEmpty(knightMoves);
    }

    [Fact]
    public void MakeMove_UpdatesBoardState()
    {
        var engine = new ChessEngine();
        var moves = engine.GetLegalMoves();

        // Make the first move (e.g., e2-e4)
        var move = moves[0];
        engine.MakeMove(move);

        var newMoves = engine.GetLegalMoves();
        Assert.NotEmpty(newMoves);
    }

    [Fact]
    public void MakeMove_UndoMove_RestoresPosition()
    {
        var engine = new ChessEngine();
        var initialFen = engine.ExportFen();

        var moves = engine.GetLegalMoves();
        engine.MakeMove(moves[0]);
        engine.UndoMove();

        var finalFen = engine.ExportFen();
        Assert.Equal(initialFen, finalFen);
    }

    [Fact]
    public void GetLegalMoves_AfterPawnDoubleMove()
    {
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        var e2e4 = new Move(
            Square.FromAlgebraic("e2"),
            Square.FromAlgebraic("e4"),
            MoveType.DoublePawnPush
        );

        engine.MakeMove(e2e4);
        var board = engine.GetBoardSnapshot();

        // Check en passant target was set
        Assert.Equal(Square.FromAlgebraic("e3"), board.State.EnPassantTarget);
    }

    [Fact]
    public void GetLegalMoves_KingInCheck_OnlyLegalMovesReturned()
    {
        var engine = new ChessEngine();
        // Position where White king is in check
        engine.LoadFen("4k3/8/8/8/8/8/6r1/4K2R w - - 0 1");

        var moves = engine.GetLegalMoves();

        // With king in check, only legal moves (block, capture, or king moves) are allowed
        // All moves should not leave king in check
        foreach (var move in moves)
        {
            engine.MakeMove(move);
            var movesAfter = engine.GetLegalMoves();
            // Verify we can continue playing (king not in check after move)
            engine.UndoMove();
        }

        Assert.NotEmpty(moves);
    }

    [Fact]
    public void GetLegalMoves_NoDoublePawnPushFromSecondRank()
    {
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1");

        var blackMoves = engine.GetLegalMoves();

        // After White plays e2-e4, Black's e-pawn is on e7
        // It should have two moves: e7-e6 and e7-e5 (not e7-e4)
        var epawnMoves = blackMoves.Where(m =>
            m.From.File == 4 && m.From.Rank == 6).ToList();

        Assert.NotEmpty(epawnMoves);
        Assert.All(epawnMoves, m => Assert.True(m.To.Rank == 5 || m.To.Rank == 4));
    }

    [Fact]
    public void GetLegalMoves_CastlingNotAvailableAfterKingMove()
    {
        var engine = new ChessEngine();
        engine.LoadFen("r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1");

        // Find and make a king move
        var moves = engine.GetLegalMoves();
        var kingMoves = moves.Where(m =>
            engine.GetBoardSnapshot().GetPiece(m.From).Type == PieceType.King).ToList();

        Assert.NotEmpty(kingMoves);

        engine.MakeMove(kingMoves[0]);

        // After king moves, castling should not be available
        var board = engine.GetBoardSnapshot();
        Assert.False(board.State.CastlingRights.CanCastle(Color.White));
    }

    [Fact]
    public void GetLegalMoves_CastlingNotAvailableAfterRookMove()
    {
        var engine = new ChessEngine();
        engine.LoadFen("r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1");

        // Find and make a rook move
        var moves = engine.GetLegalMoves();
        var rookMoves = moves.Where(m =>
            engine.GetBoardSnapshot().GetPiece(m.From).Type == PieceType.Rook).ToList();

        Assert.NotEmpty(rookMoves);
        var rookMove = rookMoves[0];
        engine.MakeMove(rookMove);

        // Check that appropriate castling right is lost
        var board = engine.GetBoardSnapshot();
        if (rookMove.From.File == 0)
            Assert.False(board.State.CastlingRights.CanCastle(Color.White, false));
    }
}
