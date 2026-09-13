using Xunit;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Types;

namespace ChessBot.Tests;

/// <summary>
/// The passed pawn term, tested on the bitboards it actually consumes rather than through a whole
/// position, so each assertion isolates one thing the term claims to know.
///
/// The accumulator invariant (EvaluateFast == Evaluate) is covered by TaperedEvaluationTests and
/// applies to this term too, but it would be satisfied by a term that always returned zero — these
/// are the assertions that say it does something.
/// </summary>
public class PassedPawnTests
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

    [Fact]
    public void APawnWithNothingInFrontOfItIsPassed()
    {
        int score = PassedPawns.Evaluate(Bits("e5"), 0UL, Midgame);

        Assert.True(score > 0, $"a lone white pawn on e5 should score as passed, got {score}");
    }

    [Fact]
    public void APawnBlockedByAnEnemyPawnIsNotPassed()
    {
        // Neither pawn is passed: each stands in the other's way.
        Assert.Equal(0, PassedPawns.Evaluate(Bits("e5"), Bits("e6"), Midgame));
    }

    [Fact]
    public void AnEnemyPawnOnAnAdjacentFileAlsoStopsIt()
    {
        // The file beside it counts, because that pawn can capture the runner on its way.
        Assert.Equal(0, PassedPawns.Evaluate(Bits("e5"), Bits("d6"), Midgame));
    }

    [Fact]
    public void AnEnemyPawnBehindTheRunnerDoesNotStopIt()
    {
        // Black's d4 pawn is level with the runner rather than ahead of it, so e5 is still passed.
        // White's d3 is there to block d4 in turn — a lone enemy pawn anywhere behind would be a
        // passer itself, on the same rank, and the two bonuses would cancel to zero and prove
        // nothing.
        int score = PassedPawns.Evaluate(Bits("e5", "d3"), Bits("d4"), Midgame);

        Assert.True(score > 0, $"an enemy pawn behind the runner does not stop it, got {score}");
    }

    [Fact]
    public void TheBonusGrowsWithTheRank()
    {
        int sixth = PassedPawns.Evaluate(Bits("e6"), 0UL, Midgame);
        int third = PassedPawns.Evaluate(Bits("e3"), 0UL, Midgame);

        Assert.True(sixth > third, $"sixth rank {sixth} should beat third rank {third}");
    }

    [Fact]
    public void TheBonusIsWorthMoreInTheEndgame()
    {
        int endgame = PassedPawns.Evaluate(Bits("e6"), 0UL, Endgame);
        int midgame = PassedPawns.Evaluate(Bits("e6"), 0UL, Midgame);

        Assert.True(endgame > midgame,
            $"a runner is worth more with the pieces gone: endgame {endgame} vs midgame {midgame}");
    }

    [Fact]
    public void AProtectedPasserScoresAboveAnUnprotectedOne()
    {
        // d4 defends e5; a4 does not. Both extra pawns are themselves passed and on the same rank,
        // so the rank bonuses cancel and the difference is exactly the protection.
        int protectedPasser   = PassedPawns.Evaluate(Bits("e5", "d4"), 0UL, Midgame);
        int unprotectedPasser = PassedPawns.Evaluate(Bits("e5", "a4"), 0UL, Midgame);

        Assert.True(protectedPasser > unprotectedPasser,
            $"protected {protectedPasser} should beat unprotected {unprotectedPasser}");
    }

    [Fact]
    public void ConnectedPassersScoreAboveSeparatedOnes()
    {
        int connected = PassedPawns.Evaluate(Bits("e5", "d5"), 0UL, Midgame);
        int separated = PassedPawns.Evaluate(Bits("e5", "a5"), 0UL, Midgame);

        Assert.True(connected > separated,
            $"connected {connected} should beat separated {separated}");
    }

    [Theory]
    [InlineData(Midgame)]
    [InlineData(Endgame)]
    public void TheTermIsColourSymmetric(int phase)
    {
        // Black's mirror of a White position must score exactly its negation, or the evaluation
        // pays one side for a structure it does not pay the other for.
        int white = PassedPawns.Evaluate(Bits("e5", "d4"), Bits("a7"), phase);
        int black = PassedPawns.Evaluate(Bits("a2"), Bits("e4", "d5"), phase);

        Assert.Equal(white, -black);
    }
}
