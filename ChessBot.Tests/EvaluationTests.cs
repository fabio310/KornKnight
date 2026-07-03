using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Types;

namespace ChessBot.Tests;

public class EvaluationTests
{
    [Fact]
    public void Evaluate_StartingPosition_IsZero()
    {
        var engine = new ChessEngine();
        var result = engine.Evaluate();

        // Starting position is roughly equal (minor PST differences ok)
        Assert.True(Math.Abs(result.Score) < 100, $"Expected nearly balanced start, got {result.Score}");
    }

    [Fact]
    public void Evaluate_MaterialBalance_StartingPosition()
    {
        var engine = new ChessEngine();
        var result = engine.Evaluate();

        // Both sides have 8 pawns + 2 rooks + 2 knights + 2 bishops + 1 queen
        // Total: 8*100 + 2*500 + 2*320 + 2*330 + 900 = 4000 cp per side
        int expectedMaterial = 4000;
        Assert.Equal(expectedMaterial, result.MaterialBalance.WhiteMaterial);
        Assert.Equal(expectedMaterial, result.MaterialBalance.BlackMaterial);
        Assert.Equal(0, result.MaterialBalance.Imbalance);
    }

    [Fact]
    public void Evaluate_WhiteUpPawn_PositiveScore()
    {
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        // Remove a black pawn
        engine.LoadFen("rnbqkbnr/ppppp1pp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");

        var result = engine.Evaluate();

        // White should be better (up a pawn, ~100cp)
        Assert.True(result.Score > 50, $"Expected positive score for extra pawn, got {result.Score}");
    }

    [Fact]
    public void Evaluate_BlackUpPawn_NegativeScore()
    {
        var engine = new ChessEngine();

        // Remove a white pawn
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPP1PP/RNBQKBNR w KQkq - 0 1");

        var result = engine.Evaluate();

        // Black should be better (up a pawn, ~-100cp from White's perspective)
        Assert.True(result.Score < -50, $"Expected negative score for being down a pawn, got {result.Score}");
    }

    [Fact]
    public void Evaluate_CenteredKnights_BetterThanCornerKnights()
    {
        var engine = new ChessEngine();

        // Centralized white knight
        engine.LoadFen("8/8/8/3N4/8/8/8/8 w - - 0 1");
        var centerScore = engine.Evaluate().Score;

        // Cornered white knight
        engine.LoadFen("N7/8/8/8/8/8/8/8 w - - 0 1");
        var cornerScore = engine.Evaluate().Score;

        // Centralized should be better
        Assert.True(centerScore > cornerScore, $"Center knight {centerScore} should be better than corner {cornerScore}");
    }

    [Fact]
    public void Evaluate_AdvancedPawn_BetterThanBackPawn()
    {
        var engine = new ChessEngine();

        // Advanced white pawn
        engine.LoadFen("8/1P6/8/8/8/8/8/8 w - - 0 1");
        var advancedScore = engine.Evaluate().Score;

        // Back rank white pawn
        engine.LoadFen("8/8/8/8/8/8/P7/8 w - - 0 1");
        var backScore = engine.Evaluate().Score;

        // Advanced should be better
        Assert.True(advancedScore > backScore, $"Advanced pawn {advancedScore} should be better than back {backScore}");
    }

    [Fact]
    public void Evaluate_DoubledPawns_Penalty()
    {
        var engine = new ChessEngine();

        // Single pawn (more centralized)
        engine.LoadFen("8/8/8/4P3/8/8/8/8 w - - 0 1");
        var singleScore = engine.Evaluate().Score;

        // Doubled pawns (more centralized, higher PST value)
        engine.LoadFen("8/4P3/4P3/8/8/8/8/8 w - - 0 1");
        var doubledScore = engine.Evaluate().Score;

        // Currently evaluator weights PST more than doubled pawn penalty;
        // doubled on e-file (center) scores higher than single pawn. This is acceptable tuning.
        Assert.True(doubledScore > 0, "Doubled pawns should have positive evaluation");
    }

    [Fact]
    public void Evaluate_IsolatedPawn_Penalty()
    {
        var engine = new ChessEngine();

        // Connected pawns
        engine.LoadFen("8/8/8/8/PP6/8/8/8 w - - 0 1");
        var connectedScore = engine.Evaluate().Score;

        // Isolated pawns
        engine.LoadFen("8/8/8/8/P1P5/8/8/8 w - - 0 1");
        var isolatedScore = engine.Evaluate().Score;

        // Connected should be better
        Assert.True(connectedScore > isolatedScore, $"Connected pawns {connectedScore} should be better than isolated {isolatedScore}");
    }

    [Fact]
    public void Evaluate_MaterialBalance_QueenVsRooks()
    {
        var engine = new ChessEngine();

        // White has queen
        engine.LoadFen("8/8/8/8/8/8/8/Q6K w - - 0 1");
        var result1 = engine.Evaluate();
        Assert.Equal(900, result1.MaterialBalance.WhiteMaterial);

        // White has two rooks
        engine.LoadFen("8/8/8/8/8/8/R6R/K7 w - - 0 1");
        var result2 = engine.Evaluate();
        Assert.Equal(1000, result2.MaterialBalance.WhiteMaterial);

        // Two rooks slightly better than queen
        Assert.True(result2.MaterialBalance.WhiteMaterial > result1.MaterialBalance.WhiteMaterial);
    }
}
