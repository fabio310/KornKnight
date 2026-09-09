using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Search;
using ChessBot.Engine.Types;
using ChessBot.Engine.Board;using ChessBot.Engine.Evaluation;using ChessBot.Engine.Hashing;

namespace ChessBot.Tests;

/// <summary>
/// Draw knowledge. Both defects here cost games rather than crashes: a dead-drawn ending scored
/// as a material advantage is played out for fifty moves, and a repetition the search cannot see
/// until the threefold is a saving perpetual the engine walks past.
/// </summary>
public class DrawDetectionTests
{
    /// <summary>
    /// White is a rook down and every black reply to both checks is forced, so the only line
    /// that does not lose is 1.Qe8+ Kh7 2.Qh5+ Kg8, repeating the root position. The second
    /// occurrence lands at ply 4; a threefold test would not see it until ply 8.
    /// </summary>
    private const string PerpetualFen = "6k1/6p1/8/7Q/8/r7/q4PPP/6K1 w - - 0 1";

    private static SearchResult Search(string fen, int depth, int ms = 10_000)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        return engine.FindBestMove(new SearchSettings { MaxDepth = depth, MaxTimeMs = ms });
    }

    private static bool Insufficient(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        return engine.GetBoardSnapshot().HasInsufficientMaterial;
    }

    // ── Insufficient material ──────────────────────────────────────────────

    [Theory]
    [InlineData("4k3/8/8/8/8/8/8/4K3 w - - 0 1")]      // K vs K
    [InlineData("4k3/8/8/8/8/8/8/4KB2 w - - 0 1")]     // K+B vs K
    [InlineData("4k3/8/8/8/8/8/8/4KN2 w - - 0 1")]     // K+N vs K
    [InlineData("4k1b1/8/8/8/8/8/8/4K3 w - - 0 1")]    // K vs K+B
    public void InsufficientMaterial_DeadDrawsAreRecognised(string fen)
    {
        Assert.True(Insufficient(fen));
        Assert.Equal(0, Search(fen, depth: 6).Evaluation);
    }

    [Theory]
    [InlineData("4k3/8/8/8/8/8/8/4KR2 w - - 0 1")]     // K+R vs K is a win
    [InlineData("4k3/8/8/8/8/8/4P3/4K3 w - - 0 1")]    // a pawn can promote
    [InlineData("4k3/8/8/8/8/8/8/3NKN2 w - - 0 1")]    // K+N+N: deliberately not claimed
    [InlineData("4k1b1/8/8/8/8/8/8/4KB2 w - - 0 1")]   // K+B vs K+B: deliberately not claimed
    public void InsufficientMaterial_IsNotClaimedForAnythingElse(string fen)
    {
        Assert.False(Insufficient(fen));
    }

    [Fact]
    public void InsufficientMaterial_KingAndRookVsKingIsStillPlayedAsAWin()
    {
        var result = Search("4k3/8/8/8/8/8/8/4KR2 w - - 0 1", depth: 6);

        Assert.True(result.Evaluation > 300,
            $"K+R vs K must still read as winning, got {result.Evaluation}");
    }

    [Fact]
    public void InsufficientMaterial_WinningAPieceIntoALoneMinorIsNotAnAdvantage()
    {
        // White Bc3 can take the knight on g7, which leaves K+B vs K. Scoring the surviving
        // bishop as material won is exactly how the engine talks itself into a dead ending.
        var result = Search("4k3/6n1/8/8/8/2B5/8/4K3 w - - 0 1", depth: 8);

        Assert.True(System.Math.Abs(result.Evaluation) < 50,
            $"trading into K+B vs K is a draw, got {result.Evaluation}");
    }

    // ── Repetition ─────────────────────────────────────────────────────────

    [Fact]
    public void Repetition_PerpetualIsFoundFourPliesEarlierThanAThreefoldWouldAllow()
    {
        // Depth 4 reaches the second occurrence and no further. Check extensions are off for
        // this one measurement only: every move in the line is a check, so with them on the
        // search reaches ply 8 anyway and the test would pass under either rule, proving
        // nothing. Under the threefold rule this position scores about -280 at every depth
        // through 8; under the first-repetition rule it is 0 from depth 4.
        var engine = new ChessEngine();
        engine.LoadFen(PerpetualFen);

        var result = engine.FindBestMove(new SearchSettings
        {
            MaxDepth           = 4,
            MaxTimeMs          = 10_000,
            UseCheckExtension  = false,
        });

        Assert.Equal(0, result.Evaluation);
        Assert.Equal(new Move(Square.FromAlgebraic("h5"), Square.FromAlgebraic("e8"), MoveType.Quiet),
                     result.BestMove);
    }

    [Fact]
    public void Repetition_RootStillRequiresAThreefold()
    {
        // K+R vs K, shuffled back to the starting position once. Inside the tree that second
        // occurrence is a draw; at the root it is not, and treating it as one would abandon a
        // won position without returning a move at all.
        var engine = new ChessEngine();
        engine.LoadFen("4k3/8/8/8/8/8/8/K6R w - - 0 1");

        engine.MakeMove(new Move(Square.FromAlgebraic("h1"), Square.FromAlgebraic("h2"), MoveType.Quiet));
        engine.MakeMove(new Move(Square.FromAlgebraic("e8"), Square.FromAlgebraic("e7"), MoveType.Quiet));
        engine.MakeMove(new Move(Square.FromAlgebraic("h2"), Square.FromAlgebraic("h1"), MoveType.Quiet));
        engine.MakeMove(new Move(Square.FromAlgebraic("e7"), Square.FromAlgebraic("e8"), MoveType.Quiet));

        var result = engine.FindBestMove(new SearchSettings { MaxDepth = 6, MaxTimeMs = 5_000 });

        Assert.Contains(result.BestMove, engine.GetLegalMoves());
        Assert.True(result.Evaluation > 300,
            $"the root position is still a win, got {result.Evaluation}");
    }

    // ── Draw checks versus the transposition table ─────────────────────────

    /// <summary>
    /// The Zobrist key covers the pieces, the side to move, castling rights and the en passant
    /// file. It carries neither the repetition history nor the halfmove clock, so one key can be
    /// a win in one game and a dead draw in another. A transposition probe that answers before
    /// the draw checks therefore hands back the stored winning score for a position that is in
    /// fact drawn — the engine repeats away a won game, or misses a saving perpetual.
    ///
    /// Exercised at the node rather than through FindBestMove on purpose: the table's score
    /// cutoff is skipped at PV nodes, and a forced repetition line *is* the principal variation,
    /// so a whole-search fixture takes the one path where the bug cannot show. This calls the
    /// node directly with a null window, which is where the cutoff actually fires.
    /// </summary>
    [Fact]
    public void DrawChecks_BeatAStoredTranspositionScoreForTheSameKey()
    {
        // K+R vs K: winning for White, and the table will be told so.
        var engine = new ChessEngine();
        engine.LoadFen("8/4k3/8/8/8/8/K6R/8 w - - 0 1");

        var board    = engine.GetBoardSnapshot();
        var searcher = new Searcher(board, new Evaluator(), new ZobristHasher());

        // Fill the table (and initialise the searcher's per-search state) from the position with
        // an empty history, so its key now carries a winning score.
        int stored = searcher.Search(new SearchSettings { MaxDepth = 6, MaxTimeMs = 5_000 }).Evaluation;
        Assert.True(stored > 300, $"fixture needs a winning entry under this key, got {stored}");

        // Walk the same position back onto the board as a repetition: a rook shuffle and a king
        // shuffle return every piece to where it started.
        foreach (var (from, to) in new[] { ("h2", "h1"), ("e7", "e8"), ("h1", "h2"), ("e8", "e7") })
            board.MakeMove(new Move(Square.FromAlgebraic(from), Square.FromAlgebraic(to), MoveType.Quiet));

        // Same key, now a repetition. A null window makes this a non-PV node, which is the only
        // kind the table is allowed to cut off.
        int score = searcher.NegamaxSearch(ply: 1, depth: 1, alpha: -1, beta: 0);

        Assert.Equal(0, score);
    }
}
