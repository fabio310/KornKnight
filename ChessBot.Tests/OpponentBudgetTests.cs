namespace ChessBot.Tests;

using ChessBot.MatchRunner;
using Xunit;

/// <summary>
/// The anchor's opponent has to be a fixed quantity, and UCI_LimitStrength is not one: it does
/// not weaken an engine by searching less, it weakens it by picking a worse move at random from
/// what it found. Three runs of one build once scored 55.8%, 49.25% and 43.25% — about 85 Elo of
/// spread on identical code.
/// </summary>
public class OpponentBudgetTests
{
    [Fact]
    public void AFixedDepthBecomesAGoDepthCommand()
    {
        Assert.Equal("go depth 8", MoveBudget.Depth(8).ToGoCommand());
        Assert.Equal("depth 8/move", MoveBudget.Depth(8).ToString());

        // No clock at all, so the opponent's answer cannot move with the machine's load.
        Assert.DoesNotContain("movetime", MoveBudget.Depth(8).ToGoCommand());
    }

    [Fact]
    public void WithoutAFixedDepthTheOpponentGetsTheClock()
    {
        var cfg = MatchConfig.Parse(new[] { "--engine", "x", "--time", "250" });

        Assert.Null(cfg.EngineDepth);
        Assert.Equal(MoveBudget.Time(250), cfg.OpponentBudget);
    }

    [Fact]
    public void AFixedDepthAppliesToTheOpponentAndLeavesOurClockAlone()
    {
        var cfg = MatchConfig.Parse(
            new[] { "--engine", "x", "--time", "250", "--engine-depth", "6" });

        Assert.Equal(6, cfg.EngineDepth);
        Assert.Equal(MoveBudget.Depth(6), cfg.OpponentBudget);

        // Our side is what the anchor is measuring, so it keeps its clock.
        Assert.Equal(250, cfg.MoveTimeMs);
        Assert.Equal(BudgetKind.Time, cfg.Budget);
    }

    /// <summary>
    /// They do not compose. A depth-limited engine also told to play at 1500 Elo picks a random
    /// worse move from what the fixed depth found, so the run gets back the randomness the fixed
    /// depth was chosen to remove.
    /// </summary>
    [Fact]
    public void TheTwoWaysToWeakenTheOpponentAreRefusedTogether()
    {
        var ex = Assert.Throws<System.ArgumentException>(() => MatchConfig.Parse(
            new[] { "--engine", "x", "--engine-elo", "1500", "--engine-depth", "6" }));

        Assert.Contains("--engine-depth", ex.Message);
    }

    [Fact]
    public void TheRunManifestRecordsWhichWasUsed()
    {
        var cfg = MatchConfig.Parse(new[] { "--engine", "x", "--engine-depth", "6" });

        Assert.Equal("6", cfg.Describe()["EngineDepth"]);
    }
}
