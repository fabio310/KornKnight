using Xunit;
using ChessBot.Engine;

namespace ChessBot.Tests;

/// <summary>
/// Perft over the six standard positions from https://www.chessprogramming.org/Perft_Results.
///
/// Perft is the correctness gate for move generation: a single wrong, missing or spurious move
/// anywhere in the tree changes the total, so an exact match at depth 5 or 6 is strong evidence
/// that castling, en passant, promotion, pin and check-evasion handling are all right. Shallow
/// depths are not: the starting position reaches neither a capture nor a castle before depth 4,
/// and the classic move-generation bugs (en passant that exposes the king along a rank, castling
/// through an attacked square, a pinned piece capturing the pinner) only appear once the tree is
/// deep enough to reach the positions that contain them.
///
/// The deep cases carry [Trait("Category", "Slow")]; see the README for running only the fast set
/// (dotnet test ChessBot.Tests --filter "Category!=Slow").
/// </summary>
public class PerftTests
{
    // ── The six standard positions ───────────────────────────────────────────

    private const string StartPositionFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    /// <summary>"Kiwipete": castling rights on both wings for both sides, plus dense tactics.</summary>
    private const string KiwipeteFen = "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1";

    /// <summary>
    /// Position 3: no castling rights, few pieces, and the en-passant capture that exposes the
    /// king along the fifth rank — the discovered-check case a naive en-passant implementation
    /// gets wrong.
    /// </summary>
    private const string Position3Fen = "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1";

    /// <summary>Position 4: promotions under check, with castling rights for Black only.</summary>
    private const string Position4Fen = "r3k2r/Pppp1ppp/1b3nbN/nP6/BBP1P3/q4N2/Pp1P2PP/R2Q1RK1 w kq - 0 1";

    /// <summary>
    /// Position 4 mirrored. Same counts as <see cref="Position4Fen"/> by symmetry, so any
    /// difference is a colour-asymmetric bug — a pawn direction, a rank offset, a castling
    /// square hardcoded for White.
    /// </summary>
    private const string Position4MirroredFen = "r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1";

    /// <summary>Position 5: a real middlegame with a pawn on the seventh and asymmetric rights.</summary>
    private const string Position5Fen = "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8";

    /// <summary>Position 6: a quiet, fully developed middlegame — the widest tree of the six.</summary>
    private const string Position6Fen = "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10";

