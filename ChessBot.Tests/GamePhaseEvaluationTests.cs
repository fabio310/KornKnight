namespace ChessBot.Tests;

using ChessBot.Engine;
using ChessBot.Engine.Board;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Search;
using ChessBot.Engine.Types;
using Xunit;

/// <summary>
/// Gates for UseGamePhaseDevelopment.
///
/// The defect being fixed is that the evaluation was not a function of the position: the
/// development term read the move number, which the Zobrist hash does not encode. Two positions
/// with identical pieces then evaluate differently, a transposition-table entry holds a score
/// that was only valid at one move number, and a search crossing move 20 sees the score improve
/// because plies elapsed rather than because anything happened on the board.
///
/// The central test here is therefore the one that evaluates the same FEN at different move
/// numbers and demands the same answer.
/// </summary>
public class GamePhaseEvaluationTests
{
    // An opening position: both kings uncastled on e1/e8, every minor piece still at home, so
    // both halves of the development term are active and any move-number dependence shows up.
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
    public void PhaseDevelopment_ScoresTheSamePositionIdenticallyAtEveryMoveNumber(
        int halfmoveClock, int fullmoveNumber)
    {
        var evaluator = new Evaluator();

        int reference = evaluator.Evaluate(
            BoardAt(OpeningFen, 0, 1), useThreatEval: true, useGamePhaseDevelopment: true);

        int actual = evaluator.Evaluate(
            BoardAt(OpeningFen, halfmoveClock, fullmoveNumber),
            useThreatEval: true, useGamePhaseDevelopment: true);

        Assert.Equal(reference, actual);
    }

    // White has castled and developed a knight, Black has not: an asymmetric position, so the
    // development term contributes something rather than cancelling itself out.
    private const string AsymmetricFen =
        "rnbqkbnr/pppppppp/8/8/8/5N2/PPPPPPPP/RNBQ1BKR w kq - {0} {1}";

    [Fact]
    public void MoveNumberDevelopment_ScoresTheSamePositionDifferently()
    {
        // The behaviour the flag exists to replace, asserted so the fix cannot quietly become a
        // no-op: with the legacy rule the identical position is worth something different purely
        // because the move counter moved.
        var evaluator = new Evaluator();

        int early = evaluator.Evaluate(BoardAt(AsymmetricFen, 0, 1),  useGamePhaseDevelopment: false);
        int late  = evaluator.Evaluate(BoardAt(AsymmetricFen, 0, 40), useGamePhaseDevelopment: false);

        Assert.NotEqual(early, late);
    }

    [Fact]
    public void PhaseDevelopment_ScoresTheAsymmetricPositionIdenticallyAtEveryMoveNumber()
    {
        var evaluator = new Evaluator();

        int early = evaluator.Evaluate(BoardAt(AsymmetricFen, 0, 1),  useGamePhaseDevelopment: true);
        int late  = evaluator.Evaluate(BoardAt(AsymmetricFen, 0, 40), useGamePhaseDevelopment: true);

        Assert.Equal(early, late);
    }

