namespace ChessBot.Tests;

using System;
using System.Collections.Generic;
using System.Linq;
using ChessBot.Engine;
using ChessBot.Engine.Search;
using Xunit;
using Xunit.Abstractions;

/// <summary>
/// How much bigger each iterative-deepening iteration is than the one before it, measured on this
/// engine rather than taken from the textbook.
///
/// It is the number a soft limit on starting an iteration has to be derived from. If iterations
/// grow by a factor r, then when iteration d finishes, the elapsed time is roughly the cost of
/// iteration d times r/(r-1), and iteration d+1 will cost r times iteration d — which is
/// (r-1) times the elapsed total. So starting d+1 is affordable exactly when
///
///     elapsed + elapsed x (r - 1)  &lt;=  budget      i.e.   elapsed &lt;= budget / r
///
/// The fraction of the budget to stop at is therefore 1/r, and nothing else. A textbook 40% or
/// 50% is an assertion that r is 2.5 or 2 on the engine the textbook's author was writing about.
///
/// Measured at fixed depth, not at a fixed time, so the answer does not move with what else is
/// running on the machine: the ratio is a property of the tree, and only the wall clock is
/// contended.
/// </summary>
public class IterationGrowthProbe
{
    private readonly ITestOutputHelper _out;
    public IterationGrowthProbe(ITestOutputHelper output) => _out = output;

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
    public void GrowthRatio()
    {
        int depth = (int)(long.TryParse(Environment.GetEnvironmentVariable("PROBE_DEPTH"),
                                        out long d) ? d : 14);

        var allRatios = new List<double>();

        foreach (string fen in Positions)
        {
            var engine = new ChessEngine();
            engine.LoadFen(fen);

            var result = engine.FindBestMove(
                new SearchSettings { MaxDepth = depth, MaxTimeMs = 600_000 });

            var nodes = result.IterationNodes;
            var ratios = new List<double>();

            // The quantity a soft limit actually needs, measured rather than derived. At the
            // moment iteration d completes, the work done is the cumulative nodes[d] and the work
            // iteration d+1 would add is its own cost. Starting it is affordable exactly when
            // nodes[d] + own(d+1) fits the budget, so the largest fraction of the budget at which
            // starting is still right is nodes[d] / (nodes[d] + own(d+1)).
            //
            // Going through a growth ratio and a geometric sum gets to 1/r, which is the same
            // thing under the assumption that iterations grow smoothly. They do not: the ratios
            // alternate hard, because an iteration whose aspiration window fails costs several
            // times one whose window holds, and the next one then inherits a good window. This
            // form does not care.
            for (int i = 3; i < nodes.Count; i++)
            {
                long done = nodes[i - 1];
                long next = nodes[i] - nodes[i - 1];
                if (done > 0 && next > 0) ratios.Add((double)done / (done + next));
            }

            // The shallow iterations are noise — a few thousand nodes each, dominated by fixed
            // costs — and they are also never the ones a soft limit is deciding about. Only the
            // deep half of the search is reported.
            var deep = ratios.Skip(ratios.Count / 2).ToList();
            allRatios.AddRange(deep);

            _out.WriteLine($"depth {result.DepthAchieved,2}  " +
                           $"deep-half fractions [{string.Join(", ", deep.Select(r => r.ToString("P0")))}]");
        }

        allRatios.Sort();
        double median = allRatios[allRatios.Count / 2];
        double mean   = allRatios.Average();

        _out.WriteLine("");
        _out.WriteLine($"{allRatios.Count} decision points over {Positions.Length} positions to depth {depth}");
        _out.WriteLine($"  median {median:P0}   mean {mean:P0}");
        _out.WriteLine($"  25th/75th = {allRatios[allRatios.Count / 4]:P0} / {allRatios[3 * allRatios.Count / 4]:P0}");
        _out.WriteLine($"  10th/90th = {allRatios[allRatios.Count / 10]:P0} / {allRatios[9 * allRatios.Count / 10]:P0}");

        Assert.True(allRatios.Count > 10, "not enough completed iterations to say anything");
    }
}
