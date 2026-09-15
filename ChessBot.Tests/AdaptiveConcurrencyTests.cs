using System;
using System.Threading;
using System.Threading.Tasks;
using Xunit;
using ChessBot.MatchRunner;

namespace ChessBot.Tests;

/// <summary>
/// The governor that sizes a run to the machine, and the gate that lets it change size without
/// interrupting a game.
///
/// The point of the governor is that it measures instead of guessing: an extra game is kept only
/// if the engines' node rate survived it. These tests therefore drive it with node rates rather
/// than through a real run, because a real run would only confirm the policy on whatever load the
/// test machine happened to have — which is the assumption the governor exists to remove.
/// </summary>
public class AdaptiveConcurrencyTests
{
    private const int Cores = 16;

    /// <summary>Fills a sample window with one node rate, enough times for the governor to trust it.</summary>
    private static ThroughputSamples RateOf(long nodesPerSecond, int count = 300)
    {
        var samples = new ThroughputSamples();
        for (int i = 0; i < count; i++) samples.Add(nodesPerSecond);
        return samples;
    }

    /// <summary>
    /// Runs the governor for a while against a machine whose node rate is a function of how many
    /// games are in flight — which is what a real machine is.
    /// </summary>
    /// <param name="rateAt">Node rate the engines achieve at a given concurrency.</param>
    private static AdaptiveConcurrency Settle(BudgetKind budget, Func<int, long> rateAt,
                                              double externalCores = 0, int ticks = 200)
    {
        var governor = new AdaptiveConcurrency(budget, Cores);
        var samples = new ThroughputSamples();

        for (int tick = 0; tick < ticks; tick++)
        {
            // The samples that accumulate between ticks are the ones produced at the target the
            // governor last set.
            for (int i = 0; i < 300; i++) samples.Add(rateAt(governor.Target));
            governor.Update(externalCores, samples);
        }

        return governor;
    }

    // ── The polite bound: what somebody else is using is not ours ────────────

    [Fact]
    public void LoadFromSomebodyElseComesOutOfOurShare()
    {
        Assert.Equal(12, AdaptiveConcurrency.PoliteBound(Cores, externalCores: 4));
        Assert.Equal(16, AdaptiveConcurrency.PoliteBound(Cores, externalCores: 0));
    }

    [Fact]
    public void ARunNeverStopsEntirelyHoweverBusyTheMachineIs()
    {
        // Yielding to the point of playing no games is not yielding, it is hanging — and the run
        // would never finish to report what it found.
        Assert.Equal(1, AdaptiveConcurrency.PoliteBound(Cores, externalCores: 16));
        Assert.Equal(1, AdaptiveConcurrency.PoliteBound(Cores, externalCores: 99));
    }

    [Fact]
    public void ASamplingArtefactBelowZeroIsNotALicenceToOversubscribe()
    {
        Assert.Equal(Cores, AdaptiveConcurrency.PoliteBound(Cores, externalCores: -5));
    }

    [Fact]
    public void ANodeBudgetTakesEveryFreeCoreWithoutProbing()
    {
        // A node-budget run is bit-identical at any concurrency, so there is nothing for a probe
        // to discover and no reason to leave a core idle.
        var governor = Settle(BudgetKind.Nodes, rateAt: _ => 1_000_000, externalCores: 4);
        Assert.Equal(12, governor.Target);
        Assert.Equal((0, 0), governor.Probes);
    }

    [Fact]
    public void ExternalLoadIsYieldedToImmediatelyRatherThanProbedFor()
    {
        // Somebody else needing the machine is not a hypothesis to be tested.
        var governor = new AdaptiveConcurrency(BudgetKind.Time, Cores);
        Assert.Equal(8, governor.Target);       // the old half-cores rule is where it starts

        for (int i = 0; i < 5; i++) governor.Update(externalCoresNow: 14, RateOf(1_000_000));

        Assert.Equal(2, governor.Target);
    }

    // ── The measured bound: an extra game is kept only if it was free ────────

    [Fact]
    public void ATimedRunStartsWhereTheOldFixedRuleDidRatherThanAtTheCeiling()
    {
        // Starting at the ceiling would make a run's first minutes its worst ones on a machine
        // that cannot sustain it, and every existing result in this project was taken at half.
        Assert.Equal(8, new AdaptiveConcurrency(BudgetKind.Time, Cores).Target);
        Assert.Equal(8, AdaptiveConcurrency.ConservativeStart(BudgetKind.Time, Cores));
        Assert.Equal(16, AdaptiveConcurrency.ConservativeStart(BudgetKind.Nodes, Cores));
    }

