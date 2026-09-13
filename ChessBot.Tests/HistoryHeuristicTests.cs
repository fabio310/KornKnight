namespace ChessBot.Tests;

using ChessBot.Engine;
using ChessBot.Engine.Board;
using ChessBot.Engine.Search;
using ChessBot.Engine.Types;
using Xunit;

/// <summary>
/// The history heuristic exists to rank quiet moves against each other. It can only do that
/// while its entries stay distinguishable: an accumulator that grows without bound and is read
/// through a clamp stops discriminating as soon as the clamp is reached, and from then on every
/// move in the saturated part of the table scores identically — which is the same as having no
/// history at all, except it also costs a table lookup.
/// </summary>
public class HistoryHeuristicTests
{
    private static MoveOrdering NewOrdering()
    {
        var engine = new ChessEngine();
        return new MoveOrdering(engine.GetBoardSnapshot());
    }

    private static Move Quiet(string from, string to) =>
        new(Square.FromAlgebraic(from), Square.FromAlgebraic(to));

    /// <summary>
    /// Orders exactly the two moves given, in the order given. The sort is a stable insertion
    /// sort, so two moves that score the same come back in the order they went in — which makes
    /// "did these score differently at all?" directly observable.
    /// </summary>
    private static Move First(MoveOrdering ordering, Move lower, Move higher)
    {
        var buffer = new[] { lower, higher };
        ordering.OrderMoves(buffer, 2, default, default, 0);
        return buffer[0];
    }

    [Fact]
    public void RepeatedCutoffsAtHighDepth_StillRankAgainstEachOther()
    {
        var ordering = NewOrdering();
        var often    = Quiet("b1", "c3");
        var seldom   = Quiet("g1", "f3");

        // At depth 12 the old update added 144 a time, so about seventy hits pinned a slot at the
        // 10,000 clamp. Both of these are far past that; only a bounded update keeps them apart.
        for (int i = 0; i < 400; i++) ordering.RecordHistoryMove(often, 12);
        for (int i = 0; i < 100; i++) ordering.RecordHistoryMove(seldom, 12);

        Assert.Equal(often, First(ordering, lower: seldom, higher: often));
    }

    [Fact]
    public void AQuietMoveThatFailedToCut_SortsBelowAnUntriedOne()
    {
        var ordering = NewOrdering();
        var failed   = Quiet("b1", "c3");
        var untried  = Quiet("g1", "f3");

        for (int i = 0; i < 20; i++) ordering.RecordHistoryFailure(failed, 8);

        Assert.Equal(untried, First(ordering, lower: failed, higher: untried));
    }

    [Fact]
    public void HistoryStaysInsideItsCeiling_HoweverManyTimesItIsRewarded()
    {
        var ordering = NewOrdering();
        var move     = Quiet("b1", "c3");

        for (int i = 0; i < 100_000; i++) ordering.RecordHistoryMove(move, 40);

        Assert.InRange(ordering.HistoryScore(move), 0, MoveOrdering.HistoryMax);
    }

    // ── What survives the move-to-move boundary ──────────────────────────────

    [Fact]
    public void NewSearch_HalvesHistoryRatherThanDiscardingIt()
    {
        var ordering = NewOrdering();
        var move     = Quiet("b1", "c3");

        for (int i = 0; i < 50; i++) ordering.RecordHistoryMove(move, 10);
        int before = ordering.HistoryScore(move);

        ordering.NewSearch();

        Assert.True(before > 1, "the fixture did not build up any history to age");
        Assert.Equal(before / 2, ordering.HistoryScore(move));
    }

    /// <summary>
    /// The per-move setup used to call Clear(), which threw away everything the previous move's
    /// search had learned about a position one ply removed from this one.
    /// </summary>
    [Fact]
    public void NewSearch_KeepsKillersAndCounterMoves()
    {
        var ordering = NewOrdering();
        var killer   = Quiet("b1", "c3");
        var counter  = Quiet("g1", "f3");
        var opponent = Quiet("e7", "e5");
        var ordinary = Quiet("d2", "d3");

        ordering.RecordKillerMove(killer, 0);
        ordering.RecordCounterMove(opponent, counter);

        ordering.NewSearch();

        Assert.Equal(killer, First(ordering, lower: ordinary, higher: killer));

        var buffer = new[] { ordinary, counter };
        ordering.OrderMoves(buffer, 2, default, opponent, 0);
        Assert.Equal(counter, buffer[0]);
    }

    [Fact]
    public void Clear_DiscardsEverything()
    {
        var ordering = NewOrdering();
        var killer   = Quiet("b1", "c3");
        var ordinary = Quiet("d2", "d3");

        ordering.RecordKillerMove(killer, 0);
        for (int i = 0; i < 50; i++) ordering.RecordHistoryMove(killer, 10);

        ordering.Clear();

        Assert.Equal(0, ordering.HistoryScore(killer));
        Assert.Equal(ordinary, First(ordering, lower: ordinary, higher: killer));
    }
}
