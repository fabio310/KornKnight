namespace ChessBot.Tests;

using ChessBot.MatchRunner;
using Xunit;

public class MatchResultDocumentRoundTripTests
{
    [Fact]
    public void WriteThenRead_PreservesSchemaVersionAndSummaryFields()
    {
        var outcome = new MatchOutcome
        {
            OpponentName = "TestEngine 1.0",
            Wins   = 3,
            Draws  = 1,
            Losses = 2,
        };

        var game = new GameResult
        {
            GameNumber      = 1,
            ChessBotIsWhite = true,
            Outcome         = GameOutcome.ChessBotWin,
            InitialFen      = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            PgnPath         = "game1.pgn",
        };
        game.Moves.Add(new MoveRecord
        {
            MoveNumber     = 1,
            IsWhiteMove    = true,
            IsChessBotMove = true,
            UciMove        = "e2e4",
            Fen            = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1",
            Depth          = 12,
            ScoreCp        = 35,
            ScoreBound     = "exact",
            Nodes          = 123456,
            Nps            = 1000000,
            ElapsedMs      = 123,
            Pv             = "e2e4 e7e5",
        });
        outcome.Games.Add(game);

        string tempFile = Path.Combine(Path.GetTempPath(), $"matchresult_{Guid.NewGuid():N}.json");
        try
        {
            MatchResultWriter.Write(outcome, tempFile);
            var roundTripped = MatchResultWriter.Read(tempFile);

            Assert.Equal(MatchResultDocument.CurrentSchemaVersion, roundTripped.SchemaVersion);
            Assert.Equal(outcome.OpponentName, roundTripped.OpponentName);
            Assert.Equal(outcome.Wins, roundTripped.Wins);
            Assert.Equal(outcome.Draws, roundTripped.Draws);
            Assert.Equal(outcome.Losses, roundTripped.Losses);
            Assert.Equal(outcome.ScoreRate, roundTripped.ScoreRate, precision: 10);

            Assert.Single(roundTripped.Games);
            var rtGame = roundTripped.Games[0];
            Assert.Equal(game.GameNumber, rtGame.GameNumber);
            Assert.Equal(game.ChessBotIsWhite, rtGame.ChessBotIsWhite);
            Assert.Equal(game.Outcome.ToString(), rtGame.Outcome);
            Assert.Equal(game.InitialFen, rtGame.InitialFen);
            Assert.Equal(game.PgnPath, rtGame.PgnPath);

            Assert.Single(rtGame.Moves);
            var rtMove = rtGame.Moves[0];
            var originalMove = game.Moves[0];
            Assert.Equal(originalMove.MoveNumber, rtMove.MoveNumber);
            Assert.Equal(originalMove.UciMove, rtMove.UciMove);
            Assert.Equal(originalMove.Fen, rtMove.Fen);
            Assert.Equal(originalMove.Depth, rtMove.Depth);
            Assert.Equal(originalMove.ScoreCp, rtMove.ScoreCp);
            Assert.Equal(originalMove.ScoreBound, rtMove.ScoreBound);
            Assert.Equal(originalMove.Nodes, rtMove.Nodes);
            Assert.Equal(originalMove.Pv, rtMove.Pv);
        }
        finally
        {
            if (File.Exists(tempFile))
                File.Delete(tempFile);
        }
    }

    [Fact]
    public void CurrentSchemaVersion_IsStableAndPositive()
    {
        Assert.True(MatchResultDocument.CurrentSchemaVersion >= 1);
    }
}
