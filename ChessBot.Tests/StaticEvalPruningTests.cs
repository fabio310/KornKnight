namespace ChessBot.Tests;

using ChessBot.Engine;
using ChessBot.Engine.Search;
using Xunit;

/// <summary>
/// The pruning rules that bet on a static evaluation instead of a search: reverse futility
/// (static null move), late move pruning, forward futility and razoring.
///
/// Every test here exists to prove a rule actually fires. A pruning rule that is silently never
/// reached costs nothing, breaks nothing, and measures as "inconclusive" — which is the one
/// failure an A/B match cannot tell apart from "the idea does not work".
/// </summary>
public class StaticEvalPruningTests
{
    // An ordinary middlegame, materially level. Deliberately not a lopsided position: the rules
    // under test are supposed to fire in normal play, and a test that needs a queen-up position
    // to reach them would hide a rule that never triggers in a real game.
    private const string Middlegame = "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10";

    // Quiet, closed and wide: many legal quiet moves at every node, which is the shape late move
    // pruning needs in order to have a tail to cut.
    private const string QuietAndWide = "r1bq1rk1/pp1nbppp/2ppp3/8/2PPP3/2N1BN2/PP2BPPP/R2Q1RK1 w - - 0 10";

    /// <summary>
    /// Nodes above beta by more than the margin must produce static-null-move cutoffs. Without
    /// this the rule could be dead code and the A/B match would be comparing a build with its
    /// own baseline.
    /// </summary>
    [Fact]
    public void ReverseFutilityPruningFires()
    {
        var engine = new ChessEngine();
        engine.LoadFen(Middlegame);

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 8, MaxTimeMs = 20_000 });

        Assert.True(result.ReverseFutilityCutoffs > 0,
                    "reverse futility pruning never fired in an ordinary middlegame at depth 8");
    }

    /// <summary>
    /// It is unsound — a static score standing in for a search — so the minimax equivalence gate
    /// has to be able to switch it off. That gate uses <see cref="SearchSettings.PlainAlphaBeta"/>,
    /// which turns off <see cref="SearchSettings.UseFutility"/>; if the rule did not honour the
    /// same switch the gate would silently stop proving anything.
    /// </summary>
    [Fact]
    public void ReverseFutilityPruningIsOffForThePlainAlphaBetaGate()
    {
        var engine = new ChessEngine();
        engine.LoadFen(Middlegame);

        var result = engine.FindBestMove(SearchSettings.PlainAlphaBeta(5));

        Assert.Equal(0L, result.ReverseFutilityCutoffs);
    }

    /// <summary>
    /// No node in check may ask for a static evaluation: the score is meaningless there and every
    /// consumer guards on it.
    ///
    /// Counted rather than asserted indirectly. With quiescence, the check extension and the
    /// transposition table all off, a depth-1 search from a position that is already in check
    /// visits exactly one node in check (the root, which must evaluate nothing) and one leaf per
    /// legal evasion (each of which must evaluate exactly once). So the call count equals the
    /// number of legal moves — it was that plus one before the root's call was removed.
    /// </summary>
    [Fact]
    public void NoStaticEvaluationIsAskedForAtANodeInCheck()
    {
        var engine = new ChessEngine();
        engine.LoadFen("4k3/8/8/8/8/8/8/r3K3 w - - 0 1");   // Ra1 checks along the first rank

        int evasions = engine.GetLegalMoves().Count;
        Assert.True(evasions > 0, "position is not a legal in-check position with evasions");

        var result = engine.FindBestMove(SearchSettings.PlainAlphaBeta(1));

        Assert.Equal((long)evasions, result.EvaluationCalls);
    }

    /// <summary>
    /// Late move pruning has to reach the tail of a wide quiet node. The threshold starts at four
    /// quiet moves at depth 1, so a closed position with thirty-odd legal moves per side must
    /// produce prunes in quantity; none at all would mean the threshold is never crossed and the
    /// rule is decoration.
    /// </summary>
    [Fact]
    public void LateMovePruningFires()
    {
        var engine = new ChessEngine();
        engine.LoadFen(QuietAndWide);

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 9, MaxTimeMs = 20_000 });

        Assert.True(result.LateMovePrunes > 0,
                    "late move pruning never fired in a closed position with a wide quiet tail");
    }

    /// <summary>
    /// Unsound for the same reason as the rest of the family — it drops moves on their position in
    /// the move list — so the minimax equivalence gate must be able to switch it off.
    /// </summary>
    [Fact]
    public void LateMovePruningIsOffForThePlainAlphaBetaGate()
    {
        var engine = new ChessEngine();
        engine.LoadFen(QuietAndWide);

        var result = engine.FindBestMove(SearchSettings.PlainAlphaBeta(5));

        Assert.Equal(0L, result.LateMovePrunes);
    }
}
