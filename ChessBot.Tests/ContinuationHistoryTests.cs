namespace ChessBot.Tests;

using ChessBot.Engine.Board;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Hashing;
using ChessBot.Engine.Search;
using ChessBot.Engine.Types;
using Xunit;

/// <summary>
/// Continuation history records a quiet move's cutoff record conditioned on the move played
/// immediately before it. The plain history table pools that record across every position the
/// move was ever tried in, so it can say "Nf3 cuts" and cannot say "Nf3 cuts against ...e5".
///
/// Every test here exists to keep the two apart. A continuation table that is written but never
/// separates two moves the plain table already ranks equally is indistinguishable from no table
/// at all, and an A/B match reports that as "inconclusive" rather than as "it never ran".
/// </summary>
public class ContinuationHistoryTests
{
    // After 1.e4 e5. The previous move under test is e7e5, whose pawn stands on e5 — which is
    // where the table reads it from, a move having already been made by the time it is context.
    private const string AfterE4E5 =
        "rnbqkbnr/pppp1ppp/8/4p3/4P3/8/PPPP1PPP/RNBQKBNR w KQkq e6 0 2";

    private static Board Posed(string fen)
    {
        var board = new Board();
        board.LoadFromFen(fen);
        return board;
    }

    private static Move Quiet(string from, string to) =>
        new(Square.FromAlgebraic(from), Square.FromAlgebraic(to));

    /// <summary>
    /// Two quiet moves given identical plain history, one of them credited under a previous move.
    /// The plain table cannot tell them apart; the continuation table can, and only when that
    /// previous move is the one actually on the board.
    /// </summary>
    [Fact]
    public void ThePreviousMoveDecidesBetweenTwoMovesTheHistoryTableRanksEqually()
    {
        var board    = Posed(AfterE4E5);
        var ordering = new MoveOrdering(board);
        var credited = Quiet("g1", "f3");
        var plain    = Quiet("b1", "c3");
        var previous = Quiet("e7", "e5");

        for (int i = 0; i < 20; i++)
        {
            ordering.RecordHistoryMove(credited, previous, 10);
            ordering.RecordHistoryMove(plain, default, 10);
        }

        Assert.Equal(ordering.HistoryScore(plain), ordering.HistoryScore(credited));
        Assert.True(ordering.ContinuationScore(credited, previous) > 0,
                    "nothing was written into the continuation table");
        Assert.Equal(0, ordering.ContinuationScore(plain, previous));

        // With that previous move on the board the credited move wins on continuation alone.
        var withContext = new[] { plain, credited };
        ordering.OrderMoves(withContext, 2, default, previous, 0);
        Assert.Equal(credited, withContext[0]);

        // Without it the two are tied again, and the stable sort returns them as given.
        var without = new[] { plain, credited };
        ordering.OrderMoves(without, 2, default, default, 0);
        Assert.Equal(plain, without[0]);
    }

    /// <summary>
    /// A different previous move is a different slot. Without this the table would be an
    /// expensive second copy of the plain history it is supposed to condition.
    /// </summary>
    [Fact]
    public void ADifferentPreviousMoveReadsADifferentSlot()
    {
        var board    = Posed("rnbqkbnr/pppp1ppp/8/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R b KQkq - 1 2");
        var ordering = new MoveOrdering(board);
        var reply    = Quiet("b8", "c6");

        for (int i = 0; i < 20; i++)
            ordering.RecordHistoryMove(reply, Quiet("g1", "f3"), 10);

        Assert.True(ordering.ContinuationScore(reply, Quiet("g1", "f3")) > 0);
        Assert.Equal(0, ordering.ContinuationScore(reply, Quiet("e2", "e4")));
    }

    /// <summary>
    /// The context move is read from its destination square, so a "previous move" whose
    /// destination is empty is not a move that was made in this line. That is what a null-move
    /// child used to find at its parent's slot, and indexing a piece table by an empty square is
    /// not a wrong answer but an out-of-range one.
    /// </summary>
    [Fact]
    public void APreviousMoveThatLeftNothingBehindIsNoContextAtAll()
    {
        var board    = Posed(AfterE4E5);
        var ordering = new MoveOrdering(board);
        var move     = Quiet("g1", "f3");
        var stale    = Quiet("a3", "a4");   // nothing stands on a4 in this position

        for (int i = 0; i < 20; i++) ordering.RecordHistoryMove(move, stale, 10);

        Assert.Equal(0, ordering.ContinuationScore(move, stale));

        var buffer = new[] { Quiet("b1", "c3"), move };
        ordering.OrderMoves(buffer, 2, default, stale, 0);   // must not throw
        Assert.Equal(move, buffer[0]);                       // on plain history alone
    }

    /// <summary>
    /// Aged with the plain table and for the same reason: the next search is two plies away, so
    /// most of what this one learned still holds, but only most.
    /// </summary>
    [Fact]
    public void NewSearchHalvesContinuationHistoryToo()
    {
        var board    = Posed(AfterE4E5);
        var ordering = new MoveOrdering(board);
        var move     = Quiet("g1", "f3");
        var previous = Quiet("e7", "e5");

        for (int i = 0; i < 20; i++) ordering.RecordHistoryMove(move, previous, 10);
        int before = ordering.ContinuationScore(move, previous);

        ordering.NewSearch();

        Assert.True(before > 1, "the fixture built up nothing to age");
        Assert.Equal(before / 2, ordering.ContinuationScore(move, previous));
    }

    /// <summary>
    /// The engine has to actually reach the table in a real search. A term that is written but
    /// never read costs a measurement the ability to say anything at all.
    /// </summary>
    [Fact]
    public void ARealSearchWritesContinuationHistory()
    {
        var board = Posed(AfterE4E5);
        var searcher = new Searcher(board, new Evaluator(), new ZobristHasher());

        var result = searcher.Search(new SearchSettings { MaxDepth = 8, MaxTimeMs = 30_000 });

        Assert.NotEqual(default, result.BestMove);
        Assert.True(searcher.Ordering.ContinuationEntriesUsed() > 0,
                    "a depth-8 search produced no continuation history at all");
    }
}
