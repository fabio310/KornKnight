using System;
using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Search;
using ChessBot.Engine.Types;

namespace ChessBot.Tests;

/// <summary>
/// A FEN that parses but describes an impossible position must be rejected where it enters the
/// engine, not four frames inside negamax. The corpus entry below did the latter for months.
/// </summary>
public class PositionLegalityTests
{
    /// <summary>The FEN that shipped as AbHarness.DefaultCorpus[8]: kings on h1 and g2.</summary>
    private const string AdjacentKingsCorpusFen = "8/8/8/8/8/8/6k1/R6K w - - 0 1";

    /// <summary>The same rook endgame with the black king one file further away.</summary>
    private const string LegalRookEndgameFen = "8/8/8/8/8/8/5k2/R6K w - - 0 1";

    private static IllegalPositionException AssertRejected(string fen)
    {
        var engine = new ChessEngine();
        return Assert.Throws<IllegalPositionException>(() => engine.LoadFen(fen));
    }

    [Fact]
    public void LoadFen_RejectsAdjacentKings()
    {
        var ex = AssertRejected("8/8/8/8/8/4k3/4K3/8 w - - 0 1");
        Assert.Equal(PositionViolation.AdjacentKings, ex.Violation);
    }

    [Fact]
    public void LoadFen_RejectsSideWithoutAKing()
    {
        var ex = AssertRejected("8/8/8/8/8/8/8/K7 w - - 0 1");
        Assert.Equal(PositionViolation.KingCount, ex.Violation);
    }

    [Fact]
    public void LoadFen_RejectsSideWithTwoKings()
    {
        var ex = AssertRejected("4k3/8/8/8/8/8/8/K5K1 w - - 0 1");
        Assert.Equal(PositionViolation.KingCount, ex.Violation);
    }

    [Fact]
    public void LoadFen_RejectsSideNotToMoveAlreadyInCheck()
    {
        // White to move, but the rook on e6 already checks the black king: the previous move
        // was illegal, so this position cannot have been reached.
        var ex = AssertRejected("4k3/8/4R3/8/8/8/8/4K3 w - - 0 1");
        Assert.Equal(PositionViolation.SideNotToMoveInCheck, ex.Violation);
    }

    [Fact]
    public void LoadFen_RejectsPawnOnFirstRank()
    {
        var ex = AssertRejected("4k3/8/8/8/8/8/8/P3K3 w - - 0 1");
        Assert.Equal(PositionViolation.PawnOnBackRank, ex.Violation);
    }

    [Fact]
    public void LoadFen_RejectsPawnOnEighthRank()
    {
        var ex = AssertRejected("p3k3/8/8/8/8/8/8/4K3 w - - 0 1");
        Assert.Equal(PositionViolation.PawnOnBackRank, ex.Violation);
    }

    [Fact]
    public void LoadFen_RejectsTheCorpusFenThatUsedToCrashTheSearch()
    {
        var ex = AssertRejected(AdjacentKingsCorpusFen);
        Assert.Equal(PositionViolation.AdjacentKings, ex.Violation);
        Assert.Equal(AdjacentKingsCorpusFen, ex.Fen);
        Assert.Contains("h1", ex.Message);
        Assert.Contains("g2", ex.Message);
    }

    [Fact]
    public void LoadFen_LeavesTheStartingPositionOnTheBoardAfterRejection()
    {
        var engine = new ChessEngine();
        Assert.Throws<IllegalPositionException>(() => engine.LoadFen(AdjacentKingsCorpusFen));

        Assert.Equal("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1", engine.ExportFen());
    }

    [Fact]
    public void LegalRookEndgame_SearchesToDepth12()
    {
        var engine = new ChessEngine();
        engine.LoadFen(LegalRookEndgameFen);

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 12 });

        Assert.Equal(12, result.DepthAchieved);
        Assert.Contains(result.BestMove, engine.GetLegalMoves());
    }
}
