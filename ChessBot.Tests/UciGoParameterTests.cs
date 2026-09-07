using Xunit;
using ChessBot.Engine.Types;
using ChessBot.Uci;

namespace ChessBot.Tests;

/// <summary>
/// Covers the mapping from a "go" command to the search's limits. The failure that matters
/// here is silent: a time manager that allocates too much does not throw, it loses on time —
/// so the budget is asserted directly rather than through a search.
/// </summary>
public class UciGoParameterTests
{
    private static GoParameters Parse(string command) =>
        GoParameters.Parse(command.Split(' ', StringSplitOptions.RemoveEmptyEntries), 1);

    // ── Parsing ──────────────────────────────────────────────────────────────

    [Fact]
    public void Parse_ReadsEverySupportedOperand()
    {
        var go = Parse("go wtime 300000 btime 290000 winc 2000 binc 1500 movestogo 40 depth 12 nodes 500000");

        Assert.Equal(300000, go.WhiteTimeMs);
        Assert.Equal(290000, go.BlackTimeMs);
        Assert.Equal(2000,   go.WhiteIncrementMs);
        Assert.Equal(1500,   go.BlackIncrementMs);
        Assert.Equal(40,     go.MovesToGo);
        Assert.Equal(12,     go.Depth);
        Assert.Equal(500000, go.Nodes);
        Assert.False(go.Infinite);
    }

    [Fact]
    public void Parse_IgnoresUnsupportedOperandsWithoutLosingTheOnesAfterThem()
    {
        // "ponder" and "searchmoves" are not implemented; the protocol still allows a GUI to
        // send them, and the operands that follow must survive.
        var go = Parse("go ponder searchmoves e2e4 d2d4 movetime 1234");

        Assert.Equal(1234, go.MoveTimeMs);
    }

    [Fact]
    public void Parse_MalformedValue_LeavesTheFieldUnsetRatherThanThrowing()
    {
        var go = Parse("go wtime abc btime 60000");

        Assert.Null(go.WhiteTimeMs);
        Assert.Equal(60000, go.BlackTimeMs);
    }

    [Fact]
    public void Parse_BareGo_HasNoTimeSource()
    {
        Assert.True(Parse("go").HasNoTimeSource);
        Assert.True(Parse("go depth 6").HasNoTimeSource);
        Assert.False(Parse("go movetime 100").HasNoTimeSource);
        Assert.False(Parse("go wtime 1000 btime 1000").HasNoTimeSource);
    }

    // ── Mapping to SearchSettings ────────────────────────────────────────────

    [Fact]
    public void MoveTime_BecomesTheExactTimeLimit()
    {
        var settings = UciTimeManager.ToSearchSettings(Parse("go movetime 2500"), Color.White);

        Assert.Equal(2500, settings.MaxTimeMs);
        Assert.Null(settings.MaxDepth);
        Assert.Null(settings.MaxNodes);
    }

    [Fact]
    public void MoveTime_OverridesTheClock()
    {
        // A GUI that sends both means the movetime: it is an instruction, not an estimate.
        var settings = UciTimeManager.ToSearchSettings(
            Parse("go wtime 600000 btime 600000 movetime 100"), Color.White);

        Assert.Equal(100, settings.MaxTimeMs);
    }

    [Fact]
    public void Depth_LimitsDepthAndLeavesTheClockUnbounded()
    {
        var settings = UciTimeManager.ToSearchSettings(Parse("go depth 7"), Color.White);

        Assert.Equal(7, settings.MaxDepth);

        // Not null: the searcher reads a null time limit as a 10-second default, which would
        // cut a fixed-depth search short instead of letting it finish.
        Assert.Equal(UciTimeManager.NoTimeLimitMs, settings.MaxTimeMs);
    }

    [Fact]
    public void Nodes_LimitsNodesAndLeavesTheClockUnbounded()
    {
        var settings = UciTimeManager.ToSearchSettings(Parse("go nodes 250000"), Color.White);

        Assert.Equal(250000, settings.MaxNodes);
        Assert.Equal(UciTimeManager.NoTimeLimitMs, settings.MaxTimeMs);
    }

