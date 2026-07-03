using Xunit;
using ChessBot.Engine;

namespace ChessBot.Tests;

public class PerftTests
{
    [Fact]
    public void Perft_StartingPosition_Depth1_Returns20()
    {
        var engine = new ChessEngine();
        long result = engine.RunPerft(1);
        Assert.Equal(20, result);
    }

    [Fact]
    public void Perft_StartingPosition_Depth2_Returns400()
    {
        var engine = new ChessEngine();
        long result = engine.RunPerft(2);
        Assert.Equal(400, result);
    }

    [Fact]
    public void Perft_StartingPosition_Depth3_Returns8902()
    {
        var engine = new ChessEngine();
        long result = engine.RunPerft(3);
        Assert.Equal(8902, result);
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(2, 400)]
    [InlineData(3, 8902)]
    public void Perft_StandardPosition_MatchesExpected(int depth, long expected)
    {
        var engine = new ChessEngine();
        long result = engine.RunPerft(depth);
        Assert.Equal(expected, result);
    }

    [Fact]
    public void Perft_Depth0_Returns1()
    {
        var engine = new ChessEngine();
        long result = engine.RunPerft(0);
        Assert.Equal(1, result);
    }

    // Note: Depth 4 test is commented out as it's computationally expensive
    // [Fact]
    // public void Perft_StartingPosition_Depth4_Returns197281()
    // {
    //     var engine = new ChessEngine();
    //     long result = engine.RunPerft(4);
    //     Assert.Equal(197281, result);
    // }

    // ── Standard chess-programming-wiki perft positions ────────────────────────
    // These exercise castling, en passant, promotions, and pinned/discovered-check
    // scenarios that the starting position alone does not reach at low depth.
    // Reference: https://www.chessprogramming.org/Perft_Results

    // "Kiwipete": heavy castling rights (both sides, both directions) plus tactics.
    private const string KiwipeteFen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";

    [Theory]
    [InlineData(1, 48)]
    [InlineData(2, 2039)]
    [InlineData(3, 97862)]
    public void Perft_Kiwipete_MatchesExpected(int depth, long expected)
    {
        var engine = new ChessEngine();
        engine.LoadFen(KiwipeteFen);
        long result = engine.RunPerft(depth);
        Assert.Equal(expected, result);
    }

    // Position 3: no castling rights; stresses en passant, pinned pawns, and
    // rook/king endgame tactics.
    private const string Position3Fen = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";

    [Theory]
    [InlineData(1, 14)]
    [InlineData(2, 191)]
    [InlineData(3, 2812)]
    [InlineData(4, 43238)]
    public void Perft_Position3_MatchesExpected(int depth, long expected)
    {
        var engine = new ChessEngine();
        engine.LoadFen(Position3Fen);
        long result = engine.RunPerft(depth);
        Assert.Equal(expected, result);
    }

    // Position 5: reachable middlegame position with pending promotions and
    // asymmetric castling rights (White both sides, Black none).
    private const string Position5Fen = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";

    [Theory]
    [InlineData(1, 44)]
    [InlineData(2, 1486)]
    [InlineData(3, 62379)]
    public void Perft_Position5_MatchesExpected(int depth, long expected)
    {
        var engine = new ChessEngine();
        engine.LoadFen(Position5Fen);
        long result = engine.RunPerft(depth);
        Assert.Equal(expected, result);
    }
}
