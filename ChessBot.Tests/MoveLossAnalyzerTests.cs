namespace ChessBot.Tests;

using ChessBot.MatchRunner;
using Xunit;
using static ChessBot.MatchRunner.MoveLossAnalyzer;

/// <summary>
/// Move loss is the only measurement in this project that claims to say something about move
/// quality, so its exclusion rules have to be exactly right: a bound treated as a value, a mate
/// mis-categorised, or an engine failure aborting the run all corrupt the number quietly.
/// </summary>
public class MoveLossAnalyzerTests
{
    private static UciMoveResult Cp(int cp, string bound = "exact") =>
        new() { ScoreCp = cp, ScoreMate = null, ScoreBound = bound };

    private static UciMoveResult Mate(int inN, string bound = "exact") =>
        new() { ScoreCp = 0, ScoreMate = inN, ScoreBound = bound };

    private static MateCategory Classify(UciMoveResult best, UciMoveResult played) =>
        (MateCategory)typeof(MoveLossAnalyzer)
            .GetMethod("ClassifyMate", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static)!
            .Invoke(null, new object[] { best, played })!;

    // ── Mate categories ──────────────────────────────────────────────────────

    [Fact]
    public void EqualMateScores_AreUnchanged_NotADistanceChange()
    {
        // The played move mates in exactly as many moves as the best move: an identical
        // outcome, which the previous classifier reported as MateDistanceChanged.
        Assert.Equal(MateCategory.MateUnchanged, Classify(Mate(5), Mate(5)));
        Assert.Equal(MateCategory.MateUnchanged, Classify(Mate(-3), Mate(-3)));
    }

    [Fact]
    public void SameSideDifferentDistance_IsADistanceChange()
    {
        Assert.Equal(MateCategory.MateDistanceChanged, Classify(Mate(3), Mate(7)));
        Assert.Equal(MateCategory.MateDistanceChanged, Classify(Mate(-2), Mate(-9)));
    }

    [Fact]
    public void MateAvailableButPlayedMoveDoesNotMate_IsMissedForcedMate()
        => Assert.Equal(MateCategory.MissedForcedMate, Classify(Mate(4), Cp(120)));

    [Fact]
    public void PlayedMoveWalksIntoMate_IsEnteredForcedMate()
    {
        Assert.Equal(MateCategory.EnteredForcedMate, Classify(Cp(50), Mate(-3)));
        Assert.Equal(MateCategory.EnteredForcedMate, Classify(Mate(6), Mate(-2)));
    }

    [Fact]
    public void UnrestrictedSaysForcedMateAgainstUsButPlayedMoveDoesNot_IsEscapedForcedMate()
        => Assert.Equal(MateCategory.EscapedForcedMate, Classify(Mate(-4), Cp(-300)));

    [Fact]
    public void RestrictedFindsAMateTheUnrestrictedSearchDidNot_IsContradictory()
    {
        // A restricted search cannot legitimately beat the unrestricted search on the same
        // position, so this is an analysis contradiction, not a good move.
        Assert.Equal(MateCategory.ContradictoryMate, Classify(Cp(80), Mate(5)));
        Assert.Equal(MateCategory.ContradictoryMate, Classify(Mate(-6), Mate(4)));
    }

    [Fact]
    public void NoMateScores_IsNone()
        => Assert.Equal(MateCategory.None, Classify(Cp(30), Cp(10)));

    // ── Aggregation ──────────────────────────────────────────────────────────

    private static MoveLossRecord Rec(int n, int? loss, SampleEligibility e,
                                      MateCategory mate = MateCategory.None) =>
        new(n, true, "e2e4", "fen", 40, null, "exact", 40 - (loss ?? 0), null, "exact",
            loss, e, mate, 0, null);

    [Fact]
    public void Aggregate_AveragesOnlyEligibleSamplesAndCountsEveryExclusion()
    {
        var records = new[]
        {
            Rec(1,  0,   SampleEligibility.Exact),
            Rec(2,  20,  SampleEligibility.Exact),
            Rec(3,  40,  SampleEligibility.Exact),
            Rec(4,  300, SampleEligibility.Exact),
            Rec(5,  null, SampleEligibility.UnresolvedBound),
            Rec(6,  null, SampleEligibility.Inconsistent),
            Rec(7,  null, SampleEligibility.AnalysisError),
            Rec(8,  null, SampleEligibility.MateInvolved, MateCategory.MissedForcedMate),
            Rec(9,  null, SampleEligibility.MateInvolved, MateCategory.MateUnchanged),
        };

        var agg = MoveLossAggregateDto.From(records);

        Assert.Equal(9, agg.TotalSamples);
        Assert.Equal(4, agg.EligibleSamples);
        Assert.Equal(5, agg.ExcludedSamples);
        Assert.Equal(1, agg.LossesAbove200Cp);

        Assert.Equal(90.0, agg.MeanCentipawnLoss);     // (0+20+40+300)/4
        Assert.Equal(40,   agg.MedianCentipawnLoss);

        Assert.Equal(4, agg.EligibilityCounts["Exact"]);
        Assert.Equal(1, agg.EligibilityCounts["UnresolvedBound"]);
        Assert.Equal(1, agg.EligibilityCounts["Inconsistent"]);
        Assert.Equal(1, agg.EligibilityCounts["AnalysisError"]);
        Assert.Equal(2, agg.EligibilityCounts["MateInvolved"]);

        Assert.Equal(1, agg.MateCategoryCounts["MissedForcedMate"]);
        Assert.Equal(1, agg.MateCategoryCounts["MateUnchanged"]);
    }

