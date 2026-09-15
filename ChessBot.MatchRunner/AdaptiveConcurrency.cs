namespace ChessBot.MatchRunner;

/// <summary>What the governor is doing on a given tick, for logs and for the manifest.</summary>
public enum ConcurrencyPhase
{
    /// <summary>Waiting for the node rate to settle after a change, so the next reading is of one thing.</summary>
    Settling,

    /// <summary>Holding a target it has no reason to move, and collecting a baseline.</summary>
    Steady,

    /// <summary>Running one extra game to find out whether it was free.</summary>
    Probing,
}

/// <summary>
/// How many games a run should have in flight right now, decided by measuring rather than by a
/// rule of thumb, and re-decided for as long as the run lasts.
///
/// Two things bound it, and they answer different questions.
///
/// <para>
/// <b>What somebody else is using</b> is a bound on what we may take. Cores busy with another
/// process are not ours to claim, so the target never exceeds the free ones. This is the polite
/// bound, and it is the only one a node budget needs: a node-budget run is bit-identical at any
/// concurrency, so contention costs it wall-clock and nothing else.
/// </para>
///
/// <para>
/// <b>What another game costs the engines</b> is the bound that matters on a time budget, and it
/// is the reason this class exists. The old rule was to take half the physical cores, on the
/// reasoning that a timed run measures the scheduler as well as the engine. That is true, but
/// "half" was never measured — it was a guess at where contention starts, and a guess is
/// necessarily wrong on some machine, under some load, at some budget. So the run finds out
/// instead: it adds a game, watches what happens to the node rate the engines actually achieve,
/// and keeps the extra game only if it cost nothing. A game that is free is free whatever a rule
/// of thumb says, and one that halves every search is not free at concurrency 4 either.
/// </para>
///
/// <para>
/// Nothing here is decided once. A cap discovered by a failed probe is relaxed again after a few
/// minutes, because the load that caused it is not permanent; the polite bound is re-read every
/// tick; and the probe repeats for the life of the run. A governor that measured the machine at
/// startup would be describing a machine that no longer exists an hour later.
/// </para>
///
/// <para>
/// The target only ever changes at a game boundary — see <see cref="ConcurrencyGate"/> — so no
/// game is played under two sets of conditions, and every game is attributable to the concurrency
/// it was played at.
/// </para>
/// </summary>
public sealed class AdaptiveConcurrency
{
    /// <summary>
    /// How many readings the polite bound is decided from. The median of these is used rather
    /// than a running average: an average lets one spike through in proportion to its size and
    /// then takes several samples to forget it, so a one-second compile would cost the run cores
    /// for half a minute. A median of five ignores a single spike and still follows a change that
    /// lasts three samples, which is the shape of the thing being detected — somebody starting
    /// work on the machine, not a burst.
    /// </summary>
    private const int LoadWindow = 5;

    /// <summary>
    /// How far the node rate may fall before an extra game counts as having cost something.
    /// Node rate varies between positions, so a threshold at zero would reject every probe on
    /// noise; this is wide enough to survive the sampling and far narrower than the ~50% a
    /// genuinely oversubscribed machine costs.
    /// </summary>
    private const double Tolerance = 0.05;

    /// <summary>
    /// Samples needed before a node-rate reading is trusted. At a 50 ms budget with several games
    /// in flight these arrive in a second or two, so this costs the probe very little time and
    /// buys a median that is not an accident of which positions were in play.
    /// </summary>
    private const int MinSamples = 200;

    /// <summary>
    /// Ticks before a cap discovered by a failed probe is relaxed by one, letting the run try for
    /// the core again. The load that caused the failure is not permanent, and a cap that outlived
    /// it would be the "decide once at startup" behaviour this class exists to avoid.
    /// </summary>
    private const int CapRelaxTicks = 60;

    private readonly BudgetKind _budget;
    private readonly int _physicalCores;
    private readonly object _lock = new();

