namespace ChessBot.Tests;

using ChessBot.MatchRunner;
using Xunit;

/// <summary>
/// The manifest is the only thing that ties an archive of results back to the code and
/// invocation that produced it, so its own claims have to hold: the start time must precede
/// the output, the source state must be the pre-run one, paths must be portable, and the
/// inventory must list everything.
/// </summary>
public class RunManifestTests : IDisposable
{
    private readonly string _dir;

    public RunManifestTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"manifest_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir)) Directory.Delete(_dir, recursive: true);
    }

    private string Write(string name, string content)
    {
        string path = Path.Combine(_dir, name);
        File.WriteAllText(path, content);
        return path;
    }

    private RunManifest Begin() => RunManifestWriter.BeginRun(
        runId: "20260906_223000",
        outputDirectory: _dir,
        commandLineArgs: new[] { "--engine", @"C:\engines\stockfish.exe", "--games", "4" },
        effectiveConfig: new Dictionary<string, string> { ["TotalGames"] = "4", ["MoveTimeMs"] = "1000" });

    [Fact]
    public void BeginRun_RecordsStartTimeAndInvocationBeforeAnyOutputExists()
    {
        var before = DateTime.UtcNow;
        var manifest = Begin();
        var after = DateTime.UtcNow;

        Assert.InRange(manifest.StartedAtUtc, before.AddSeconds(-1), after.AddSeconds(1));
        Assert.Null(manifest.CompletedAtUtc);
        Assert.Null(manifest.DurationSeconds);

        Assert.Equal(new[] { "--engine", @"C:\engines\stockfish.exe", "--games", "4" }, manifest.CommandLineArgs);
        Assert.Equal("4", manifest.EffectiveConfig["TotalGames"]);
        Assert.Equal("20260906_223000", manifest.RunId);
    }

    [Fact]
    public void CompleteRun_AddsCompletionTimeAndDuration()
    {
        var manifest = Begin();
        RunManifestWriter.CompleteRun(manifest, Array.Empty<string>());

        Assert.NotNull(manifest.CompletedAtUtc);
        Assert.NotNull(manifest.DurationSeconds);
        Assert.True(manifest.CompletedAtUtc >= manifest.StartedAtUtc);
        Assert.True(manifest.DurationSeconds >= 0);
    }

    [Fact]
    public void CompleteRun_ListsEveryArtifactWithPortableRelativePathSizeAndChecksum()
    {
        var manifest = Begin();

        string pgn      = Write("game1_20260906_223000.pgn", "[Event \"x\"]\n1. e4 e5 *\n");
        string gameLog  = Write("game1_20260906_223000.log", "=== ChessBot Game Log ===\n");
        string lossLog  = Write("game1_20260906_223000.moveloss.log", "=== Move-Loss Report ===\n");
        string summary  = Write("match_summary.log", "ChessBot Match Summary\n");
        string result   = Write("match_result.json", "{\"SchemaVersion\":2}");
        string manifestPath = Path.Combine(_dir, "run_manifest.json");

        RunManifestWriter.CompleteRun(manifest,
            new[] { pgn, gameLog, lossLog, summary, result, manifestPath });

        var names = manifest.ArtifactFiles.Select(a => a.RelativePath).ToList();
        Assert.Contains("game1_20260906_223000.pgn", names);
        Assert.Contains("game1_20260906_223000.log", names);          // per-game logs were previously missing
        Assert.Contains("game1_20260906_223000.moveloss.log", names);
        Assert.Contains("match_summary.log", names);
        Assert.Contains("match_result.json", names);
        Assert.Contains("run_manifest.json", names);                  // the manifest lists itself

        // Portable: relative, forward slashes, no drive letters.
        Assert.All(manifest.ArtifactFiles, a =>
        {
            Assert.False(Path.IsPathRooted(a.RelativePath), $"'{a.RelativePath}' is absolute");
            Assert.DoesNotContain('\\', a.RelativePath);
        });

        // Existing files carry a real size and checksum; the not-yet-written manifest does not.
        var pgnEntry = manifest.ArtifactFiles.Single(a => a.RelativePath.EndsWith(".pgn"));
        Assert.True(pgnEntry.SizeBytes > 0);
        Assert.Equal(64, pgnEntry.Sha256.Length);
        Assert.Matches("^[0-9a-f]{64}$", pgnEntry.Sha256);
    }

    [Fact]
    public void CompleteRun_ChecksumMatchesFileContent()
    {
        var manifest = Begin();
        string path = Write("a.log", "deterministic content");
        RunManifestWriter.CompleteRun(manifest, new[] { path });

        // SHA-256 of "deterministic content"
        using var sha = System.Security.Cryptography.SHA256.Create();
        string expected = Convert.ToHexString(
            sha.ComputeHash(System.Text.Encoding.UTF8.GetBytes("deterministic content"))).ToLowerInvariant();

        Assert.Equal(expected, manifest.ArtifactFiles.Single().Sha256);
    }

    [Fact]
    public void CompleteRun_DeduplicatesRepeatedArtifactPaths()
    {
        var manifest = Begin();
        string path = Write("once.log", "x");

        RunManifestWriter.CompleteRun(manifest, new[] { path, path, path });

        Assert.Single(manifest.ArtifactFiles);
    }

    [Fact]
    public void Write_RoundTripsThroughJson()
    {
        var manifest = Begin();
        Write("g.pgn", "x");
        RunManifestWriter.CompleteRun(manifest, new[] { Path.Combine(_dir, "g.pgn") });

        string jsonPath = Path.Combine(_dir, "run_manifest.json");
        RunManifestWriter.Write(manifest, jsonPath);

        var back = System.Text.Json.JsonSerializer.Deserialize<RunManifest>(File.ReadAllText(jsonPath))!;

        Assert.Equal(manifest.RunId, back.RunId);
        Assert.Equal(manifest.CommitHash, back.CommitHash);
        Assert.Equal(manifest.CommandLineArgs, back.CommandLineArgs);
        Assert.Equal(manifest.EffectiveConfig["TotalGames"], back.EffectiveConfig["TotalGames"]);
        Assert.Equal(manifest.ArtifactFiles.Single().Sha256, back.ArtifactFiles.Single().Sha256);
        Assert.Equal(manifest.StartedAtUtc, back.StartedAtUtc);
        Assert.Equal(manifest.CompletedAtUtc, back.CompletedAtUtc);
    }
}
