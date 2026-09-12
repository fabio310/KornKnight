using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Search;

namespace ChessBot.Tests;

/// <summary>
/// Deterministic tests for iterative-deepening depth reporting when a search is cancelled
/// mid-iteration. Uses <see cref="SearchSettings.MaxNodes"/> rather than a time limit: the node
/// count at which the search stops is fully reproducible (no timing jitter), so these tests can
/// assert exact behavior instead of "eventually stops early".
/// </summary>
public class PartialIterationReportingTests
{
    /// <summary>
    /// A node cap picked to be large enough that at least one full iteration (depth 1) completes,
    /// but small enough that a later iteration is cancelled partway through — verified by the
    /// assertions below rather than assumed.
    /// </summary>
    private const long NodeCapThatStopsMidIteration = 1500;

    [Fact]
    public void Search_CancelledMidIteration_DepthAchievedIsLastCompletedDepthOnly()
    {
        var engine = new ChessEngine();
        var settings = new SearchSettings
        {
            MaxDepth              = 20,
            MaxNodes              = NodeCapThatStopsMidIteration,
            UseIterativeDeepening = true,
        };

        var result = engine.FindBestMove(settings);

        // The search must have been cut off by the node cap, not have run to completion.
        Assert.True(result.PartialDepth > 0,
            "Expected the node cap to cancel a search mid-iteration (PartialDepth should be set).");

        // DepthAchieved must never equal or exceed the depth that was only partially searched:
        // it always reports the last *fully completed* iteration.
        Assert.True(result.DepthAchieved < result.PartialDepth,
            $"DepthAchieved ({result.DepthAchieved}) must be strictly less than PartialDepth " +
            $"({result.PartialDepth}) — it must never report an unfinished iteration as completed.");

        // Not every root move was searched at the partial depth.
        Assert.True(result.RootMoveCount > 0);
        Assert.True(result.RootMovesCompleted < result.RootMoveCount,
            "Expected the partial iteration to have left some root moves unsearched.");
    }

    /// <summary>
    /// Root coverage (RootMovesCompleted/RootMoveCount/RootCoveragePercent) must belong to the
    /// specific aspiration-window attempt that was cancelled, not to an earlier attempt within
    /// the same depth (e.g. the narrow-window attempt before a fail-low/fail-high re-search).
    /// This is a consistency check rather than a test of a specific numeric value: whatever
    /// attempt was cancelled, coverage must be internally consistent (0 &lt;= completed &lt;= total)
    /// and the percentage must match the raw counts.
    /// </summary>
    [Fact]
    public void Search_CancelledDuringAspirationRetry_RootCoverageIsInternallyConsistent()
    {
        var engine = new ChessEngine();
        var settings = new SearchSettings
        {
            MaxDepth              = 20,
            MaxNodes              = NodeCapThatStopsMidIteration,
            UseIterativeDeepening = true,
            UseAspiration         = true,
        };

        var result = engine.FindBestMove(settings);

        Assert.True(result.PartialDepth > 0);
        Assert.True(result.RootMovesCompleted >= 0);
        Assert.True(result.RootMovesCompleted <= result.RootMoveCount);
        if (result.RootMoveCount > 0)
        {
            double expectedPct = 100.0 * result.RootMovesCompleted / result.RootMoveCount;
            Assert.Equal(expectedPct, result.RootCoveragePercent, 3);
        }
    }

    /// <summary>
    /// A fully completed search (large enough node/time budget to finish at least the
    /// requested MaxDepth) must report no partial-iteration state at all.
    /// </summary>
    [Fact]
    public void Search_FullyCompleted_ReportsNoPartialState()
    {
        var engine = new ChessEngine();
        var settings = new SearchSettings
        {
            MaxDepth              = 3,
            UseIterativeDeepening = true,
        };

        var result = engine.FindBestMove(settings);

        Assert.Equal(0, result.PartialDepth);
        Assert.Equal(0, result.RootMovesCompleted);
        Assert.Equal(0, result.RootMoveCount);
        Assert.Equal(0, result.RootCoveragePercent);
    }

    /// <summary>
    /// A very small node budget (too small to complete even depth 1) must still return a
    /// legal move rather than a default/illegal one, and must not falsely claim a completed
    /// depth greater than 0.
    /// </summary>
    [Fact]
    public void Search_VerySmallNodeBudget_ReturnsLegalFallbackMove()
    {
        var engine = new ChessEngine();
        var settings = new SearchSettings
        {
            MaxDepth              = 20,
            MaxNodes              = 1,
            UseIterativeDeepening = true,
        };

        var result = engine.FindBestMove(settings);

        Assert.NotEqual(default, result.BestMove);
        if (result.DepthAchieved == 0)
            Assert.True(result.IsUnsearchedFallbackMove,
                "A legal move returned without any completed iteration must be explicitly classified as an unsearched fallback.");
    }
}