    [Fact]
    public void ATimedRunClimbsPastHalfWhenTheExtraGamesCostNothing()
    {
        // The whole point. A machine that sustains a flat node rate at any concurrency has spare
        // capacity, and the old half-cores rule would have left half of it idle for ever.
        var governor = Settle(BudgetKind.Time, rateAt: _ => 1_000_000);

        Assert.Equal(Cores, governor.Target);
        Assert.True(governor.Probes.Accepted > 0, "the free games should have been detected as free");
    }

    [Fact]
    public void ATimedRunStopsClimbingWhereTheNodeRateStartsToFall()
    {
        // A machine that is saturated beyond ten concurrent searches. The governor should find
        // ten, rather than either stopping at the old eight or running at sixteen.
        //
        // The assertion is on the discovered limit and the mean, not on the target at the moment
        // the loop happened to end: the governor deliberately re-probes for ever, so it spends
        // most of its time at ten and a tick here and there at eleven finding out that eleven is
        // still too many. A test that demanded exactly ten would be asserting that the governor
        // had stopped adapting.
        var governor = Settle(BudgetKind.Time, rateAt: n => n <= 10 ? 1_000_000 : 400_000);
        var (min, max, mean) = governor.Observed;

        Assert.InRange(mean, 9.5, 10.5);   // where it lives
        Assert.Equal(8, min);              // where it started
        Assert.Equal(11, max);             // one game above the limit, repeatedly, on purpose
        Assert.True(governor.Probes.Rejected > 0, "the costly game should have been given back");
    }

    [Fact]
    public void ARateFallWithinToleranceIsNotTreatedAsACost()
    {
        // Node rate varies between positions, so a threshold at zero would reject every probe on
        // noise alone and the run would never climb.
        var governor = Settle(BudgetKind.Time, rateAt: n => 1_000_000 - (n * 2_000));

        Assert.Equal(Cores, governor.Target);
    }

    [Fact]
    public void ACostlyGameIsGivenBackRatherThanKept()
    {
        var governor = Settle(BudgetKind.Time, rateAt: n => n <= 9 ? 1_000_000 : 300_000, ticks: 40);

        Assert.InRange(governor.Observed.Mean, 8.5, 9.5);
        Assert.True(governor.Target <= 10,
            $"target {governor.Target} should not sit above the level the node rate supports");
    }

    // ── And it never decides once ────────────────────────────────────────────

    [Fact]
    public void ACapFromAFailedProbeIsRelaxedAgainLater()
    {
        // The load that caused a probe to fail is not permanent. A cap that outlived it would be
        // exactly the decide-once-at-startup behaviour this governor exists to avoid.
        var governor = new AdaptiveConcurrency(BudgetKind.Time, Cores);
        var samples = new ThroughputSamples();

        // A machine that cannot sustain more than eight: every probe above it fails.
        for (int tick = 0; tick < 20; tick++)
        {
            for (int i = 0; i < 300; i++) samples.Add(governor.Target <= 8 ? 1_000_000 : 200_000);
            governor.Update(0, samples);
        }

        int cappedAt = governor.ThroughputCap;
        Assert.Equal(8, cappedAt);

        // Long enough for the cap to relax, on a machine that has since freed up.
        for (int tick = 0; tick < 200; tick++)
        {
            for (int i = 0; i < 300; i++) samples.Add(1_000_000);
            governor.Update(0, samples);
        }

        Assert.True(governor.Target > cappedAt,
            $"the run should have tried again and climbed past {cappedAt}, but sat at {governor.Target}");
    }

    [Fact]
    public void AMachineThatFreesUpIsClimbedBackInto()
    {
        // Load arrives, the run yields, the load goes away, and the run takes the cores back —
        // without anybody restarting it.
        var governor = new AdaptiveConcurrency(BudgetKind.Time, Cores);
        var samples = new ThroughputSamples();

        for (int tick = 0; tick < 20; tick++)
        {
            for (int i = 0; i < 300; i++) samples.Add(1_000_000);
            governor.Update(externalCoresNow: 13, samples);
        }
        int whileBusy = governor.Target;
        Assert.Equal(3, whileBusy);

        for (int tick = 0; tick < 200; tick++)
        {
            for (int i = 0; i < 300; i++) samples.Add(1_000_000);
            governor.Update(externalCoresNow: 0, samples);
        }

        Assert.Equal(Cores, governor.Target);
    }

    [Fact]
    public void ASingleBusySampleIsIgnoredEntirely()
    {
        // The median is what stops a one-second compile from costing the run cores. A running
        // average would let the spike through in proportion to its size and then take several
        // samples to forget it.
        var governor = new AdaptiveConcurrency(BudgetKind.Nodes, Cores);
        var samples = RateOf(1_000_000);

        for (int i = 0; i < 20; i++) governor.Update(0, samples);
        Assert.Equal(Cores, governor.Target);

        governor.Update(externalCoresNow: 16, samples);

        Assert.Equal(Cores, governor.Target);
    }