    private readonly double[] _loadReadings = new double[LoadWindow];
    private int _loadCount;
    private int _nextLoad;

    private int _throughputCap;
    private int _ticksSinceCapSet;
    private double _baselineNodeRate;
    private int _targetBeforeProbe;

    private int _observedMin;
    private int _observedMax;
    private long _targetSum;
    private long _targetSamples;
    private int _probesAccepted;
    private int _probesRejected;

    public AdaptiveConcurrency(BudgetKind budget, int physicalCores, int? startAt = null)
    {
        _budget = budget;
        _physicalCores = Math.Max(1, physicalCores);
        Ceiling = Math.Max(1, _physicalCores);
        _throughputCap = Ceiling;

        // Starts where the old fixed rule would have put it, and climbs from there only once a
        // probe has shown the extra game is free. Starting at the ceiling would make the run's
        // first minutes its worst ones on a machine that cannot sustain it.
        Target = Math.Clamp(startAt ?? ConservativeStart(budget, _physicalCores), 1, Ceiling);

        _observedMin = Target;
        _observedMax = Target;
        Phase = ConcurrencyPhase.Settling;
    }

    /// <summary>The most games this run may ever have in flight: one per physical core.</summary>
    public int Ceiling { get; }

    /// <summary>How many games should be in flight right now.</summary>
    public int Target { get; private set; }

    /// <summary>What the governor is currently doing.</summary>
    public ConcurrencyPhase Phase { get; private set; }

    /// <summary>The highest target the node rate currently supports. Relaxes upward over time.</summary>
    public int ThroughputCap { get { lock (_lock) return _throughputCap; } }

    /// <summary>The smallest and largest target the run actually used, and the mean, for the manifest.</summary>
    public (int Min, int Max, double Mean) Observed
    {
        get
        {
            lock (_lock)
            {
                double mean = _targetSamples == 0 ? Target : (double)_targetSum / _targetSamples;
                return (_observedMin, _observedMax, mean);
            }
        }
    }

    /// <summary>How many extra games turned out to be free, and how many did not.</summary>
    public (int Accepted, int Rejected) Probes
    {
        get { lock (_lock) return (_probesAccepted, _probesRejected); }
    }

    /// <summary>
    /// Where a run starts before it has measured anything: every core for a node budget, which is
    /// free by construction, and the old half-cores rule for a timed one, which is the value this
    /// project's existing results were taken at.
    /// </summary>
    public static int ConservativeStart(BudgetKind budget, int physicalCores) =>
        budget == BudgetKind.Nodes
            ? Math.Max(1, physicalCores)
            : Math.Max(1, physicalCores / 2);

    /// <summary>
    /// The most games the machine's free cores allow, ignoring throughput. Cores somebody else is
    /// using are not ours to take, whichever budget we are on.
    /// </summary>
    public static int PoliteBound(int physicalCores, double externalCores)
    {
        double free = physicalCores - Math.Max(0, externalCores);
        return Math.Clamp((int)Math.Floor(free), 1, Math.Max(1, physicalCores));
    }

