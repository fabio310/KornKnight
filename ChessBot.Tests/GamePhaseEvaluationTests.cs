namespace ChessBot.Tests;

using ChessBot.Engine;
using ChessBot.Engine.Board;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Search;
using ChessBot.Engine.Types;
using Xunit;

/// <summary>
/// Gates for the material game phase, and for the property that the evaluation is a function of
/// the position and nothing else.
///
/// The defect these were written for is that the evaluation once read the move number, which the
/// Zobrist hash does not encode. Two positions with identical pieces then evaluate differently, a
/// transposition-table entry holds a score that was only valid at one move number, and a search
/// crossing move 20 sees the score improve because plies elapsed rather than because anything
/// happened on the board.
///
/// The rule that read the move number was the development term, which is now gone; the property
/// it violated is not, so the tests that evaluate the same FEN at different move numbers stay.
/// </summary>
public class GamePhaseEvaluationTests
{
    // An opening position with the full starting array, so every phase-scaled term is at full
    // weight and any move-number dependence would show up.
    private const string OpeningFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - {0} {1}";

    private static Board BoardAt(string fenTemplate, int halfmoveClock, int fullmoveNumber)
    {
        var engine = new ChessEngine();
        engine.LoadFen(string.Format(fenTemplate, halfmoveClock, fullmoveNumber));
        return engine.GetBoardSnapshot();
    }

    // ── The regression this task exists for ──────────────────────────────────

    [Theory]
    [InlineData(0, 1)]
    [InlineData(0, 9)]
    [InlineData(4, 10)]     // the legacy king-penalty threshold
    [InlineData(30, 15)]
    [InlineData(50, 20)]    // the legacy cut-off
    [InlineData(60, 21)]    // one move past it
    [InlineData(99, 40)]
    public void Evaluation_ScoresTheSamePositionIdenticallyAtEveryMoveNumber(
        int halfmoveClock, int fullmoveNumber)
    {
        var evaluator = new Evaluator();

        int reference = evaluator.Evaluate(BoardAt(OpeningFen, 0, 1));
        int actual    = evaluator.Evaluate(BoardAt(OpeningFen, halfmoveClock, fullmoveNumber));

        Assert.Equal(reference, actual);
    }

    // White has castled and developed a knight, Black has not: an asymmetric position, so the
    // development term contributes something rather than cancelling itself out.
    private const string AsymmetricFen =
        "rnbqkbnr/pppppppp/8/8/8/5N2/PPPPPPPP/RNBQ1BKR w kq - {0} {1}";

    [Fact]
    public void Evaluation_ScoresTheAsymmetricPositionIdenticallyAtEveryMoveNumber()
    {
        var evaluator = new Evaluator();

        int early = evaluator.Evaluate(BoardAt(AsymmetricFen, 0, 1));
        int late  = evaluator.Evaluate(BoardAt(AsymmetricFen, 0, 40));

        Assert.Equal(early, late);
    }

    [Fact]
    public void Evaluation_SurvivesATranspositionToTheSamePosition()
    {
        // Reaching one position by two routes of different lengths must not change its score —
        // this is the property the transposition table silently assumes.
        var direct = new ChessEngine();
        direct.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        var viaShuffle = new ChessEngine();
        foreach (string move in new[] { "g1f3", "g8f6", "f3g1", "f6g8" })
        {
            var legal = viaShuffle.GetLegalMoves();
            viaShuffle.MakeMove(legal.Single(m => m.ToString() == move));
        }

        var evaluator = new Evaluator();

        Assert.Equal(
            evaluator.Evaluate(direct.GetBoardSnapshot()),
            evaluator.Evaluate(viaShuffle.GetBoardSnapshot()));
    }

    // ── The phase itself ─────────────────────────────────────────────────────

    [Fact]
    public void Phase_IsTwentyFourAtTheStartingPositionAndZeroInAPawnEndgame()
    {
        var start = new ChessEngine();
        Assert.Equal(GamePhase.Max, start.GetBoardSnapshot().IncrementalPhase);

        var pawns = new ChessEngine();
        pawns.LoadFen("4k3/pppppppp/8/8/8/8/PPPPPPPP/4K3 w - - 0 1");
        Assert.Equal(0, pawns.GetBoardSnapshot().IncrementalPhase);
    }

    [Fact]
    public void Phase_TracksCapturesAndIsRestoredExactlyOnUndo()
    {
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        // A make/unmake that mismatches would desynchronise the accumulator from the board, and
        // every later evaluation in the search would be wrong by a silent, drifting amount.
        var board = engine.GetBoardSnapshot();
        int before = board.IncrementalPhase;

        var legal = engine.GetLegalMoves();
        foreach (var move in legal)
        {
            engine.MakeMove(move);
            engine.UndoMove();
        }

        Assert.Equal(before, engine.GetBoardSnapshot().IncrementalPhase);
    }

    [Fact]
    public void Phase_DropsByTheCapturedPiecesWeight()
    {
        // Black queen on d5, white knight on c3 that can take it: a queen is 4 phase points.
        var engine = new ChessEngine();
        engine.LoadFen("4k3/8/8/3q4/8/2N5/8/4K3 w - - 0 1");

        int before = engine.GetBoardSnapshot().IncrementalPhase;
        Assert.Equal(GamePhase.WeightFor(PieceType.Queen) + GamePhase.WeightFor(PieceType.Knight), before);

        var capture = engine.GetLegalMoves().Single(m => m.To == Square.FromAlgebraic("d5"));
        engine.MakeMove(capture);

        Assert.Equal(before - GamePhase.WeightFor(PieceType.Queen),
                     engine.GetBoardSnapshot().IncrementalPhase);
    }

    [Fact]
    public void Phase_CountsAPromotedQueen()
    {
        var engine = new ChessEngine();
        engine.LoadFen("4k3/1P6/8/8/8/8/8/4K3 w - - 0 1");

        Assert.Equal(0, engine.GetBoardSnapshot().IncrementalPhase);

        engine.MakeMove(engine.GetLegalMoves().Single(
            m => m.PromotionType == PieceType.Queen && m.To == Square.FromAlgebraic("b8")));

        Assert.Equal(GamePhase.WeightFor(PieceType.Queen), engine.GetBoardSnapshot().IncrementalPhase);
    }

    [Fact]
    public void Phase_IsClampedForConsumers()
    {
        // Promotions can push the raw sum past a full starting array; "more opening than the
        // opening" is not a thing a term should scale by.
        Assert.Equal(GamePhase.Max, GamePhase.Clamp(GamePhase.Max + 8));
        Assert.Equal(0, GamePhase.Clamp(-1));
    }

    // ── The two evaluation paths stay in step ────────────────────────────────

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1")]
    [InlineData("4k3/8/8/8/8/8/4P3/4K3 w - - 0 1")]
    // Rooks doubled on an open file, a rook on the seventh, and a bishop pair against two knights
    // — the positions that exercise the terms whose state is maintained incrementally rather than
    // read from the piece list.
    [InlineData("2r3k1/pp2Rppp/8/8/8/8/PP3PPP/2R3K1 w - - 0 1")]
    [InlineData("4k3/8/8/8/8/2n2n2/1B2B3/4K3 w - - 0 1")]
    [InlineData("r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1")]
    public void EvaluateFast_MatchesEvaluate(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        var board = engine.GetBoardSnapshot();
        var evaluator = new Evaluator();

        // The incremental phase must reproduce the from-scratch scan exactly, or the search's
        // scores would differ from the reference evaluation.
        Assert.Equal(evaluator.Evaluate(board),
                     evaluator.EvaluateFast(board));
    }
}
