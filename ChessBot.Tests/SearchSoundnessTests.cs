using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Search;
using ChessBot.Engine.Types;

namespace ChessBot.Tests;

/// <summary>
/// Correctness gate for the search itself.
///
/// Alpha-beta is an exact algorithm: with a full window it must return the same value as
/// an exhaustive minimax of the same depth, whatever the move ordering. Any disagreement
/// is a search bug, not a tuning issue. Null-move, LMR and futility pruning are knowingly
/// unsound (they trade exactness for depth) and the check extension changes the shape of a
/// fixed-depth tree, so all of them are switched off via
/// <see cref="SearchSettings.PlainAlphaBeta"/> for this comparison.
///
/// The reference below deliberately re-implements the leaf and terminal conventions of
/// <c>Searcher</c> — <c>EvaluateFast</c> at the horizon, <c>-MATE_SCORE + ply</c> for
/// checkmate, 0 for stalemate — so that a mismatch can only come from the pruning logic.
/// </summary>
public class SearchSoundnessTests
{
    private const int MateScore = 100_000;
    private const int Infinity  = MateScore + 1;

    // A spread of position types: opening, tactical middlegame, castling-rich, promotion,
    // endgame, and an in-check position.
    public static TheoryData<string> Positions => new()
    {
        "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
        "r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R b KQkq - 3 3",
        "r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1",
        "8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
        "rnbq1k1r/pp1Pbppp/2p5/8/2B5/8/PPP1NnPP/RNBQK2R w KQ - 1 8",
        "r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10",
        "4k3/8/8/8/8/8/4P3/4K3 w - - 0 1",
        "rnb1kbnr/pppp1ppp/8/4p3/6Pq/5P2/PPPPP2P/RNBQKBNR w KQkq - 1 3",
    };

    [Theory]
    [MemberData(nameof(Positions))]
    public void PlainAlphaBeta_EqualsExhaustiveMinimax_Depth3(string fen)
        => AssertMatchesMinimax(fen, depth: 3);

    [Theory]
    [MemberData(nameof(Positions))]
    public void PlainAlphaBeta_EqualsExhaustiveMinimax_Depth4(string fen)
        => AssertMatchesMinimax(fen, depth: 4);

    private static void AssertMatchesMinimax(string fen, int depth)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);

        var result = engine.FindBestMove(SearchSettings.PlainAlphaBeta(depth));

        var board     = engine.GetBoardSnapshot();
        var evaluator = new Evaluator();
        int expected  = Minimax(board, evaluator, depth, ply: 0);

        Assert.Equal(expected, result.Evaluation);
    }

    /// <summary>Exhaustive negamax with no pruning of any kind.</summary>
    private static int Minimax(Engine.Board.Board board, Evaluator evaluator, int depth, int ply)
    {
        var moveGen = new Engine.Board.MoveGenerator(board);
        var checker = new Engine.Board.CheckDetector(board);

        var moves = new Move[Engine.Board.MoveGenerator.MaxMoves];
        moveGen.GenerateLegalMovesInto(moves, out int count);

        if (count == 0)
            return checker.IsInCheck(board.State.ActiveColor) ? -MateScore + ply : 0;

        if (depth <= 0)
            return evaluator.EvaluateFast(board);

        int best = -Infinity;
        for (int i = 0; i < count; i++)
        {
            board.MakeMove(moves[i]);
            int score = -Minimax(board, evaluator, depth - 1, ply + 1);
            board.UndoMove();
            if (score > best) best = score;
        }
        return best;
    }

    /// <summary>
    /// The root move must always be legal. A search that returns an illegal move corrupts
    /// the game regardless of how good its score looks.
    /// </summary>
    [Theory]
    [MemberData(nameof(Positions))]
    public void SearchReturnsLegalRootMove(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 5 });
        if (result.BestMove == default) return; // terminal position

        Assert.Contains(engine.GetLegalMoves(), m => m == result.BestMove);
    }

    /// <summary>
    /// Every move of the principal variation must be legal in sequence. An illegal PV means
    /// the PV table is being populated from a position other than the one it claims.
    /// </summary>
    [Theory]
    [MemberData(nameof(Positions))]
    public void PrincipalVariationIsFullyLegal(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 6 });

        int applied = 0;
        foreach (var move in result.PrincipalVariation)
        {
            Assert.Contains(engine.GetLegalMoves(), m => m == move);
            engine.MakeMove(move);
            applied++;
        }

        for (int i = 0; i < applied; i++) engine.UndoMove();
    }

    /// <summary>
    /// A node-limited search must be reproducible down to the node count. Unlike a time
    /// limit — where the same position yields a different depth on a busier machine — a node
    /// budget makes two search configurations comparable without timing noise, which is what
    /// any A/B measurement of a search change depends on.
    /// </summary>
    [Theory]
    [MemberData(nameof(Positions))]
    public void FixedNodeSearchIsDeterministic(string fen)
    {
        var a = new ChessEngine(); a.LoadFen(fen);
        var b = new ChessEngine(); b.LoadFen(fen);

        var settings = () => new SearchSettings { MaxNodes = 200_000, MaxTimeMs = 600_000 };

        var ra = a.FindBestMove(settings());
        var rb = b.FindBestMove(settings());

        Assert.Equal(ra.NodesSearched, rb.NodesSearched);
        Assert.Equal(ra.Evaluation,    rb.Evaluation);
        Assert.Equal(ra.BestMove,      rb.BestMove);
        Assert.Equal(ra.DepthAchieved, rb.DepthAchieved);
    }

    /// <summary>The node budget must actually bound the search, not merely be advisory.</summary>
    [Fact]
    public void FixedNodeSearchRespectsItsBudget()
    {
        var e = new ChessEngine();
        e.LoadFen("r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1");

        const long budget = 150_000;
        var r = e.FindBestMove(new SearchSettings { MaxNodes = budget, MaxTimeMs = 600_000 });

        // One node of overshoot is possible between the increment and the check.
        Assert.InRange(r.NodesSearched, 1, budget + 8);
    }

    /// <summary>
    /// A fixed-depth search with the transposition table disabled must be reproducible:
    /// same position, same settings, same score and move.
    /// </summary>
    [Theory]
    [MemberData(nameof(Positions))]
    public void FixedDepthSearchIsDeterministic(string fen)
    {
        var a = new ChessEngine(); a.LoadFen(fen);
        var b = new ChessEngine(); b.LoadFen(fen);

        var ra = a.FindBestMove(SearchSettings.PlainAlphaBeta(4));
        var rb = b.FindBestMove(SearchSettings.PlainAlphaBeta(4));

        Assert.Equal(ra.Evaluation, rb.Evaluation);
        Assert.Equal(ra.BestMove, rb.BestMove);
        Assert.Equal(ra.NodesSearched, rb.NodesSearched);
    }
}
