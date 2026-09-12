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

    // ── Kings in the tapered evaluation ──────────────────────────────────────

    [Fact]
    public void MidgameKingTable_NeverRewardsWalkingTheKingUpTheBoard()
    {
        // The midgame king table is White-perspective, so it has to fall away from the home rank
        // and never climb back: a king that leaves its shelter and walks up the board must not
        // score better for having done so. The table was written by mirroring the bottom four
        // ranks into the top four, so rank 8 scored the same as rank 1 — a White king on g8 was
        // worth the same +30 as a White king castled on g1. That was invisible while kings were
        // excluded from the accumulator. It is not invisible any more.
        var king = PieceSquareTables.TableFor(PieceType.King);

        for (int file = 0; file < 8; file++)
            for (int rank = 1; rank < 8; rank++)
                Assert.True(king[rank][file] <= king[rank - 1][file],
                    $"file {file}: rank {rank + 1} scores {king[rank][file]}, better than " +
                    $"rank {rank} at {king[rank - 1][file]}");
    }

    [Fact]
    public void KingPst_IsInTheIncrementalAccumulators()
    {
        // A move that changes nothing but where the king stands has to move both accumulators,
        // or the king is not in the taper at all.
        var engine = new ChessEngine();
        engine.LoadFen("4k3/pppppppp/8/8/4K3/8/PPPPPPPP/8 w - - 0 1");

        var before = engine.GetBoardSnapshot();
        int midgameBefore = before.IncrementalPstScore;
        int endgameBefore = before.IncrementalEndgamePstScore;

        engine.MakeMove(engine.GetLegalMoves().Single(m => m.ToString() == "e4f3"));

        var after = engine.GetBoardSnapshot();
        Assert.NotEqual(midgameBefore, after.IncrementalPstScore);
        Assert.NotEqual(endgameBefore, after.IncrementalEndgamePstScore);
    }

    [Fact]
    public void TaperedEval_ValuesACentralisedKingMoreAsMaterialLeavesTheBoard()
    {
        // The same king, on d4 instead of a1, judged with a full enemy army on the board and
        // again with nothing but pawns. This is the transition the taper exists to make
        // continuous, and the one the king used to bypass entirely: a hard
        // `totalMaterial < 1000` threshold switched a centralisation bonus on, and the midgame
        // table — the one thing that says a king belongs behind its pawns while queens are on —
        // was never read at all.
        int withArmy = CentralisationGain(
            "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/K7 w kq - 0 1",
            "rnbqkbnr/pppppppp/8/8/3K4/8/PPPPPPPP/8 w kq - 0 1");

        int bareBoard = CentralisationGain(
            "4k3/pppppppp/8/8/8/8/PPPPPPPP/K7 w - - 0 1",
            "4k3/pppppppp/8/8/3K4/8/PPPPPPPP/8 w - - 0 1");

        Assert.True(bareBoard > withArmy,
            $"centralising the king should be worth more as material leaves: {withArmy} -> {bareBoard}");

        // At phase 0 the endgame table carries the whole weight, so the gain is exactly the
        // difference between its d4 and a1 entries and nothing else.
        Assert.Equal(PieceSquareTables.EndgameTableFor(PieceType.King)[3][3]
                   - PieceSquareTables.EndgameTableFor(PieceType.King)[0][0], bareBoard);
    }

    /// <summary>Tapered score difference between a corner king and a centralised one.</summary>
    private static int CentralisationGain(string corner, string centre)
    {
        var evaluator = new Evaluator();

        var a = new ChessEngine(); a.LoadFen(corner);
        var b = new ChessEngine(); b.LoadFen(centre);

        return evaluator.Evaluate(b.GetBoardSnapshot(), useTaperedEval: true)
             - evaluator.Evaluate(a.GetBoardSnapshot(), useTaperedEval: true);
    }


    [Fact]
    public void DefaultSettings_KeepTheSingleTableBehaviour()
    {
        Assert.False(new SearchSettings().UseTaperedEval);
    }
}
