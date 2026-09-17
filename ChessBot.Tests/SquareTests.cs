namespace ChessBot.Tests;

using ChessBot.Engine.Types;
using Xunit;

/// <summary>
/// Square is the smallest type in the engine and one of the most frequently constructed: move
/// generation builds several per move and tens per node. Two things about it are load-bearing
/// and neither is obvious from reading it.
///
/// The file and rank accessors use a mask and a shift where they used to use a remainder and a
/// division. Those agree only because the index is non-negative — C# rounds integer division
/// toward zero and gives the remainder the sign of the dividend, so for a negative index the
/// two forms differ. The validating constructor is what guarantees non-negative, which makes
/// the unchecked factories below the place where that guarantee could be lost.
///
/// The unchecked factories exist because the validating constructor was too expensive where it
/// sat — its throw path kept it from being inlined into move generation's innermost loops, and
/// removing it there was worth 7.4% of node rate. The cost of that is that a bad index no
/// longer throws; it silently names the wrong square. Perft is the real gate on that, and these
/// tests are the cheap one.
/// </summary>
public class SquareTests
{
    [Fact]
    public void FileAndRankAgreeWithDivisionOverEverySquare()
    {
        for (int index = 0; index < 64; index++)
        {
            var square = new Square(index);

            Assert.Equal(index % 8, square.File);
            Assert.Equal(index / 8, square.Rank);
            Assert.Equal(index, square.Index);
        }
    }

    [Fact]
    public void TheUncheckedIndexFactoryMatchesTheValidatingConstructor()
    {
        for (int index = 0; index < 64; index++)
        {
            Assert.Equal(new Square(index), Square.FromIndexUnsafe(index));
            Assert.Equal(index, Square.FromIndexUnsafe(index).Index);
        }
    }

    [Fact]
    public void TheUncheckedFileRankFactoryMatchesTheValidatingConstructor()
    {
        // Every file/rank pair, because the factory computes (rank << 3) + file by hand rather
        // than deferring to the constructor, and an argument order swapped at one call site
        // would be invisible on the diagonal.
        for (int file = 0; file < 8; file++)
        {
            for (int rank = 0; rank < 8; rank++)
            {
                var expected = new Square(file, rank);
                var actual   = Square.FromFileRankUnsafe(file, rank);

                Assert.Equal(expected, actual);
                Assert.Equal(file, actual.File);
                Assert.Equal(rank, actual.Rank);
            }
        }
    }

    [Fact]
    public void TheValidatingConstructorStillRejectsWhatItAlwaysDid()
    {
        // The unchecked path is an addition, not a replacement. Everything that parses input
        // from outside the engine — FEN, algebraic notation, a UI click — still goes through
        // the constructor, and it still refuses an impossible square.
        Assert.Throws<ArgumentOutOfRangeException>(() => new Square(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Square(64));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Square(8, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Square(0, 8));
        Assert.Throws<ArgumentOutOfRangeException>(() => new Square(-1, 0));
    }

    [Theory]
    [InlineData("a1", 0, 0, 0)]
    [InlineData("h1", 7, 7, 0)]
    [InlineData("a8", 56, 0, 7)]
    [InlineData("h8", 63, 7, 7)]
    [InlineData("e4", 28, 4, 3)]
    public void AlgebraicNotationRoundTrips(string notation, int index, int file, int rank)
    {
        var square = Square.FromAlgebraic(notation);

        Assert.Equal(index, square.Index);
        Assert.Equal(file, square.File);
        Assert.Equal(rank, square.Rank);
        Assert.Equal(notation, square.ToString());
    }
}
