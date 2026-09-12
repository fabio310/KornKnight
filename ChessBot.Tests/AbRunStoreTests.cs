namespace ChessBot.Tests;

using ChessBot.MatchRunner;
using Xunit;

/// <summary>
/// Gates for the durable record of a run.
///
/// Two measurement attempts in this project have already been lost to a process dying mid-run.
/// What has to hold is narrow and absolute: a finished game is on disk before the next one
/// starts, a restart continues rather than starting over, a restart against a different
/// experiment is refused, and the partial line a kill leaves behind costs one game rather than
/// the whole file.
/// </summary>
public class AbRunStoreTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"abstore_{Guid.NewGuid():N}");

    private static AbRunState StateWith(string fingerprint) => new()
    {
        RunId       = "test",
        Fingerprint = fingerprint,
        TotalGames  = 10,
    };

    private static ArmGameRecord Game(int index, ArmOutcome outcome) => new()
    {
        GameIndex   = index,
        Opening     = "Ruy Lopez",
        ArmAIsWhite = index % 2 == 0,
        Outcome     = outcome,
        Termination = "Checkmate",
        Plies       = 60,
        Pgn         = $"game{index:D5}.pgn",
    };

    [Fact]
    public void Append_FlushesEachGameSoAKillLosesAtMostTheOneInProgress()
    {
        string path = Path.Combine(_dir, AbRunStore.GamesFileName);

        using (var store = AbRunStore.OpenOrCreate(_dir, StateWith("f1")))
        {
            store.Append(Game(0, ArmOutcome.ArmAWin));

            // Readable from another handle while the store is still open: that is what "already
            // on disk" has to mean for it to survive a kill.
            Assert.Single(AbRunStore.ReadGames(path));

            store.Append(Game(1, ArmOutcome.Draw));
            Assert.Equal(2, AbRunStore.ReadGames(path).Count);
        }
    }

    [Fact]
    public void OpenOrCreate_ResumesAMatchingRunWithItsFinishedGames()
    {
        using (var first = AbRunStore.OpenOrCreate(_dir, StateWith("f1")))
        {
            Assert.False(first.Resumed);
            first.Append(Game(0, ArmOutcome.ArmAWin));
            first.Append(Game(3, ArmOutcome.ArmBWin));
        }

        using var second = AbRunStore.OpenOrCreate(_dir, StateWith("f1"));

        Assert.True(second.Resumed);
        Assert.Equal(new[] { 0, 3 }, second.Completed.Keys.OrderBy(k => k));
        Assert.Equal(ArmOutcome.ArmBWin, second.Completed[3].Outcome);

        // The run identity comes from what was already on disk, not from the new caller's copy.
        Assert.Equal("test", second.State.RunId);
    }

    [Fact]
    public void OpenOrCreate_RefusesADirectoryHoldingADifferentRun()
    {
        using (var first = AbRunStore.OpenOrCreate(_dir, StateWith("f1")))
            first.Append(Game(0, ArmOutcome.Draw));

        var ex = Assert.Throws<InvalidOperationException>(
            () => AbRunStore.OpenOrCreate(_dir, StateWith("f2")));

        Assert.Contains("different run", ex.Message);

        // Refused, not overwritten: the earlier run's games are still there.
        Assert.Single(AbRunStore.ReadGames(Path.Combine(_dir, AbRunStore.GamesFileName)));
    }

    [Fact]
    public void ReadGames_KeepsEveryCompleteLineWhenTheLastOneWasTruncated()
    {
        string path = Path.Combine(_dir, AbRunStore.GamesFileName);

        using (var store = AbRunStore.OpenOrCreate(_dir, StateWith("f1")))
        {
            store.Append(Game(0, ArmOutcome.ArmAWin));
            store.Append(Game(1, ArmOutcome.Draw));
        }

        // What a process killed mid-write leaves behind. Refusing to read the file because of it
        // would throw away every game before it — the exact loss the log exists to prevent.
        File.AppendAllText(path, "{\"GameIndex\":2,\"Opening\":\"Ital");

        var records = AbRunStore.ReadGames(path);
        Assert.Equal(2, records.Count);
        Assert.Equal(new[] { 0, 1 }, records.Select(r => r.GameIndex));

        // And the run picks up from the games that survived.
        using var resumed = AbRunStore.OpenOrCreate(_dir, StateWith("f1"));
        Assert.Equal(2, resumed.Completed.Count);
    }

    [Fact]
    public void FingerprintOf_ChangesWithAnyPartAndIsStableForTheSameOne()
    {
        string first  = AbRunStore.FingerprintOf("armA", "armB", "openings", "nodes", "200");
        string second = AbRunStore.FingerprintOf("armA", "armB", "openings", "nodes", "200");
        string other  = AbRunStore.FingerprintOf("armA", "armB", "openings", "nodes", "400");

        Assert.Equal(first, second);
        Assert.NotEqual(first, other);
        Assert.Equal(64, first.Length);
    }

    [Fact]
    public void ScoreForB_CountsAWinAsOneADrawAsAHalfAndAnAbortAsNothing()
    {
        Assert.Equal(1.0, Game(0, ArmOutcome.ArmBWin).ScoreForB);
        Assert.Equal(0.5, Game(0, ArmOutcome.Draw).ScoreForB);
        Assert.Equal(0.0, Game(0, ArmOutcome.ArmAWin).ScoreForB);

        // An aborted game is a protocol failure, not half a point for the arm that crashed.
        Assert.Equal(0.0, Game(0, ArmOutcome.Aborted).ScoreForB);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* a leftover temp directory is not worth failing a test over */ }
    }
}
