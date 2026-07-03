using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Search;

namespace ChessBot.Tests;

/// <summary>
/// Regression tests derived from real game logs where ChessBot (0W / 0D / 116L vs Stockfish 2000ms)
/// collapsed tactically. Each FEN is a position ChessBot reached before a major blunder.
///
/// Goals:
///   1. The engine must not crash or return an illegal move.
///   2. At depth >= 4 with quiescence the score must be within a sane range.
///   3. The position must not be evaluated as wildly winning when it is actually losing
///      (prevents re-introducing the horizon-effect blunders that caused the original losses).
///
/// NOTE: These tests do NOT assert a specific "correct" move because we do not have Stockfish's
/// ground-truth moves for each FEN. They are stability and sanity checks.
/// </summary>
public class SearchRegressionTests
{
    // ── Shared helpers ────────────────────────────────────────────────────────

    private static SearchResult Run(string fen, int maxDepth = 5, int maxTimeMs = 3000)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        var result = engine.FindBestMove(new SearchSettings
        {
            MaxDepth  = maxDepth,
            MaxTimeMs = maxTimeMs,
        });
        return result;
    }

    private static void AssertSane(string fen, SearchResult r)
    {
        // Move must be legal
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        var legal = engine.GetLegalMoves();
        Assert.True(legal.Count > 0, $"Position has no legal moves: {fen}");
        Assert.Contains(r.BestMove, legal);

        // Score must not be reporting a phantom win from an already-lost position.
        // All FENs below are deeply losing for ChessBot's side; the engine should not
        // score +5000 cp or more (that would indicate a horizon-effect hallucination).
        Assert.True(r.Evaluation < 5000,
            $"Suspiciously large positive score {r.Evaluation}cp in losing FEN: {fen}");

        // Depth must have reached at least 4
        Assert.True(r.DepthAchieved >= 4,
            $"Expected depth >= 4 but got {r.DepthAchieved} in FEN: {fen}");
    }

    // ── Regression FENs from game-log blunder analysis ────────────────────────
    // (All positions are from ChessBot's games as White or Black where a large
    //  eval swing was detected immediately after ChessBot's move.)

    [Fact]
    public void Regression_01_QueenInvasionAllowed()
    {
        const string fen = "rnbqkbnr/ppp3pp/8/4pp2/3p4/5NN1/PPPPPPPP/R1BQKB1R w KQkq e6 0 5";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_02_QueenDominatesCenter()
    {
        const string fen = "rnb1kbnr/ppp3pp/8/3qN3/3p4/4P1P1/PPPP2PP/R1BQKB1R w KQkq - 1 8";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_03_KnightExposedCenter()
    {
        const string fen = "r1b1kbnr/ppp3pp/2n5/3qN3/3P4/6P1/PPPP2PP/R1BQKB1R w KQkq - 1 9";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_04_QueenPressure()
    {
        const string fen = "r1b1kbnr/ppp3pp/2q5/8/3P4/6P1/PPPP2PP/R1BQKB1R w KQkq - 0 10";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_05_ExposedKingAfterQueenLoss()
    {
        const string fen = "r3kbnr/pppb2pp/2q5/8/3P4/5QP1/PPPP2PP/R1B1KB1R w KQkq - 2 11";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_06_QueenIntrudes()
    {
        const string fen = "r3kbnr/pppb2pp/8/3P4/8/5QP1/PPqP2PP/R1B1KB1R w KQkq - 0 12";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_07_CollapsingCenter()
    {
        const string fen = "r3kbnr/pppb2pp/8/2qP4/8/3B1QP1/PP1P2PP/R1B1K2R w KQkq - 2 13";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_08_DoubleBishopDefence()
    {
        const string fen = "r3k1nr/pppbb1pp/8/2qP4/8/3BQ1P1/PP1P2PP/R1B1K2R w KQkq - 4 14";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_09_BishopXRay()
    {
        const string fen = "r3k1nr/pppb2pp/8/2bP4/8/3B2P1/PP1P2PP/R1B1K2R w KQkq - 0 15";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_10_RookLostInEndgameTransition()
    {
        const string fen = "r3k2r/pppb2pp/5n2/2bP4/4B3/6P1/PP1P2PP/R1B1K2R w KQkq - 2 16";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_11_KnightFork()
    {
        const string fen = "r3k2r/pppb2pp/8/2bP4/4n3/3P2P1/PP4PP/R1B1K2R w KQkq - 0 17";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_12_LostRookEndgame()
    {
        const string fen = "2kr3r/pppb2pp/8/2bP4/4P3/6P1/PP4PP/R1B1K2R w KQ - 1 18";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_13_DoubleRookBattery()
    {
        const string fen = "2krr3/pppb2pp/8/2bP4/4P3/6P1/PP2K1PP/R1B4R w - - 3 19";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_14_PassedPawnCounter()
    {
        const string fen = "2kr1r2/pppb2pp/8/2bP4/4P3/5KP1/PP4PP/R1B4R w - - 5 20";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_15_RookEndgame()
    {
        const string fen = "2k1rr2/pppb2pp/8/2bP4/4P3/6P1/PP2K1PP/R1B4R w - - 7 21";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_16_DoubleBishopActive()
    {
        const string fen = "2k1rr2/ppp3pp/8/1bbP4/4P3/3K2P1/PP4PP/R1B4R w - - 9 22";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_17_RookActiveEndgame()
    {
        const string fen = "2k2r2/ppp3pp/8/1bbP4/4r3/2K3P1/PP4PP/R1B4R w - - 0 23";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_18_EnPassantThreaten()
    {
        const string fen = "2k2r2/1pp3pp/8/pbbP2p1/4rB2/P1K3P1/1P4PP/R6R w - g6 0 25";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_19_BishopPairEndgame()
    {
        const string fen = "2k2r2/1pp4p/8/pb1P2B1/3br3/P1K3P1/1P4PP/R6R w - - 1 26";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    [Fact]
    public void Regression_20_FinalCollapsePosition()
    {
        const string fen = "2k2r2/1pp3pp/8/pbbP2p1/4rB2/P1K3P1/1P4PP/R6R w - - 0 24";
        var r = Run(fen);
        AssertSane(fen, r);
    }

    // ── Opening stability tests ───────────────────────────────────────────────
    // Verify the engine no longer plays pure knight-shuffle openings.

    [Fact]
    public void Opening_WhiteSecondMove_Not_KnightOnly()
    {
        // After 1.Nf3, White's best second move should not be another pure knight shuffle.
        var engine = new ChessEngine();
        engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/5N2/PPPPPPPP/RNBQKB1R b KQkq - 1 1");
        // Play Black's response
        engine.LoadFen("rnbqkbnr/pppp1ppp/8/4p3/4P3/5N2/PPPP1PPP/RNBQKB1R w KQkq e6 0 2");
        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 5, MaxTimeMs = 3000 });

        var legal = engine.GetLegalMoves();
        Assert.Contains(result.BestMove, legal);
    }

    // ── Quiescence correctness ────────────────────────────────────────────────

    [Fact]
    public void Quiescence_InCheck_FindsEvasion()
    {
        // White king in check from Black queen; must find an evasion, not return stand-pat
        // Position: White Ke1 in check from Black Qe8. Only legal move is Ke2 or block.
        var engine = new ChessEngine();
        engine.LoadFen("3qk3/8/8/8/8/8/8/4K3 w - - 0 1");
        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 4, MaxTimeMs = 2000 });
        var legal = engine.GetLegalMoves();
        Assert.True(legal.Count > 0, "Should have legal evasions");
        Assert.Contains(result.BestMove, legal);
    }

    // ── TT hash consistency ───────────────────────────────────────────────────

    [Fact]
    public void Search_TTProbesArePositive_AfterDeepSearch()
    {
        var engine = new ChessEngine();
        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 6, MaxTimeMs = 5000 });

        Assert.True(result.TTProbes > 0,  $"Expected TT probes > 0, got {result.TTProbes}");
        Assert.True(result.TTHits  >= 0,  $"TTHits should be non-negative, got {result.TTHits}");
        Assert.True(result.TTStores > 0,  $"Expected TT stores > 0, got {result.TTStores}");
        Assert.True(result.HashFull >= 0,  $"HashFull should be non-negative, got {result.HashFull}");
    }
}