    [Fact]
    public void LoadThatPersistsIsActedOnWithinThreeSamples()
    {
        // The other half of that trade-off: ignoring a spike must not mean ignoring a build that
        // has actually started.
        var governor = new AdaptiveConcurrency(BudgetKind.Nodes, Cores);
        var samples = RateOf(1_000_000);
        for (int i = 0; i < 20; i++) governor.Update(0, samples);

        for (int i = 0; i < 3; i++) governor.Update(externalCoresNow: 8, samples);

        Assert.True(governor.Target < Cores,
            $"three busy samples should have started giving cores back, target is {governor.Target}");
    }

    [Fact]
    public void TheRunRecordsTheRangeItActuallyHeld()
    {
        // A result taken under changing conditions has to say so; one number would be a claim the
        // run cannot make.
        var governor = Settle(BudgetKind.Time, rateAt: _ => 1_000_000);
        var (min, max, mean) = governor.Observed;

        Assert.Equal(8, min);
        Assert.Equal(16, max);
        Assert.InRange(mean, min, max);
        Assert.Contains("adaptive concurrency 8-16", governor.Describe());
    }

    // ── The node rate window ─────────────────────────────────────────────────

    [Fact]
    public void TheMedianIgnoresAPositionWithATinyTree()
    {
        // An endgame with a small tree is a long way from the middle, and a mean would chase it.
        var samples = new ThroughputSamples();
        for (int i = 0; i < 99; i++) samples.Add(1_000_000);
        samples.Add(1);

        Assert.Equal(1_000_000, samples.Median());
    }

    [Fact]
    public void AMoveThatReportedNoRateIsNotCountedAsZero()
    {
        var samples = new ThroughputSamples();
        samples.Add(0);
        samples.Add(-5);

        Assert.Equal(0, samples.Count);
        Assert.Equal(0, samples.Median());
    }

    // ── The gate ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task TheGateLetsExactlyItsSizeThrough()
    {
        using var gate = new ConcurrencyGate(initial: 2, ceiling: 8);

        await gate.AcquireAsync(CancellationToken.None);
        await gate.AcquireAsync(CancellationToken.None);

        var third = gate.AcquireAsync(CancellationToken.None);
        Assert.False(third.IsCompleted);

        gate.Release();
        await third;
    }

    [Fact]
    public async Task GrowingHandsOutPermitsImmediately()
    {
        using var gate = new ConcurrencyGate(initial: 1, ceiling: 8);
        await gate.AcquireAsync(CancellationToken.None);

        var waiting = gate.AcquireAsync(CancellationToken.None);
        Assert.False(waiting.IsCompleted);

        gate.Resize(3);

        await waiting.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task ShrinkingWaitsForAGameToEndRatherThanInterruptingOne()
    {
        // The property the whole design rests on: a worker mid-game keeps its permit. Conditions
        // may change between games and never during one, so every game is attributable to the
        // concurrency it was played at.
        using var gate = new ConcurrencyGate(initial: 2, ceiling: 8);

        await gate.AcquireAsync(CancellationToken.None);
        await gate.AcquireAsync(CancellationToken.None);

        gate.Resize(1);
        Assert.Equal(2, gate.Issued);          // nobody was interrupted

        gate.Release();                        // one "game" ends
        await WaitUntil(() => gate.Issued == 1);

        var blocked = gate.AcquireAsync(CancellationToken.None);
        Assert.False(blocked.IsCompleted);
    }

    [Fact]
    public async Task ShrinkingThenGrowingBeforeAnyGameEndsCancelsTheDebt()
    {
        // Load that appears and disappears inside one game must leave the gate exactly as it was,
        // not owing a permit it reclaims later for no reason.
        using var gate = new ConcurrencyGate(initial: 4, ceiling: 8);
        for (int i = 0; i < 4; i++) await gate.AcquireAsync(CancellationToken.None);

        gate.Resize(2);
        gate.Resize(4);

        gate.Release();
        gate.Release();

        await gate.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        await gate.AcquireAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(4, gate.Issued);
    }

    [Fact]
    public void TheGateNeverGrowsPastItsCeiling()
    {
        using var gate = new ConcurrencyGate(initial: 2, ceiling: 4);
        gate.Resize(99);
        Assert.Equal(4, gate.Issued);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        for (int i = 0; i < 200 && !condition(); i++) await Task.Delay(25);
        Assert.True(condition(), "condition was not reached within five seconds");
    }
}
