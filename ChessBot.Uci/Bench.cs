namespace ChessBot.Uci;

using System.Diagnostics;
using ChessBot.Engine;
using ChessBot.Engine.Search;

/// <summary>
/// The engine's node-rate regression harness: a fixed set of positions searched to a fixed
/// depth, reporting the node total and the nodes per second.
///
/// Fixed DEPTH, not a fixed node count, and that is the whole design. A speed change must not
/// alter the tree it walks, so the measurement has to be able to say two things at once: that
/// the tree is the same, and that walking it got faster. At fixed depth the node total is a
/// fingerprint of the tree — it moves if and only if the search made a different decision
/// somewhere — while the elapsed time is free to move, which is exactly the separation a
/// speed-neutral classification asks for. A fixed-node run has the property backwards: the
/// node count is pinned by construction and so tells you nothing, and the depth reached
/// becomes the signal, which is far coarser (it is an integer, and it moves only on a change
/// of roughly a factor of two).
///
/// Every position starts from cleared tables, so the numbers do not depend on the order the
/// positions are run in or on anything the previous search learned. That costs a little
/// realism — a real game keeps its table between moves — and buys reproducibility, which is
/// the only property a regression harness actually needs.
///
/// The best move and score are printed per position as well. Two builds that claim to be
/// speed-neutral with respect to each other must agree on all three columns exactly; that is
/// a stronger statement than any match result, and it costs seconds rather than hours.
/// </summary>
public static class Bench
{
    /// <summary>
    /// Depth every position is searched to when "bench" is given no argument.
    ///
    /// Eleven, chosen by measuring the alternatives rather than by taste. What the harness has
    /// to be able to do is resolve a few per cent of node rate, so the question is how stable
    /// the reported figure is across whole processes. At depth 9 a pass is 575 ms and the
    /// individual positions fall to 3 ms, where millisecond timer resolution is itself several
    /// per cent. At depth 11 a pass is 2.0 s, and three separate processes reported 1,174,484 /
    /// 1,185,123 / 1,192,324 nodes per second — a spread of 0.8%, against a node total that
    /// was bit-identical in all three. Depth 13 would be better still and costs about eight
    /// times as much, which is past the point where this gets run between two edits.
    ///
    /// Deep enough also matters for coverage: the parts of the search that only appear with
    /// depth to spare — the LMR table's upper rows, aspiration re-searches, null-move at a
    /// reduction of three — are on the measured path here and are not at depth 6.
    /// </summary>
    public const int DefaultDepth = 11;

    /// <summary>Deepest depth "bench" will accept, so a typo cannot start an hour-long run.</summary>
    public const int MaxDepth = 20;

    /// <summary>
    /// Twelve positions: two openings, four middlegames of different character, and endgames.
    ///
    /// The first six are NodeCostProbe's set, unchanged and in its order, so that the two rigs
    /// mean the same thing by "the corpus" and a figure from one can be read next to a figure
    /// from the other. The six after them widen it where the probe is thin: the starting
    /// position, a tactical position with every piece still on (Kiwipete, which is also a perft
    /// position, so a generation bug shows up here as a changed node count rather than as a
    /// quiet wrong move), a symmetrical open middlegame, a queenside-attack middlegame, a
    /// king-and-pawn ending where the passed-pawn terms decide everything, and a rook ending
    /// that is all technique.
    ///
    /// Twelve rather than six because the node total is used as an equality test, and a change
    /// that quietly reorders one move somewhere shows up in the total of a wider set with much
    /// better odds. Every entry is legal by the board's own validation, which is stricter than
    /// FEN parsing — see PositionLegalityTests for the corpus entry that was not.
    /// </summary>
    public static readonly string[] Positions =
    {
        // NodeCostProbe's six, verbatim.
        "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
        "r1bq1rk1/pp1nbppp/2ppp3/8/2PPP3/2N1BN2/PP2BPPP/R2Q1RK1 w - - 0 10",
        "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
        "2rq1rk1/pp1bppbp/3p1np1/8/3NP3/1BN1BP2/PPPQ2PP/2KR3R w - - 0 12",
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "8/8/4k3/8/2p5/8/B2P2K1/8 w - - 0 1",

        // Widening the set.
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        "r1bqk2r/pp2bppp/2n1pn2/3p4/3P4/2NBPN2/PP3PPP/R1BQK2R w KQkq - 0 8",
        "r4rk1/pp2ppbp/2n3p1/q7/3PB3/2N1B3/PP3PPP/R2Q1RK1 w - - 0 14",
        "8/8/1p1r1k2/p1pPN1p1/P3KnP1/1P6/8/3R4 b - - 0 1",
        "6k1/5ppp/8/8/8/8/4PPPP/4R1K1 w - - 0 1",
    };

    /// <summary>
    /// How many times the set is searched, with the fastest pass reported.
    ///
    /// Best-of rather than mean, because the two things that perturb a pass on a developer
    /// machine — a background process taking the core, the GC choosing this moment — only ever
    /// make it slower. The fastest pass is the closest estimate of what the code costs when
    /// nothing is interfering; averaging mixes in a measurement of the interference.
    ///
    /// Three passes rather than one also makes the run self-checking: every pass must produce
    /// the same node total, and a differing total means the search is reading something that
    /// does not survive being run twice.
    /// </summary>
    public const int DefaultRepeats = 3;

