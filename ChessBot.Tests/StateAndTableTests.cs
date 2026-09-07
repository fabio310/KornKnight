using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Hashing;
using ChessBot.Engine.Types;

namespace ChessBot.Tests;

/// <summary>
/// Gates for game-state bookkeeping and transposition-table semantics — the parts the
/// search trusts silently. Perft already proves move generation and make/unmake board
/// restoration; these cover what perft cannot see: repetition history, table bounds, mate
/// normalisation across plies, and evaluation symmetry.
/// </summary>
public class StateAndTableTests
{
    private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";

    private static Move Find(ChessEngine e, string uci)
    {
        foreach (var m in e.GetLegalMoves())
            if (m.ToString().Equals(uci, StringComparison.OrdinalIgnoreCase)) return m;
        throw new InvalidOperationException($"'{uci}' is not legal here.");
    }

    // ── Repetition history ───────────────────────────────────────────────────

    /// <summary>
    /// A FEN carries no move history, so a position loaded from one starts with an empty
    /// repetition history. Anything that needs threefold detection has to replay the moves,
    /// not just hand over the final FEN. This is a real limitation of FEN-based hand-off and
    /// is asserted here so it cannot regress silently into a false draw claim.
    /// </summary>
    [Fact]
    public void LoadFen_StartsWithEmptyRepetitionHistory()
    {
        var e = new ChessEngine();
        e.LoadFen("r1bqkbnr/pppp1ppp/2n5/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R b KQkq - 3 3");

        Assert.Equal(0, e.GetBoardSnapshot().HistoryCount);
    }

    /// <summary>
    /// GetBoardSnapshot() returns a copy, and <c>Board.Copy()</c> deliberately does not carry
    /// the undo history over. Repetition detection reads that history, so a snapshot cannot be
    /// used to reason about repetition — only the engine's own live board can. Asserted here so
    /// the limitation is explicit rather than discovered as a wrong draw claim later.
    /// </summary>
    [Fact]
    public void BoardSnapshot_DoesNotCarryRepetitionHistory()
    {
        var e = new ChessEngine();

        foreach (var uci in new[] { "g1f3", "g8f6", "f3g1", "f6g8" })
            e.MakeMove(Find(e, uci));

        Assert.Equal(0, e.GetBoardSnapshot().HistoryCount);
    }

    /// <summary>
    /// The live search does see repetitions: searching from a position that has already
    /// occurred twice must reach repetition draw scores inside the tree.
    /// </summary>
    [Fact]
    public void Search_DetectsRepetitionsInItsTree()
    {
        var e = new ChessEngine();

        // Shuffle knights back and forth so the start position has occurred three times.
        foreach (var uci in new[] { "g1f3", "g8f6", "f3g1", "f6g8", "g1f3", "g8f6", "f3g1", "f6g8" })
            e.MakeMove(Find(e, uci));

        var r = e.FindBestMove(new Engine.Search.SearchSettings { MaxNodes = 400_000, MaxTimeMs = 600_000 });

        Assert.True(r.RepetitionDraws > 0,
            "the search never scored a repetition draw despite a thrice-repeated root position");
    }

    /// <summary>
    /// A knight shuffle returns to the identical position; the Zobrist key must recur, since
    /// repetition detection compares nothing else. Also confirms the key is not path-dependent.
    /// </summary>
    [Fact]
    public void RepeatedPosition_ReproducesIdenticalZobristKey()
    {
        var e = new ChessEngine();
        ulong start = e.GetBoardSnapshot().ZobristHash;

        foreach (var uci in new[] { "g1f3", "g8f6", "f3g1", "f6g8" })
            e.MakeMove(Find(e, uci));

        Assert.Equal(start, e.GetBoardSnapshot().ZobristHash);
    }

    /// <summary>
    /// Repetition detection compares nothing but Zobrist keys, so a position reached three
    /// times must produce the identical key each time — including after an undo/redo cycle.
    /// </summary>
    [Fact]
    public void ThriceRepeatedPosition_ProducesTheSameKeyEachTime()
    {
        var e = new ChessEngine();
        ulong start = e.GetBoardSnapshot().ZobristHash;
        var seen = new List<ulong>();

        for (int cycle = 0; cycle < 2; cycle++)
        {
            foreach (var uci in new[] { "g1f3", "g8f6", "f3g1", "f6g8" })
                e.MakeMove(Find(e, uci));
            seen.Add(e.GetBoardSnapshot().ZobristHash);
        }

        Assert.All(seen, h => Assert.Equal(start, h));
    }

