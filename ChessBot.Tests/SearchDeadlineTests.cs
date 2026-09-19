using System.Diagnostics;
using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Search;

namespace ChessBot.Tests;

/// <summary>
/// The search's clock runs from the caller's timestamp, not from when the search happens to
/// start. A GUI measures from the moment it wrote "go", and everything between that and the
/// first node — a previous search being stopped, a thread-pool hop, a lock — is on its clock.
/// </summary>
public class SearchDeadlineTests
{
    private static long TicksAgo(int ms) =>
        Stopwatch.GetTimestamp() - ms * Stopwatch.Frequency / 1000;

    /// <summary>
    /// A budget that started 150 ms before the call and lasts 300 ms has 150 ms left. The search
    /// must use what is left, not the whole budget again. Before the timestamp existed this took
    /// the full 300 ms from the call.
    /// </summary>
    [Fact]
    public void TheBudgetRunsFromTheCallersTimestamp()
    {
        var engine = new ChessEngine();
        var settings = new SearchSettings { MaxTimeMs = 300, StartTimestamp = TicksAgo(150) };

        var clock = Stopwatch.StartNew();
        engine.FindBestMove(settings);
        clock.Stop();

        Assert.True(clock.ElapsedMilliseconds < 250,
            $"150 ms of budget remained, and the search took {clock.ElapsedMilliseconds} ms");
    }

    /// <summary>
    /// A budget already spent before the search starts still produces a searched move. Depth 1 is
    /// always started, because the alternative is the unsearched fallback — root move 0 in
    /// generation order, a move nothing chose.
    /// </summary>
    [Fact]
    public void ABudgetSpentBeforeTheSearchStarts_StillSearchesDepthOne()
    {
        var engine = new ChessEngine();
        engine.LoadFen("r1bqkbnr/pppp1ppp/2n5/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq - 2 3");

        var settings = new SearchSettings { MaxTimeMs = 5, StartTimestamp = TicksAgo(50) };

        var clock  = Stopwatch.StartNew();
        var result = engine.FindBestMove(settings);
        clock.Stop();

        Assert.False(result.IsUnsearchedFallbackMove);
        Assert.True(result.DepthAchieved >= 1);
        Assert.True(clock.ElapsedMilliseconds < 100,
            $"an exhausted budget took {clock.ElapsedMilliseconds} ms to give up");
    }

    /// <summary>
    /// With no timestamp the budget runs from the call, which is what every caller without a
    /// clock of its own (the match runner, the UI) has always had.
    /// </summary>
    [Fact]
    public void WithoutATimestamp_TheBudgetRunsFromTheCall()
    {
        var engine = new ChessEngine();

        var clock  = Stopwatch.StartNew();
        var result = engine.FindBestMove(new SearchSettings { MaxTimeMs = 200 });
        clock.Stop();

        Assert.True(result.DepthAchieved >= 1);
        Assert.InRange(clock.ElapsedMilliseconds, 100, 2_000);
    }
}
