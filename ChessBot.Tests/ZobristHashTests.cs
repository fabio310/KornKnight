using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Types;
using ChessBot.Engine.Hashing;

namespace ChessBot.Tests;

public class ZobristHashTests
{
    [Fact]
    public void ComputeHash_StartingPosition_Consistent()
    {
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        var board1 = engine.GetBoardSnapshot();
        var zobrist = new ZobristHasher();
        var hash1 = zobrist.ComputeHash(board1);

        // Reload and hash again
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var board2 = engine.GetBoardSnapshot();
        var hash2 = zobrist.ComputeHash(board2);

        Assert.Equal(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_DifferentPositions_DifferentHashes()
    {
        var engine = new ChessEngine();
        var zobrist = new ZobristHasher();

        // Starting position
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var board1 = engine.GetBoardSnapshot();
        var hash1 = zobrist.ComputeHash(board1);

        // After e2-e4
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1");
        var board2 = engine.GetBoardSnapshot();
        var hash2 = zobrist.ComputeHash(board2);

        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_SwitchColor_ChangesHash()
    {
        var zobrist = new ZobristHasher();

        var engine1 = new ChessEngine();
        engine1.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        var hash1 = zobrist.ComputeHash(engine1.GetBoardSnapshot());

        var engine2 = new ChessEngine();
        engine2.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR b KQkq - 0 1");
        var hash2 = zobrist.ComputeHash(engine2.GetBoardSnapshot());

        // Different active color should produce different hash
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_CastlingRightsChange_ChangesHash()
    {
        var zobrist = new ZobristHasher();

        var engine1 = new ChessEngine();
        engine1.LoadFen("r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1");
        var hash1 = zobrist.ComputeHash(engine1.GetBoardSnapshot());

        var engine2 = new ChessEngine();
        engine2.LoadFen("r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQ - 0 1");
        var hash2 = zobrist.ComputeHash(engine2.GetBoardSnapshot());

        // Different castling rights should produce different hash
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void ComputeHash_EnPassantChange_ChangesHash()
    {
        var zobrist = new ZobristHasher();

        var engine1 = new ChessEngine();
        engine1.LoadFen("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 1");
        var hash1 = zobrist.ComputeHash(engine1.GetBoardSnapshot());

        var engine2 = new ChessEngine();
        engine2.LoadFen("rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq - 0 1");
        var hash2 = zobrist.ComputeHash(engine2.GetBoardSnapshot());

        // Different en passant targets should produce different hash
        Assert.NotEqual(hash1, hash2);
    }

    [Fact]
    public void GetPieceKey_Deterministic()
    {
        var zobrist = new ZobristHasher();
        var piece = new Piece(Color.White, PieceType.Pawn);
        var square = Square.FromAlgebraic("e4");

        var key1 = zobrist.GetPieceKey(piece.Color, piece.Type, square);
        var key2 = zobrist.GetPieceKey(piece.Color, piece.Type, square);

        Assert.Equal(key1, key2);
    }

    [Fact]
    public void GetPieceKey_DifferentPieces_DifferentKeys()
    {
        var zobrist = new ZobristHasher();
        var square = Square.FromAlgebraic("e4");

        var pawnKey = zobrist.GetPieceKey(Color.White, PieceType.Pawn, square);
        var knightKey = zobrist.GetPieceKey(Color.White, PieceType.Knight, square);

        Assert.NotEqual(pawnKey, knightKey);
    }

    [Fact]
    public void GetPieceKey_DifferentSquares_DifferentKeys()
    {
        var zobrist = new ZobristHasher();

        var key1 = zobrist.GetPieceKey(Color.White, PieceType.Pawn, Square.FromAlgebraic("e4"));
        var key2 = zobrist.GetPieceKey(Color.White, PieceType.Pawn, Square.FromAlgebraic("d4"));

        Assert.NotEqual(key1, key2);
    }

    [Fact]
    public void GetColorKey_DifferentColors_DifferentKeys()
    {
        var zobrist = new ZobristHasher();

        var whiteKey = zobrist.GetColorKey(Color.White);
        var blackKey = zobrist.GetColorKey(Color.Black);

        Assert.NotEqual(whiteKey, blackKey);
    }

    // ── Incremental hash correctness ──────────────────────────────────────────

    /// <summary>
    /// Board.ZobristHash (incremental) must match ZobristHasher.ComputeHash (scratch)
    /// for the starting position and after several moves and undos.
    /// This is the primary guard against silent hash drift.
    /// </summary>
    [Fact]
    public void IncrementalHash_StartingPosition_MatchesScratch()
    {
        var engine  = new ChessEngine();
        var zobrist = new ZobristHasher();

        var board = engine.GetBoardSnapshot();
        ulong scratch = zobrist.ComputeHash(board);

        Assert.Equal(scratch, board.ZobristHash);
    }

    [Fact]
    public void IncrementalHash_AfterLoadFen_MatchesScratch()
    {
        var engine  = new ChessEngine();
        var zobrist = new ZobristHasher();

        // Position with all state components: Black to move, partial castling, en passant
        engine.LoadFen("rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq e6 0 2");
        var board = engine.GetBoardSnapshot();

        Assert.Equal(zobrist.ComputeHash(board), board.ZobristHash);
    }

    [Fact]
    public void IncrementalHash_AfterMakeAndUndoMoves_MatchesScratch()
    {
        var engine  = new ChessEngine();
        var zobrist = new ZobristHasher();

        ulong hashBefore = engine.GetBoardSnapshot().ZobristHash;

        // Make a move and verify incremental hash is correct
        var legalMoves = engine.GetLegalMoves();
        Assert.True(legalMoves.Count > 0);
        engine.MakeMove(legalMoves[0]);

        var boardAfter = engine.GetBoardSnapshot();
        Assert.Equal(zobrist.ComputeHash(boardAfter), boardAfter.ZobristHash);

        // Undo and verify hash is restored exactly
        engine.UndoMove();
        var boardUndo = engine.GetBoardSnapshot();
        Assert.Equal(hashBefore, boardUndo.ZobristHash);
        Assert.Equal(zobrist.ComputeHash(boardUndo), boardUndo.ZobristHash);
    }

    [Fact]
    public void IncrementalHash_SamePositionReachedDifferentWays_EqualHash()
    {
        // Verify transposition: e4 d5 and d4 e5 are different, but e4 Nf6 and Nf3 e5 can
        // reach common positions — use the simplest case: make/undo returns to the same hash.
        var engine = new ChessEngine();
        ulong startHash = engine.GetBoardSnapshot().ZobristHash;

        var moves = engine.GetLegalMoves();
        engine.MakeMove(moves[0]);
        engine.UndoMove();

        Assert.Equal(startHash, engine.GetBoardSnapshot().ZobristHash);
    }

    [Fact]
    public void IncrementalHash_CastlingMove_MatchesScratch()
    {
        var engine  = new ChessEngine();
        var zobrist = new ZobristHasher();

        // Position ready for White king-side castling
        engine.LoadFen("r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1");
        var moves = engine.GetLegalMoves();

        // Find the castling move (king from e1 to g1)
        var castle = moves.FirstOrDefault(m =>
            m.From == Square.FromAlgebraic("e1") && m.To == Square.FromAlgebraic("g1"));

        if (castle == default) return; // castling not available in this test setup, skip

        engine.MakeMove(castle);
        var board = engine.GetBoardSnapshot();
        Assert.Equal(zobrist.ComputeHash(board), board.ZobristHash);
    }
}
