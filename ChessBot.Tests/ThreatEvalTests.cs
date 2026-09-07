namespace ChessBot.Tests;

using ChessBot.Engine;
using ChessBot.Engine.Board;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Search;
using Xunit;

/// <summary>
/// Gates for the UseThreatEval switch. The point of the flag is to make the hanging-piece term
/// measurable, which requires two things this file checks: that turning it off actually changes
/// the static evaluation (otherwise an A/B run compares nothing), and that the two evaluation
/// paths stay numerically identical under either setting (otherwise the incremental fast path
/// and the from-scratch path would disagree, which would corrupt the search rather than the
/// measurement).
/// </summary>
public class ThreatEvalTests
{
    // Black's queen on d5 is attacked by the knight on c3 and defended by nothing. The knight is
    // deliberately not attacked in return (a queen does not attack along a knight's move), so
    // exactly one piece hangs and the term's contribution is a single known quantity.
    private const string HangingQueenFen = "4k3/8/8/3q4/8/2N5/8/4K3 w - - 0 1";

    // A quiet position: no non-pawn piece is attacked, so the term contributes nothing.
    private const string QuietFen = "4k3/pp6/8/8/8/8/6PP/4K3 w - - 0 1";

    private static Board BoardFor(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        return engine.GetBoardSnapshot();
    }

    [Fact]
    public void ThreatTerm_ChangesTheScoreWhenAPieceIsHanging()
    {
        var board = BoardFor(HangingQueenFen);
        var evaluator = new Evaluator();

        int withTerm    = evaluator.Evaluate(board, useThreatEval: true);
        int withoutTerm = evaluator.Evaluate(board, useThreatEval: false);

        // Half a queen, from White's point of view (White is to move, so the sign is positive).
        Assert.Equal(450, withTerm - withoutTerm);
    }

    [Fact]
    public void ThreatTerm_ChangesNothingWhenNoPieceIsHanging()
    {
        var board = BoardFor(QuietFen);
        var evaluator = new Evaluator();

        Assert.Equal(evaluator.Evaluate(board, useThreatEval: false),
                     evaluator.Evaluate(board, useThreatEval: true));
    }

    [Theory]
    [InlineData(HangingQueenFen)]
    [InlineData(QuietFen)]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1")]
    [InlineData("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1")]
    [InlineData("r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1")]
    public void EvaluateFast_MatchesEvaluate_UnderEitherSetting(string fen)
    {
        var board = BoardFor(fen);
        var evaluator = new Evaluator();

        // The incremental accumulators must reproduce the from-scratch scan exactly, whichever
        // terms are enabled — the flag must not be able to desynchronise them.
        Assert.Equal(evaluator.Evaluate(board, useThreatEval: true),
                     evaluator.EvaluateFast(board, useThreatEval: true));

        Assert.Equal(evaluator.Evaluate(board, useThreatEval: false),
                     evaluator.EvaluateFast(board, useThreatEval: false));
    }

    [Fact]
    public void Search_HonoursTheSetting()
    {
        // Both white knights are attacked by a rook and defended by nothing, and no single move
        // saves both — so every child of the root still carries a hanging piece. That makes a
        // depth-1 search with quiescence off a direct read of the static evaluation, and a
        // difference here proves the flag reaches the evaluator through the search rather than
        // only through a direct call.
        const string TwoHangingKnightsFen = "4k3/8/8/8/r6r/8/8/N3K2N w - - 0 1";

        SearchSettings Settings(bool useThreatEval) => new()
        {
            MaxDepth       = 1,
            MaxTimeMs      = 5_000,
            UseQuiescence  = false,
            UseThreatEval  = useThreatEval,
        };

        var engine = new ChessEngine();
        engine.LoadFen(TwoHangingKnightsFen);
        int withTerm = engine.FindBestMove(Settings(true)).Evaluation;

        engine.LoadFen(TwoHangingKnightsFen);
        int withoutTerm = engine.FindBestMove(Settings(false)).Evaluation;

        Assert.NotEqual(withTerm, withoutTerm);
    }

    [Fact]
    public void DefaultSettings_KeepTheTermEnabled()
    {
        // The flag exists to allow a measurement, not to change behaviour before one exists.
        Assert.True(new SearchSettings().UseThreatEval);
    }

    [Fact]
    public void PublicEvaluate_IsUnaffectedByTheFlagsExistence()
    {
        // ChessEngine.Evaluate is the public static-evaluation API and keeps the term.
        var engine = new ChessEngine();
        engine.LoadFen(HangingQueenFen);

        var evaluator = new Evaluator();
        int expected  = evaluator.Evaluate(engine.GetBoardSnapshot(), useThreatEval: true);

        // ChessEngine reports White-positive; the evaluator returns side-to-move relative.
        int sign = engine.SideToMove == ChessBot.Engine.Types.Color.White ? 1 : -1;
        Assert.Equal(expected * sign, engine.Evaluate().Score);
    }
}
