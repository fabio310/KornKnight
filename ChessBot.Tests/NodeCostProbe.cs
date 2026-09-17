namespace ChessBot.Tests;

using System;
using System.Diagnostics;
using ChessBot.Engine;
using ChessBot.Engine.Search;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// What a change costs per node, and whether it changed what the search decides.
///
/// ENGINEERING.md picks a node budget by default and a time budget when a change alters what
/// evaluation or move ordering costs to compute. Deciding which of the two a change needs is
/// itself a measurement — the cost-neutral premise has been wrong in both of the last two groups —
/// and this is what takes it: a fixed node count over six positions spanning the phases, timed.
///
/// It also prints the best move, score and node count per position. Two builds that are supposed
/// to make identical decisions must agree on all three exactly; that is a stronger statement than
/// any match result and it costs seconds.
///
/// Skipped by default: it is a timing measurement and wants a quiet machine, so it runs only when
/// asked for by name.
///
///   dotnet test ChessBot.Tests -c Release --filter "FullyQualifiedName~NodeCostProbe" \
///       -l "console;verbosity=detailed"
///
/// PROBE_NODES and PROBE_REPEATS override the defaults.
/// </summary>
public class NodeCostProbe
{
    private readonly ITestOutputHelper _out;
    public NodeCostProbe(ITestOutputHelper output) => _out = output;

    // Two openings, two middlegames, two endgames.
    private static readonly string[] Positions =
    {
        "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
        "r1bq1rk1/pp1nbppp/2ppp3/8/2PPP3/2N1BN2/PP2BPPP/R2Q1RK1 w - - 0 10",
        "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
        "2rq1rk1/pp1bppbp/3p1np1/8/3NP3/1BN1BP2/PPPQ2PP/2KR3R w - - 0 12",
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "8/8/4k3/8/2p5/8/B2P2K1/8 w - - 0 1",
    };

    [Fact]
    public void NodeCost()
    {
        long nodes   = Setting("PROBE_NODES", 2_000_000);
        int  repeats = (int)Setting("PROBE_REPEATS", 3);

        // One untimed pass so the JIT has compiled the search before anything is measured.
        foreach (string fen in Positions) Run(fen, nodes / 20);

        double best = double.MaxValue;
        for (int repeat = 0; repeat < repeats; repeat++)
        {
            var clock = Stopwatch.StartNew();
            foreach (string fen in Positions) Run(fen, nodes);
            clock.Stop();

            _out.WriteLine($"repeat {repeat + 1}: {clock.Elapsed.TotalSeconds:F2} s");
            best = Math.Min(best, clock.Elapsed.TotalSeconds);
        }

        long totalNodes = nodes * Positions.Length;
        _out.WriteLine($"BEST {best:F2} s for {totalNodes:N0} nodes " +
                       $"= {totalNodes / best:N0} nodes/s");
        _out.WriteLine("");

        // Per position, what the search decided. Two builds meant to be equivalent agree here
        // exactly or they are not equivalent.
        foreach (string fen in Positions)
        {
            var result = Run(fen, nodes);
            _out.WriteLine($"DECISION {result.BestMove}  score {result.Evaluation}  " +
                           $"depth {result.DepthAchieved}  nodes {result.NodesSearched}  " +
                           $"qnodes {result.QNodesSearched}  " +
                           $"futility {result.FutilitySkips}  lmp {result.LateMovePrunes}");
        }
    }

    private static SearchResult Run(string fen, long nodes)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);

        return engine.FindBestMove(new SearchSettings { MaxNodes = nodes, MaxTimeMs = 600_000 });
    }

    private static long Setting(string name, long fallback) =>
        long.TryParse(Environment.GetEnvironmentVariable(name), out long value) ? value : fallback;
}
