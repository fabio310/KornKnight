namespace ChessBot.MatchRunner;

using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

/// <summary>
/// One side of an A/B comparison: an engine binary and the UCI options it is run with.
///
/// Not a <c>SearchSettings</c> object. A comparison between two in-process settings objects can
/// only compare behaviours the engine still carries a flag for, so every question answered that
/// way leaves a flag behind — and a flag that survives a decision is a decision that was never
/// made. Two binaries have no such requirement: branch, change the code, build, compare, and
/// delete the loser. The harness never needs to know what differs between them.
/// </summary>
public sealed class EngineArm
{
    /// <summary>Short label used in reports and PGN tags, normally "A" or "B".</summary>
    public required string Label { get; init; }

    public required string EnginePath { get; init; }

    /// <summary>UCI options sent to this arm after the handshake, before the first search.</summary>
    public List<UciOptionSetting> Options { get; init; } = new();

    /// <summary>
    /// What the binary turned out to be, filled in by <see cref="IdentifyAsync"/>. Null until the
    /// run has actually started the engine once — the identity is read from the engine, never
    /// guessed from the path.
    /// </summary>
    public ArmIdentity? Identity { get; private set; }

    /// <summary>
    /// Starts the engine once, records what it says about itself and what the binary hashes to,
    /// then shuts it down. Run before the games so a killed run still has both arms' identities
    /// on disk.
    /// </summary>
    public async Task<ArmIdentity> IdentifyAsync(CancellationToken ct = default)
    {
        using var engine = new UciAdapter(EnginePath);
        await engine.InitializeAsync(ct);

        var identity = ArmIdentity.From(this, engine.EngineName, engine.HandshakeLines);
        Identity = identity;
        return identity;
    }

    /// <summary>Sends this arm's options to an initialized engine and waits for it to digest them.</summary>
    public async Task ApplyOptionsAsync(UciAdapter engine, CancellationToken ct = default)
    {
        if (Options.Count == 0) return;

        foreach (var option in Options)
            await engine.SetOptionAsync(option.Name, option.Value);

        await engine.SyncAsync(ct: ct);
    }

    /// <summary>
    /// What this arm has to match for a killed run to be resumable. The binary's content hash
    /// rather than its path: resuming a run against a rebuilt binary would silently splice games
    /// from two different engines into one result.
    /// </summary>
    public string Fingerprint =>
        $"{Identity?.BinarySha256 ?? "(unidentified)"}|" +
        string.Join(",", Options.Select(o => $"{o.Name}={o.Value}"));
}

/// <summary>
/// What an arm's binary is: what it says about itself over UCI, and what it actually hashes to.
///
/// Both halves are recorded because neither is sufficient alone. The reported build identity is
/// what makes a result traceable to a commit, but only our own engine reports one. The content
/// hash is available for any binary and is what a resumed run checks against, so a rebuilt arm
/// cannot be spliced into an earlier run's games.
/// </summary>
public sealed class ArmIdentity
{
    public string Label      { get; set; } = string.Empty;
    public string EnginePath { get; set; } = string.Empty;

    /// <summary>The engine's full "id name" line.</summary>
    public string ReportedName { get; set; } = string.Empty;

    /// <summary>Commit the binary was built from, or "(not reported)" for an engine that says nothing.</summary>
    public string Commit { get; set; } = NotReported;

    /// <summary>Debug/Release, or "(not reported)".</summary>
    public string BuildConfiguration { get; set; } = NotReported;

    /// <summary>
    /// True when the binary was built from a working tree with uncommitted changes, which means
    /// its commit does not describe the code that produced the result.
    /// </summary>
    public bool BuiltFromDirtyTree { get; set; }

    public string   BinarySha256       { get; set; } = string.Empty;
    public long     BinarySizeBytes    { get; set; }
    public DateTime BinaryLastWriteUtc { get; set; }

    /// <summary>Options this run set on the arm.</summary>
    public Dictionary<string, string> Options { get; set; } = new();