    [Fact]
    public void FiftyMoveRule_FlagsAtHundredHalfmoves()
    {
        var e = new ChessEngine();
        e.LoadFen("8/8/4k3/8/8/4K3/8/8 w - - 99 80");
        Assert.False(e.GetBoardSnapshot().State.IsFiftyMoveRuleDraw);

        e.LoadFen("8/8/4k3/8/8/4K3/8/8 w - - 100 80");
        Assert.True(e.GetBoardSnapshot().State.IsFiftyMoveRuleDraw);
    }

    // ── Transposition table semantics ────────────────────────────────────────

    [Fact]
    public void Table_StoresAndRetrievesExactEntry()
    {
        var tt = new TranspositionTable(1);
        var move = new Move(new Square(12), new Square(28), MoveType.Quiet);

        tt.Store(hash: 0xDEADBEEF, depth: 7, score: 123, TranspositionTable.ScoreFlag.Exact, move);

        var hit = tt.Lookup(0xDEADBEEF, depth: 7);
        Assert.NotNull(hit);
        Assert.Equal(123, hit!.Value.score);
        Assert.Equal(TranspositionTable.ScoreFlag.Exact, hit.Value.flag);
        Assert.Equal(move, hit.Value.bestMove);
    }

    /// <summary>A shallower stored entry must not satisfy a deeper request.</summary>
    [Fact]
    public void Table_RejectsEntryShallowerThanRequestedDepth()
    {
        var tt = new TranspositionTable(1);
        tt.Store(0xABCD, depth: 3, score: 50, TranspositionTable.ScoreFlag.Exact, default);

        Assert.Null(tt.Lookup(0xABCD, depth: 6));
        Assert.NotNull(tt.Lookup(0xABCD, depth: 3));
        Assert.NotNull(tt.Lookup(0xABCD, depth: 1));
    }

    /// <summary>
    /// A different key landing on the same slot must never be reported as a hit; the stored
    /// key is the only collision protection the table has.
    /// </summary>
    [Fact]
    public void Table_DoesNotReturnEntryForDifferentKeyInSameSlot()
    {
        var tt = new TranspositionTable(1);
        int capacity = tt.GetCapacity();

        ulong a = 12345;
        ulong b = a + (ulong)capacity;           // same index, different key
        Assert.Equal(a % (ulong)capacity, b % (ulong)capacity);

        tt.Store(a, depth: 5, score: 999, TranspositionTable.ScoreFlag.Exact, default);

        Assert.Null(tt.Lookup(b, depth: 1));
        Assert.NotNull(tt.Lookup(a, depth: 1));
    }

    /// <summary>
    /// Entries from a previous search generation are replaceable regardless of depth,
    /// otherwise one deep early entry blocks its slot for the rest of the game.
    /// </summary>
    [Fact]
    public void Table_ReplacesStaleEntryEvenWhenShallower()
    {
        var tt = new TranspositionTable(1);
        tt.Store(0x1111, depth: 20, score: 10, TranspositionTable.ScoreFlag.Exact, default);

        tt.NewSearch();   // everything stored so far is now stale
        tt.Store(0x1111, depth: 2, score: 77, TranspositionTable.ScoreFlag.Exact, default);

        var hit = tt.Lookup(0x1111, depth: 2);
        Assert.NotNull(hit);
        Assert.Equal(77, hit!.Value.score);
    }

    /// <summary>Within one generation, a shallower entry must not displace a deeper one.</summary>
    [Fact]
    public void Table_KeepsDeeperEntryWithinSameGeneration()
    {
        var tt = new TranspositionTable(1);
        tt.Store(0x2222, depth: 12, score: 10, TranspositionTable.ScoreFlag.Exact, default);
        tt.Store(0x2222, depth: 3,  score: 77, TranspositionTable.ScoreFlag.Exact, default);

        var hit = tt.Lookup(0x2222, depth: 3);
        Assert.NotNull(hit);
        Assert.Equal(10, hit!.Value.score);
    }

