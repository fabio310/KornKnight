namespace ChessBot.MatchRunner;

/// <summary>How a run's per-move budget is expressed. The two are not comparable.</summary>
public enum BudgetKind
{
    /// <summary>
    /// A fixed node count per move. Deterministic and immune to CPU contention: the same
    /// position always searches the same nodes and returns the same move, so concurrency 32 is
    /// bit-identical to concurrency 1 and simply finishes sooner.
    /// </summary>
    Nodes,

    /// <summary>
    /// A fixed wall-clock budget per move. This is the only budget that charges a change for
    /// what it costs to compute — and it measures the scheduler alongside the engine, so the
    /// number it produces depends on how loaded the machine was.
    /// </summary>
    Time,
}

/// <summary>
/// One side's budget for one move, and the UCI command that expresses it.
///
/// The two kinds are not interchangeable and a run has to say which it used. A node budget makes
/// a game reproducible: the same position searches the same nodes and returns the same move
/// whatever else the machine is doing. A time budget is the only one that charges a change for
/// what it costs to compute, and the only one whose result depends on how loaded the machine was.
/// </summary>
public readonly record struct MoveBudget(BudgetKind Kind, long Value)
{
    public static MoveBudget Nodes(long nodes) => new(BudgetKind.Nodes, Math.Max(1, nodes));
    public static MoveBudget Time(int ms)      => new(BudgetKind.Time,  Math.Max(1, ms));

    /// <summary>
    /// The "go" command for this budget. <c>go nodes</c> carries no clock, so a UCI engine has
    /// nothing but the node count to stop on and the search stays deterministic; adding a
    /// movetime alongside it would quietly turn a node budget back into a time budget on any
    /// position that ran long.
    /// </summary>
    public string ToGoCommand() => Kind == BudgetKind.Nodes
        ? $"go nodes {Value}"
        : $"go movetime {Value}";

    /// <summary>
    /// How long to wait for the engine's answer before treating it as hung. A timed move has a
    /// known deadline and gets a fixed grace period on top; a node budget has no wall-clock
    /// bound at all, so it gets a generous ceiling that only catches a genuinely stuck process.
    /// </summary>
    public int ResponseTimeoutMs => Kind == BudgetKind.Time
        ? checked((int)Math.Min(int.MaxValue - 5_000, Value)) + 5_000
        : 300_000;

    public override string ToString() => Kind == BudgetKind.Nodes
        ? $"{Value:N0} nodes/move"
        : $"{Value:N0} ms/move";
}

/// <summary>
/// Decides how much of the machine a run may take, from the one fact that actually settles it:
/// which budget the run is on.
///
/// A node budget is reproducible under any load, so there is no reason to leave a core idle.
/// A time budget is a measurement of the machine as much as of the engine, so it stays at half
/// the physical cores and warns when asked for more. Sizing both the same way — as this harness
/// did, at a flat cap of ten — makes node-budget runs three times slower than they need to be on
/// a large box and lets timed runs oversubscribe a small one.
/// </summary>
public static class ConcurrencyPolicy
{
    /// <summary>
    /// The default degree of parallelism for a run of <paramref name="games"/> games on
    /// <paramref name="budget"/>, never more than there is work for.
    /// </summary>
    public static int DefaultFor(BudgetKind budget, int games)
    {
        int cores = Math.Max(1, MachineTopology.PhysicalCoreCount);
        int ceiling = budget == BudgetKind.Nodes ? cores : Math.Max(1, cores / 2);
        return Math.Clamp(games <= 0 ? ceiling : games, 1, ceiling);
    }

    /// <summary>The highest concurrency a timed run can use before its timings stop being trustworthy.</summary>
    public static int RecommendedTimedCeiling => Math.Max(1, MachineTopology.PhysicalCoreCount / 2);

    /// <summary>
    /// Warns on stderr when a timed run was asked to use more of the machine than its timings can
    /// survive. Node-budget runs are never warned about: there is nothing for contention to
    /// distort. Returns the warning text (empty when none), so the manifest can record that the
    /// run was flagged rather than leaving it only in a console scrollback.
    /// </summary>
    public static string WarnIfOversubscribed(BudgetKind budget, int concurrency)
    {
        if (budget != BudgetKind.Time || concurrency <= RecommendedTimedCeiling) return string.Empty;

        string warning =
            $"WARNING: timed run at concurrency {concurrency} on {MachineTopology.PhysicalCoreCount} " +
            $"physical cores ({MachineTopology.LogicalProcessorCount} logical, " +
            $"{MachineTopology.CoreCountSource}). Above {RecommendedTimedCeiling} the games compete " +
            "for cores, so every move gets fewer nodes and NPS from this run must not be quoted. " +
            "The comparison stays fair — both sides are slowed equally — but it measures strength " +
            "at that speed, not at the machine's.";

        Console.Error.WriteLine(warning);
        return warning;
    }

    /// <summary>One line describing what the run took from the machine, for logs and manifests.</summary>
    public static string Describe(BudgetKind budget, int concurrency) =>
        $"{budget} budget, concurrency {concurrency} of {MachineTopology.PhysicalCoreCount} physical " +
        $"cores ({MachineTopology.LogicalProcessorCount} logical, {MachineTopology.CoreCountSource})";
}
