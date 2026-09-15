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

    /// <summary>
    /// A board the test owns, so it can be re-posed under a <see cref="MoveOrdering"/> that keeps
    /// its tables. Re-posing is the only way to exhibit the collisions the table used to have:
    /// they are between things that stand on one square at different points in a search tree, not
    /// between two moves of one position.
    /// </summary>
    private static Board Posed(string fen)
    {
        var board = new Board();
        board.LoadFromFen(fen);
        return board;
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
        for (int i = 0; i < 400; i++) ordering.RecordHistoryMove(often, default, 12);
        for (int i = 0; i < 100; i++) ordering.RecordHistoryMove(seldom, default, 12);

        Assert.Equal(often, First(ordering, lower: seldom, higher: often));
    }

    [Fact]
    public void AQuietMoveThatFailedToCut_SortsBelowAnUntriedOne()
    {
        var ordering = NewOrdering();
        var failed   = Quiet("b1", "c3");
        var untried  = Quiet("g1", "f3");

        for (int i = 0; i < 20; i++) ordering.RecordHistoryFailure(failed, default, 8);

        Assert.Equal(untried, First(ordering, lower: failed, higher: untried));
    }

    [Fact]
    public void HistoryStaysInsideItsCeiling_HoweverManyTimesItIsRewarded()
    {
        var ordering = NewOrdering();
        var move     = Quiet("b1", "c3");

        for (int i = 0; i < 100_000; i++) ordering.RecordHistoryMove(move, default, 40);

        Assert.InRange(ordering.HistoryScore(move), 0, MoveOrdering.HistoryMax);
    }

    // ── What the table is keyed by ───────────────────────────────────────────

    /// <summary>
    /// A rook that cut on d1-d4 says nothing about a queen that later stands on d1 and plays the
    /// same squares. On a [from, to] table it said everything: one slot, two pieces, and whichever
    /// wrote last decided how the other was ordered.
    /// </summary>
    [Fact]
    public void AQueenDoesNotInheritTheRooksRecordOnTheSameSquares()
    {
        var board    = Posed("7k/8/8/8/8/8/8/3R2K1 w - - 0 1");
        var ordering = new MoveOrdering(board);
        var d1d4     = Quiet("d1", "d4");

        for (int i = 0; i < 50; i++) ordering.RecordHistoryMove(d1d4, default, 10);
        Assert.True(ordering.HistoryScore(d1d4) > 0, "the rook built up no history to inherit");

        board.LoadFromFen("7k/8/8/8/8/8/8/3Q2K1 w - - 0 1");

        Assert.Equal(0, ordering.HistoryScore(d1d4));
    }

    /// <summary>
    /// And neither does the other side. Both colours wrote into the same 4,096 slots, so a cutoff
    /// White had learned was read as evidence by Black.
    /// </summary>
    [Fact]
    public void BlackDoesNotInheritWhitesRecordOnTheSameSquares()
    {
        var board    = Posed("7k/8/8/8/8/8/8/3R2K1 w - - 0 1");
        var ordering = new MoveOrdering(board);
        var d1d4     = Quiet("d1", "d4");

        for (int i = 0; i < 50; i++) ordering.RecordHistoryMove(d1d4, default, 10);
        Assert.True(ordering.HistoryScore(d1d4) > 0, "White built up no history to inherit");

        board.LoadFromFen("6k1/8/7K/8/8/8/8/3r4 b - - 0 1");

        Assert.Equal(0, ordering.HistoryScore(d1d4));
    }

    /// <summary>
    /// The counter-move table had the history table's defect in a table consulted far less often:
    /// one slot per (from, to) of the opponent's last move, shared by both sides. A counter White
    /// learned was then offered to Black at 150,000 — a band above every quiet move — as its own
    /// reply to the same squares.
    /// </summary>
    [Fact]
    public void BlackIsNotOfferedTheCounterWhiteLearned()
    {
        const string Start = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

        var board    = Posed(Start);
        var ordering = new MoveOrdering(board);
        var counter  = Quiet("g1", "f3");
        var ordinary = Quiet("b1", "c3");
        var opponent = Quiet("e7", "e5");

        ordering.RecordCounterMove(opponent, counter);

        var white = new[] { ordinary, counter };
        ordering.OrderMoves(white, 2, default, opponent, 0);
        Assert.Equal(counter, white[0]);

        board.LoadFromFen(Start.Replace(" w ", " b "));

        var black = new[] { ordinary, counter };
        ordering.OrderMoves(black, 2, default, opponent, 0);
        Assert.Equal(ordinary, black[0]);
    }

    // ── What survives the move-to-move boundary ──────────────────────────────

    [Fact]
    public void NewSearch_HalvesHistoryRatherThanDiscardingIt()
    {
        var ordering = NewOrdering();
        var move     = Quiet("b1", "c3");

        for (int i = 0; i < 50; i++) ordering.RecordHistoryMove(move, default, 10);
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
        for (int i = 0; i < 50; i++) ordering.RecordHistoryMove(killer, default, 10);

        ordering.Clear();

        Assert.Equal(0, ordering.HistoryScore(killer));
        Assert.Equal(ordinary, First(ordering, lower: ordinary, higher: killer));
    }
}