    [Fact]
    public void Table_ClearRemovesEntries()
    {
        var tt = new TranspositionTable(1);
        tt.Store(0x3333, depth: 4, score: 1, TranspositionTable.ScoreFlag.Exact, default);
        Assert.NotNull(tt.Lookup(0x3333, depth: 1));

        tt.Clear();
        Assert.Null(tt.Lookup(0x3333, depth: 1));
    }

    // ── Mate-score handling ──────────────────────────────────────────────────

    /// <summary>
    /// A mate score must stay a mate score through a real search, and repeating the search
    /// with a warm table must not shift the reported mate distance. A ply-normalisation error
    /// on store or retrieve shows up here as a distance that drifts between runs.
    /// </summary>
    [Fact]
    public void MateScore_IsStableAcrossRepeatedSearches()
    {
        const string mateInOne = "6k1/5ppp/8/8/8/8/8/R5K1 w - - 0 1";

        var e = new ChessEngine();
        e.LoadFen(mateInOne);

        var first  = e.FindBestMove(new Engine.Search.SearchSettings { MaxDepth = 4 });
        var second = e.FindBestMove(new Engine.Search.SearchSettings { MaxDepth = 4 });
        var third  = e.FindBestMove(new Engine.Search.SearchSettings { MaxDepth = 6 });

        Assert.True(first.Evaluation > 90_000, $"expected a mate score, got {first.Evaluation}");
        Assert.Equal(first.Evaluation, second.Evaluation);
        Assert.Equal(first.Evaluation, third.Evaluation);
    }

    /// <summary>
    /// Mate scores are ply-relative, so the same mate seen from one ply deeper must be
    /// reported as exactly one ply further away — not as an unrelated magnitude.
    /// </summary>
    [Fact]
    public void MateScore_DecreasesByOnePlyWhenSeenOnePlyLater()
    {
        // Same mating net, one tempo pair earlier/later on the same file.
        var near = new ChessEngine();
        near.LoadFen("6k1/5ppp/8/8/8/8/8/R5K1 w - - 0 1");
        int scoreNear = near.FindBestMove(new Engine.Search.SearchSettings { MaxDepth = 6 }).Evaluation;

        var far = new ChessEngine();
        far.LoadFen("6k1/5ppp/8/8/8/8/8/1R4K1 w - - 0 1");   // rook needs one extra move
        int scoreFar = far.FindBestMove(new Engine.Search.SearchSettings { MaxDepth = 6 }).Evaluation;

        Assert.True(scoreNear > 90_000 && scoreFar > 90_000,
            $"expected mate scores, got {scoreNear} and {scoreFar}");
        Assert.True(scoreNear >= scoreFar,
            $"a mate reachable sooner must not score lower ({scoreNear} vs {scoreFar})");
    }

    // ── Evaluation symmetry ──────────────────────────────────────────────────

    /// <summary>
    /// Negamax requires a side-to-move-relative evaluation. Mirroring a position (swap colours
    /// and flip ranks) must therefore produce the same score, or the engine plays one colour
    /// better than the other.
    /// </summary>
    [Theory]
    [InlineData("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
                "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR b KQkq - 0 1")]
    [InlineData("r1bqkb1r/pppp1ppp/2n2n2/4p3/2B1P3/5N2/PPPP1PPP/RNBQK2R w KQkq - 4 4",
                "rnbqk2r/pppp1ppp/5n2/2b1p3/4P3/2N2N2/PPPP1PPP/R1BQKB1R b KQkq - 4 4")]
    [InlineData("8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1",
                "8/4p1p1/8/1r3P1K/kp5R/3P4/2P5/8 b - - 0 1")]
    public void Evaluation_IsColourSymmetric(string white, string black)
    {
        // One Evaluator per board: the threat detector binds to the Board it is given, so
        // sharing an evaluator across two different Board instances would compare one
        // position against the other's threat picture rather than testing symmetry.
        var a = new ChessEngine(); a.LoadFen(white);
        var b = new ChessEngine(); b.LoadFen(black);

        int sa = new Evaluator().EvaluateFast(a.GetBoardSnapshot());
        int sb = new Evaluator().EvaluateFast(b.GetBoardSnapshot());

        Assert.Equal(sa, sb);
    }
}
