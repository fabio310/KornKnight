namespace ChessBot.Tests;

using ChessBot.Engine;
using ChessBot.Engine.Search;
using ChessBot.MatchRunner;
using Xunit;

/// <summary>
/// Gates for the A/B harness itself. The harness exists to decide whether a search change is
/// worth keeping, so a harness that silently fails to exercise the change under test is worse
/// than none: it produces a confident-looking report about a code path that never ran.
///
/// These tests therefore assert that the compared behaviour actually differed, not merely that
/// two searches completed.
/// </summary>
public class AbHarnessTests
{
    private static readonly string[] TinyCorpus =
    {
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1",
    };

    // Budgets found by scanning the search: at these settings an iterative-deepening pass is
    // reliably cancelled mid-iteration *and* the partial candidate beats the completed score,
    // so UsePartialRootResult actually changes the reported move. Fixed-node searches are
    // deterministic, so these stay reproducible.
    private const string PartialFen    = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const int    PartialDepth  = 8;
    private const long   PartialBudget = 12_000;

    [Fact]
    public void Run_TwoIdenticalConfigs_ProducesConsistentDeterministicMetrics()
    {
        var configA = new AbConfig { Name = "A", Build = () => new SearchSettings() };
        var configB = new AbConfig { Name = "B", Build = () => new SearchSettings() };

        var results = AbHarness.Run(configA, configB, TinyCorpus, maxDepth: 4, maxNodes: 20_000);

        Assert.Equal(2, results.Count);
        var a = results[0];
        var b = results[1];

        Assert.Equal(TinyCorpus.Length, a.Positions.Count);

        for (int i = 0; i < TinyCorpus.Length; i++)
        {
            Assert.Equal(a.Positions[i].BestMove, b.Positions[i].BestMove);
            Assert.Equal(a.Positions[i].DepthAchieved, b.Positions[i].DepthAchieved);
            Assert.Equal(a.Positions[i].TotalNodes, b.Positions[i].TotalNodes);
            Assert.False(string.IsNullOrWhiteSpace(a.Positions[i].BestMove));
        }
    }

    /// <summary>
    /// The node budget must actually bound the search. Before this was fixed the harness
    /// assigned the budget to MinNodeTarget, which the search never reads, so every position
    /// simply ran to MaxDepth and the "fixed node-budget comparison" in the report was false.
    /// </summary>
    [Fact]
    public void Run_NodeBudget_IsActuallyEnforced()
    {
        const long budget = 15_000;
        var cfg = new AbConfig { Name = "budgeted", Build = () => new SearchSettings() };

        var results = AbHarness.Run(cfg, cfg, new[] { PartialFen }, maxDepth: 20, maxNodes: budget);
        var pos = results[0].Positions[0];

        Assert.True(pos.TotalNodes <= budget + 16,
            $"search used {pos.TotalNodes:N0} nodes against a {budget:N0} budget — budget not enforced");
        Assert.True(pos.NodeBudgetReached, "the budget should have been consumed at depth 20");
        Assert.True(pos.PartialDepth > 0,
            "a budget this small must cancel an iteration mid-flight; PartialDepth stayed 0");
    }

    /// <summary>
    /// Proves the partial-root feature is genuinely exercised: the enabled configuration must
    /// report a cancelled iteration AND select its partial candidate, and the two configurations
    /// must actually differ somewhere. A conditional assertion that passes when the feature never
    /// fires would let a broken harness look healthy.
    /// </summary>
    [Fact]
    public void Run_PartialRoot_ActuallySelectsAPartialResultAndDiffersFromBaseline()
    {
        var baseline = new AbConfig
        {
            Name = "baseline",
            Build = () => new SearchSettings { UsePartialRootResult = false },
        };
        var partial = new AbConfig
        {
            Name = "partial-root",
            Build = () => new SearchSettings { UsePartialRootResult = true },
        };

        var results = AbHarness.Run(baseline, partial, new[] { PartialFen },
                                    maxDepth: PartialDepth, maxNodes: PartialBudget);

        var b = results[0].Positions[0];
        var p = results[1].Positions[0];

        // Cancellation must have happened for the feature to have anything to do.
        Assert.True(p.PartialDepth > 0, "no iteration was cancelled, so the feature could not apply");
        Assert.True(p.PartialDepth > p.DepthAchieved,
            $"partial depth {p.PartialDepth} should exceed completed depth {p.DepthAchieved}");

        // The feature must have been used, not merely available.
        Assert.True(p.UsedPartialRootResult,
            "UsePartialRootResult=true did not select a partial result in a case chosen to force one");
        Assert.False(b.UsedPartialRootResult,
            "UsePartialRootResult=false must never select a partial result");

        // Root coverage is a real fraction of the root move list, not a placeholder.
        Assert.InRange(p.RootCoveragePercent, 0.0001, 100.0);
        Assert.True(p.RootMovesCompleted > 0 && p.RootMovesCompleted < p.RootMoveCount,
            $"expected a genuinely partial root sweep, got {p.RootMovesCompleted}/{p.RootMoveCount}");

        // Both configs consumed the same budget: the difference is the selection, not the search.
        Assert.Equal(b.TotalNodes, p.TotalNodes);
    }