    [Fact]
    public void Infinite_IsUnboundedInTimeAndDepth()
    {
        var settings = UciTimeManager.ToSearchSettings(Parse("go infinite"), Color.White);

        Assert.Equal(UciTimeManager.NoTimeLimitMs, settings.MaxTimeMs);
        Assert.Null(settings.MaxDepth);
    }

    [Fact]
    public void BareGo_IsTreatedAsInfinite()
    {
        var settings = UciTimeManager.ToSearchSettings(Parse("go"), Color.White);

        Assert.Equal(UciTimeManager.NoTimeLimitMs, settings.MaxTimeMs);
    }

    [Fact]
    public void DepthAndClock_BothApply()
    {
        var settings = UciTimeManager.ToSearchSettings(
            Parse("go wtime 60000 btime 60000 depth 20"), Color.White);

        Assert.Equal(20, settings.MaxDepth);
        Assert.True(settings.MaxTimeMs < 60000, "the clock must still bound a fixed-depth search");
    }

    [Fact]
    public void Clock_UsesTheSideToMovesOwnTime()
    {
        var go = Parse("go wtime 600000 btime 6000 winc 0 binc 0 movestogo 30");

        int whiteBudget = UciTimeManager.ResolveTimeBudgetMs(go, Color.White);
        int blackBudget = UciTimeManager.ResolveTimeBudgetMs(go, Color.Black);

        Assert.True(whiteBudget > blackBudget * 10,
            $"White has 100x the clock but was allocated {whiteBudget}ms against Black's {blackBudget}ms");
    }

    [Fact]
    public void Clock_WithoutMovesToGo_UsesTheDefaultHorizon()
    {
        // 60s sudden death, no increment: roughly a thirtieth of the clock.
        int budget = UciTimeManager.AllocateTimeMs(60_000, incrementMs: 0, movesToGo: null);

        int expected = (60_000 - UciTimeManager.MoveOverheadMs) / UciTimeManager.DefaultMovesToGo;
        Assert.Equal(expected, budget);
    }

    [Fact]
    public void Clock_AddsTheIncrement()
    {
        int withoutIncrement = UciTimeManager.AllocateTimeMs(60_000, incrementMs: 0,    movesToGo: 30);
        int withIncrement    = UciTimeManager.AllocateTimeMs(60_000, incrementMs: 1000, movesToGo: 30);

        Assert.Equal(withoutIncrement + 1000, withIncrement);
    }

    [Fact]
    public void Clock_MovesToGo_SplitsTheRemainingTime()
    {
        int budget = UciTimeManager.AllocateTimeMs(120_000, incrementMs: 0, movesToGo: 4);

        Assert.Equal((120_000 - UciTimeManager.MoveOverheadMs) / 4, budget);
    }

    [Fact]
    public void Clock_NeverCommitsMoreThanHalfOfWhatIsLeft()
    {
        // 1s left with a 5s increment: the naive estimate exceeds the clock outright.
        int budget = UciTimeManager.AllocateTimeMs(1_000, incrementMs: 5_000, movesToGo: 1);

        Assert.True(budget < 1_000, $"allocated {budget}ms of a 1000ms clock");
        Assert.True(budget <= (1_000 - UciTimeManager.MoveOverheadMs) / 2);
    }

    [Fact]
    public void Clock_AtTenPlusTenth_AllocatesASmallFractionOfTheClock()
    {
        // The acceptance time control. The budget has to leave room for ~30 more moves.
        int budget = UciTimeManager.AllocateTimeMs(10_000, incrementMs: 100, movesToGo: null);

        Assert.InRange(budget, 100, 1_000);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(-500)]   // a GUI reporting an already-overdrawn clock
    public void Clock_NearZero_StillReturnsAPositiveBudget(int remainingMs)
    {
        int budget = UciTimeManager.AllocateTimeMs(remainingMs, incrementMs: 0, movesToGo: null);

        // Moving instantly on a bad move beats forfeiting while computing a good one.
        Assert.True(budget >= 1, $"allocated {budget}ms");
    }

    [Fact]
    public void Clock_OnlyTheOpponentsTimeSent_DoesNotAllocateFromIt()
    {
        // Allocating White's move from Black's clock would be worse than not allocating at all.
        var settings = UciTimeManager.ToSearchSettings(Parse("go btime 5000"), Color.White);

        Assert.Equal(UciTimeManager.NoTimeLimitMs, settings.MaxTimeMs);
    }
}
