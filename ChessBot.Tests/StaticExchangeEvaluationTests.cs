namespace ChessBot.Tests;

using ChessBot.Engine;
using ChessBot.Engine.Board;
using ChessBot.Engine.Search;
using ChessBot.Engine.Types;
using ChessBot.Uci;
using Xunit;

/// <summary>
/// SEE answers one question without searching: if both sides keep capturing on a square, taking
/// with their least valuable attacker each time and stopping when continuing would cost them,
/// what does the exchange net the side that starts it? Move ordering needs it to tell a winning
/// capture from a losing one — without it every capture is "good" and QxP defended by a pawn is
/// searched in full ahead of every quiet move.
/// </summary>
public class StaticExchangeEvaluationTests
{
    private static (MoveOrdering ordering, Board board) Setup(string fen)
    {
        var engine = new ChessEngine();
        engine.LoadFen(fen);
        var board = engine.GetBoardSnapshot();
        return (new MoveOrdering(board), board);
    }

    /// <summary>Finds the generated legal move matching a long-algebraic string.</summary>
    private static Move Find(Board board, string uci)
    {
        var gen   = new MoveGenerator(board);
        var moves = new Move[MoveGenerator.MaxMoves];
        gen.GenerateLegalMovesInto(moves, out int count);

        // Rendered by the one routine that defines the wire format, not by a local approximation
        // of it. Spelling a promotion as the piece name's first letter — which is what stood here —
        // writes a knight as "k", so "b7a8n" matches nothing and "b7a8k" matches a move that does
        // not exist in chess. The same shortcut in a match harness cost two games out of ~380
        // before it was traced, because it only ever misfires on an underpromotion to a knight.
        for (int i = 0; i < count; i++)
        {
            if (UciMoveNotation.Format(moves[i]) == uci) return moves[i];
        }

        Assert.Fail($"move {uci} is not legal in this position");
        return default;
    }

    private static int See(string fen, string uci)
    {
        var (ordering, board) = Setup(fen);
        return ordering.StaticExchangeEvaluation(Find(board, uci));
    }

    private static bool SeeGe(string fen, string uci, int threshold)
    {
        var (ordering, board) = Setup(fen);
        return ordering.SeeGe(Find(board, uci), threshold);
    }

    // ── The five cases that distinguish a real SEE from the victim's value ────

    [Fact]
    public void QueenTakesPawnDefendedByPawn_IsNegative()
        => Assert.Equal(100 - 900, See("4k3/8/2p5/3p4/8/8/8/3QK3 w - - 0 1", "d1d5"));

    [Fact]
    public void RookTakesQueenDefendedByKing_IsPositive()
        => Assert.Equal(900 - 500, See("qk6/8/8/8/8/8/8/R3K3 w - - 0 1", "a1a8"));

    [Fact]
    public void UndefendedCapture_EqualsTheVictimsValue()
        => Assert.Equal(100, See("7k/3p4/8/8/8/8/8/3RK3 w - - 0 1", "d1d7"));

    [Fact]
    public void CaptureDefendedOnlyByAMoreValuablePiece_IsPositive()
        => Assert.Equal(320 - 100, See("q6k/8/8/3n4/4P3/8/8/4K3 w - - 0 1", "e4d5"));

    [Fact]
    public void EqualTrade_IsZero()
        => Assert.Equal(0, See("r6r/8/8/8/8/8/8/R3K2k w - - 0 1", "a1a8"));

    // ── The parts of a swap-off that a one-step check cannot express ─────────

    /// <summary>
    /// Rd3xd5 exd5 Rd1xd5: the second rook only attacks d5 once the first has left d3, so a SEE
    /// that does not re-scan through vacated squares misses it and calls this a lost rook.
    /// </summary>
    [Fact]
    public void XrayAttackerBehindTheCapturingRook_IsCounted()
        => Assert.Equal(100 - 500 + 100, See("7k/8/4p3/3p4/8/3R4/8/3RK3 w - - 0 1", "d3d5"));

    /// <summary>
    /// The en-passant victim stands beside the target square, so vacating it opens the file the
    /// black rook attacks d6 along. Miss that and this reads as a free pawn.
    /// </summary>
    [Fact]
    public void EnPassant_RemovesTheVictimFromItsOwnSquare()
        => Assert.Equal(0, See("7k/8/8/3pP3/3r4/8/8/4K3 w - d6 0 1", "e5d6"));

    /// <summary>A capture-promotion banks the victim plus the promotion premium.</summary>
    [Fact]
    public void CapturePromotion_CountsTheVictimAndThePromotionGain()
        => Assert.Equal(500 + (900 - 100), See("r6k/1P6/8/8/8/8/8/4K3 w - - 0 1", "b7a8q"));

    /// <summary>
    /// The same capture-promotion, underpromoting. Present because the promotion piece is the one
    /// part of a UCI move that is not derivable from the squares, so every helper that renders a
    /// move has to get it right — and a knight is where they stop doing so.
    /// </summary>
    [Fact]
    public void CapturePromotionToAKnight_CountsTheKnightsPremium()
        => Assert.Equal(500 + (320 - 100), See("r6k/1P6/8/8/8/8/8/4K3 w - - 0 1", "b7a8n"));

