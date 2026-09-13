namespace ChessBot.Tests;

using ChessBot.Engine.Search;
using ChessBot.MatchRunner;
using ChessBot.Uci;
using Xunit;

/// <summary>
/// The search speaks in ply-relative mate scores (<c>±(Mate - plies)</c>). Everything that
/// reports a score to something outside the engine — the UCI "score" token, the plain-text game
/// log, the structured match result, the UI eval panel — has to turn that back into a mate
/// distance, and there must be exactly one rule doing it. Three copies of the rule existed, with
/// three different thresholds (99,936 in the score encoding, 99,000 in the game log, 90,000 in
/// the UI) and one call site with no rule at all, which is why the same move could be a mate in
/// one artifact and a 99,992-centipawn evaluation in another.
/// </summary>
public class ReportedScoreTests
{
    private static MoveRecord MoveWith(int scoreCp, int? scoreMate) => new()
    {
        MoveNumber = 1, IsWhiteMove = true, IsChessBotMove = true,
        UciMove = "e2e4", Fen = "startpos", Depth = 10,
        ScoreCp = scoreCp, ScoreMate = scoreMate,
        Nodes = 1, Nps = 1, ElapsedMs = 1, Pv = "e2e4",
    };

    // ── The single conversion ────────────────────────────────────────────────

    [Theory]
    // Mate for the side to move: Mate - plies, reported as full moves.
    [InlineData(SearchScores.Mate - 1, 1)]    // mate on the move
    [InlineData(SearchScores.Mate - 5, 3)]
    [InlineData(SearchScores.Mate - 8, 4)]
    // Mated: the same distances with the sign flipped. -99992 is the value the baseline run
    // recorded as a centipawn score on move 22.
    [InlineData(-(SearchScores.Mate - 1), -1)]
    [InlineData(-(SearchScores.Mate - 5), -3)]
    [InlineData(-99_992, -4)]
    public void ToReported_TurnsAMateScoreIntoASignedDistanceInMoves(int score, int expectedMate)
    {
        var reported = SearchScores.ToReported(score);

        Assert.Equal(expectedMate, reported.MateInMoves);
        Assert.Equal(0, reported.Cp);   // never leaves the raw constant in the centipawn field
    }

    [Theory]
    [InlineData(0)]
    [InlineData(35)]
    [InlineData(-1_200)]
    [InlineData(SearchScores.MateThreshold - 1)]      // the boundary, just outside the band
    [InlineData(-(SearchScores.MateThreshold - 1))]
    public void ToReported_LeavesEverythingBelowTheMateBandInCentipawns(int score)
    {
        var reported = SearchScores.ToReported(score);

        Assert.Null(reported.MateInMoves);
        Assert.Equal(score, reported.Cp);
    }

    [Fact]
    public void ToReported_TreatsTheThresholdItselfAsAMate()
    {
        Assert.NotNull(SearchScores.ToReported(SearchScores.MateThreshold).MateInMoves);
        Assert.NotNull(SearchScores.ToReported(-SearchScores.MateThreshold).MateInMoves);
    }

    // ── Every reporting surface reads from it ────────────────────────────────

    [Theory]
    [InlineData(-99_992, "mate -4")]
    [InlineData(SearchScores.Mate - 5, "mate 3")]
    [InlineData(35, "cp 35")]
    [InlineData(SearchScores.MateThreshold - 1, "cp 99743")]
    public void UciLayer_FormatsTheSameConversion(int score, string expected)
        => Assert.Equal(expected, UciSession.FormatScore(score));

    /// <summary>
    /// The game log's "Mate-scored moves" counter used its own 99,000 threshold against the raw
    /// centipawn field, so it disagreed with the score encoding on any value between the two
    /// thresholds — and, worse, agreed with it for the wrong reason on ChessBot's own mate moves,
    /// where the centipawn field was carrying a mate constant it should never have held.
    /// </summary>
    [Theory]
    [InlineData(99_500)]
    [InlineData(-99_500)]
    [InlineData(99_100)]
    public void GameLogMateCounter_AgreesWithTheScoreEncoding(int scoreCp)
        => Assert.Equal(SearchScores.IsMateScore(scoreCp),
                        PositionAnalyzer.IsMateScored(MoveWith(scoreCp, scoreMate: null)));

    [Fact]
    public void GameLogMateCounter_CountsAMoveThatCarriesAMateDistance()
        => Assert.True(PositionAnalyzer.IsMateScored(MoveWith(0, scoreMate: -4)));

    // ── Round trip through the structured result ─────────────────────────────

    [Fact]
    public void MatchResultSerializer_PreservesAConvertedMateScore()
    {
        var reported = SearchScores.ToReported(-99_992);

        var outcome = new MatchOutcome { OpponentName = "Stockfish" };
        var game    = new GameResult { GameNumber = 1, ChessBotIsWhite = true, Outcome = GameOutcome.ChessBotLoss };
        game.Moves.Add(MoveWith(reported.Cp, reported.MateInMoves));
        outcome.Games.Add(game);

        string path = Path.Combine(Path.GetTempPath(), $"reported_{Guid.NewGuid():N}.json");
        try
        {
            MatchResultWriter.Write(outcome, path);
            var back = MatchResultWriter.Read(path).Games.Single().Moves.Single();

            Assert.Equal(-4, back.ScoreMate);
            Assert.Equal(0,  back.ScoreCp);
        }
        finally
        {
            if (File.Exists(path)) File.Delete(path);
        }
    }
}
