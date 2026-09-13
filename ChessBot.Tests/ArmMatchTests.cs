namespace ChessBot.Tests;

using System.Runtime.InteropServices;
using ChessBot.MatchRunner;
using Xunit;

/// <summary>
/// Gates for A/B runs between two engine binaries.
///
/// These drive the real UCI executable as both arms, because that is the thing the harness now
/// compares: an arm is a binary, not a settings object, and everything that could go wrong —
/// the handshake, the node budget, the colour pairing, the durable log — lives in the process
/// boundary that an in-process test would skip over.
/// </summary>
[Trait("Category", "Slow")]
public class ArmMatchTests : IDisposable
{
    private readonly string _dir = Path.Combine(
        Path.GetTempPath(), $"abrun_{Guid.NewGuid():N}");

    /// <summary>
    /// The UCI binary, which the test project's reference to it puts alongside the test assembly.
    /// </summary>
    private static string EnginePath
    {
        get
        {
            string name = RuntimeInformation.IsOSPlatform(OSPlatform.Windows)
                ? "ChessBot.Uci.exe" : "ChessBot.Uci";
            return Path.Combine(AppContext.BaseDirectory, name);
        }
    }

    private static EngineArm Arm(string label) => new() { Label = label, EnginePath = EnginePath };

    private ArmMatchConfig Config(int games, int concurrency, string? subDir = null, SprtSettings? sprt = null) => new()
    {
        ArmA        = Arm("A"),
        ArmB        = Arm("B"),
        Openings    = OpeningBook.Standard,
        // Small enough to keep the suite quick, large enough that the engines still play chess.
        Budget      = MoveBudget.Nodes(1_200),
        Games       = games,
        Concurrency = concurrency,
        OutputDir   = subDir is null ? _dir : Path.Combine(_dir, subDir),
        Sprt        = sprt,
        PinWorkers  = false,
    };

    [Fact]
    public void TheUciBinaryIsWhereTheTestsExpectIt()
    {
        // A missing binary would make every test below silently vacuous, so it is asserted once
        // rather than guarded for in each of them.
        Assert.True(File.Exists(EnginePath), $"expected the UCI binary at {EnginePath}");
    }

    // ── Identity ─────────────────────────────────────────────────────────────

    [Fact]
    public async Task Arm_ReportsTheCommitAndConfigurationItsBinaryWasBuiltFrom()
    {
        var identity = await Arm("A").IdentifyAsync();

        // A result is only traceable to two commits if each arm can say which commit it is.
        Assert.NotEqual(ArmIdentity.NotReported, identity.Commit);
        Assert.Contains(identity.BuildConfiguration, new[] { "Debug", "Release" });
        Assert.Equal(64, identity.BinarySha256.Length);
        Assert.True(identity.BinarySizeBytes > 0);
        Assert.Contains("KornKnight", identity.ReportedName);
    }

    [Fact]
    public async Task Arm_FingerprintCoversTheBinaryContentsAndItsOptions()
    {
        var plain = Arm("A");
        await plain.IdentifyAsync();

        var withOption = new EngineArm
        {
            Label = "B", EnginePath = EnginePath,
            Options = { new UciOptionSetting("Hash", "64") },
        };
        await withOption.IdentifyAsync();

        // Same binary, different options: a resumed run must not splice the two together.
        Assert.NotEqual(plain.Fingerprint, withOption.Fingerprint);
    }

    // ── Games ────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_PlaysEveryScheduledGameAndAccountsForAllOfThem()
    {
        var cfg = Config(games: 4, concurrency: 2);
        var result = await ArmMatch.RunAsync(cfg, Array.Empty<string>());

        Assert.Equal(4, result.Games + result.Aborted);
        Assert.Equal(result.Games, result.WinsA + result.WinsB + result.Draws);
        Assert.Equal(0, result.Aborted);

        // Paired openings: arm A takes White in exactly half of them.
        Assert.True(result.IsColourBalanced);
        Assert.Equal(2, result.GamesArmAAsWhite);

        // Two games per opening, so four games are two openings, each played both ways.
        var pgns = Directory.GetFiles(cfg.OutputDir, "game*.pgn");
        Assert.Equal(4, pgns.Length);
        Assert.All(pgns, p => Assert.Contains("[SetUp \"1\"]", File.ReadAllText(p)));
    }

    [Fact]
    public async Task Run_ANodeBudgetGivesTheSameResultAtEveryConcurrency()
    {
        // This is the claim that lets a node-budget run take the whole machine. "go nodes" sends
        // no clock, so an engine's answer depends on the position alone and contention cannot
        // change it. If this ever stops holding, saturating a large box is unsafe.
        var serial   = await ArmMatch.RunAsync(Config(4, concurrency: 1, "serial"), Array.Empty<string>());
        var parallel = await ArmMatch.RunAsync(Config(4, concurrency: 4, "parallel"), Array.Empty<string>());

        Assert.Equal(serial.Games, parallel.Games);
        Assert.Equal(serial.WinsA, parallel.WinsA);
        Assert.Equal(serial.WinsB, parallel.WinsB);
        Assert.Equal(serial.Draws, parallel.Draws);

        // Folded in schedule order, so even the per-game reasons line up: a result that depends
        // on the order the workers finished in is not a result.
        Assert.Equal(serial.TerminationReasons, parallel.TerminationReasons);
    }

