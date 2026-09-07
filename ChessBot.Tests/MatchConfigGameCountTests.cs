namespace ChessBot.Tests;

using ChessBot.MatchRunner;
using Xunit;

/// <summary>
/// <c>--games n</c> must play exactly n games. It previously halved the value and rounded
/// down, so <c>--games 5</c> silently played four and <c>--games 1</c> played none — a run's
/// artifacts then could not be explained by the invocation that produced them.
/// </summary>
public class MatchConfigGameCountTests
{
    private static MatchConfig Parse(params string[] args) => MatchConfig.Parse(args);

    [Theory]
    [InlineData(1, 1)]
    [InlineData(2, 2)]
    [InlineData(3, 3)]
    [InlineData(4, 4)]
    [InlineData(5, 5)]
    [InlineData(56, 56)]
    public void Games_PlaysExactlyTheRequestedTotal(int requested, int expectedTotal)
    {
        var cfg = Parse("--games", requested.ToString());
        Assert.Equal(expectedTotal, cfg.TotalGames);
    }

    [Theory]
    [InlineData(1, 1, 0)]   // 1 White, 0 Black
    [InlineData(2, 1, 1)]
    [InlineData(5, 3, 2)]   // colors alternate from White
    public void Games_OddTotalsAlternateColorsAndReportTheImbalance(int total, int white, int black)
    {
        var cfg = Parse("--games", total.ToString());

        Assert.Equal(total, cfg.TotalGames);
        Assert.Equal(white, (cfg.TotalGames + 1) / 2);
        Assert.Equal(black, cfg.TotalGames / 2);
        Assert.Equal(total % 2, cfg.ColorImbalance);
    }

    [Fact]
    public void GamesPerSide_MeansTwiceThatManyGames()
    {
        Assert.Equal(2,  Parse("--games-per-side", "1").TotalGames);
        Assert.Equal(8,  Parse("--games-per-side", "4").TotalGames);
        Assert.Equal(4,  Parse("--games-per-side", "2").GamesPerSide * 2);
    }

    [Fact]
    public void DefaultIsTwoGames()
    {
        Assert.Equal(2, Parse().TotalGames);
        Assert.Equal(0, Parse().ColorImbalance);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("-10")]
    public void Games_RejectsZeroAndNegative(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => Parse("--games", value));
        Assert.Contains("--games", ex.Message);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-3")]
    public void GamesPerSide_RejectsZeroAndNegative(string value)
    {
        var ex = Assert.Throws<ArgumentException>(() => Parse("--games-per-side", value));
        Assert.Contains("--games-per-side", ex.Message);
    }

    /// <summary>
    /// The two flags are different views of one count, so setting one must be readable through
    /// the other without drift.
    /// </summary>
    [Fact]
    public void TotalGamesAndGamesPerSide_StayConsistent()
    {
        var cfg = new MatchConfig { TotalGames = 7 };
        Assert.Equal(4, cfg.GamesPerSide);      // 4 White, 3 Black
        Assert.Equal(1, cfg.ColorImbalance);

        cfg.GamesPerSide = 3;
        Assert.Equal(6, cfg.TotalGames);
        Assert.Equal(0, cfg.ColorImbalance);
    }

    // ── Concurrency ──────────────────────────────────────────────────────────

    [Fact]
    public void Concurrency_DefaultsToWhatTheMachineCanTake()
    {
        int machineLimit = Math.Min(MatchConfig.MaxAutoConcurrency,
                                    Math.Max(1, Environment.ProcessorCount / 2));

        // Never more workers than there are games to play.
        Assert.Equal(1, new MatchConfig { TotalGames = 1 }.Concurrency);
        Assert.Equal(Math.Min(2, machineLimit), new MatchConfig { TotalGames = 2 }.Concurrency);

        // And never more than the machine's share, however many games are queued.
        Assert.Equal(machineLimit, new MatchConfig { TotalGames = 500 }.Concurrency);
        Assert.True(new MatchConfig { TotalGames = 500 }.Concurrency <= MatchConfig.MaxAutoConcurrency);
    }

    [Fact]
    public void Concurrency_ExplicitValueWins()
    {
        // A run that wants full-speed timings has to be able to say so, whatever the machine has.
        Assert.Equal(1, new MatchConfig { TotalGames = 100, Concurrency = 1 }.Concurrency);
        Assert.Equal(32, new MatchConfig { TotalGames = 100, Concurrency = 32 }.Concurrency);

        // Zero or negative would stop the match dead; clamped rather than accepted.
        Assert.Equal(1, new MatchConfig { TotalGames = 100, Concurrency = 0 }.Concurrency);
        Assert.Equal(1, new MatchConfig { TotalGames = 100, Concurrency = -4 }.Concurrency);
    }

    [Fact]
    public void Concurrency_IsParsedAndRecorded()
    {
        var cfg = Parse("--concurrency", "3");

        Assert.Equal(3, cfg.Concurrency);

        // It qualifies every timing-derived number in the run, so the manifest must state it.
        Assert.Equal("3", cfg.Describe()["Concurrency"]);
    }
}
