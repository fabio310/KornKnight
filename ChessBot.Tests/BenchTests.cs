namespace ChessBot.Tests;

using System.IO;
using ChessBot.Engine;
using ChessBot.Uci;
using Xunit;

/// <summary>
/// Gates on the node-rate harness itself.
///
/// The harness makes exactly one claim that everything in the performance work then rests on:
/// that its node total is a fingerprint of the search tree, so two builds reporting the same
/// total walked the same tree, and a build reporting a different total did not. That claim is
/// worth nothing unless the total is reproducible for a single build, which is what is tested
/// here.
///
/// Deliberately shallow. The depths used below are chosen to keep the suite fast; the harness
/// runs at <see cref="Bench.DefaultDepth"/> when it is measuring, and the reproducibility
/// property does not depend on the depth.
/// </summary>
public class BenchTests
{
    [Fact]
    public void EveryBenchPositionIsLegal()
    {
        // The corpus is loaded through the board's own validation, which is stricter than FEN
        // parsing: a FEN can parse and still describe a position the rules cannot produce.
        // A corpus entry that throws here would take the harness down mid-measurement, and
        // this repository has shipped exactly that entry before (PositionLegalityTests).
        var engine = new ChessEngine();

        foreach (string fen in Bench.Positions)
        {
            engine.LoadFen(fen);
            Assert.NotEmpty(engine.GetLegalMoves());
        }
    }

    [Fact]
    public void TheCorpusSpansMoreThanOnePhase()
    {
        // A node-rate figure taken entirely on middlegames would be measuring one shape of
        // tree. The cheap proxy for phase coverage is piece count, and the assertion is only
        // that the set is not clustered: a full board and a bare-bones ending both appear.
        int fullest  = 0;
        int sparsest = int.MaxValue;

        foreach (string fen in Bench.Positions)
        {
            int pieces = fen.Split(' ')[0].Count(char.IsLetter);
            fullest  = Math.Max(fullest, pieces);
            sparsest = Math.Min(sparsest, pieces);
        }

        Assert.True(fullest >= 30, $"no position with a full board; fullest has {fullest} pieces");
        Assert.True(sparsest <= 8, $"no endgame in the set; sparsest has {sparsest} pieces");
    }

    [Fact]
    public void TheNodeTotalIsReproducible()
    {
        // Two independent runs of the same build must agree on the node total exactly. This is
        // the property that lets a speed-neutral change be verified by comparing two numbers
        // instead of by running a match: if this were only approximately true, a changed total
        // would not be evidence of a changed tree.
        var first  = Bench.Run(TextWriter.Null, depth: 5, repeats: 1);
        var second = Bench.Run(TextWriter.Null, depth: 5, repeats: 1);

        Assert.Equal(first.Nodes, second.Nodes);
        Assert.Equal(Bench.Positions.Length, first.Positions);
        Assert.Equal(5, first.Depth);
        Assert.True(first.Nodes > 0);
    }

    [Fact]
    public void ASingleRunWithRepeatsChecksItsOwnReproducibility()
    {
        // The multi-pass run reports a warning line rather than throwing when its passes
        // disagree. On a correct build there is no warning, and that absence is the assertion:
        // it is how the harness reports that its own figure is trustworthy.
        var report = new StringWriter();
        Bench.Run(report, depth: 5, repeats: 2);

        Assert.DoesNotContain("WARNING", report.ToString());
    }

    [Fact]
    public void TheReportCarriesTheNumbersAMeasurementNeeds()
    {
        var report = new StringWriter();
        var summary = Bench.Run(report, depth: 4, repeats: 1);
        string text = report.ToString();

        // One line per position, each naming the node count and the move chosen, because a
        // changed total is only actionable if the report says which position moved.
        foreach (string line in Bench.Positions.Select((_, i) => $"position {i + 1,2}/{Bench.Positions.Length}"))
            Assert.Contains(line, text);

        Assert.Contains($"Nodes searched   : {summary.Nodes}", text);
        Assert.Contains("Nodes/second", text);
    }

    [Fact]
    public void BenchRunsFromTheUciCommandLoop()
    {
        // The measurement has to be available from the shipped binary, not only from a test
        // host: the test host is a different process with different GC settings, and those are
        // two different numbers.
        var output = new StringWriter();
        using var session = new UciSession(new ChessEngine(), output);

        Assert.True(session.Execute("bench 4 16 1"));

        var summary = session.LastBench;
        Assert.NotNull(summary);
        Assert.Equal(4, summary!.Value.Depth);
        Assert.True(summary.Value.Nodes > 0);
        Assert.Contains("Nodes/second", output.ToString());
    }

    [Fact]
    public void BenchLeavesTheSessionPositionAlone()
    {
        // The bench runs on its own engine. A host that asks for a measurement mid-game must
        // not find its position replaced by the last entry of the corpus.
        var engine = new ChessEngine();
        var output = new StringWriter();
        using var session = new UciSession(engine, output);

        session.Execute("position startpos moves e2e4 e7e5");
        string before = engine.ExportFen();

        session.Execute("bench 4 16 1");

        Assert.Equal(before, engine.ExportFen());
    }

    [Fact]
    public void AMistypedArgumentFallsBackRatherThanRefusing()
    {
        var output = new StringWriter();
        using var session = new UciSession(new ChessEngine(), output);

        // "huge" is not a table size. The rest of this session treats malformed input by
        // falling back rather than by refusing, and the bench follows that. The depth is given
        // explicitly and small here only to keep the test quick — a mistyped depth takes the
        // same path.
        Assert.True(session.Execute("bench 4 huge 1"));

        Assert.NotNull(session.LastBench);
        Assert.Equal(4, session.LastBench!.Value.Depth);
        Assert.True(session.LastBench.Value.Nodes > 0);
    }
}