    // ── What the classification one level up is for ──────────────────────────

    /// <summary>
    /// MVV-LVA alone prefers QxR (a 4,100 point victim/attacker ratio) over axb (900), so with
    /// every capture classified as good, a queen that loses 400 to a defended rook is searched
    /// ahead of a pawn that wins one for free. Only a real SEE separates them.
    /// </summary>
    [Fact]
    public void ALosingCaptureIsOrderedBehindAWinningOne()
    {
        const string fen = "7k/8/4p3/1p1r4/P7/8/8/3QK3 w - - 0 1";
        var (ordering, board) = Setup(fen);

        var gen   = new MoveGenerator(board);
        var moves = new Move[MoveGenerator.MaxMoves];
        gen.GenerateLegalMovesInto(moves, out int count);

        ordering.OrderMoves(moves, count, default, default, 0);

        int losing = -1, winning = -1;
        for (int i = 0; i < count; i++)
        {
            if (moves[i].From == Square.FromAlgebraic("d1") && moves[i].To == Square.FromAlgebraic("d5")) losing  = i;
            if (moves[i].From == Square.FromAlgebraic("a4") && moves[i].To == Square.FromAlgebraic("b5")) winning = i;
        }

        Assert.True(losing  >= 0, "Qxd5 was not generated");
        Assert.True(winning >= 0, "axb5 was not generated");
        Assert.True(winning < losing,
            $"axb5 (SEE +100) must be ordered before Qxd5 (SEE -400); got axb5 at {winning}, Qxd5 at {losing}");
    }

    // ── The threshold form that move ordering actually runs ──────────────────
    //
    // Ordering only ever needed the sign, and paid for the whole exchange to get it. SeeGe stops
    // as soon as the balance can no longer cross the bound, so the guarantee that has to hold is
    // that stopping early never changes the answer: on every case above, and over a whole corpus
    // of real positions, its verdict must be exactly "the full exchange clears the threshold".

    [Theory]
    [InlineData("4k3/8/2p5/3p4/8/8/8/3QK3 w - - 0 1",   "d1d5")]  // Qxd5, defended by a pawn
    [InlineData("qk6/8/8/8/8/8/8/R3K3 w - - 0 1",       "a1a8")]  // Rxa8, defended only by the king
    [InlineData("7k/3p4/8/8/8/8/8/3RK3 w - - 0 1",      "d1d7")]  // Rxd7, undefended
    [InlineData("q6k/8/8/3n4/4P3/8/8/4K3 w - - 0 1",    "e4d5")]  // exd5, defended by a queen
    [InlineData("r6r/8/8/8/8/8/8/R3K2k w - - 0 1",      "a1a8")]  // Rxa8, an even trade
    [InlineData("7k/8/4p3/3p4/8/3R4/8/3RK3 w - - 0 1",  "d3d5")]  // Rxd5 with a rook behind it
    [InlineData("7k/8/8/3pP3/3r4/8/8/4K3 w - d6 0 1",   "e5d6")]  // exd6 e.p., opening the file
    [InlineData("r6k/1P6/8/8/8/8/8/4K3 w - - 0 1",      "b7a8q")] // bxa8=Q
    public void SeeGeAgreesWithTheFullExchangeAtEveryThreshold(string fen, string uci)
    {
        int exact = See(fen, uci);

        // Straddle the true value: the verdict must flip exactly at it, nowhere else.
        foreach (int threshold in new[] { exact - 1, exact, exact + 1, -900, -100, 0, 100, 900 })
            Assert.Equal(exact >= threshold, SeeGe(fen, uci, threshold));
    }

    /// <summary>
    /// The early exit is only safe if it never changes a verdict, and the cases above are all
    /// hand-built. This runs both forms over every capture in several hundred positions from a
    /// seeded random walk, which is where a deep or unusual exchange that the hand-built cases
    /// miss actually turns up.
    /// </summary>
    [Fact]
    public void SeeGeAgreesWithTheFullExchangeOverACorpus()
    {
        var rng     = new Random(20260908);
        int checked_ = 0;

        for (int line = 0; line < 40; line++)
        {
            var engine = new ChessEngine();

            for (int ply = 0; ply < 40; ply++)
            {
                var legal = engine.GetLegalMoves();
                if (legal.Count == 0) break;

                var board    = engine.GetBoardSnapshot();
                var ordering = new MoveOrdering(board);

                foreach (var move in legal)
                {
                    if (!move.MoveType.IsTactical()) continue;

                    int exact = ordering.StaticExchangeEvaluation(move);
                    checked_++;

                    foreach (int threshold in new[] { exact - 1, exact, exact + 1, -100, 0, 100 })
                        Assert.True(
                            (exact >= threshold) == ordering.SeeGe(move, threshold),
                            $"SeeGe disagreed with SEE {exact} at threshold {threshold} " +
                            $"for {move.From}{move.To} in {engine.ExportFen()}");
                }

                engine.MakeMove(legal[rng.Next(legal.Count)]);
            }
        }

        Assert.True(checked_ > 500, $"corpus produced only {checked_} captures; too few to mean anything");
    }
}
