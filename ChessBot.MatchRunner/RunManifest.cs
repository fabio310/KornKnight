namespace ChessBot.MatchRunner;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// Reproducibility metadata for a single match run: exact source commit, build
/// configuration, OS/runtime, timestamp, and the list of artifact files produced.
/// Written once per match (alongside match_summary.log) so results can later be
/// traced back to the exact code and environment that produced them.
/// </summary>
public sealed class RunManifest
{
    public string CommitHash        { get; set; } = "unknown";
    public bool   HasUncommittedChanges { get; set; }
    public string BuildConfiguration { get; set; } =
#if DEBUG
        "Debug";
#else
        "Release";
#endif
    public string TargetFramework   { get; set; } = System.Reflection.Assembly
        .GetExecutingAssembly()
        .GetCustomAttributes(typeof(System.Runtime.Versioning.TargetFrameworkAttribute), false)
        .OfType<System.Runtime.Versioning.TargetFrameworkAttribute>()
        .FirstOrDefault()?.FrameworkName ?? "unknown";
    public string OsDescription     { get; set; } = RuntimeInformation.OSDescription;
    public string OsArchitecture    { get; set; } = RuntimeInformation.OSArchitecture.ToString();
    public string RuntimeIdentifier { get; set; } = RuntimeInformation.RuntimeIdentifier;
    public string FrameworkDescription { get; set; } = RuntimeInformation.FrameworkDescription;
    public string ProcessArchitecture  { get; set; } = RuntimeInformation.ProcessArchitecture.ToString();
    public DateTime StartedAtUtc    { get; set; } = DateTime.UtcNow;
    public List<string> ArtifactFiles { get; set; } = new();
}

/// <summary>
/// Builds and writes a <see cref="RunManifest"/> for a match run.
/// </summary>
public static class RunManifestWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    /// <summary>
    /// Builds a manifest, capturing the current git commit hash (best-effort — falls
    /// back to "unknown" if git is unavailable or the workspace is not a repo).
    /// </summary>
    public static RunManifest Build(IEnumerable<string> artifactFiles)
    {
        var manifest = new RunManifest
        {
            ArtifactFiles = artifactFiles.ToList(),
        };

        (manifest.CommitHash, manifest.HasUncommittedChanges) = TryGetGitState();
        return manifest;
    }

    public static void Write(RunManifest manifest, string path)
    {
        string json = JsonSerializer.Serialize(manifest, JsonOptions);
        File.WriteAllText(path, json);
    }

    private static (string hash, bool dirty) TryGetGitState()
    {
        string hash = "unknown";
        bool dirty = false;
        try
        {
            hash = RunGit("rev-parse HEAD").Trim();
            string status = RunGit("status --porcelain");
            dirty = !string.IsNullOrWhiteSpace(status);
        }
        catch
        {
            // Not a git repo, git not installed, or any other failure: leave defaults.
        }
        return (string.IsNullOrWhiteSpace(hash) ? "unknown" : hash, dirty);
    }

    private static string RunGit(string args)
    {
        var psi = new ProcessStartInfo("git", args)
        {
            RedirectStandardOutput = true,
            RedirectStandardError  = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start git");
        string output = process.StandardOutput.ReadToEnd();
        process.WaitForExit(5000);
        if (process.ExitCode != 0)
            throw new InvalidOperationException($"git {args} failed with exit code {process.ExitCode}");
        return output;
    }
}