    /// <summary>
    /// Searches every position to <paramref name="depth"/>, writing one line per position plus
    /// a summary. Returns the totals so a test can assert the run is reproducible without
    /// having to parse the output.
    /// </summary>
    /// <param name="output">Where the report is written.</param>
    /// <param name="depth">Plies per position; clamped to 1..<see cref="MaxDepth"/>.</param>
    /// <param name="hashSizeMb">Transposition table size for the run, in megabytes.</param>
    /// <param name="repeats">Timed passes over the set; the fastest one is reported.</param>
    public static BenchSummary Run(TextWriter output, int depth = DefaultDepth,
                                   int hashSizeMb = ChessEngine.DefaultHashSizeMb,
                                   int repeats = DefaultRepeats)
    {
        depth   = Math.Clamp(depth, 1, MaxDepth);
        repeats = Math.Clamp(repeats, 1, 20);

        // ── Warm-up ──────────────────────────────────────────────────────────
        // Untimed, discarded, and not optional. Tiered compilation means the first pass over
        // the set is measuring the JIT as much as the search: on the first run of this harness
        // position 1 took 230 ms for 51,671 nodes while position 3 took 57 ms for 58,322 — a
        // 4x apparent slowdown that was entirely compilation of code the later positions found
        // already compiled. A shallower depth is enough, because it is the same code; what is
        // needed is that every method has been through the JIT once, not that the tree has
        // been walked to its full height.
        RunPass(TextWriter.Null, new ChessEngine(hashSizeMb), Math.Max(1, depth - 3));

        var engine = new ChessEngine(hashSizeMb);

        var best = default(PassResult);
        for (int pass = 0; pass < repeats; pass++)
        {
            // Only the fastest pass is written out, so the report describes one coherent run
            // rather than three interleaved ones.
            var buffered = new StringWriter();
            var result   = RunPass(buffered, engine, depth);

            if (best.Report is null)
            {
                best = result with { Report = buffered.ToString() };
            }
            else
            {
                // The node total is the tree fingerprint, so two passes disagreeing about it
                // is not measurement noise — it is the search having depended on something
                // outside the position. Reported rather than swallowed: it invalidates every
                // comparison anyone would make with this run.
                if (result.Nodes != best.Nodes)
                {
                    output.WriteLine(
                        $"bench WARNING non-reproducible: pass {pass + 1} searched " +
                        $"{result.Nodes} nodes, pass 1 searched {best.Nodes}");
                }

                if (result.ElapsedMs < best.ElapsedMs)
                    best = result with { Report = buffered.ToString() };
            }
        }

        output.Write(best.Report);

        long nps = best.ElapsedMs > 0 ? (long)(best.Nodes / (best.ElapsedMs / 1000.0)) : 0;

        output.WriteLine("===========================");
        output.WriteLine($"Depth            : {depth}");
        output.WriteLine($"Positions        : {Positions.Length}");
        output.WriteLine($"Passes           : {repeats} (fastest reported)");
        output.WriteLine($"Total time (ms)  : {best.ElapsedMs}");
        output.WriteLine($"Nodes searched   : {best.Nodes}");
        output.WriteLine($"Nodes/second     : {nps}");

        return new BenchSummary(depth, Positions.Length, best.Nodes, best.ElapsedMs, nps);
    }

    /// <summary>
    /// One pass over the whole set on <paramref name="engine"/>, at fixed depth.
    /// </summary>
    private static PassResult RunPass(TextWriter output, ChessEngine engine, int depth)
    {
        long totalNodes = 0;
        long totalMs    = 0;

        for (int i = 0; i < Positions.Length; i++)
        {
            // Cleared tables per position. NewGame drops the transposition table and the
            // killer/history/counter-move tables, which is what makes the numbers below
            // independent of the positions searched before them — and what makes one pass
            // comparable to the next on the same engine instance.
            engine.NewGame();
            engine.LoadFen(Positions[i]);

            var settings = new SearchSettings
            {
                MaxDepth = depth,

                // Explicitly unbounded. A null MaxTimeMs is not "no limit" — the search reads
                // it as ten seconds — and a bench that silently stops short on the slowest
                // position of the set reports a node total that is a fingerprint of nothing.
                MaxTimeMs = UciTimeManager.NoTimeLimitMs,
            };

            var clock  = Stopwatch.StartNew();
            var result = engine.FindBestMove(settings);
            clock.Stop();

            totalNodes += result.NodesSearched;
            totalMs    += clock.ElapsedMilliseconds;

            output.WriteLine(
                $"bench position {i + 1,2}/{Positions.Length} " +
                $"depth {result.DepthAchieved,2} " +
                $"nodes {result.NodesSearched,10} " +
                $"time {clock.ElapsedMilliseconds,6} " +
                $"bestmove {result.BestMove,-6} " +
                $"score {UciSession.FormatScore(result.Evaluation)}");
        }

        return new PassResult(totalNodes, totalMs, null);
    }

    /// <summary>One timed pass: its node total, its wall time, and the report it produced.</summary>
    private readonly record struct PassResult(long Nodes, long ElapsedMs, string? Report);
}

/// <summary>
/// The totals of one bench run. <paramref name="Nodes"/> is the tree fingerprint — reproducible
/// across runs of the same binary, and different only when the search made a different
/// decision — while <paramref name="ElapsedMs"/> and <paramref name="Nps"/> are the timing,
/// which is not reproducible and is not meant to be.
/// </summary>
/// <param name="Depth">Plies each position was searched to.</param>
/// <param name="Positions">How many positions were searched.</param>
/// <param name="Nodes">Total nodes (main + quiescence) over the whole set.</param>
/// <param name="ElapsedMs">Total wall time over the whole set.</param>
/// <param name="Nps">Nodes per second over the whole set.</param>
public readonly record struct BenchSummary(int Depth, int Positions, long Nodes, long ElapsedMs, long Nps);
