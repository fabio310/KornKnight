using System.Linq;
using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Types;

namespace ChessBot.Tests;

/// <summary>
/// The two evaluation terms that read counts rather than squares: the phase-scaled doubled and
/// isolated pawn penalties, and the bishop pair.
///
/// Both are tested through whole positions, because both are about what the position contains
/// rather than about a geometry a bitboard can state on its own — and because the bishop pair's
/// counter is maintained incrementally, which is a property only a real make/unmake can exercise.
///
/// Every position pair here is built so that the only thing that differs is the term under test:
/// the pawn skeletons are chosen to leave no passed pawn on either side, and material and
/// piece-square values are subtracted out.
/// </summary>
public class PawnStructureAndBishopPairTests
{
    /// <summary>
    /// White-positive evaluation of a FEN. Every position here has White to move and the evaluator
    /// returns a side-to-move relative score, so no sign correction is needed.
    /// </summary>
    private static int Eval(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        return new Evaluator().Evaluate(engine.GetBoardSnapshot());
    }

    /// <summary>
    /// The difference two positions make once material and piece-square tables are subtracted out,
    /// which is what leaves the term under test on its own.
    /// </summary>
    private static int NonTableDelta(string from, string to)
        => (Eval(to) - Eval(from)) - (TableScoreOf(to) - TableScoreOf(from));

    private static int TableScoreOf(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        var board = engine.GetBoardSnapshot();

        return PieceSquareTables.Interpolate(
            board.IncrementalMaterialScore + board.IncrementalPstScore,
            board.IncrementalEndgameMaterialScore + board.IncrementalEndgamePstScore,
            board.IncrementalPhase);
    }

    // ── Doubled and isolated pawns scale with the phase ──────────────────────

    // The same eight White pawns in every position: eight abreast on the second rank, or the same
    // eight with the e-pawn standing on c3 instead, which doubles the c-file and leaves nothing
    // else about the structure changed. Black keeps seven pawns with the c-file empty, which
    // blocks every White pawn from being passed in both positions and is itself unchanged.
    // One pair has the full starting array behind it (phase 24), the other only kings (phase 0).
    private const string MidgameCleanFen   = "rnbqkbnr/pp1ppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const string MidgameDoubledFen = "rnbqkbnr/pp1ppppp/8/8/8/2P5/PPPP1PPP/RNBQKBNR w KQkq - 0 1";
    private const string EndgameCleanFen   = "4k3/pp1ppppp/8/8/8/8/PPPPPPPP/4K3 w - - 0 1";
    private const string EndgameDoubledFen = "4k3/pp1ppppp/8/8/8/2P5/PPPP1PPP/4K3 w - - 0 1";

    [Fact]
    public void DoubledPawnsCostMoreInAnEndgameThanInAMidgame()
    {
        int midgame = NonTableDelta(MidgameCleanFen, MidgameDoubledFen);
        int endgame = NonTableDelta(EndgameCleanFen, EndgameDoubledFen);

        Assert.True(midgame < 0, $"doubling a pawn should cost something in the midgame, got {midgame}");
        Assert.True(endgame < midgame,
            $"doubled pawns should cost more once the pieces are gone: {midgame} then {endgame}");
    }

    [Fact]
    public void IsolatedPawnsCostMoreInAnEndgameThanInAMidgame()
    {
        // A lone White d-pawn with no neighbour on c or e, against the same pawn given one. Black
        // holds c7/d7/e7 throughout, so nothing on either side is passed and Black's own structure
        // is identical in all four. Adding the neighbour lifts the isolation penalty from both
        // White pawns, so the delta is positive and its size is what scales with the phase.
        const string MidgameIsolatedFen  = "3qk3/2ppp3/8/8/8/2n2n2/3P4/3QK3 w - - 0 1";
        const string MidgameSupportedFen = "3qk3/2ppp3/8/8/8/2n2n2/2PP4/3QK3 w - - 0 1";
        const string EndgameIsolatedFen  = "4k3/2ppp3/8/8/8/8/3P4/4K3 w - - 0 1";
        const string EndgameSupportedFen = "4k3/2ppp3/8/8/8/8/2PP4/4K3 w - - 0 1";

        int midgame = NonTableDelta(MidgameIsolatedFen, MidgameSupportedFen);
        int endgame = NonTableDelta(EndgameIsolatedFen, EndgameSupportedFen);

        Assert.True(midgame > 0, $"isolation should cost something in the midgame, got {midgame}");
        Assert.True(endgame > midgame,
            $"isolation should cost more once the pieces are gone: {midgame} then {endgame}");
    }