    /// <summary>
    /// Folds in one tick's worth of evidence — what everything else is doing to the CPU, and what
    /// node rate the engines have achieved since the last change — and returns the new target.
    ///
    /// Call from one thread at a time; the governor timer is the only caller.
    /// </summary>
    /// <param name="externalCoresNow">Cores' worth of CPU used by everything that is not this run.</param>
    /// <param name="samples">Node rates recorded since the last change. Cleared here when a change is made.</param>
    public int Update(double externalCoresNow, ThroughputSamples samples)
    {
        lock (_lock)
        {
            RecordLoad(externalCoresNow);

            if (_ticksSinceCapSet++ >= CapRelaxTicks && _throughputCap < Ceiling)
            {
                _throughputCap++;
                _ticksSinceCapSet = 0;
            }

            int polite = PoliteBound(_physicalCores, MedianLoad());

            // Yielding comes first and does not wait for a measurement. Somebody else needing the
            // machine is not a hypothesis to be tested, and a probe in flight is abandoned.
            if (polite < Target)
            {
                SetTarget(polite, samples);
                return Record();
            }

            // A node budget is identical at any concurrency, so there is nothing for a probe to
            // discover: take whatever is free.
            if (_budget == BudgetKind.Nodes)
            {
                if (polite != Target) SetTarget(polite, samples);
                return Record();
            }

            // One tick is skipped after every change so the reading that follows describes the
            // new concurrency rather than the transition into it.
            if (Phase == ConcurrencyPhase.Settling)
            {
                Phase = _targetBeforeProbe == 0 ? ConcurrencyPhase.Steady : ConcurrencyPhase.Probing;
                samples.Clear();
                return Record();
            }

            if (samples.Count < MinSamples) return Record();

            double rate = samples.Median();

            if (Phase == ConcurrencyPhase.Probing)
            {
                bool free = rate >= _baselineNodeRate * (1 - Tolerance);

                if (free)
                {
                    // The extra game cost nothing. Keep it, and leave the cap alone so the next
                    // probe can try for another.
                    _probesAccepted++;
                    _baselineNodeRate = rate;
                }
                else
                {
                    // It cost the engines nodes, which on a timed budget is the one thing that
                    // must not happen. Give the game back and remember this level, for a while.
                    _probesRejected++;
                    _throughputCap = _targetBeforeProbe;
                    _ticksSinceCapSet = 0;
                    SetTarget(_targetBeforeProbe, samples);
                }

                _targetBeforeProbe = 0;
                Phase = ConcurrencyPhase.Settling;
                samples.Clear();
                return Record();
            }

            // Steady: hold a baseline, and try one more game when both bounds allow it.
            _baselineNodeRate = rate;

            int allowed = Math.Min(polite, _throughputCap);
            if (Target < allowed)
            {
                _targetBeforeProbe = Target;
                SetTarget(Target + 1, samples);
                Phase = ConcurrencyPhase.Settling;
            }

            return Record();
        }
    }

    private void SetTarget(int value, ThroughputSamples samples)
    {
        int clamped = Math.Clamp(value, 1, Ceiling);
        if (clamped == Target) return;

        Target = clamped;
        Phase = ConcurrencyPhase.Settling;
        samples.Clear();
    }

    private int Record()
    {
        if (Target < _observedMin) _observedMin = Target;
        if (Target > _observedMax) _observedMax = Target;
        _targetSum += Target;
        _targetSamples++;
        return Target;
    }

    private void RecordLoad(double reading)
    {
        _loadReadings[_nextLoad] = reading;
        _nextLoad = (_nextLoad + 1) % LoadWindow;
        if (_loadCount < LoadWindow) _loadCount++;
    }

    private double MedianLoad()
    {
        Span<double> sorted = stackalloc double[LoadWindow];
        for (int i = 0; i < _loadCount; i++) sorted[i] = _loadReadings[i];
        sorted = sorted[.._loadCount];
        sorted.Sort();
        return _loadCount == 0 ? 0 : sorted[_loadCount / 2];
    }

    /// <summary>One line describing what the run took from the machine, for logs and manifests.</summary>
    public string Describe()
    {
        var (min, max, mean) = Observed;
        var (accepted, rejected) = Probes;

        string range = min == max ? $"{min}" : $"{min}-{max}, mean {mean:F1}";
        string probes = accepted + rejected == 0 ? "" : $", {accepted} probes free / {rejected} too many";

        return $"{_budget} budget, adaptive concurrency {range} of a {Ceiling} ceiling on " +
               $"{_physicalCores} physical cores ({MachineTopology.LogicalProcessorCount} logical, " +
               $"{MachineTopology.CoreCountSource}){probes}";
    }
}