    /// <summary>Options the engine reported, including the defaults it was left at.</summary>
    public Dictionary<string, string> ReportedOptions { get; set; } = new();

    public const string NotReported = "(not reported)";

    /// <summary>
    /// Pulls the build identity out of an "id name" line of the form
    /// <c>Name 1.0.0+abcdef123456 (Release, dirty)</c>, the shape this project's UCI front end
    /// emits. A foreign engine matches none of it and is left at "(not reported)", where its
    /// content hash is the identity instead — recorded as unknown rather than invented.
    /// </summary>
    private static readonly Regex BuildIdentityPattern = new(
        @"\+(?<commit>[0-9A-Za-z]+?)(?<dirty>\.dirty)?\s*\((?<configuration>[^)]*?)(?:,\s*dirty)?\)\s*$",
        RegexOptions.Compiled);

    public static ArmIdentity From(EngineArm arm, string reportedName, IReadOnlyList<string> handshakeLines)
    {
        var identity = new ArmIdentity
        {
            Label        = arm.Label,
            EnginePath   = arm.EnginePath,
            ReportedName = reportedName,
            Options      = arm.Options.ToDictionary(o => o.Name, o => o.Value),
            ReportedOptions = ParseReportedOptions(handshakeLines),
        };

        var match = BuildIdentityPattern.Match(reportedName);
        if (match.Success)
        {
            identity.Commit             = match.Groups["commit"].Value;
            identity.BuildConfiguration = match.Groups["configuration"].Value.Trim();
            identity.BuiltFromDirtyTree = match.Groups["dirty"].Success ||
                                          reportedName.Contains(", dirty)", StringComparison.Ordinal);
        }

        try
        {
            var info = new FileInfo(arm.EnginePath);
            if (info.Exists)
            {
                identity.BinarySizeBytes    = info.Length;
                identity.BinaryLastWriteUtc = info.LastWriteTimeUtc;

                using var stream = File.OpenRead(arm.EnginePath);
                identity.BinarySha256 = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
            }
        }
        catch
        {
            // A binary that cannot be hashed still plays; it just cannot be resumed against, and
            // the empty hash is what says so.
        }

        return identity;
    }

    /// <summary>
    /// Turns "option name X type spin default 1 min 1 max 1024" handshake lines into name →
    /// default pairs, so a run records the options an arm was left at as well as the ones it set.
    /// </summary>
    private static Dictionary<string, string> ParseReportedOptions(IReadOnlyList<string> handshakeLines)
    {
        var options = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (string line in handshakeLines)
        {
            if (!line.StartsWith("option name ", StringComparison.OrdinalIgnoreCase)) continue;

            int typeIdx = line.IndexOf(" type ", StringComparison.OrdinalIgnoreCase);
            if (typeIdx < 0) continue;

            string name = line["option name ".Length..typeIdx].Trim();

            int defaultIdx = line.IndexOf(" default ", StringComparison.OrdinalIgnoreCase);
            string value = "(no default reported)";
            if (defaultIdx >= 0)
            {
                string rest = line[(defaultIdx + " default ".Length)..];
                foreach (string terminator in new[] { " min ", " max ", " var " })
                {
                    int cut = rest.IndexOf(terminator, StringComparison.OrdinalIgnoreCase);
                    if (cut >= 0) rest = rest[..cut];
                }
                value = rest.Trim();
            }

            options[name] = value;
        }

        return options;
    }

    /// <summary>One line for the console and the report header.</summary>
    public string Describe()
    {
        var sb = new StringBuilder();
        sb.Append(Label).Append(": ").Append(ReportedName);
        if (Commit != NotReported) sb.Append("  commit ").Append(Commit);
        if (BuiltFromDirtyTree)    sb.Append(" (DIRTY TREE — not traceable to that commit)");
        if (BinarySha256.Length >= 12) sb.Append("  sha256 ").Append(BinarySha256[..12]).Append('…');
        if (Options.Count > 0)
            sb.Append("  options ").Append(string.Join(", ", Options.Select(o => $"{o.Key}={o.Value}")));
        return sb.ToString();
    }
}