    [Fact]
    public async Task Run_IdenticalArmsAreScoredAsAnExactlyEvenContest()
    {
        // Same binary, same options, node budget: every colour-reversed pair is the same game
        // played twice with the sides swapped, so the two arms must end level by construction.
        // Anything else here is the harness, not the engine.
        var result = await ArmMatch.RunAsync(Config(6, concurrency: 3), Array.Empty<string>());

        Assert.Equal(result.WinsA, result.WinsB);
        Assert.Equal(0.5, result.ScoreRateB, 10);
    }

    // ── Resumability ─────────────────────────────────────────────────────────

    [Fact]
    public async Task Run_ResumingKeepsTheGamesTheKilledRunFinished()
    {
        // Two measurement attempts in this project have already been lost to a process dying
        // mid-run. A restart must continue, not start over.
        var first = await ArmMatch.RunAsync(Config(4, concurrency: 1, "resume"), Array.Empty<string>());
        Assert.Equal(4, first.Games);

        string log = Path.Combine(_dir, "resume", AbRunStore.GamesFileName);
        var before = File.ReadAllLines(log);
        Assert.Equal(4, before.Length);

        var second = await ArmMatch.RunAsync(Config(4, concurrency: 1, "resume"), Array.Empty<string>());

        Assert.True(second.Resumed);
        Assert.Equal(4, second.GamesFromEarlierRun);
        Assert.Equal(4, second.Games);

        // Nothing replayed: the log is byte-for-byte what it was.
        Assert.Equal(before, File.ReadAllLines(log));
    }

    [Fact]
    public async Task Run_RefusesToResumeIntoADirectoryHoldingADifferentRun()
    {
        await ArmMatch.RunAsync(Config(2, concurrency: 1, "mismatch"), Array.Empty<string>());

        // A different budget is a different experiment. Continuing into it would splice two
        // measurements into one number.
        var different = new ArmMatchConfig
        {
            ArmA = Arm("A"), ArmB = Arm("B"),
            Openings = OpeningBook.Standard,
            Budget = MoveBudget.Nodes(9_999),
            Games = 2,
            Concurrency = 1,
            OutputDir = Path.Combine(_dir, "mismatch"),
            PinWorkers = false,
        };

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            () => ArmMatch.RunAsync(different, Array.Empty<string>()));
        Assert.Contains("different run", ex.Message);
    }

    [Fact]
    public async Task Run_WritesEachGameBeforeTheNextOneStarts()
    {
        var cfg = Config(4, concurrency: 1, "incremental");
        await ArmMatch.RunAsync(cfg, Array.Empty<string>());

        // Every finished game is its own line, so a kill at any point loses at most the game in
        // progress.
        var records = AbRunStore.ReadGames(Path.Combine(cfg.OutputDir, AbRunStore.GamesFileName));
        Assert.Equal(4, records.Count);
        Assert.Equal(new[] { 0, 1, 2, 3 }, records.Select(r => r.GameIndex).OrderBy(i => i));
        Assert.All(records, r => Assert.False(string.IsNullOrWhiteSpace(r.Termination)));

        // And a readable report exists without waiting for the run to end.
        Assert.True(File.Exists(Path.Combine(cfg.OutputDir, "ab_report.log")));
        Assert.True(File.Exists(Path.Combine(cfg.OutputDir, "ab_result.json")));
    }

    [Fact]
    public async Task Run_ASprtThatIsAlreadyConclusiveStopsBeforePlayingEveryGame()
    {
        // Identical arms at elo0=-200, elo1=-100 make H0 ("B is not 100 Elo worse") reachable
        // almost immediately, which is what lets this assert early stopping without playing
        // thousands of games.
        var sprt = new SprtSettings { Elo0 = -200, Elo1 = -100, Alpha = 0.2, Beta = 0.2 };
        var cfg = Config(40, concurrency: 2, "sprt", sprt);

        var result = await ArmMatch.RunAsync(cfg, Array.Empty<string>());

        Assert.NotNull(result.SprtProgress);
        Assert.True(result.Games < 40, $"expected an early stop, played {result.Games}");
        Assert.True(result.SprtProgress!.Value.IsConclusive);

        // Games are dispatched in schedule order, so where a run stops is a property of the run
        // and not of how the thread pool happened to start its tasks. Repeating the same run in
        // a fresh directory must stop at the same place.
        var repeat = await ArmMatch.RunAsync(
            Config(40, concurrency: 2, "sprt-repeat", sprt), Array.Empty<string>());
        Assert.Equal(result.Games, repeat.Games);

        // Never mid-pair: a run that stopped on an odd game would end with one arm having had
        // White more often, and the bias would land in the reported score.
        Assert.True(result.IsColourBalanced);
        Assert.Contains("LLR", result.StopReason);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true); }
        catch { /* a leftover temp directory is not worth failing a test over */ }
    }
}