    // ── The bishop pair ──────────────────────────────────────────────────────

    // White's second minor piece is a bishop in one position of each pair and a knight in the
    // other; everything else is identical, so once material and piece-square values are subtracted
    // out what remains is the pair bonus alone.
    private const string MidgamePairFen   = "r2qk2r/8/8/8/8/2n2n2/1B2B3/R2QK2R w KQkq - 0 1";
    private const string MidgameSingleFen = "r2qk2r/8/8/8/8/2n2n2/1B2N3/R2QK2R w KQkq - 0 1";
    private const string EndgamePairFen   = "4k3/8/8/8/8/8/1B2B3/4K3 w - - 0 1";
    private const string EndgameSingleFen = "4k3/8/8/8/8/8/1B2N3/4K3 w - - 0 1";

    [Fact]
    public void TwoBishopsScoreMoreThanOneOnceMaterialIsSubtractedOut()
    {
        int delta = NonTableDelta(EndgameSingleFen, EndgamePairFen);

        Assert.True(delta > 0, $"the second bishop should be worth a pair bonus, got {delta}");
    }

    [Fact]
    public void TheBishopPairIsWorthMoreOnceThePiecesAreGone()
    {
        int midgame = NonTableDelta(MidgameSingleFen, MidgamePairFen);
        int endgame = NonTableDelta(EndgameSingleFen, EndgamePairFen);

        Assert.True(endgame > midgame, $"the pair should open up as the board does: {midgame} then {endgame}");
    }

    [Fact]
    public void BothSidesHoldingThePairCancels()
    {
        // A bonus one side can earn and the other cannot would be a bug, not a term. The position
        // is rotated 180°, which is the symmetry the piece-square tables are mirrored under.
        Assert.Equal(0, Eval("3k4/3b2b1/8/8/8/8/1B2B3/4K3 w - - 0 1"));
    }

    // ── The incremental counter stays in step with the board ─────────────────

    [Fact]
    public void CapturingABishopEndsThePairAndUndoRestoresIt()
    {
        // The counter is maintained on the same make/unmake path as the pawn files. A mismatch
        // would desynchronise it from the board, and every later evaluation in the search would be
        // wrong by a silent, drifting amount.
        var evaluator = new Evaluator();
        var engine = new ChessEngine();
        engine.LoadFen("4k3/8/b7/8/8/8/1B2B3/4K3 b - - 0 1");

        int before = evaluator.Evaluate(engine.GetBoardSnapshot());

        var capture = engine.GetLegalMoves().Single(m => m.To == Square.FromAlgebraic("e2"));
        engine.MakeMove(capture);

        var afterBoard = engine.GetBoardSnapshot();
        Assert.Equal(evaluator.Evaluate(afterBoard), evaluator.EvaluateFast(afterBoard));

        engine.UndoMove();
        var restored = engine.GetBoardSnapshot();

        Assert.Equal(before, evaluator.Evaluate(restored));
        Assert.Equal(before, evaluator.EvaluateFast(restored));
    }

    [Fact]
    public void PromotingToABishopCreatesThePair()
    {
        var evaluator = new Evaluator();
        var engine = new ChessEngine();
        engine.LoadFen("4k3/1P6/8/8/8/8/4B3/4K3 w - - 0 1");

        engine.MakeMove(engine.GetLegalMoves().Single(
            m => m.PromotionType == PieceType.Bishop && m.To == Square.FromAlgebraic("b8")));

        // The incremental path must agree with a from-scratch scan on a position it reached by
        // promoting — the case most likely to miss a counter update.
        var after = engine.GetBoardSnapshot();
        Assert.Equal(evaluator.Evaluate(after), evaluator.EvaluateFast(after));
    }
}
