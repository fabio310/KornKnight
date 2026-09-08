namespace ChessBot.Tests;

using ChessBot.Engine;
using ChessBot.Engine.Board;
using ChessBot.Engine.Search;
using ChessBot.Engine.Types;
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

        for (int i = 0; i < count; i++)
        {
            var m = moves[i];
            string s = $"{m.From}{m.To}";
            if ((m.MoveType & MoveType.Promotion) != 0)
                s += char.ToLowerInvariant(m.PromotionType.ToString()[0]);
            if (s == uci) return m;
        }

        Assert.Fail($"move {uci} is not legal in this position");
        return default;
    }

    private static int See(string fen, string uci)
    {
        var (ordering, board) = Setup(fen);
        return ordering.StaticExchangeEvaluation(Find(board, uci));
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
}
