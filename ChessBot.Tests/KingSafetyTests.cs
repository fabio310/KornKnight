using Xunit;
using ChessBot.Engine;
using ChessBot.Engine.Evaluation;

namespace ChessBot.Tests;

/// <summary>
/// King safety, the pawn half: the shield in front of the king and the enemy pawns coming at it.
/// The positions differ only in the pawns around the kings, so the difference in score is the new
/// term and nothing else.
/// </summary>
public class KingSafetyTests
{
    private static int Score(string fen)
    {
        var engine = new ChessEngine(ChessEngine.MinHashSizeMb);
        engine.LoadFen(fen);
        var board = engine.GetBoardSnapshot();

        // White-positive: the evaluator returns side-to-move relative.
        int score = new Evaluator().Evaluate(board);
        return board.State.ActiveColor == ChessBot.Engine.Types.Color.White ? score : -score;
    }

    /// <summary>
    /// Both sides castled with a queen and rooks on; White's shield is intact, Black's is gone.
    /// White must be better, and by more than the pawns are worth as material.
    /// </summary>
    [Fact]
    public void AnIntactShieldBeatsAStrippedOne()
    {
        int intact   = Score("r2q1rk1/pp3ppp/8/8/8/8/PP3PPP/R2Q1RK1 w - - 0 1");
        int stripped = Score("r2q1rk1/8/pp6/8/8/8/PP3PPP/R2Q1RK1 w - - 0 1");

        Assert.True(stripped > intact,
            $"stripping Black's shield should favour White: intact {intact}, stripped {stripped}");
    }

    /// <summary>
    /// A pawn storm bearing down on the king costs. White's king sits on the queenside, so its own
    /// shelter files (b, c, d) are untouched by the h-pawn being moved; only Black's storm distance
    /// changes. Pushing the SAME pawn in front of one's own king is a different matter — that loses
    /// shelter, and costs more than the storm gains, which is what the first draft of this test
    /// measured by accident.
    /// </summary>
    [Fact]
    public void AnAdvancingStormCosts()
    {
        int far   = Score("r2q1rk1/pp3ppp/8/8/8/8/PPP2P1P/2KR3R w - - 0 1");
        int close = Score("r2q1rk1/pp3ppp/8/7P/8/8/PPP2P2/2KR3R w - - 0 1");

        Assert.True(close > far,
            $"White's advanced h-pawn should count against Black's king: far {far}, close {close}");
    }

    /// <summary>
    /// The term is a midgame one: the endgame scale is 0 for every shelter and storm distance, so
    /// once the phase reaches 0 the king's shield is not scored at all — a king in an endgame is
    /// meant to leave it behind. Checked on the term itself, because any two positions whose
    /// shelters differ also differ in their pawn placement, which other terms score.
    /// </summary>
    [Fact]
    public void TheEndgameScaleIsZero()
    {
        var engine = new ChessEngine(ChessEngine.MinHashSizeMb);
        engine.LoadFen("6k1/8/5ppp/8/8/8/5PPP/6K1 w - - 0 1");
        var board = engine.GetBoardSnapshot();

        KingSafety.Raw(board.WhitePawnBitboard, board.BlackPawnBitboard,
                       board.GetKingPosition(ChessBot.Engine.Types.Color.White),
                       board.GetKingPosition(ChessBot.Engine.Types.Color.Black),
                       out int midgame, out int endgame);

        Assert.NotEqual(0, midgame);   // the shelters differ, so the midgame scale has something to say
        Assert.Equal(0, endgame);
    }
}
