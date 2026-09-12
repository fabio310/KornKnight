namespace ChessBot.Tests;

using ChessBot.Engine;
using ChessBot.Engine.Board;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Hashing;
using ChessBot.Engine.Search;
using ChessBot.Engine.Types;
using Xunit;

/// <summary>
/// A promotion is worth roughly eight pawns and a capture-promotion more than a queen, so both
/// belong with the winning captures at the front of the move list and both have to survive
/// quiescence's delta margin. Scoring them by the victim alone — or by a flat promotion bonus
/// well below the capture band — buries the one resource that saves a lost position with a pawn
/// on the seventh.
/// </summary>
public class PromotionOrderingTests
{
    // White is lost on material and has one pawn on b7 against a rook on a8. bxa8=Q wins the
    // rook and promotes; b8=Q merely walks into Rxb8.
    private const string PasserOnTheSeventh = "r6k/1P6/8/8/8/8/3q4/6K1 w - - 0 1";

    private static (Board board, MoveOrdering ordering, Move[] moves, int count) Ordered(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);

        var board    = engine.GetBoardSnapshot();
        var ordering = new MoveOrdering(board);
        var gen      = new MoveGenerator(board);

        var moves = new Move[MoveGenerator.MaxMoves];
        gen.GenerateLegalMovesInto(moves, out int count);
        ordering.OrderMoves(moves, count, default, default, 0);

        return (board, ordering, moves, count);
    }

    private static int IndexOf(Move[] moves, int count, string from, string to, PieceType promotion)
    {
        for (int i = 0; i < count; i++)
            if (moves[i].From == Square.FromAlgebraic(from) &&
                moves[i].To   == Square.FromAlgebraic(to)   &&
                moves[i].PromotionType == promotion)
                return i;
        return -1;
    }

    // ── (a) a queen promotion is not a below-the-captures move ───────────────

    /// <summary>
    /// A queen promotion is an ~800 centipawn swing. Scored as a flat 304,000 it sat below the
    /// capture band's floor of 500,000, so every capture — including one that hangs a queen —
    /// was searched ahead of it.
    /// </summary>
    [Fact]
    public void QueenPromotion_IsOrderedAheadOfALosingCapture()
    {
        // White can promote on f8, or take the a7 pawn with the queen — where the king recaptures.
        var (_, _, moves, count) = Ordered("1k4n1/p4P2/8/8/8/8/8/Q5K1 w - - 0 1");

        int promotion     = IndexOf(moves, count, "f7", "f8", PieceType.Queen);
        int losingCapture = IndexOf(moves, count, "a1", "a7", PieceType.None);

        Assert.True(promotion >= 0, "f7f8=Q was not generated");
        Assert.True(losingCapture >= 0, "Qxa7+ was not generated");
        Assert.True(promotion < losingCapture,
            $"f8=Q (+800) must precede Qxa7+ (SEE -800); got promotion at {promotion}, capture at {losingCapture}");
    }

    // ── (b) a capture-promotion is not merely a capture ──────────────────────

    /// <summary>
    /// The capture branch returned before the promotion branch could run, so exd8=Q was scored as
    /// a capture of whatever stood on d8 and the promotion never entered the score at all. Here
    /// the knight on d8 is a smaller victim than the rook on h4, so MVV-LVA alone puts the plain
    /// capture first even though the promotion is worth three times as much.
    /// </summary>
    [Fact]
    public void CapturePromotion_IsOrderedAheadOfABiggerPlainVictim()
    {
        var (_, _, moves, count) = Ordered("3n3k/4P3/8/8/7r/8/8/4K2R w - - 0 1");

        int capturePromotion = IndexOf(moves, count, "e7", "d8", PieceType.Queen);
        int plainCapture     = IndexOf(moves, count, "h1", "h4", PieceType.None);

        Assert.True(capturePromotion >= 0, "e7d8=Q was not generated");
        Assert.True(plainCapture >= 0, "Rxh4 was not generated");
        Assert.True(capturePromotion < plainCapture,
            $"exd8=Q (+1120) must precede Rxh4 (+500); got promotion at {capturePromotion}, capture at {plainCapture}");
    }

    [Fact]
    public void TheSavingCapturePromotion_IsOrderedInTheFirstFewMoves()
    {
        var (_, _, moves, count) = Ordered(PasserOnTheSeventh);

        int index = IndexOf(moves, count, "b7", "a8", PieceType.Queen);

        Assert.True(index >= 0, "b7a8=Q was not generated");
        Assert.InRange(index, 0, 2);
    }

    // ── (c) delta pruning charges the promotion at its real value ────────────

    /// <summary>
    /// Delta pruning valued exd8=Q at the victim alone, so a capture-promotion worth 1,300
    /// centipawns was discarded whenever stand-pat plus the victim plus the margin fell short of
    /// alpha — which is precisely the lost position the promotion exists to rescue.
    ///
    /// The window below is chosen against the real stand-pat: the victim alone (500) plus the
    /// 200 margin falls under alpha, the full swing (1,300) plus the margin clears it. Pruned,
    /// quiescence returns alpha untouched, because the only other tactical moves are the b8
    /// promotions that walk into Rxb8.
    ///
    /// The offset is 800 rather than 1,000 because alpha has to sit below what the promotion is
    /// actually worth, not below what delta pruning charges it. bxa8=Q trades a pawn for the rook
    /// and yields a queen, which leaves K+Q against K+Q — level material, about 970 centipawns
    /// above a stand-pat of roughly -1,006. An alpha of stand-pat + 1,000 lands *above* that, so
    /// quiescence fails low for a reason that has nothing to do with pruning, and the test would
    /// pass or fail on a few centipawns of evaluation drift.
    /// </summary>
    [Fact]
    public void DeltaPruning_DoesNotDiscardACapturePromotion()
    {
        var engine = new ChessEngine();
        engine.LoadFen(PasserOnTheSeventh);

        var board    = engine.GetBoardSnapshot();
        var settings = new SearchSettings { MaxDepth = 3 };

        int standPat = new Evaluator().EvaluateFast(board, settings.UseThreatEval);

        var searcher = new Searcher(board, new Evaluator(), new ZobristHasher());
        searcher.Search(settings);   // initialises the per-search state the node functions need

        int alpha = standPat + 800;
        int score = searcher.QuiescenceSearch(0, alpha, alpha + 5000);

        Assert.True(score > alpha,
            $"quiescence returned alpha ({alpha}) unchanged from a stand-pat of {standPat}: " +
            "the capture-promotion was pruned away");
    }
}
