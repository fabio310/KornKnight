namespace ChessBot.Tests;

using ChessBot.Engine.Search;
using ChessBot.MatchRunner;
using Xunit;

/// <summary>
/// Gates for how a run takes hold of the machine.
///
/// The harness previously ran whole games under <c>Parallel.For</c>. The thread pool's
/// hill-climbing adds and removes threads while a run is in progress, so the number of games
/// actually in flight changed mid-run and nothing reported it — a timed measurement whose
/// concurrency moves underneath it is not one measurement. Nothing pinned a worker either, so a
/// search was migrated between cores and lost its tables to a cold cache; one past run's NPS
/// varied between 367k and 2,373k within itself.
/// </summary>
public class MeasurementRigTests
{
    // ── Fixed degree of parallelism ──────────────────────────────────────────

    [Fact]
    public async Task PinnedWorkerPool_NeverRunsMoreGamesAtOnceThanItsSize()
    {
        const int size = 3;
        const int jobs = 40;

        using var pool = new PinnedWorkerPool(size, raiseThreadPriority: false);
        using var gate = new SemaphoreSlim(size);

        int inFlight = 0;
        int peak = 0;
        var peakLock = new object();

        var running = new List<Task>(jobs);
        for (int job = 0; job < jobs; job++)
        {
            running.Add(Task.Run(async () =>
            {
                await gate.WaitAsync();
                try
                {
                    await pool.RunAsync(async () =>
                    {
                        int now = Interlocked.Increment(ref inFlight);
                        lock (peakLock) peak = Math.Max(peak, now);
                        await Task.Delay(5);
                        Interlocked.Decrement(ref inFlight);
                        return true;
                    }, CancellationToken.None);
                }
                finally { gate.Release(); }
            }));
        }

        await Task.WhenAll(running);

        Assert.Equal(size, peak);
        Assert.Equal(0, inFlight);
    }

    [Fact]
    public async Task PinnedWorker_KeepsAGameOnOneThreadAcrossItsAwaits()
    {
        // This is the whole point of the worker. A match game is asynchronous — it waits on the
        // opponent process between moves — so without a synchronization context of its own,
        // every await is free to resume the game, and ChessBot's next search, on a different
        // thread. Pinning a thread the work then leaves is pinning nothing.
        using var worker = new PinnedWorker(core: 0, name: "test-worker", raiseThreadPriority: false);

        var threadIds = await worker.RunAsync(async () =>
        {
            var ids = new List<int> { Environment.CurrentManagedThreadId };
            await Task.Yield();
            ids.Add(Environment.CurrentManagedThreadId);
            await Task.Delay(5);
            ids.Add(Environment.CurrentManagedThreadId);
            return ids;
        });

        Assert.Single(threadIds.Distinct());
        Assert.NotEqual(Environment.CurrentManagedThreadId, threadIds[0]);
    }

    [Fact]
    public void PinnedWorkerPool_ReportsTheCoresItTookRatherThanAssumingThem()
    {
        using var pool = new PinnedWorkerPool(2, raiseThreadPriority: false);

        // Affinity can be refused by the OS. What must never happen is a manifest claiming a
        // pinned run when the pin failed, so the pool reports what was granted.
        Assert.Equal(2, pool.Cores.Count);
        Assert.Equal(2, pool.Cores.Distinct().Count());
    }

    [Fact]
    public void CoreSlotPool_HandsOutDistinctCoresAndTakesThemBack()
    {
        var pool = new CoreSlotPool(4, stride: 1);

        var taken = new[] { pool.Take(), pool.Take(), pool.Take(), pool.Take() };
        Assert.Equal(4, taken.Distinct().Count());
        Assert.Equal(-1, pool.Take());       // exhausted: the caller runs unpinned rather than waiting

        pool.Return(taken[1]);
        Assert.Equal(taken[1], pool.Take());
    }

    // ── A node budget must not depend on how many games ran at once ──────────

    [Fact]
    public void PlayHeadToHead_NodeBudgetGivesTheSameResultAtEveryConcurrency()
    {
        // This is the claim that lets a node-budget run take the whole machine: concurrency 4 is
        // bit-identical to concurrency 1, just faster. If it ever stops holding, the defaults
        // that saturate a large box are unsafe and the split by budget kind is wrong.
        var configA = new AbConfig { Name = "A", Build = () => new SearchSettings() };
        var configB = new AbConfig { Name = "B", Build = () => new SearchSettings { UseThreatEval = false } };

        var openings = AbHarness.GenerateOpeningPositions(4, seed: 20260907);

        var serial   = AbHarness.PlayHeadToHead(configA, configB, openings, nodesPerMove: 1_500, maxPlies: 30, concurrency: 1);
        var parallel = AbHarness.PlayHeadToHead(configA, configB, openings, nodesPerMove: 1_500, maxPlies: 30, concurrency: 4);

        Assert.Equal(serial.Games,  parallel.Games);
        Assert.Equal(serial.WinsA,  parallel.WinsA);
        Assert.Equal(serial.WinsB,  parallel.WinsB);
        Assert.Equal(serial.Draws,  parallel.Draws);

        // Folded in a fixed order, so even the per-game reasons line up: a result that depends
        // on the order the workers happened to finish in is not a result.
        Assert.Equal(serial.TerminationReasons, parallel.TerminationReasons);
    }

    [Fact]
    public void PlayHeadToHead_AccountsForEveryGameAtEveryConcurrency()
    {
        var cfg = new AbConfig { Name = "same", Build = () => new SearchSettings() };
        var openings = AbHarness.GenerateOpeningPositions(3, seed: 20260907);

        foreach (int concurrency in new[] { 1, 2, 8 })
        {
            var h2h = AbHarness.PlayHeadToHead(
                cfg, cfg, openings, nodesPerMove: 1_500, maxPlies: 30, concurrency: concurrency);

            // Every opening twice, once with each side as White, and nothing lost to scheduling.
            Assert.Equal(openings.Count * 2, h2h.Games);
            Assert.Equal(h2h.Games, h2h.WinsA + h2h.WinsB + h2h.Draws);
            Assert.Equal(concurrency, h2h.Concurrency);
        }
    }
}
