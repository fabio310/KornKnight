namespace ChessBot.Tests;

using ChessBot.Engine;
using ChessBot.Engine.Search;
using Xunit;

/// <summary>
/// The check extension adds a ply of depth without consuming one, so along a line where the side
/// to move keeps being in check the remaining depth stops falling. Uncapped, the amount by which
/// a line can outrun its nominal depth is bounded only by the search stack: node counts at a
/// nominal depth stop meaning anything, and the deepest lines are pushed toward the ply bound.
/// The budget bounds it per root-to-leaf line, which is the only place a running total is
/// meaningful — a global count would be spent by the first check-heavy subtree.
/// </summary>
public class CheckExtensionBudgetTests
{
    // A queen against two rooks with both kings exposed: nearly every move is a check or an
    // evasion, which is the shape that makes the extension compound.
    private const string CheckFest = "rr6/8/8/3k4/8/8/8/Q6K w - - 0 1";

    [Fact]
    public void NoLineExceedsTheExtensionBudget()
    {
        var engine = new ChessEngine();
        engine.LoadFen(CheckFest);

        var result = engine.FindBestMove(new SearchSettings
        {
            MaxNodes  = 3_000_000,
            MaxDepth  = 60,
            MaxTimeMs = 120_000,
        });

        Assert.True(result.CheckExtensions > 0, "position did not exercise the check extension at all");
        Assert.InRange(result.MaxCheckExtensionsInLine, 0, Searcher.MAX_EXTENSIONS);
    }

    /// <summary>
    /// The budget must bound long lines without switching the extension off for short ones: a
    /// forced mate that is only visible because checks extend has to still be found.
    /// </summary>
    [Fact]
    public void ShortForcedMateIsStillFound()
    {
        var engine = new ChessEngine();
        engine.LoadFen("6k1/5Q2/8/8/8/8/8/4R1K1 w - - 0 1");   // mate in 2

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 8, MaxTimeMs = 10_000 });

        Assert.True(result.CheckExtensions > 0);
        Assert.Equal(2, SearchScores.ToReported(result.Evaluation).MateInMoves);
    }
}
