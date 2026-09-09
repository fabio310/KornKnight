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
    //
    // The fixture is inherently sensitive to move ordering — how far a fixed node budget gets
    // through an iteration is exactly what ordering decides — so a change to ordering can stop
    // it provoking the condition and has to be retuned here rather than asserted around. It was
    // last retuned when a repetition inside the tree started scoring as a draw, which cuts a
    // king-and-pawn search short and let 12,000 nodes finish depth 12 outright; before that,
    // when SEE stopped classifying every capture as a good one. A king-and-pawn endgame is used
    // because its score climbs with depth, which is what makes a partial candidate beat the
    // previous iteration's completed score in the first place. 4,100–4,600 nodes all work; the
    // middle of that band is taken so the next small shift in ordering does not break it.
    private const string PartialFen    = "8/8/8/4k3/8/8/4P3/4K3 w - - 0 1";
    private const int    PartialDepth  = 12;
    private const long   PartialBudget = 4_400;

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

    // ── Threat-eval mode, time budgets and head-to-head play ─────────────────

    [Fact]
    public void Run_ThreatEvalOnVsOff_ActuallyChangesTheSearch()
    {
        var on = new AbConfig
        {
            Name = "threat-eval-on",
            Build = () => new SearchSettings { UseThreatEval = true },
        };
        var off = new AbConfig
        {
            Name = "threat-eval-off",
            Build = () => new SearchSettings { UseThreatEval = false },
        };

        var corpus = AbHarness.GenerateCorpus(20, seed: 4242);
        var results = AbHarness.Run(on, off, corpus, maxDepth: 6, maxNodes: 30_000);

        Assert.Equal("True",  results[0].EffectiveSettings["UseThreatEval"]);
        Assert.Equal("False", results[1].EffectiveSettings["UseThreatEval"]);

        // If the two configurations produced identical evaluations everywhere, the flag is not
        // reaching the search and the whole comparison would be vacuous.
        var report = AbHarness.BuildReport(results, "threat-eval", 6, 30_000, "test corpus");
        Assert.True(report.EvaluationAgreementRate < 1.0,
            "disabling the threat term changed no evaluation on any position");
    }

    [Fact]
    public void Run_TimeBudget_LetsTheNodeCountVaryInsteadOfCappingIt()
    {
        var cfg = new AbConfig { Name = "cfg", Build = () => new SearchSettings() };

        var results = AbHarness.Run(cfg, cfg, TinyCorpus, maxDepth: 6, maxNodes: 1_000, maxTimeMs: 200);

        // The node cap must not apply: with maxNodes=1000 in force, a node-budget run would stop
        // there, and the time-budget run's whole purpose is that nodes are an outcome.
        Assert.True(results[0].TotalNodes > 1_000,
            $"time-budget run stopped at {results[0].TotalNodes} nodes, as if the node cap applied");
        Assert.Equal(0, results[0].NodeBudgetReachedCount);
    }

    [Fact]
    public void NodesPerSecond_IsTotalNodesOverTotalTime()
    {
        var cfg = new AbConfig { Name = "cfg", Build = () => new SearchSettings() };
        var result = AbHarness.Run(cfg, cfg, TinyCorpus, maxDepth: 6, maxNodes: 50_000)[0];

        double expected = result.TotalNodes / (result.TotalElapsedMs / 1000.0);
        Assert.Equal(expected, result.NodesPerSecond, 3);
    }

    [Fact]
    public void GenerateOpeningPositions_AreDeterministicBalancedAndPlayable()
    {
        var a = AbHarness.GenerateOpeningPositions(6, seed: 777);
        var b = AbHarness.GenerateOpeningPositions(6, seed: 777);

        Assert.Equal(6, a.Count);
        Assert.Equal(a, b);
        Assert.Equal(a.Count, a.Distinct().Count());

        foreach (string fen in a)
        {
            var engine = new ChessEngine();
            engine.LoadFen(fen);

            // A game start where one side is already a piece up measures the opening, not the
            // change under test.
            Assert.Equal(0, engine.Evaluate().MaterialBalance.Imbalance);
            Assert.NotEmpty(engine.GetLegalMoves());
        }
    }

    [Fact]
    public void PlayHeadToHead_PlaysEveryOpeningWithBothColoursAndAccountsForEveryGame()
    {
        var cfg = new AbConfig { Name = "cfg", Build = () => new SearchSettings() };
        var openings = AbHarness.GenerateOpeningPositions(2, seed: 31337);

        var h2h = AbHarness.PlayHeadToHead(cfg, cfg, openings, nodesPerMove: 2_000, maxPlies: 40);

        Assert.Equal(openings.Count * 2, h2h.Games);
        Assert.Equal(h2h.Games, h2h.WinsA + h2h.WinsB + h2h.Draws);
        Assert.Equal(h2h.Games, h2h.TerminationReasons.Count);
        Assert.All(h2h.TerminationReasons, r => Assert.NotEqual("cancelled", r));
    }

    [Fact]
    public void HeadToHead_ScoreRateAndSignificance_ReflectTheResult()
    {
        var even = new AbHeadToHeadResult { Games = 100, WinsB = 25, WinsA = 25, Draws = 50 };
        Assert.Equal(0.5, even.ScoreRateB);
        Assert.False(even.IsSignificant);
        Assert.Equal(0, even.EloDifference!.Value, 6);

        // A small edge over few games must not read as evidence.
        var slightEdge = new AbHeadToHeadResult { Games = 100, WinsB = 30, WinsA = 26, Draws = 44 };
        Assert.True(slightEdge.ScoreRateB > 0.5);
        Assert.False(slightEdge.IsSignificant);

        var decisive = new AbHeadToHeadResult { Games = 200, WinsB = 120, WinsA = 40, Draws = 40 };
        Assert.True(decisive.IsSignificant);
        Assert.True(decisive.EloDifference > 0);

        // A clean sweep has no finite logistic estimate; reporting one would be an invention.
        var sweep = new AbHeadToHeadResult { Games = 10, WinsB = 10 };
        Assert.Null(sweep.EloDifference);
        Assert.Null(sweep.EloMarginOfError);
    }

    [Fact]
    public void ThreatEvalVerdict_NeedsGamesOrAdjudication()
    {
        var cfg = new AbConfig { Name = "cfg", Build = () => new SearchSettings() };
        var results = AbHarness.Run(cfg, cfg, TinyCorpus, maxDepth: 4, maxNodes: 20_000);
        var report = AbHarness.BuildReport(results, "threat-eval", 4, 20_000, "test corpus");

        // Node rate and depth show what the term costs, never whether removing it wins.
        Assert.Equal(AbVerdict.Inconclusive, report.ThreatEvalVerdict);

        // An inconclusive game result must stay inconclusive rather than being read as a win.
        AbHarness.AttachHeadToHead(report, new AbHeadToHeadResult
        {
            Games = 20, WinsB = 11, WinsA = 9, Draws = 0, NodesPerMove = 1_000,
        });
        Assert.Equal(AbVerdict.Inconclusive, report.ThreatEvalVerdict);

        AbHarness.AttachHeadToHead(report, new AbHeadToHeadResult
        {
            Games = 400, WinsB = 240, WinsA = 80, Draws = 80, NodesPerMove = 1_000,
        });
        Assert.Equal(AbVerdict.Keep, report.ThreatEvalVerdict);
    }

    [Fact]
    public void ThreatEvalVerdict_IsInconclusiveInOtherModes()
    {
        var cfg = new AbConfig { Name = "cfg", Build = () => new SearchSettings() };
        var results = AbHarness.Run(cfg, cfg, TinyCorpus, maxDepth: 4, maxNodes: 20_000);
        var report = AbHarness.BuildReport(results, "lmr", 4, 20_000, "test corpus");

        Assert.Equal(AbVerdict.Inconclusive, report.ThreatEvalVerdict);
        Assert.Contains("not compared", report.ThreatEvalRationale);
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

    // ── Corpus legality ────────────────────────────────────────────────────
    //
    // DefaultCorpus[8] shipped for months with the two kings on h1 and g2. Nothing caught it,
    // because an illegal position does not announce itself: it crashed inside negamax, and only
    // for A/B runs that took the built-in corpus rather than a generated one. Every FEN the
    // harness can hand to a search is checked here, so the next bad entry fails at build time
    // rather than mid-measurement.

    /// <summary>
    /// Names every corpus entry the engine cannot search: one it refuses to load, and one that
    /// is already checkmate or stalemate. A terminal position does not crash, it just returns a
    /// null move having visited no nodes, which quietly shrinks the corpus a measurement was
    /// sized for. Returns the failures joined, empty when all entries are usable, so an
    /// assertion failure says which position and why rather than only that one existed.
    /// </summary>
    private static string IllegalEntriesIn(IEnumerable<string> fens, string label)
    {
        var failures = new List<string>();
        int index = 0;

        foreach (string fen in fens)
        {
            try
            {
                var engine = new ChessEngine();
                engine.LoadFen(fen);

                if (engine.GetLegalMoves().Count == 0)
                    failures.Add($"{label}[{index}]: terminal position, nothing to search. FEN: '{fen}'.");
            }
            catch (ArgumentException ex)
            {
                failures.Add($"{label}[{index}]: {ex.Message}");
            }
            index++;
        }

        return string.Join(Environment.NewLine, failures);
    }

    [Fact]
    public void DefaultCorpus_ContainsOnlyLegalPositions()
    {
        string failures = IllegalEntriesIn(AbHarness.DefaultCorpus, nameof(AbHarness.DefaultCorpus));

        Assert.True(failures.Length == 0, failures);
    }

    [Fact]
    public void GenerateCorpus_ProducesOnlyLegalPositions()
    {
        // Two seeds, including the shipped default: the walk is seeded, so a generator that
        // could produce an unloadable FEN would do it for some seeds and not others.
        foreach (int seed in new[] { 20260906, 4242 })
        {
            string failures = IllegalEntriesIn(AbHarness.GenerateCorpus(200, seed), $"GenerateCorpus(seed:{seed})");
            Assert.True(failures.Length == 0, failures);
        }
    }

    [Fact]
    public void GenerateOpeningPositions_ProduceOnlyLegalPositions()
    {
        string failures = IllegalEntriesIn(AbHarness.GenerateOpeningPositions(40, seed: 20260907),
                                           nameof(AbHarness.GenerateOpeningPositions));

        Assert.True(failures.Length == 0, failures);
    }
}
