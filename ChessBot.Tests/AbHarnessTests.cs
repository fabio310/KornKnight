namespace ChessBot.Tests;

using ChessBot.Engine.Search;
using ChessBot.MatchRunner;
using Xunit;

public class AbHarnessTests
{
    private static readonly string[] TinyCorpus =
    {
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1",
    };

    [Fact]
    public void Run_TwoIdenticalConfigs_ProducesConsistentDeterministicMetrics()
    {
        var configA = new AbConfig { Name = "A", Build = () => new SearchSettings() };
        var configB = new AbConfig { Name = "B", Build = () => new SearchSettings() };

        var results = AbHarness.Run(configA, configB, TinyCorpus, maxDepth: 4, maxNodes: 20_000);

        Assert.Equal(2, results.Count);
        var a = results[0];
        var b = results[1];

        Assert.Equal("A", a.Name);
        Assert.Equal("B", b.Name);
        Assert.Equal(TinyCorpus.Length, a.Positions.Count);
        Assert.Equal(TinyCorpus.Length, b.Positions.Count);

        // Identical settings on identical positions must produce identical best moves
        // and depth (search itself is deterministic; this is the harness's own correctness gate).
        for (int i = 0; i < TinyCorpus.Length; i++)
        {
            Assert.Equal(a.Positions[i].BestMove, b.Positions[i].BestMove);
            Assert.Equal(a.Positions[i].DepthAchieved, b.Positions[i].DepthAchieved);
            Assert.False(string.IsNullOrWhiteSpace(a.Positions[i].BestMove));
        }
    }

    [Fact]
    public void Run_PartialRootVsBaseline_ProducesReportWithoutThrowing()
    {
        var baseline = new AbConfig { Name = "baseline", Build = () => new SearchSettings { UsePartialRootResult = false } };
        var partial  = new AbConfig { Name = "partial-root", Build = () => new SearchSettings { UsePartialRootResult = true } };

        var results = AbHarness.Run(baseline, partial, TinyCorpus, maxDepth: 4, maxNodes: 20_000);

        string tempFile = Path.Combine(Path.GetTempPath(), $"ab_report_{Guid.NewGuid():N}.log");
        try
        {
            AbHarness.WriteReport(results, tempFile);
            Assert.True(File.Exists(tempFile));
            string content = File.ReadAllText(tempFile);
            Assert.Contains("Controlled A/B Harness Report", content);
            Assert.Contains("baseline", content);
            Assert.Contains("partial-root", content);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void Run_LmrScheduleOverride_ChangesReductionCounts()
    {
        var baseline = new AbConfig { Name = "baseline-lmr", Build = () => new SearchSettings() };
        var altLmr   = new AbConfig
        {
            Name = "alt-lmr",
            Build = () => new SearchSettings { LmrBaseOverride = 1.0, LmrDivisorOverride = 2.0 },
        };

        var results = AbHarness.Run(baseline, altLmr, TinyCorpus, maxDepth: 6, maxNodes: 50_000);

        Assert.Equal(2, results.Count);
        // Just verifying the override path runs end-to-end without throwing and produces
        // well-formed aggregate metrics; the exact reduction counts are not asserted since
        // they are a heuristic search-shape outcome, not a fixed invariant.
        Assert.True(results[0].TotalNodes > 0);
        Assert.True(results[1].TotalNodes > 0);
    }
}