    private static long Perft(string fen, int depth)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        return engine.RunPerft(depth);
    }

    // ── Fast set: depths 1-4 ─────────────────────────────────────────────────

    [Fact]
    public void Perft_Depth0_CountsThePositionItself()
    {
        Assert.Equal(1, new ChessEngine().RunPerft(0));
    }

    [Theory]
    [InlineData(1, 20)]
    [InlineData(2, 400)]
    [InlineData(3, 8902)]
    [InlineData(4, 197281)]
    public void Perft_StartPosition(int depth, long expected) =>
        Assert.Equal(expected, Perft(StartPositionFen, depth));

    [Theory]
    [InlineData(1, 48)]
    [InlineData(2, 2039)]
    [InlineData(3, 97862)]
    [InlineData(4, 4085603)]
    public void Perft_Kiwipete(int depth, long expected) =>
        Assert.Equal(expected, Perft(KiwipeteFen, depth));

    [Theory]
    [InlineData(1, 14)]
    [InlineData(2, 191)]
    [InlineData(3, 2812)]
    [InlineData(4, 43238)]
    public void Perft_Position3(int depth, long expected) =>
        Assert.Equal(expected, Perft(Position3Fen, depth));

    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, 264)]
    [InlineData(3, 9467)]
    [InlineData(4, 422333)]
    public void Perft_Position4(int depth, long expected) =>
        Assert.Equal(expected, Perft(Position4Fen, depth));

    [Theory]
    [InlineData(1, 6)]
    [InlineData(2, 264)]
    [InlineData(3, 9467)]
    [InlineData(4, 422333)]
    public void Perft_Position4Mirrored(int depth, long expected) =>
        Assert.Equal(expected, Perft(Position4MirroredFen, depth));

    [Theory]
    [InlineData(1, 44)]
    [InlineData(2, 1486)]
    [InlineData(3, 62379)]
    [InlineData(4, 2103487)]
    public void Perft_Position5(int depth, long expected) =>
        Assert.Equal(expected, Perft(Position5Fen, depth));

    [Theory]
    [InlineData(1, 46)]
    [InlineData(2, 2079)]
    [InlineData(3, 89890)]
    [InlineData(4, 3894594)]
    public void Perft_Position6(int depth, long expected) =>
        Assert.Equal(expected, Perft(Position6Fen, depth));

    // ── Slow set: depth 5 everywhere, depth 6 where the reference count is published ──
    // These are the depths that actually prove move generation. They are separated by trait
    // rather than weakened, so the fast set stays usable as an inner-loop check.

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(5, 4865609)]
    [InlineData(6, 119060324)]
    public void Perft_StartPosition_Deep(int depth, long expected) =>
        Assert.Equal(expected, Perft(StartPositionFen, depth));

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(5, 193690690)]
    public void Perft_Kiwipete_Deep(int depth, long expected) =>
        Assert.Equal(expected, Perft(KiwipeteFen, depth));

    /// <summary>
    /// Kiwipete to depth 6: eight billion nodes, minutes rather than seconds even in a release
    /// build. Kept separate from the rest of the slow set so it can be excluded on its own when
    /// a run has to fit in a shorter budget.
    /// </summary>
    [Fact]
    [Trait("Category", "Slow")]
    [Trait("Category", "VerySlow")]
    public void Perft_Kiwipete_Depth6() =>
        Assert.Equal(8031647685, Perft(KiwipeteFen, 6));

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(5, 674624)]
    [InlineData(6, 11030083)]
    public void Perft_Position3_Deep(int depth, long expected) =>
        Assert.Equal(expected, Perft(Position3Fen, depth));

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(5, 15833292)]
    public void Perft_Position4_Deep(int depth, long expected) =>
        Assert.Equal(expected, Perft(Position4Fen, depth));

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(5, 15833292)]
    public void Perft_Position4Mirrored_Deep(int depth, long expected) =>
        Assert.Equal(expected, Perft(Position4MirroredFen, depth));

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(5, 89941194)]
    public void Perft_Position5_Deep(int depth, long expected) =>
        Assert.Equal(expected, Perft(Position5Fen, depth));

    [Theory]
    [Trait("Category", "Slow")]
    [InlineData(5, 164075551)]
    public void Perft_Position6_Deep(int depth, long expected) =>
        Assert.Equal(expected, Perft(Position6Fen, depth));

    // ── Perft divide ─────────────────────────────────────────────────────────

    [Fact]
    public void PerftDivide_EntriesSumToTheTotal()
    {
        var engine = new ChessEngine();
        engine.LoadFen(KiwipeteFen);

        var entries = engine.RunPerftDivide(4);

        Assert.Equal(48, entries.Count);
        Assert.Equal(4085603, entries.Sum(e => e.Nodes));
    }

    [Fact]
    public void PerftDivide_EachEntryEqualsPerftAfterThatMove()
    {
        var engine = new ChessEngine();
        engine.LoadFen(KiwipeteFen);

        // This is the property that makes divide useful for localising a bug: an entry has to
        // be the perft of the position the move leads to, or following it down proves nothing.
        foreach (var entry in engine.RunPerftDivide(3))
        {
            engine.MakeMove(entry.Move);
            Assert.Equal(entry.Nodes, engine.RunPerft(2));
            engine.UndoMove();
        }
    }

    [Fact]
    public void PerftDivide_AtDepth1_EnumeratesTheLegalMoves()
    {
        var engine = new ChessEngine();
        engine.LoadFen(Position3Fen);

        var entries = engine.RunPerftDivide(1);

        Assert.Equal(engine.GetLegalMoves().Count, entries.Count);
        Assert.All(entries, e => Assert.Equal(1, e.Nodes));
    }

    [Fact]
    public void PerftDivide_LeavesThePositionUnchanged()
    {
        var engine = new ChessEngine();
        engine.LoadFen(Position5Fen);

        engine.RunPerftDivide(3);

        Assert.Equal(Position5Fen, engine.ExportFen());
    }

    [Fact]
    public void PerftDivide_Depth0_IsEmpty()
    {
        Assert.Empty(new ChessEngine().RunPerftDivide(0));
    }

    [Fact]
    public void PerftDivide_FormatsAsMoveAndCount()
    {
        var engine = new ChessEngine();
        engine.LoadFen("4k3/8/8/8/8/8/8/4K3 w - - 0 1");

        // The wire-format shape reference engines print, so a divide can be diffed directly.
        Assert.All(engine.RunPerftDivide(2), e => Assert.Matches(@"^[a-h][1-8][a-h][1-8][nbrq]?: \d+$", e.ToString()));
    }
}