    /// <summary>
    /// Compares the flat schedule the engine used before the reduction table against the
    /// logarithmic schedule it uses now, and asserts the reduction behaviour actually changed.
    /// The previous version of this test compared two logarithmic parameterisations and asserted
    /// only that both searches produced nodes, which tested neither its name nor its purpose.
    /// </summary>
    [Fact]
    public void Run_LegacyFlatVsLogarithmicLmr_ChangesReductionBehaviour()
    {
        var legacy = new AbConfig
        {
            Name = "legacy-flat-lmr",
            Build = () => new SearchSettings { UseLegacyFlatLmr = true },
        };
        var logarithmic = new AbConfig
        {
            Name = "logarithmic-lmr",
            Build = () => new SearchSettings { UseLegacyFlatLmr = false },
        };

        var corpus = new[]
        {
            "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
            "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP2PPP/R2Q1RK1 w - - 0 1",
        };

        var results = AbHarness.Run(legacy, logarithmic, corpus, maxDepth: 8, maxNodes: 200_000);
        var flat = results[0];
        var log  = results[1];

        Assert.True(flat.TotalLmrReductions > 0, "the flat schedule performed no reductions at all");
        Assert.True(log.TotalLmrReductions  > 0, "the logarithmic schedule performed no reductions at all");

        // The schedules must differ in how deeply they cut, not just in how many moves they
        // touch: the logarithmic table scales the reduction with depth, the flat one does not.
        Assert.True(flat.TotalLmrPliesSaved != log.TotalLmrPliesSaved,
            $"both schedules removed the same {flat.TotalLmrPliesSaved} plies — they are not actually different");

        // And the difference must reach the search result, not stay an internal counter.
        bool anyObservableDifference =
            flat.TotalNodes != log.TotalNodes ||
            flat.AvgCompletedDepth != log.AvgCompletedDepth ||
            Enumerable.Range(0, corpus.Length).Any(i =>
                flat.Positions[i].BestMove != log.Positions[i].BestMove ||
                flat.Positions[i].Evaluation != log.Positions[i].Evaluation);

        Assert.True(anyObservableDifference,
            "the two schedules produced identical search output, so the comparison proves nothing");
    }

    /// <summary>
    /// The report must state the enforced budget, whether it was reached, both depths, root
    /// coverage, the partial-selection count and a verdict — and must not claim a strength
    /// result from a ten-position corpus.
    /// </summary>
    [Fact]
    public void BuildReport_SmallCorpus_NeverReturnsKeep()
    {
        var baseline = new AbConfig { Name = "baseline", Build = () => new SearchSettings { UsePartialRootResult = false } };
        var partial  = new AbConfig { Name = "partial-root", Build = () => new SearchSettings { UsePartialRootResult = true } };

        var results = AbHarness.Run(baseline, partial, new[] { PartialFen },
                                    maxDepth: PartialDepth, maxNodes: PartialBudget);
        var report = AbHarness.BuildReport(results, "partial-root", PartialDepth, PartialBudget, "test corpus");

        Assert.True(report.CorpusSize < AbHarness.MinCorpusForStrengthVerdict);
        Assert.NotEqual(AbVerdict.Keep, report.PartialSelectionVerdict);
        Assert.NotEqual(AbVerdict.Keep, report.LmrVerdict);
        Assert.Contains("below", report.PartialSelectionRationale);

        string tempFile = Path.Combine(Path.GetTempPath(), $"ab_report_{Guid.NewGuid():N}.log");
        try
        {
            AbHarness.WriteReport(report, tempFile);
            string content = File.ReadAllText(tempFile);

            Assert.Contains("node budget", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Node budget reached", content);
            Assert.Contains("Avg completed depth", content);
            Assert.Contains("Avg partial depth", content);
            Assert.Contains("Partial selections", content);
            Assert.Contains("root coverage", content, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Quiescence node share", content);
            Assert.Contains("Aspiration retry nodes", content);
            Assert.Contains("LMR re-search rate", content);
            Assert.Contains("Verdicts", content);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void GenerateCorpus_IsDeterministicAndDistinct()
    {
        var a = AbHarness.GenerateCorpus(40, seed: 12345);
        var b = AbHarness.GenerateCorpus(40, seed: 12345);
        var c = AbHarness.GenerateCorpus(40, seed: 999);

        Assert.Equal(40, a.Count);
        Assert.Equal(a, b);                       // same seed → same corpus
        Assert.NotEqual(a, c);                    // different seed → different corpus
        Assert.Equal(a.Count, a.Distinct().Count()); // no duplicate positions
    }
}