    [Fact]
    public void Aggregate_WithNoEligibleSamples_ReportsNoAverageRatherThanZero()
    {
        var agg = MoveLossAggregateDto.From(new[]
        {
            Rec(1, null, SampleEligibility.AnalysisError),
            Rec(2, null, SampleEligibility.UnresolvedBound),
        });

        Assert.Equal(2, agg.TotalSamples);
        Assert.Equal(0, agg.EligibleSamples);
        Assert.Null(agg.MeanCentipawnLoss);
        Assert.Null(agg.MedianCentipawnLoss);
        Assert.Null(agg.P95CentipawnLoss);
    }
}

/// <summary>
/// A UCI "info" line carrying a score is a complete score report, not a patch. Without a reset
/// a mate flag or a bound qualifier from a shallower iteration survives onto a later line that
/// reports a plain exact centipawn score, silently corrupting the final result.
/// </summary>
public class UciAdapterScoreStateTests
{
    private static UciMoveResult Parse(params string[] lines)
    {
        var result = new UciMoveResult();
        foreach (var line in lines) UciAdapter.ParseInfoLine(line, result);
        return result;
    }

    [Fact]
    public void MateScoreFromEarlierIteration_DoesNotSurviveOntoALaterCentipawnScore()
    {
        var r = Parse(
            "info depth 5 seldepth 9 multipv 1 score mate 3 nodes 100 nps 1000 time 1 pv e2e4",
            "info depth 6 seldepth 11 multipv 1 score cp 42 nodes 200 nps 2000 time 2 pv e2e4 e7e5");

        Assert.Null(r.ScoreMate);
        Assert.Equal(42, r.ScoreCp);
        Assert.Equal("exact", r.ScoreBound);
    }

    [Fact]
    public void BoundQualifierFromEarlierIteration_DoesNotSurvive()
    {
        var r = Parse(
            "info depth 7 score cp 30 lowerbound nodes 100 nps 1000 time 1 pv e2e4",
            "info depth 8 score cp 25 nodes 200 nps 2000 time 2 pv e2e4");

        Assert.Equal("exact", r.ScoreBound);
        Assert.Equal(25, r.ScoreCp);
    }

    [Fact]
    public void CentipawnScoreFromEarlierIteration_DoesNotSurviveOntoALaterMateScore()
    {
        var r = Parse(
            "info depth 7 score cp 120 nodes 100 nps 1000 time 1 pv e2e4",
            "info depth 9 score mate -2 nodes 300 nps 3000 time 3 pv e2e4 e7e5");

        Assert.Equal(-2, r.ScoreMate);
        Assert.Equal(0,  r.ScoreCp);      // reset, not the stale 120
    }

    [Fact]
    public void InfoLineWithoutAScore_LeavesTheScoreUntouched()
    {
        var r = Parse(
            "info depth 8 score cp 55 nodes 100 nps 1000 time 1 pv e2e4",
            "info depth 9 currmove g1f3 currmovenumber 2");

        Assert.Equal(55, r.ScoreCp);
        Assert.Equal("exact", r.ScoreBound);
    }

    [Fact]
    public void UpperAndLowerBoundsAreRecorded()
    {
        Assert.Equal("lowerbound", Parse("info depth 8 score cp 55 lowerbound nodes 1 time 1").ScoreBound);
        Assert.Equal("upperbound", Parse("info depth 8 score cp 55 upperbound nodes 1 time 1").ScoreBound);
    }

    [Fact]
    public void PrincipalVariationAndStatsAreParsed()
    {
        var r = Parse("info depth 12 seldepth 20 multipv 1 score cp 15 nodes 5000 nps 250000 " +
                      "hashfull 120 tbhits 3 time 20 pv e2e4 e7e5 g1f3");

        Assert.Equal(12, r.Depth);
        Assert.Equal(20, r.SelDepth);
        Assert.Equal(5000, r.Nodes);
        Assert.Equal(250000, r.Nps);
        Assert.Equal(120, r.HashFull);
        Assert.Equal(3, r.TbHits);
        Assert.Equal(20, r.TimeMs);
        Assert.Equal("e2e4 e7e5 g1f3", r.Pv);
    }
}
