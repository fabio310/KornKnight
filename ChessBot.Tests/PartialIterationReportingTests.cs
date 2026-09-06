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
            UsePartialRootResult  = true,
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

    [Fact]
    public void Search_UsePartialRootResultDisabled_NeverReportsPartialMove()
    {
        var engine = new ChessEngine();
        var settings = new SearchSettings
        {
            MaxDepth              = 20,
            MaxNodes              = NodeCapThatStopsMidIteration,
            UseIterativeDeepening = true,
            UsePartialRootResult  = false,
        };

        var result = engine.FindBestMove(settings);

        Assert.True(result.PartialDepth > 0, "Expected a mid-iteration cancellation to exercise this path.");
        Assert.False(result.UsedPartialRootResult,
            "UsePartialRootResult=false must never let a partial-iteration root move replace the completed result.");
    }

    [Fact]
    public void Search_UsePartialRootResultEnabled_FlagMatchesWhetherPartialMoveWasUsed()
    {
        var engine = new ChessEngine();
        var settings = new SearchSettings
        {
            MaxDepth              = 20,
            MaxNodes              = NodeCapThatStopsMidIteration,
            UseIterativeDeepening = true,
            UsePartialRootResult  = true,
        };

        var result = engine.FindBestMove(settings);

        Assert.True(result.PartialDepth > 0, "Expected a mid-iteration cancellation to exercise this path.");

        // Whenever the partial result was used, DepthAchieved must still reflect only the
        // last fully completed iteration — never the partial depth.
        if (result.UsedPartialRootResult)
            Assert.True(result.DepthAchieved < result.PartialDepth);
    }
}
