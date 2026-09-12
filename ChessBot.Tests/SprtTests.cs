namespace ChessBot.Tests;

using ChessBot.MatchRunner;
using Xunit;

/// <summary>
/// Gates for the sequential test that decides when a run has seen enough.
///
/// The point of SPRT here is to stop early, which means it is trusted to end a measurement. A
/// test that reaches a bound too eagerly discards good changes and adopts bad ones at rates
/// nobody chose, so what is checked is that the bounds come from alpha and beta, that the LLR
/// moves in the direction the games do, and that the degenerate cases refuse to conclude rather
/// than concluding wrongly.
/// </summary>
public class SprtTests
{
    private static readonly SprtSettings Standard = new() { Elo0 = 0, Elo1 = 5, Alpha = 0.05, Beta = 0.05 };

    [Fact]
    public void Bounds_ComeFromAlphaAndBeta()
    {
        // The textbook Wald bounds: log(beta/(1-alpha)) and log((1-beta)/alpha).
        Assert.Equal(Math.Log(0.05 / 0.95), Standard.LowerBound, 10);
        Assert.Equal(Math.Log(0.95 / 0.05), Standard.UpperBound, 10);
        Assert.True(Standard.LowerBound < 0 && Standard.UpperBound > 0);

        // Tighter error rates must make the bounds harder to reach, not easier.
        var strict = new SprtSettings { Alpha = 0.01, Beta = 0.01 };
        Assert.True(strict.UpperBound > Standard.UpperBound);
        Assert.True(strict.LowerBound < Standard.LowerBound);
    }

    [Fact]
    public void EloToScore_IsTheLogisticModelAndRoundTrips()
    {
        Assert.Equal(0.5, Sprt.EloToScore(0), 10);
        Assert.True(Sprt.EloToScore(100) > 0.5);
        Assert.True(Sprt.EloToScore(-100) < 0.5);

        Assert.Equal(50, Sprt.ScoreToElo(Sprt.EloToScore(50))!.Value, 6);

        // A clean sweep has no finite Elo, and must not be reported as an enormous one.
        Assert.Null(Sprt.ScoreToElo(1.0));
        Assert.Null(Sprt.ScoreToElo(0.0));
    }

    [Fact]
    public void Evaluate_WithNoGamesOrNoVarianceRefusesToConclude()
    {
        Assert.Equal(SprtVerdict.Continue, Sprt.Evaluate(Standard, 0, 0, 0).Verdict);

        // Every game a draw: the observed variance is zero, so there is no scale on which to
        // measure a difference. Continuing is right; dividing by it would not be.
        var allDraws = Sprt.Evaluate(Standard, 0, 200, 0);
        Assert.Equal(SprtVerdict.Continue, allDraws.Verdict);
        Assert.Equal(0, allDraws.Llr);

        // Same for a clean sweep, which has no variance either.
        Assert.Equal(SprtVerdict.Continue, Sprt.Evaluate(Standard, 50, 0, 0).Verdict);
    }

    [Fact]
    public void Evaluate_LlrRisesWithBsScoreAndFallsWithAs()
    {
        double even   = Sprt.Evaluate(Standard, 100, 200, 100).Llr;
        double bAhead = Sprt.Evaluate(Standard, 140, 200, 60).Llr;
        double aAhead = Sprt.Evaluate(Standard, 60, 200, 140).Llr;

        Assert.True(bAhead > even, $"B ahead should raise the LLR: {bAhead} vs {even}");
        Assert.True(aAhead < even, $"A ahead should lower the LLR: {aAhead} vs {even}");
    }

    [Fact]
    public void Evaluate_AcceptsH1WhenBIsClearlyStrongerAndH0WhenItIsNot()
    {
        // A large, one-sided sample must reach the upper bound: this is the case the run exists
        // to detect early.
        var strong = Sprt.Evaluate(Standard, winsB: 600, draws: 1200, winsA: 400);
        Assert.Equal(SprtVerdict.AcceptH1, strong.Verdict);
        Assert.True(strong.Llr >= Standard.UpperBound);
        Assert.True(strong.IsConclusive);

        // A long, dead-level run must accept H0 rather than running forever: "this change is
        // worth nothing" is a result.
        var level = Sprt.Evaluate(Standard, winsB: 1000, draws: 8000, winsA: 1000);
        Assert.Equal(SprtVerdict.AcceptH0, level.Verdict);
        Assert.True(level.Llr <= Standard.LowerBound);
    }

