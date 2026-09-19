namespace ChessBot.Tests;

using System.Runtime.CompilerServices;
using ChessBot.Engine.Types;
using Xunit;

/// <summary>
/// Move is packed into a single 32-bit word. A packing bug in this type is the quiet kind: the
/// engine keeps running and simply plays a different move than the one it decided on, because
/// the field that got truncated was read back as something else that is still a legal move.
///
/// The tests below are the ones that would have caught each way the packing can be wrong: a
/// field too narrow, two fields overlapping, an accessor shifted by the wrong amount, and the
/// default value no longer meaning "no move".
/// </summary>
public class MoveTests
{
    [Fact]
    public void AMoveIsFourBytes()
    {
        // The reason for the whole change. Two Squares plus two byte enums measured twelve
        // bytes after padding, and the search holds 128 x 256 of them in per-ply move buffers
        // alone. If this ever goes back up, the cache argument in Move's own documentation has
        // quietly stopped being true.
        Assert.Equal(4, Unsafe.SizeOf<Move>());
    }

    [Fact]
    public void EverySquarePairSurvivesThePacking()
    {
        // All 4,096 combinations, so a from/to mask one bit short shows up rather than working
        // for the first half of the board.
        for (int from = 0; from < 64; from++)
        {
            for (int to = 0; to < 64; to++)
            {
                var move = new Move(new Square(from), new Square(to));

                Assert.Equal(from, move.From.Index);
                Assert.Equal(to, move.To.Index);
            }
        }
    }

    [Fact]
    public void EveryMoveTypeFlagCombinationSurvivesThePacking()
    {
        // MoveType is a [Flags] byte using all eight bits — Check and Checkmate live at 5 and 6
        // — so the field is eight bits wide and every value of it has to round-trip, not just
        // the named ones.
        for (int bits = 0; bits <= 255; bits++)
        {
            var move = new Move(Square.FromAlgebraic("e2"), Square.FromAlgebraic("e4"),
                                (MoveType)(byte)bits);

            Assert.Equal((MoveType)(byte)bits, move.MoveType);
            Assert.Equal(12, move.From.Index);   // e2
            Assert.Equal(28, move.To.Index);     // e4
        }
    }

    [Fact]
    public void EveryPromotionPieceSurvivesThePacking()
    {
        foreach (PieceType piece in new[]
                 {
                     PieceType.None, PieceType.Pawn, PieceType.Knight, PieceType.Bishop,
                     PieceType.Rook, PieceType.Queen, PieceType.King,
                 })
        {
            var move = new Move(Square.FromAlgebraic("h7"), Square.FromAlgebraic("g8"),
                                MoveType.Capture | MoveType.Promotion, piece);

            Assert.Equal(piece, move.PromotionType);
            Assert.Equal(MoveType.Capture | MoveType.Promotion, move.MoveType);
        }
    }

    [Fact]
    public void TheFieldsDoNotOverlap()
    {
        // The case that would catch a shift that is right for each field alone but lets two of
        // them share a bit: every field at once, each at a value that sets bits at the top of
        // its own width.
        var move = new Move(Square.FromAlgebraic("h8"), Square.FromAlgebraic("h8"),
                            (MoveType)0xFF, PieceType.King);

        Assert.Equal(63, move.From.Index);
        Assert.Equal(63, move.To.Index);
        Assert.Equal((MoveType)0xFF, move.MoveType);
        Assert.Equal(PieceType.King, move.PromotionType);
    }

    [Fact]
    public void TheDefaultMoveIsStillTheAbsenceOfAMove()
    {
        // The search says "no move here" with default(Move) and tests it with != default. That
        // only works if the zero word decodes to what it decoded to before the packing: a1-a1,
        // quiet, no promotion.
        var none = default(Move);

        Assert.Equal(0, none.From.Index);
        Assert.Equal(0, none.To.Index);
        Assert.Equal(MoveType.Quiet, none.MoveType);
        Assert.Equal(PieceType.None, none.PromotionType);
        Assert.False(none.IsPromotion);

        Assert.Equal(default, none);
        Assert.NotEqual(default, new Move(Square.FromAlgebraic("e2"), Square.FromAlgebraic("e4")));
    }

    [Fact]
    public void EqualityDistinguishesEveryFieldSeparately()
    {
        var a2 = Square.FromAlgebraic("a2");
        var a4 = Square.FromAlgebraic("a4");
        var b2 = Square.FromAlgebraic("b2");

        var baseline = new Move(a2, a4, MoveType.Quiet, PieceType.None);

        Assert.Equal(baseline, new Move(a2, a4, MoveType.Quiet, PieceType.None));
        Assert.True(baseline == new Move(a2, a4, MoveType.Quiet, PieceType.None));

        // Each of the four fields, changed alone.
        Assert.NotEqual(baseline, new Move(b2, a4, MoveType.Quiet, PieceType.None));
        Assert.NotEqual(baseline, new Move(a2, b2, MoveType.Quiet, PieceType.None));
        Assert.NotEqual(baseline, new Move(a2, a4, MoveType.Capture, PieceType.None));
        Assert.NotEqual(baseline, new Move(a2, a4, MoveType.Quiet, PieceType.Queen));

        Assert.True(baseline != new Move(a2, a4, MoveType.Capture, PieceType.None));
    }

    [Fact]
    public void EqualMovesHashEqually()
    {
        var one = new Move(Square.FromAlgebraic("g1"), Square.FromAlgebraic("f3"),
                           MoveType.Quiet, PieceType.None);
        var two = new Move(Square.FromAlgebraic("g1"), Square.FromAlgebraic("f3"),
                           MoveType.Quiet, PieceType.None);

        Assert.Equal(one.GetHashCode(), two.GetHashCode());
    }

    [Theory]
    [InlineData("e2e4")]
    [InlineData("a1a1")]
    [InlineData("h7h8q")]
    [InlineData("b2b1n")]
    public void AlgebraicNotationRoundTrips(string notation)
    {
        Assert.Equal(notation, Move.FromAlgebraic(notation).ToString());
    }
}
