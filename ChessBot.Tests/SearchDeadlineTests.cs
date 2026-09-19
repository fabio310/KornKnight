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
[Collection(SerialCollection.Name)]   // asserts wall-clock bounds
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
    /// The search returns inside its budget, not at it: the hard deadline sits a return reserve
    /// before the end of the budget, so that unwinding and building the result are paid for out of
    /// the budget rather than on top of it. Several positions, because a capture-heavy one is where
    /// a check that is not reached often enough shows up.
    /// </summary>
    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1")]
    public void TheSearchReturnsInsideItsBudget(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);

        for (int i = 0; i < 3; i++)
        {
            long start  = Stopwatch.GetTimestamp();
            var  result = engine.FindBestMove(new SearchSettings { MaxTimeMs = 100, StartTimestamp = start });
            double ms   = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;

            Assert.True(ms < 100, $"a 100 ms budget returned at {ms:F2} ms");
            Assert.True(result.DepthAchieved >= 1);
        }
    }

    /// <summary>
    /// A caller's cancellation stops the search, promptly. Moved here from SearchTests, where it
    /// asserted "depth &lt; 10 after a 100 ms cancel" — a claim about node rate, not about
    /// cancellation. Run warm, after the rest of the suite has let the JIT optimise the search,
    /// the start position completes depth 11 (164,450 nodes) inside 100 ms plus CancelAfter's
    /// 15.6 ms timer tick, and it failed for that reason alone. It now measures what its name says:
    /// the time from the cancel to the return, against a depth nothing reaches in that time.
    /// </summary>
    [Fact]
    public void FindBestMove_CancellationToken_StopsSearch()
    {
        var engine = new ChessEngine();
        using var cts = new CancellationTokenSource();
        var settings = new SearchSettings { MaxDepth = 64, MaxTimeMs = 60_000 };

        cts.CancelAfter(100);
        var clock  = Stopwatch.StartNew();
        var result = engine.FindBestMove(settings, cts.Token);
        clock.Stop();

        Assert.True(result.DepthAchieved < 64, $"the search ran to depth {result.DepthAchieved}");
        Assert.True(clock.ElapsedMilliseconds < 500,
            $"cancelled at 100 ms, returned at {clock.ElapsedMilliseconds} ms");
        Assert.False(result.IsUnsearchedFallbackMove);
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