    [Fact]
    public void PhaseDevelopment_SurvivesATranspositionToTheSamePosition()
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
            evaluator.Evaluate(direct.GetBoardSnapshot(), useGamePhaseDevelopment: true),
            evaluator.Evaluate(viaShuffle.GetBoardSnapshot(), useGamePhaseDevelopment: true));
    }

    // ── The term still does its job ──────────────────────────────────────────

    /// <summary>
    /// Evaluates a FEN with the phase-based development term, White-positive.
    ///
    /// Every position passed here has White to move, and the evaluator returns a side-to-move
    /// relative score, so no sign correction is needed.
    /// </summary>
    private static int Eval(string fen, bool useGamePhase = true)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        return new Evaluator().Evaluate(engine.GetBoardSnapshot(), useGamePhaseDevelopment: useGamePhase);
    }

    // Two positions identical in every piece except where White's king stands: e1 (uncastled)
    // versus g1 (castled). Material and piece-square tables both skip kings, so the difference
    // between these evaluations is exactly the uncastled-king penalty and nothing else.
    private const string KingOnE1Fen = "rnbqkbnr/pppppppp/8/8/8/5N2/PPPPPPPP/RNBQKB1R w KQkq - 0 1";
    private const string KingOnG1Fen = "rnbqkbnr/pppppppp/8/8/8/5N2/PPPPPPPP/RNBQ1BKR w kq - 0 1";

    [Fact]
    public void PhaseDevelopment_StillRewardsCastling()
    {
        // A full starting array is phase 24, so the term applies at full weight.
        Assert.Equal(40, Eval(KingOnG1Fen) - Eval(KingOnE1Fen));
    }

    [Fact]
    public void PhaseDevelopment_StillRewardsDeveloping()
    {
        // Knight home on b1 versus developed to c3. This pair does differ in piece-square value,
        // so that part is subtracted out to leave the development term alone.
        const string KnightHomeFen      = "rnbqkbnr/pppppppp/8/8/8/5N2/PPPPPPPP/RNBQKB1R w KQkq - 0 1";
        const string KnightDevelopedFen = "rnbqkbnr/pppppppp/8/8/8/2N2N2/PPPPPPPP/R1BQKB1R w KQkq - 0 1";

        int scoreDelta = Eval(KnightDevelopedFen) - Eval(KnightHomeFen);
        int pstDelta   = PstOf(KnightDevelopedFen) - PstOf(KnightHomeFen);

        // The b1 knight's home-square penalty is 30, at full phase weight.
        Assert.Equal(30, scoreDelta - pstDelta);
    }

    [Fact]
    public void PhaseDevelopment_FadesAsMaterialLeavesTheBoard()
    {
        // The same castling difference, measured with a full army and with only rooks left.
        // Rooks and pawns only: phase 8 of 24, so the term keeps a third of its weight.
        const string EndgameKingOnE1Fen = "r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R3K2R w KQkq - 0 1";
        const string EndgameKingOnG1Fen = "r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R4RK1 w kq - 0 1";

        int openingDelta = Eval(KingOnG1Fen) - Eval(KingOnE1Fen);
        int endgameDelta = Eval(EndgameKingOnG1Fen) - Eval(EndgameKingOnE1Fen);

        Assert.Equal(40, openingDelta);            // phase 24/24
        Assert.Equal(40 * 8 / 24, endgameDelta);   // phase  8/24

        Assert.True(endgameDelta > 0 && endgameDelta < openingDelta,
            $"the term should fade rather than switch off: {openingDelta} → {endgameDelta}");
    }

    /// <summary>Piece-square-table score alone, so a comparison can exclude what is not under test.</summary>
    private static int PstOf(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        return engine.GetBoardSnapshot().IncrementalPstScore;
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
        Assert.Equal(100, GamePhase.ScaleByOpening(100, GamePhase.Max + 8));
    }

    [Fact]
    public void ScaleByOpening_IsColourSymmetric()
    {
        // Integer division must not favour one sign, or mirroring a position would change the
        // White-minus-Black difference.
        for (int phase = 0; phase <= GamePhase.Max; phase++)
            for (int score = -120; score <= 120; score += 7)
                Assert.Equal(-GamePhase.ScaleByOpening(score, phase),
                              GamePhase.ScaleByOpening(-score, phase));
    }

    // ── The two evaluation paths stay in step ────────────────────────────────

    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1")]
    [InlineData("4k3/8/8/8/8/8/4P3/4K3 w - - 0 1")]
    public void EvaluateFast_MatchesEvaluate_WithPhaseDevelopment(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        var board = engine.GetBoardSnapshot();
        var evaluator = new Evaluator();

        // The incremental phase must reproduce the from-scratch scan exactly, or the search's
        // scores would differ from the reference evaluation.
        Assert.Equal(evaluator.Evaluate(board, useGamePhaseDevelopment: true),
                     evaluator.EvaluateFast(board, useGamePhaseDevelopment: true));
    }

    [Fact]
    public void Search_HonoursTheSetting()
    {
        // Same position, same depth, only the flag differs: the reported score must change,
        // proving the setting reaches the evaluator through the search.
        SearchSettings Settings(bool usePhase) => new()
        {
            MaxDepth = 1,
            MaxTimeMs = 5_000,
            UseQuiescence = false,
            UseGamePhaseDevelopment = usePhase,
        };

        const string LateOpeningFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 30";

        var engine = new ChessEngine();
        engine.LoadFen(LateOpeningFen);
        int legacy = engine.FindBestMove(Settings(false)).Evaluation;

        engine.LoadFen(LateOpeningFen);
        int phased = engine.FindBestMove(Settings(true)).Evaluation;

        // At move 30 the legacy term is switched off entirely; the phase term is at full weight.
        Assert.NotEqual(legacy, phased);
    }

    [Fact]
    public void DefaultSettings_KeepTheMoveNumberBehaviour()
    {
        Assert.False(new SearchSettings().UseGamePhaseDevelopment);
    }
}
