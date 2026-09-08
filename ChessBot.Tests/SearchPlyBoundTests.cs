using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Hashing;
using ChessBot.Engine.Search;

namespace ChessBot.Tests;

/// <summary>
/// The search stack is a set of fixed-size, ply-indexed arrays (PV table, PV lengths, per-ply
/// move buffers, last-move and null-move flags), so a node has to establish that its ply is
/// inside the bound *before* it touches any of them. A guard that sits below the first indexed
/// write is not a guard — the write throws first, and the deepest ply is exactly where the
/// engine is under time pressure in a long game.
///
/// Reaching the bound through a real search takes tens of seconds of check-extension chaining
/// (the extension keeps depth constant across a check/evasion pair, so ply grows at roughly
/// twice the nominal depth), which is far too slow and too machine-dependent for a regression
/// test. The two entry points are therefore called directly at the boundary ply: that is the
/// exact condition the guard exists for, and it costs microseconds.
/// </summary>
public class SearchPlyBoundTests
{
    private const int Infinity = SearchScores.Mate + 1;

    // A position with no forced tactics, so the node under test does real work rather than
    // returning from a terminal condition.
    private const string QuietPosition = "r1bq1rk1/pp2bppp/2n1pn2/3p4/3P4/2NBPN2/PPQ2PPP/R1B2RK1 w - - 0 1";

    /// <summary>
    /// Builds a searcher whose per-search state (settings, timer, cancellation token, counters)
    /// has been initialised, which is what <see cref="Searcher.Search"/> does before it enters
    /// the recursion. Calling a node function on a fresh instance would fail for that reason
    /// rather than the one under test.
    /// </summary>
    private static Searcher WarmedSearcher(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);

        var searcher = new Searcher(engine.GetBoardSnapshot(), new Evaluator(), new ZobristHasher());
        searcher.Search(new SearchSettings { MaxDepth = 3 });
        return searcher;
    }

    [Fact]
    public void NegamaxSearch_AtTheDeepestPly_ReturnsAScoreInsteadOfThrowing()
    {
        var searcher = WarmedSearcher(QuietPosition);

        int score = searcher.NegamaxSearch(Searcher.MAX_PLY, depth: 4, -Infinity, Infinity);

        Assert.InRange(score, -SearchScores.Mate, SearchScores.Mate);
    }

    [Fact]
    public void QuiescenceSearch_AtTheDeepestPly_ReturnsAScoreInsteadOfThrowing()
    {
        var searcher = WarmedSearcher(QuietPosition);

        int score = searcher.QuiescenceSearch(Searcher.MAX_PLY, -Infinity, Infinity);

        Assert.InRange(score, -SearchScores.Mate, SearchScores.Mate);
    }

    /// <summary>
    /// The mate band and the search stack are independent quantities, but they are not
    /// unrelated: a mate found at the deepest reachable ply scores <c>Mate - ply</c>, so the
    /// band has to be at least as wide as the stack or that score decodes as a centipawn
    /// evaluation instead of a mate. This is the invariant that used to be enforced by
    /// aliasing the two together.
    /// </summary>
    [Fact]
    public void MateBand_IsWideEnoughForEveryReachablePly()
    {
        Assert.True(SearchScores.MateDistanceLimit >= Searcher.MAX_PLY,
            $"Mate band {SearchScores.MateDistanceLimit} is narrower than the search stack {Searcher.MAX_PLY}: " +
            "a mate found at the deepest ply would be reported as a centipawn score.");
    }

    /// <summary>
    /// The behavioural counterpart: a check-heavy endgame where the uncapped check extension
    /// drives the selective depth far past the nominal depth. The node budget (rather than a
    /// clock) keeps the run reproducible across machines.
    /// </summary>
    [Fact]
    public void DeepCheckSequence_ReturnsAScoreAndStaysInsideThePlyBound()
    {
        var engine = new ChessEngine();
        engine.LoadFen("rr6/8/8/3k4/8/8/8/Q6K w - - 0 1");

        var result = engine.FindBestMove(new SearchSettings
        {
            MaxNodes  = 2_000_000,
            MaxDepth  = 100,
            MaxTimeMs = 120_000,
        });

        Assert.NotEqual(default, result.BestMove);
        Assert.InRange(result.SelDepth, 30, Searcher.MAX_PLY - 1);
    }
}
