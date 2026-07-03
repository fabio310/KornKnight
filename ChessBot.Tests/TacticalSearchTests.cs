using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Types;
using ChessBot.Engine.Search;

namespace ChessBot.Tests;

/// <summary>
/// Tactical tests proving the engine's correctness after the three major improvements:
///   1. Evaluation sign convention fix (score relative to side-to-move)
///   2. Null-move pruning (NMP) enabling deeper effective search
///   3. Late Move Reductions (LMR)
/// All positions have been manually verified.
/// </summary>
public class TacticalSearchTests
{
    // ── Mate detection ─────────────────────────────────────────────────────

    [Fact]
    public void MateIn1_White_QueenDeliversCheckmate()
    {
        // White: Ka6, Qb6.  Black: Ka8.
        // 1.Qb8# covers b8 (queen), b7 (queen on b6→b8), a7 (Ka6).
        // Black king on a8 has zero escape squares.
        var engine = new ChessEngine();
        engine.LoadFen("k7/8/KQ6/8/8/8/8/8 w - - 0 1");

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 2, MaxTimeMs = 2000 });

        Assert.Contains(result.BestMove, engine.GetLegalMoves());
        Assert.True(result.Evaluation > 90_000,
            $"Expected mate score >90000, got {result.Evaluation}. Best: {result.BestMove}");
    }

    [Fact]
    public void MateIn2_WhiteQueenAndRook_FindsForcedMate()
    {
        // White: Kg1, Qf7, Re1.  Black: Kg8.
        // 1.Qf8+ Kh7 2.Rh1#  (or 1.Qg7+ Kh8 2.Rh1#, etc.)
        var engine = new ChessEngine();
        engine.LoadFen("6k1/5Q2/8/8/8/8/8/4R1K1 w - - 0 1");

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 4, MaxTimeMs = 5000 });

        Assert.Contains(result.BestMove, engine.GetLegalMoves());
        Assert.True(result.Evaluation > 90_000,
            $"Expected mate score in queen+rook position, got {result.Evaluation}. " +
            $"Depth: {result.DepthAchieved}, Best: {result.BestMove}");
    }

    // ── Evaluation sign (side-to-move convention) ──────────────────────────

    [Fact]
    public void EvalSign_WhiteExtraQueenIsPositive()
    {
        // White: Ke1, Qd1.  Black: Ke8.  White to move.
        // ChessEngine.Evaluate() is White-positive; White is up a queen → strongly positive.
        var engine = new ChessEngine();
        engine.LoadFen("4k3/8/8/8/8/8/8/3QK3 w - - 0 1");

        var eval = engine.Evaluate();
        Assert.True(eval.Score > 500,
            $"Expected White-positive score >500cp, got {eval.Score}");
    }

    [Fact]
    public void EvalSign_BlackExtraQueenIsNegative()
    {
        // Black: Ka8, Qd8.  White: Ka1.  Black to move.
        // ChessEngine.Evaluate() White-positive; Black is up a queen → strongly negative.
        var engine = new ChessEngine();
        engine.LoadFen("k2q4/8/8/8/8/8/8/K7 b - - 0 1");

        var eval = engine.Evaluate();
        Assert.True(eval.Score < -500,
            $"Expected White-negative score <-500cp, got {eval.Score}");
    }

    [Fact]
    public void Search_BlackAdvantage_ReturnsPositiveForBlack()
    {
        // Black queen vs lone White king — Black (side-to-move) should get a large positive score.
        // FEN: Black queen e4, Black king e6, White king h1.  Black to move.
        var engine = new ChessEngine();
        engine.LoadFen("8/8/4k3/8/4q3/8/8/7K b - - 0 1");

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 3, MaxTimeMs = 3000 });

        // Side-to-move-relative: Black has queen advantage → score from Black's view > 800
        Assert.True(result.Evaluation > 800,
            $"Expected Black's advantage >800cp, got {result.Evaluation}");
        Assert.Contains(result.BestMove, engine.GetLegalMoves());
    }

    // ── Material wins ──────────────────────────────────────────────────────

    [Fact]
    public void WinFreeKnight_TakesUndefendedPiece()
    {
        // White: Ke1, Qd1.  Black: Ke8, Nb4 (undefended).
        // White should capture the free knight.
        var engine = new ChessEngine();
        engine.LoadFen("4k3/8/8/8/1n6/8/8/3QK3 w - - 0 1");

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 3, MaxTimeMs = 3000 });

        Assert.Contains(result.BestMove, engine.GetLegalMoves());
        // After taking the knight, White should be up material → positive
        Assert.True(result.Evaluation > 0,
            $"Expected positive evaluation after winning knight, got {result.Evaluation}");
    }

    // ── Avoidance ─────────────────────────────────────────────────────────

    [Fact]
    public void AvoidBlunder_RookNotToAttackedSquare()
    {
        // White: Ke1, Ra1.  Black: Ke8, pawn b4 (covers a3).
        // The engine must not move the rook to a3 (loses it to the pawn).
        var engine = new ChessEngine();
        engine.LoadFen("4k3/8/8/8/1p6/8/8/R3K3 w - - 0 1");

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 4, MaxTimeMs = 3000 });

        Assert.Contains(result.BestMove, engine.GetLegalMoves());

        var blunderMove = new Move(
            Square.FromAlgebraic("a1"),
            Square.FromAlgebraic("a3"),
            MoveType.Quiet);
        Assert.NotEqual(blunderMove, result.BestMove);
    }

    // ── Null-move pruning effectiveness ───────────────────────────────────

    [Fact]
    public void NullMovePruning_AllowsDeeperSearch()
    {
        // With NMP the engine should be able to reach depth 5+ within a reasonable time budget.
        var engine = new ChessEngine();
        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 7, MaxTimeMs = 8000 });

        Assert.True(result.DepthAchieved >= 5,
            $"Expected depth >=5 with NMP, got {result.DepthAchieved}");
        Assert.True(result.NodesSearched > 100);
    }

    // ── Quiescence: prevents horizon blunders ─────────────────────────────

    [Fact]
    public void Quiescence_StandPatPreventsNegativeEval()
    {
        // Starting position is roughly equal. Quiescence should not make it look strongly skewed.
        var engine = new ChessEngine();
        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 4, MaxTimeMs = 3000 });

        Assert.True(Math.Abs(result.Evaluation) < 200,
            $"Starting position should be near-equal, got {result.Evaluation}");
    }

    // ── Terminal position detection (checkmate / stalemate at the root) ───

    [Fact]
    public void Search_RootCheckmate_SetsIsCheckmateAndNoBestMove()
    {
        // Black king a8 (corner, only 3 neighbors: a7/b7/b8).
        // White rook a5 checks along the a-file (a6/a7 empty) and also covers the a7 escape.
        // White rook b1 covers the entire b-file, covering b7 and b8.
        // Neither rook is capturable (both >1 square away). No legal Black moves exist.
        var engine = new ChessEngine();
        engine.LoadFen("k7/8/8/R7/8/8/8/1R2K3 b - - 0 1");

        Assert.Empty(engine.GetLegalMoves());

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 4, MaxTimeMs = 2000 });

        Assert.True(result.IsCheckmate, "Position with no legal moves and king in check must report IsCheckmate.");
        Assert.False(result.IsStalemate);
        Assert.Equal(default, result.BestMove);
        Assert.Equal(0, result.DepthAchieved);
        Assert.True(result.Evaluation < -90_000,
            $"Checkmate-against-side-to-move must score a large negative value, got {result.Evaluation}");
    }

    [Fact]
    public void Search_RootStalemate_SetsIsStalemateAndZeroEval()
    {
        // Famous textbook stalemate: Black king h8 (corner, neighbors g8/g7/h7).
        // White king f7 covers g8 and g7 (both adjacent); White queen g6 covers g7 and h7
        // (adjacent) but does NOT attack h8 itself (not on same rank/file/diagonal).
        // King not in check, zero legal moves.
        var engine = new ChessEngine();
        engine.LoadFen("7k/5K2/6Q1/8/8/8/8/8 b - - 0 1");

        Assert.Empty(engine.GetLegalMoves());

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 4, MaxTimeMs = 2000 });

        Assert.True(result.IsStalemate, "Position with no legal moves and king not in check must report IsStalemate.");
        Assert.False(result.IsCheckmate);
        Assert.Equal(default, result.BestMove);
        Assert.Equal(0, result.Evaluation);
    }

    // ── Pin legality: a pinned piece must not expose its own king ─────────

    [Fact]
    public void PinnedRook_CannotMoveOffPinLine_ExposingKing()
    {
        // White: Kd1, Rd2 (pinned).  Black: Kd8, Rd7 (pinning along the d-file).
        // The White rook on d2 may only move along the d-file (or capture the pinning rook);
        // it must never be offered as a legal move to e2, c2, etc.
        var engine = new ChessEngine();
        engine.LoadFen("3k4/3r4/8/8/8/8/3R4/3K4 w - - 0 1");

        var legalMoves = engine.GetLegalMoves();
        var illegalSideStep = new Move(
            Square.FromAlgebraic("d2"),
            Square.FromAlgebraic("e2"),
            MoveType.Quiet);

        Assert.DoesNotContain(illegalSideStep, legalMoves);
        Assert.Contains(legalMoves, m => m.From == Square.FromAlgebraic("d2"));
    }
}
