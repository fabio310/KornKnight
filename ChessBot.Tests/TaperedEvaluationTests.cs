namespace ChessBot.Tests;

using ChessBot.Engine;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Search;
using ChessBot.Engine.Types;
using Xunit;

/// <summary>
/// Gates for UseTaperedEval.
///
/// The load-bearing test here is the one that plays a self-play game and demands
/// EvaluateFast == Evaluate at every position along it. That equality is the only thing keeping
/// the incremental accumulators honest: they are updated piece by piece through make/unmake,
/// and a single mismatched update would put the search's scores quietly out of step with the
/// from-scratch evaluation for the rest of the game. Tapering doubles the number of
/// accumulators that have to stay in step, so it doubles what that test is protecting.
/// </summary>
public class TaperedEvaluationTests
{
    /// <summary>
    /// Positions from real self-play, which is what makes them a fair test of make/unmake:
    /// they are reached by actual moves — captures, promotions, castling, en passant — rather
    /// than loaded from FEN, so every position depends on every update before it being right.
    /// </summary>
    private static List<ChessBot.Engine.Board.Board> PlaySelfPlayPositions(int wanted)
    {
        var positions = new List<ChessBot.Engine.Board.Board>();

        // A small fixed node budget keeps the games deterministic and the test quick.
        var settings = new SearchSettings { MaxNodes = 1_500, MaxTimeMs = int.MaxValue };

        var openings = new[]
        {
            "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
            "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
            "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
            "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        };

        foreach (string opening in openings)
        {
            var engine = new ChessEngine();
            engine.LoadFen(opening);

            for (int ply = 0; ply < 200 && positions.Count < wanted; ply++)
            {
                if (engine.GetLegalMoves().Count == 0) break;
                if (engine.GetBoardSnapshot().State.IsFiftyMoveRuleDraw) break;

                engine.MakeMove(engine.FindBestMove(settings).BestMove);

                // GetBoardSnapshot copies the live accumulators as make/unmake left them. Going
                // through a FEN round-trip instead would rebuild them from a full scan, which is
                // exactly the code path this test must not trust.
                positions.Add(engine.GetBoardSnapshot());
            }

            if (positions.Count >= wanted) break;
        }

        return positions;
    }

    [Fact]
    public void EvaluateFast_MatchesEvaluate_AcrossASelfPlayGame()
    {
        var evaluator = new Evaluator();
        var positions = PlaySelfPlayPositions(400);

        Assert.True(positions.Count >= 300,
            $"expected several hundred positions to check, got {positions.Count}");

        foreach (var board in positions)
        {
            foreach (bool tapered in new[] { false, true })
            {
                Assert.Equal(
                    evaluator.Evaluate(board, useTaperedEval: tapered),
                    evaluator.EvaluateFast(board, useTaperedEval: tapered));
            }
        }
    }

    [Fact]
    public void EvaluateFast_MatchesEvaluate_ThroughMakeAndUnmake()
    {
        // The self-play test above evaluates positions the search settled on. This one checks
        // the accumulators along every branch the search touches and back again: an update that
        // is right going forward but wrong in reverse would leave the board subtly corrupted
        // after the search returned, and nothing else would notice.
        var evaluator = new Evaluator();
        var engine = new ChessEngine();
        engine.LoadFen("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");

        int before = evaluator.EvaluateFast(engine.GetBoardSnapshot(), useTaperedEval: true);

        foreach (var move in engine.GetLegalMoves())
        {
            engine.MakeMove(move);

            var board = engine.GetBoardSnapshot();
            Assert.Equal(evaluator.Evaluate(board, useTaperedEval: true),
                         evaluator.EvaluateFast(board, useTaperedEval: true));

            engine.UndoMove();

            Assert.Equal(before, evaluator.EvaluateFast(engine.GetBoardSnapshot(), useTaperedEval: true));
        }
    }

    [Fact]
    public void TaperedEval_AtFullPhase_EqualsTheUntaperedScore()
    {
        // The midgame set is the table the engine already used, so the opening end of the taper
        // is a no-op. This is what confines any measured difference to positions where material
        // has actually left the board.
        var evaluator = new Evaluator();
        var engine = new ChessEngine();

        var board = engine.GetBoardSnapshot();
        Assert.Equal(GamePhase.Max, board.IncrementalPhase);

        Assert.Equal(evaluator.Evaluate(board, useTaperedEval: false),
                     evaluator.Evaluate(board, useTaperedEval: true));
    }

    [Fact]
    public void TaperedEval_ChangesTheScoreOnceMaterialHasLeftTheBoard()
    {
        // A rook endgame: phase 4 of 24, so the endgame table set carries five sixths of the
        // weight and the score must differ from the single-table one.
        var evaluator = new Evaluator();
        var engine = new ChessEngine();
        engine.LoadFen("4k3/pppppppp/8/8/8/8/PPPPPPPP/R3K3 w Q - 0 1");

        var board = engine.GetBoardSnapshot();

        Assert.NotEqual(evaluator.Evaluate(board, useTaperedEval: false),
                        evaluator.Evaluate(board, useTaperedEval: true));
    }

    [Fact]
    public void TaperedEval_ValuesAnAdvancedPawnMoreInAnEndgame()
    {
        // The point of tapering, in one comparison: the same pawn push is worth more when the
        // board is empty than when it is full.
        int openingGain = PushGain("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w - - 0 1",
                                   "rnbqkbnr/pppppppp/8/8/8/P7/1PPPPPPP/RNBQKBNR w - - 0 1");

        int endgameGain = PushGain("4k3/pppppppp/8/8/8/8/PPPPPPPP/4K3 w - - 0 1",
                                   "4k3/pppppppp/8/8/8/P7/1PPPPPPP/4K3 w - - 0 1");

        Assert.True(endgameGain > openingGain,
            $"advancing a pawn should be worth more in an endgame: opening {openingGain}, endgame {endgameGain}");
    }

    /// <summary>Tapered score difference between a position and the same position after a pawn push.</summary>
    private static int PushGain(string before, string after)
    {
        var evaluator = new Evaluator();

        var b = new ChessEngine(); b.LoadFen(before);
        var a = new ChessEngine(); a.LoadFen(after);

        return evaluator.Evaluate(a.GetBoardSnapshot(), useTaperedEval: true)
             - evaluator.Evaluate(b.GetBoardSnapshot(), useTaperedEval: true);
    }

    // ── The accumulators themselves ──────────────────────────────────────────

    [Fact]
    public void EndgameAccumulators_AreRestoredExactlyOnUndo()
    {
        var engine = new ChessEngine();
        engine.LoadFen("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");

        var start = engine.GetBoardSnapshot();
        int material = start.IncrementalEndgameMaterialScore;
        int pst      = start.IncrementalEndgamePstScore;

        foreach (var move in engine.GetLegalMoves())
        {
            engine.MakeMove(move);
            engine.UndoMove();
        }

        var end = engine.GetBoardSnapshot();
        Assert.Equal(material, end.IncrementalEndgameMaterialScore);
        Assert.Equal(pst, end.IncrementalEndgamePstScore);
    }

    [Fact]
    public void EndgameMaterial_UsesTheEndgameScale()
    {
        // A pawn is worth more in the endgame than the midgame, and a knight less. If these ever
        // become equal the taper is a no-op dressed up as a feature.
        Assert.True(PieceSquareTables.EndgameMaterialValue(PieceType.Pawn) >
                    PieceType.Pawn.MaterialValue());

        Assert.True(PieceSquareTables.EndgameMaterialValue(PieceType.Knight) <
                    PieceType.Knight.MaterialValue());

        Assert.True(PieceSquareTables.EndgameMaterialValue(PieceType.Rook) >
                    PieceType.Rook.MaterialValue());
    }

    [Fact]
    public void Interpolate_EndsAtTheMidgameAndEndgameValues()
    {
        Assert.Equal(100, PieceSquareTables.Interpolate(100, 300, GamePhase.Max));
        Assert.Equal(300, PieceSquareTables.Interpolate(100, 300, 0));
        Assert.Equal(200, PieceSquareTables.Interpolate(100, 300, GamePhase.Max / 2));

        // Beyond a full starting array (possible after promotions) it must not extrapolate.
        Assert.Equal(100, PieceSquareTables.Interpolate(100, 300, GamePhase.Max + 12));
    }

    [Fact]
    public void Interpolate_IsColourSymmetric()
    {
        for (int phase = 0; phase <= GamePhase.Max; phase++)
            for (int mg = -300; mg <= 300; mg += 37)
                for (int eg = -300; eg <= 300; eg += 53)
                    Assert.Equal(-PieceSquareTables.Interpolate(mg, eg, phase),
                                  PieceSquareTables.Interpolate(-mg, -eg, phase));
    }

    [Fact]
    public void EndgameTables_AreLeftRightSymmetric()
    {
        // An asymmetric table would make the engine prefer one wing for no reason, and would
        // break the mirror symmetry the evaluation relies on.
        foreach (PieceType type in new[] { PieceType.Pawn, PieceType.Knight, PieceType.Bishop,
                                           PieceType.Rook, PieceType.Queen, PieceType.King })
        {
            var table = PieceSquareTables.EndgameTableFor(type);
            for (int rank = 0; rank < 8; rank++)
                for (int file = 0; file < 8; file++)
                    Assert.Equal(table[rank][file], table[rank][7 - file]);
        }
    }

    [Fact]
    public void Search_HonoursTheSetting()
    {
        SearchSettings Settings(bool tapered) => new()
        {
            MaxDepth = 1,
            MaxTimeMs = 5_000,
            UseQuiescence = false,
            UseTaperedEval = tapered,
        };

        const string EndgameFen = "4k3/pppppppp/8/8/8/8/PPPPPPPP/R3K3 w Q - 0 1";

        var engine = new ChessEngine();
        engine.LoadFen(EndgameFen);
        int flat = engine.FindBestMove(Settings(false)).Evaluation;

        engine.LoadFen(EndgameFen);
        int tapered = engine.FindBestMove(Settings(true)).Evaluation;

        Assert.NotEqual(flat, tapered);
    }

    [Fact]
    public void DefaultSettings_KeepTheSingleTableBehaviour()
    {
        Assert.False(new SearchSettings().UseTaperedEval);
    }
}
