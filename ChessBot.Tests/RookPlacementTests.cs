using Xunit;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Types;

namespace ChessBot.Tests;

/// <summary>
/// The rook placement term, tested on the bitboards it consumes rather than through a whole
/// position, so each assertion isolates one thing the term claims to know.
///
/// The accumulator invariant (EvaluateFast == Evaluate) is covered in GamePhaseEvaluationTests and
/// applies here too, but it would be satisfied by a term that always returned zero — these are the
/// assertions that say it does something.
/// </summary>
public class RookPlacementTests
{
    private const int Midgame = GamePhase.Max;   // full starting array
    private const int Endgame = 0;               // pieces gone

    private static ulong Bits(params string[] squares)
    {
        ulong bits = 0UL;
        foreach (string name in squares)
            bits |= 1UL << Square.FromAlgebraic(name).Index;
        return bits;
    }

    private static int White(ulong rooks, ulong whitePawns, ulong blackPawns, int phase)
        => RookFiles.Evaluate(rooks, 0UL, whitePawns, blackPawns, phase);

    [Fact]
    public void ARookOnAFileWithNoPawnsAtAllScoresMostForTheFile()
    {
        // d-file empty of pawns either way; the rook is on rank 3, so the seventh-rank bonus is
        // not in the number.
        int open     = White(Bits("d3"), Bits("a2"), Bits("a7"), Midgame);
        int halfOpen = White(Bits("d3"), Bits("a2"), Bits("a7", "d7"), Midgame);
        int closed   = White(Bits("d3"), Bits("a2", "d2"), Bits("a7", "d7"), Midgame);

        Assert.True(open > halfOpen, $"open {open} should beat half-open {halfOpen}");
        Assert.True(halfOpen > closed, $"half-open {halfOpen} should beat closed {closed}");
        Assert.Equal(0, closed);
    }

    [Fact]
    public void OurOwnPawnIsWhatClosesAFile()
    {
        // A file with only an enemy pawn on it is half-open for us: their pawn is a target, ours
        // would be a blocker. The asymmetry is the whole point of distinguishing the two.
        int theirPawnOnly = White(Bits("d3"), 0UL, Bits("d7"), Midgame);
        int ourPawnOnly   = White(Bits("d3"), Bits("d2"), 0UL, Midgame);

        Assert.True(theirPawnOnly > 0, $"an enemy pawn leaves the file half-open, got {theirPawnOnly}");
        Assert.Equal(0, ourPawnOnly);
    }

    [Fact]
    public void ARookOnTheSeventhIsPaidForSeparatelyFromItsFile()
    {
        // Same closed file in both, so the only difference is the rank.
        int seventh = White(Bits("d7"), Bits("d2"), Bits("d6"), Midgame);
        int third   = White(Bits("d3"), Bits("d2"), Bits("d6"), Midgame);

        Assert.True(seventh > third, $"seventh {seventh} should beat third {third}");
        Assert.Equal(0, third);
    }

    [Fact]
    public void TheSeventhRankIsCountedFromEachSidesOwnEnd()
    {
        // Black's seventh rank is rank 2 in absolute terms. Mirrored positions must score as exact
        // opposites, or the term would hand one colour a bonus the other cannot earn.
        int white = RookFiles.Evaluate(Bits("d7"), 0UL, Bits("d2"), Bits("d6"), Midgame);
        int black = RookFiles.Evaluate(0UL, Bits("d2"), Bits("d3"), Bits("d7"), Midgame);

        Assert.True(white > 0);
        Assert.Equal(-white, black);
    }

    [Fact]
    public void TheSeventhRankMattersMoreInAnEndgameAndTheOpenFileLess()
    {
        int seventhMid = White(Bits("d7"), Bits("d2"), Bits("d6"), Midgame);
        int seventhEnd = White(Bits("d7"), Bits("d2"), Bits("d6"), Endgame);

        int openMid = White(Bits("d3"), Bits("a2"), Bits("a7"), Midgame);
        int openEnd = White(Bits("d3"), Bits("a2"), Bits("a7"), Endgame);

        Assert.True(seventhEnd > seventhMid, $"seventh: {seventhMid} → {seventhEnd}");
        Assert.True(openEnd < openMid, $"open file: {openMid} → {openEnd}");
    }

    [Fact]
    public void TwoRooksOnTheSameOpenFileAreBothPaid()
    {
        int one = White(Bits("d1"), 0UL, 0UL, Midgame);
        int two = White(Bits("d1", "d2"), 0UL, 0UL, Midgame);

        Assert.Equal(2 * one, two);
    }

    [Fact]
    public void AMirroredPositionScoresToZero()
    {
        // Colour symmetry across the whole term: the same structure for both sides must cancel.
        int score = RookFiles.Evaluate(Bits("d1", "e7"), Bits("d8", "e2"),
                                       Bits("a2", "b2"), Bits("a7", "b7"), Midgame);
        Assert.Equal(0, score);
    }
}
