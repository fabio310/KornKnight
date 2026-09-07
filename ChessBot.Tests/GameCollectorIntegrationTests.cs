namespace ChessBot.Tests;

using ChessBot.EloEvaluator.Models;
using ChessBot.EloEvaluator.Parsing;
using Xunit;

/// <summary>
/// A MatchRunner output directory holds several kinds of artifact. Only per-game logs and PGNs
/// are games; feeding the others to the game parser produced extra entries that were then
/// reported as invalid games, quietly inflating the game count an Elo estimate is based on.
/// </summary>
public class GameCollectorIntegrationTests : IDisposable
{
    private readonly string _dir;

    public GameCollectorIntegrationTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"collector_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private void Write(string name, string content) =>
        File.WriteAllText(Path.Combine(_dir, name), content);

    /// <summary>Minimal per-game log in the shape LogFileParser expects.</summary>
    private static string GameLog(int number, string color, string result) => $"""
        === ChessBot Game Log ===
        Game         : {number}
        ChessBot     : {color}
        Opponent     : C:\engines\stockfish.exe
        Move time    : 1000 ms per move
        Result       : {result}
        Termination  : Checkmate
        Total plies  : 4  (2 ChessBot, 2 opponent)
        Date         : 2026-09-06 18:08:28

        === Move List ===

        Move 1 — {color} (ChessBot)
          Move        : e2e4
          Score       : +15cp  [exact]
          Depth       : 10  /  seldepth 20
          Nodes       : 1.000  |  NPS 1.000  |  Time 100 ms  |  TT fill 1,0%
          PV          : e2e4
          FEN before  : rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1
        """;

    private static string Pgn(int number) => $"""
        [Event "ChessBot vs Stockfish"]
        [Site "local"]
        [Date "2026.09.06"]
        [Round "{number}"]
        [White "ChessBot"]
        [Black "Stockfish"]
        [Result "0-1"]

        1. e4 e5 0-1
        """;

    /// <summary>
    /// With a readable structured result present it is the authority, and the text artifacts in
    /// the same directory — including the move-loss reports — are not parsed at all.
    /// </summary>
    [Fact]
    public void Collect_PrefersStructuredResultOverTextArtifacts()
    {
        Write("game1_20260906_180828.log", GameLog(1, "White", "ChessBotLoss"));
        Write("game1_20260906_180828.pgn", Pgn(1));
        Write("game1_20260906_180828.moveloss.log", "=== Move-Loss Report ===\n");
        Write("match_summary.log", "ChessBot Match Summary\n");

        var outcome = new MatchRunner.MatchOutcome { OpponentName = "Stockfish 18", Losses = 2, RunId = "r" };
        outcome.Games.Add(new MatchRunner.GameResult
        { GameNumber = 1, ChessBotIsWhite = true,  Outcome = MatchRunner.GameOutcome.ChessBotLoss });
        outcome.Games.Add(new MatchRunner.GameResult
        { GameNumber = 2, ChessBotIsWhite = false, Outcome = MatchRunner.GameOutcome.Draw });
        MatchRunner.MatchResultWriter.Write(outcome, Path.Combine(_dir, "match_result.json"));

        var games = GameCollector.Collect(_dir, announceCounts: false);

        Assert.Equal(2, games.Count);
        Assert.All(games, g => Assert.Equal("json", g.SourceType));
        Assert.Equal(new[] { 1, 2 }, games.Select(g => g.GameNumber).OrderBy(n => n));
        Assert.Equal("White", games.Single(g => g.GameNumber == 1).ChessBotColor);
        Assert.Equal(GameOutcomeKind.Draw, games.Single(g => g.GameNumber == 2).Outcome);
    }

    /// <summary>
    /// A structured result declaring a newer schema must not be guessed at: the collector falls
    /// back to text parsing rather than misreading fields it does not understand.
    /// </summary>
    [Fact]
    public void Collect_FallsBackToTextWhenStructuredResultIsUnreadable()
    {
        Write("game1_20260906_180828.log", GameLog(1, "White", "ChessBotLoss"));
        Write("match_result.json", "{\"SchemaVersion\":9999}");

        var games = GameCollector.Collect(_dir, announceCounts: false);

        Assert.Single(games);
        Assert.Equal("log", games[0].SourceType);
    }

