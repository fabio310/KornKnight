namespace ChessBot.MatchRunner;

using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>
/// One produced file, recorded portably so a manifest can be checked on a different machine.
/// </summary>
public sealed class ArtifactEntry
{
    /// <summary>Path relative to the run's output directory, using forward slashes.</summary>
    public string RelativePath { get; set; } = string.Empty;
    public long   SizeBytes    { get; set; }
    /// <summary>SHA-256 of the file contents, lowercase hex. Empty if the file was unreadable.</summary>
    public string Sha256       { get; set; } = string.Empty;
}

/// <summary>
/// Reproducibility metadata for a single match run: the source state as it was *before* the run
/// started, the exact invocation, the effective configuration, environment, timing, and every
/// artifact produced.
///
/// Source state and start time are captured before any output is written. Capturing them
/// afterwards made two things untrue: the recorded start time was really the completion time,
/// and the run's own generated files inside the working tree made the source look uncommitted
/// even when it had been clean.
/// </summary>
public sealed class RunManifest
{
    public int    SchemaVersion = 2;

    public string RunId { get; set; } = string.Empty;

    // ── Source state, captured before the run produced anything ──────────────
    public string CommitHash            { get; set; } = "unknown";
    /// <summary>Whether tracked source files were modified before the run started.</summary>
    public bool   HasUncommittedChanges { get; set; }
    /// <summary>The paths that made the source dirty, so "dirty" can be judged rather than trusted.</summary>
    public List<string> UncommittedPaths { get; set; } = new();

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

    // ── Invocation ───────────────────────────────────────────────────────────
    /// <summary>The exact command-line arguments the process was started with.</summary>
    public List<string> CommandLineArgs { get; set; } = new();
    /// <summary>The configuration those arguments actually parsed to, field by field.</summary>
    public Dictionary<string, string> EffectiveConfig { get; set; } = new();

    // ── Timing ───────────────────────────────────────────────────────────────
    public DateTime  StartedAtUtc   { get; set; }
    public DateTime? CompletedAtUtc { get; set; }
    public double?   DurationSeconds { get; set; }

    /// <summary>Directory the relative artifact paths are resolved against.</summary>
    public string OutputDirectory { get; set; } = string.Empty;
    public List<ArtifactEntry> ArtifactFiles { get; set; } = new();
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
    /// Captures source state, invocation and start time. Call this before creating any output:
    /// the git status it records is the pre-run state, which is the only one that says anything
    /// about the code that produced the results.
    /// </summary>
    public static RunManifest BeginRun(
        string runId,
        string outputDirectory,
        IEnumerable<string> commandLineArgs,
        Dictionary<string, string> effectiveConfig)
    {
        var manifest = new RunManifest
        {
            RunId           = runId,
            OutputDirectory = outputDirectory,
            CommandLineArgs = commandLineArgs.ToList(),
            EffectiveConfig = effectiveConfig,
            StartedAtUtc    = DateTime.UtcNow,
        };

        var (hash, dirty, paths) = TryGetGitState();
        manifest.CommitHash            = hash;
        manifest.HasUncommittedChanges = dirty;
        manifest.UncommittedPaths      = paths;

        return manifest;
    }

    /// <summary>
    /// Records completion time, duration and the full artifact inventory. Paths are stored
    /// relative to the run's output directory so the manifest is portable, and each entry
    /// carries a size and SHA-256 so an archive can be checked against it.
    /// </summary>
    public static void CompleteRun(RunManifest manifest, IEnumerable<string> artifactFiles)
    {
        manifest.CompletedAtUtc  = DateTime.UtcNow;
        manifest.DurationSeconds = (manifest.CompletedAtUtc.Value - manifest.StartedAtUtc).TotalSeconds;

        string root = Path.GetFullPath(manifest.OutputDirectory);
        var entries = new List<ArtifactEntry>();

        foreach (string path in artifactFiles.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            string full = Path.GetFullPath(path);
            var entry = new ArtifactEntry { RelativePath = ToRelative(root, full) };

            try
            {
                var info = new FileInfo(full);
                if (info.Exists)
                {
                    entry.SizeBytes = info.Length;
                    entry.Sha256    = Sha256Of(full);
                }
            }
            catch
            {
                // A file that cannot be read is still listed, with size 0 and no checksum,
                // rather than dropped from the inventory.
            }

            entries.Add(entry);
        }

        manifest.ArtifactFiles = entries.OrderBy(e => e.RelativePath, StringComparer.Ordinal).ToList();
    }

    public static void Write(RunManifest manifest, string path)
        => File.WriteAllText(path, JsonSerializer.Serialize(manifest, JsonOptions));

    private static string ToRelative(string root, string full)
    {
        string rel = full.StartsWith(root, StringComparison.OrdinalIgnoreCase)
            ? Path.GetRelativePath(root, full)
            : full;                                    // outside the output dir: keep it explicit
        return rel.Replace(Path.DirectorySeparatorChar, '/');
    }

    private static string Sha256Of(string path)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
    }

    private static (string hash, bool dirty, List<string> paths) TryGetGitState()
    {
        string hash = "unknown";
        bool dirty = false;
        var paths = new List<string>();
        try
        {
            hash = RunGit("rev-parse HEAD").Trim();
            string status = RunGit("status --porcelain");
            paths = status.Split('\n', StringSplitOptions.RemoveEmptyEntries)
                          .Select(l => l.Trim())
                          .ToList();
            dirty = paths.Count > 0;
        }
        catch
        {
            // Not a git repo, git not installed, or any other failure: leave defaults.
        }
        return (string.IsNullOrWhiteSpace(hash) ? "unknown" : hash, dirty, paths);
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