    [Fact]
    public void Evaluate_ASmallSampleStaysInconclusiveWhateverItsScore()
    {
        // Ten games cannot settle a 5-Elo question, and the test must not pretend otherwise.
        var small = Sprt.Evaluate(Standard, winsB: 7, draws: 0, winsA: 3);
        Assert.Equal(SprtVerdict.Continue, small.Verdict);
        Assert.Contains("inside", small.Explanation);
    }

    [Fact]
    public void Evaluate_WiderHypothesesNeedFewerGamesToSeparate()
    {
        // elo0=0 vs elo1=50 is a much coarser question than 0 vs 5, so the same games carry more
        // evidence towards it. This is what choosing the hypotheses actually buys.
        var narrow = new SprtSettings { Elo0 = 0, Elo1 = 5 };
        var wide   = new SprtSettings { Elo0 = 0, Elo1 = 50 };

        double narrowLlr = Sprt.Evaluate(narrow, 120, 160, 120).Llr;
        double wideLlr   = Sprt.Evaluate(wide,   120, 160, 120).Llr;

        // At a dead-level score both point at H0, and the wider hypothesis rejects it harder.
        Assert.True(wideLlr < narrowLlr, $"wide {wideLlr} should be further from H1 than narrow {narrowLlr}");
    }

    [Fact]
    public void Validate_RefusesParametersThatAreNotATest()
    {
        // An alpha outside (0,1) is not an error rate, and it makes the bounds NaN rather than
        // merely wrong. NaN compares false against everything, so the run would play every game
        // and report an SPRT that could never conclude — a silent non-result. This is how a
        // locale-mangled "--sprt-alpha 0.05" (parsed as 5) surfaced.
        Assert.Throws<ArgumentException>(() => new SprtSettings { Alpha = 5 }.Validate());
        Assert.Throws<ArgumentException>(() => new SprtSettings { Alpha = 0 }.Validate());
        Assert.Throws<ArgumentException>(() => new SprtSettings { Beta = 1 }.Validate());
        Assert.Throws<ArgumentException>(() => new SprtSettings { Beta = -0.1 }.Validate());

        // H0 must be the weaker hypothesis, or the test is asking its question backwards.
        Assert.Throws<ArgumentException>(() => new SprtSettings { Elo0 = 10, Elo1 = 5 }.Validate());
        Assert.Throws<ArgumentException>(() => new SprtSettings { Elo0 = 5, Elo1 = 5 }.Validate());

        Standard.Validate();                                    // the usual pair is accepted
        new SprtSettings { Elo0 = -5, Elo1 = 0 }.Validate();     // so is a regression test
    }

    [Fact]
    public void BoundsOfValidSettings_AreAlwaysFinite()
    {
        foreach (var settings in new[]
        {
            new SprtSettings { Alpha = 0.01, Beta = 0.01 },
            new SprtSettings { Alpha = 0.5,  Beta = 0.5  },
            new SprtSettings { Alpha = 0.99, Beta = 0.99 },
        })
        {
            settings.Validate();
            Assert.True(double.IsFinite(settings.UpperBound));
            Assert.True(double.IsFinite(settings.LowerBound));
        }
    }

    [Fact]
    public void Evaluate_ReportsTheRunningCountsItWasGiven()
    {
        var state = Sprt.Evaluate(Standard, winsB: 12, draws: 30, winsA: 8);

        Assert.Equal(50, state.Games);
        Assert.Equal(12, state.WinsB);
        Assert.Equal(30, state.Draws);
        Assert.Equal(8,  state.WinsA);
        Assert.False(string.IsNullOrWhiteSpace(state.Explanation));
    }
}