    [Fact]
    public void Collect_MixedDirectory_CountsOnlyRealGamesExactlyOnce()
    {
        // Two real games, each with a log and a matching PGN.
        Write("game1_20260906_180828.log", GameLog(1, "White", "ChessBotLoss"));
        Write("game1_20260906_180828.pgn", Pgn(1));
        Write("game2_20260906_180951.log", GameLog(2, "Black", "ChessBotLoss"));
        Write("game2_20260906_180951.pgn", Pgn(2));

        // Artifacts that are NOT games and must be ignored.
        Write("game1_20260906_180828.moveloss.log", "=== Move-Loss Report ===\nmedian 12 cp\n");
        Write("game2_20260906_180951.moveloss.log", "=== Move-Loss Report ===\nmedian 20 cp\n");
        Write("match_summary.log", "ChessBot Match Summary\nResult: +0=0-2\n");
        Write("match_result.json", "{\"SchemaVersion\":2}");
        Write("run_manifest.json", "{\"RunId\":\"x\"}");
        Write("ab_report_lmr.log", "=== ChessBot Controlled A/B Harness Report ===\n");
        Write("some_unrelated.log", "nothing to do with chess\n");
        Write("build.log", "MSBuild output\n");

        // Text-parsing path: this test is about which *files* are games, so the structured
        // result is bypassed deliberately (its own preference is covered above).
        var games = GameCollector.Collect(_dir, verbose: false, announceCounts: false,
                                          preferStructuredResult: false);

        Assert.Equal(2, games.Count);
        Assert.Equal(new[] { 1, 2 }, games.Select(g => g.GameNumber).OrderBy(n => n));
        Assert.All(games, g => Assert.True(g.IsValid || g.ValidationErrors.Count > 0));

        // Nothing named like a move-loss report may appear as a source file.
        Assert.DoesNotContain(games, g => g.SourceFile.Contains("moveloss", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(games, g => g.SourceFile.Contains("match_summary", StringComparison.OrdinalIgnoreCase));
        Assert.DoesNotContain(games, g => g.SourceFile.Contains("ab_report", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Two runs on the same day both produce "game1" dated today, so date-plus-number
    /// deduplication cannot separate them. The run id (the artifact timestamp) can.
    /// </summary>
    [Fact]
    public void Collect_WithRunId_IgnoresStaleGamesFromAnEarlierRunTheSameDay()
    {
        Write("game1_20260906_180828.log", GameLog(1, "White", "ChessBotLoss"));   // earlier run
        Write("game2_20260906_180951.log", GameLog(2, "Black", "ChessBotLoss"));   // earlier run
        Write("game1_20260906_223000.log", GameLog(1, "White", "ChessBotWin"));    // current run
        Write("game2_20260906_223015.log", GameLog(2, "Black", "Draw"));           // current run

        var all = GameCollector.Collect(_dir, announceCounts: false, preferStructuredResult: false);
        Assert.Equal(4, all.Count);   // without a run id, every game log is a game

        var current = GameCollector.Collect(_dir, announceCounts: false, runId: "20260906_223",
                                            preferStructuredResult: false);
        Assert.Equal(2, current.Count);
        Assert.All(current, g => Assert.Contains("2230", g.SourceFile));
    }

    [Theory]
    [InlineData("game1_20260906_180828.log", true)]
    [InlineData("game12_20260906_180828.log", true)]
    [InlineData("game1_20260906_180828.moveloss.log", false)]
    [InlineData("match_summary.log", false)]
    [InlineData("ab_report_partial_root.log", false)]
    [InlineData("random.log", false)]
    [InlineData("game1.log", false)]
    public void IsGameLog_RecognisesOnlyPerGameLogs(string name, bool expected)
        => Assert.Equal(expected, GameCollector.IsGameLog(name));
}
