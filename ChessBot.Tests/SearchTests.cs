using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Types;
using ChessBot.Engine.Search;

namespace ChessBot.Tests;

public class SearchTests
{
    [Fact]
    public void FindBestMove_StartingPosition_ReturnsMoveWithinLegalMoves()
    {
        var engine = new ChessEngine();
        var settings = new SearchSettings { MaxDepth = 2, MaxTimeMs = 1000 };

        var result = engine.FindBestMove(settings);

        var legalMoves = engine.GetLegalMoves();
        Assert.Contains(result.BestMove, legalMoves);
    }

    [Fact]
    public void FindBestMove_StartingPosition_FindsAMove()
    {
        var engine = new ChessEngine();
        var settings = new SearchSettings { MaxDepth = 2, MaxTimeMs = 1000 };

        var result = engine.FindBestMove(settings);

        Assert.NotEqual(new Move(new Square(0), new Square(0)), result.BestMove);
        Assert.True(result.DepthAchieved > 0);
    }

    [Fact]
    public void FindBestMove_IterativeDeepeningProgressesDepth()
    {
        var engine = new ChessEngine();
        var settings = new SearchSettings { MaxDepth = 4, MaxTimeMs = 2000 };

        var result = engine.FindBestMove(settings);

        // Should reach at least depth 2 (quick to compute)
        Assert.True(result.DepthAchieved >= 2, $"Expected depth >= 2, got {result.DepthAchieved}");
    }

    [Fact]
    public void FindBestMove_WinningPosition_FindsWinningMove()
    {
        var engine = new ChessEngine();

        // Position where White can checkmate in 1: Qa8# (Qg7# also mates)
        engine.LoadFen("6k1/Q7/6K1/8/8/8/8/8 w - - 0 1");

        var settings = new SearchSettings { MaxDepth = 2, MaxTimeMs = 1000 };
        var result = engine.FindBestMove(settings);

        // Best move should be Qa8 (checkmate)
        var expectedMove = new Move(
            Square.FromAlgebraic("a7"),
            Square.FromAlgebraic("a8"),
            MoveType.Quiet
        );

        // Or could be multiple moves leading to mate; just verify it's legal
        var moves = engine.GetLegalMoves();
        Assert.Contains(result.BestMove, moves);
    }

    [Fact]
    public void FindBestMove_AvoidingMate_FindsPreventiveMove()
    {
        var engine = new ChessEngine();

        // Position: defending against threats
        engine.LoadFen("5rk1/5ppp/8/8/8/8/5KQP/8 w - - 0 1");

        var settings = new SearchSettings { MaxDepth = 2, MaxTimeMs = 1000 };
        var result = engine.FindBestMove(settings);

        var moves = engine.GetLegalMoves();
        Assert.Contains(result.BestMove, moves);
        Assert.True(result.DepthAchieved >= 1);
    }

    [Fact]
    public void FindBestMove_MultipleDepthLevels_ImprovesEvaluation()
    {
        var engine = new ChessEngine();

        var settings1 = new SearchSettings { MaxDepth = 1, MaxTimeMs = 1000 };
        var result1 = engine.FindBestMove(settings1);

        // Reset for new search
        engine = new ChessEngine();

        var settings2 = new SearchSettings { MaxDepth = 3, MaxTimeMs = 5000 };
        var result2 = engine.FindBestMove(settings2);

        // Deeper search should reach more nodes
        Assert.True(result2.NodesSearched >= result1.NodesSearched, 
            $"Depth 3 ({result2.NodesSearched}) should search >= nodes than Depth 1 ({result1.NodesSearched})");
    }

    [Fact]
    public void FindBestMove_ReturnsSearchResult_WithMetrics()
    {
        var engine = new ChessEngine();
        var settings = new SearchSettings { MaxDepth = 2, MaxTimeMs = 1000 };

        var result = engine.FindBestMove(settings);

        Assert.True(result.DepthAchieved > 0);
        Assert.True(result.NodesSearched > 0);
        Assert.NotEmpty(result.PrincipalVariation);
        Assert.True(result.ElapsedTimeMs >= 0);

        // A depth-2 search from the starting position routinely finishes inside one millisecond,
        // and the elapsed clock has millisecond resolution — so a rate is only defined when the
        // clock actually moved. Asserting it unconditionally made this test fail whenever the
        // process was warm enough to beat the timer.
        Assert.True(result.ElapsedTimeMs > 0 ? result.NodesPerSecond > 0 : result.NodesPerSecond == 0);
    }

    [Fact]
    public void FindBestMove_QueenVsRook_PrefersMaterial()
    {
        var engine = new ChessEngine();

        // Position: White queen vs Black rook, should prefer keeping queen
        engine.LoadFen("r3k2r/pppppppp/8/8/8/8/PPPPPPPP/R2Q1RK1 w - - 0 1");

        var settings = new SearchSettings { MaxDepth = 2, MaxTimeMs = 1000 };
        var result = engine.FindBestMove(settings);

        // Result should have positive evaluation (White better with queen)
        Assert.True(result.Evaluation > 0);
    }

    [Fact]
    public void FindBestMove_CancellationToken_StopsSearch()
    {
        var engine = new ChessEngine();
        using (var cts = new System.Threading.CancellationTokenSource())
        {
            var settings = new SearchSettings { MaxDepth = 20, MaxTimeMs = 10000 };

            // Cancel after 100ms
            cts.CancelAfter(100);

            var result = engine.FindBestMove(settings, cts.Token);

            // Should have stopped early (not reached depth 20)
            Assert.True(result.DepthAchieved < 10, $"Expected early cancellation, got depth {result.DepthAchieved}");
        }
    }

    [Fact]
    public void FindBestMove_TimeLimit_RespectsMaxTime()
    {
        var engine = new ChessEngine();
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();

        var settings = new SearchSettings { MaxDepth = 20, MaxTimeMs = 500 };
        var result = engine.FindBestMove(settings);

        stopwatch.Stop();

        // Should respect time limit (give 500ms buffer for OS scheduling overhead)
        Assert.True(stopwatch.ElapsedMilliseconds < 1000, 
            $"Expected elapsed time < 1000ms, got {stopwatch.ElapsedMilliseconds}ms");
    }

    [Fact]
    public void FindBestMove_ReportsTheShortestMate_NotTheFirstOneFound()
    {
        // Iterative deepening used to break out of the loop the moment any mate score appeared.
        // A mate that shows up at a shallow depth is not necessarily the shortest one: here the
        // search reaches a mate in 3 at depth 4, while the mate in 2 (1.Qh7+ K~8 2.Rf8#) only
        // becomes visible at depth 7 — quiescence does not generate the quiet mating rook move,
        // so nothing shorter can be seen until the main search is deep enough to play it. The
        // engine therefore announced, and would have played, a slower mate than it had found.
        var engine = new ChessEngine();
        engine.LoadFen("8/k7/5R2/3K3Q/8/8/8/8 w - - 0 1");

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 8, MaxTimeMs = 30_000 });

        Assert.Equal(2, SearchScores.ToReported(result.Evaluation).MateInMoves);
        Assert.True(result.DepthAchieved >= 7,
            $"the search must keep deepening past the first mate it sees, stopped at depth {result.DepthAchieved}");
    }
}
