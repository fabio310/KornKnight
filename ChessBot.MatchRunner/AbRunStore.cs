namespace ChessBot.MatchRunner;

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

/// <summary>How one finished game came out, from arm A's point of view.</summary>
public enum ArmOutcome { ArmAWin, ArmBWin, Draw, Aborted }

/// <summary>
/// The durable record of one finished game: enough to rebuild the run's tallies without
/// re-reading a single PGN, and nothing more, so the file stays small over thousands of games.
/// </summary>
public sealed class ArmGameRecord
{
    /// <summary>Position in the schedule. This is what a resumed run skips on.</summary>
    public int GameIndex { get; set; }

    public string Opening      { get; set; } = string.Empty;
    public bool   ArmAIsWhite  { get; set; }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public ArmOutcome Outcome  { get; set; }

    public string Termination  { get; set; } = string.Empty;
    public int    Plies        { get; set; }
    public string Pgn          { get; set; } = string.Empty;
    public string FinishedUtc  { get; set; } = string.Empty;

    /// <summary>Reference-engine move loss, when a reference engine was configured.</summary>
    public MoveLossAggregateDto? MoveLossArmA { get; set; }
    public MoveLossAggregateDto? MoveLossArmB { get; set; }

    /// <summary>Score for arm B, the changed arm, as SPRT and the score rate count it.</summary>
    [JsonIgnore]
    public double ScoreForB => Outcome switch
    {
        ArmOutcome.ArmBWin => 1.0,
        ArmOutcome.Draw    => 0.5,
        _                  => 0.0,
    };
}

/// <summary>
/// Everything a run needs to be recognised as the same run when it is restarted. Written once,
/// before the first game.
/// </summary>
public sealed class AbRunState
{
    public int    SchemaVersion { get; set; } = 1;
    public string RunId         { get; set; } = string.Empty;
    public string StartedUtc    { get; set; } = string.Empty;

    /// <summary>
    /// Hash over everything that must not change between the two halves of an interrupted run:
    /// both binaries' contents, their options, the openings, the budget, the game count and the
    /// SPRT parameters. Resuming across a change in any of them would splice two different
    /// experiments into one result.
    /// </summary>
    public string Fingerprint { get; set; } = string.Empty;

    public ArmIdentity?   ArmA     { get; set; }
    public ArmIdentity?   ArmB     { get; set; }
    public OpeningSetDto? Openings { get; set; }
    public RunConditions? Conditions { get; set; }

    public string Budget     { get; set; } = string.Empty;
    public int    TotalGames { get; set; }

    public SprtSettings? Sprt { get; set; }

    public List<string> CommandLineArgs { get; set; } = new();
}

/// <summary>
/// The run's results on disk, written as they happen.
///
/// Two measurement attempts in this project have already been lost to a process dying mid-run,
/// which is what this exists to prevent: every finished game is appended and flushed before the
/// next one starts, so a killed run still yields the games it finished and a restart continues
/// from there. The append-only log is deliberately the primary record — a document rewritten
/// in place is exactly the thing a kill can truncate.
/// </summary>
public sealed class AbRunStore : IDisposable
{
    public const string StateFileName = "run_state.json";
    public const string GamesFileName = "games.jsonl";

    private static readonly JsonSerializerOptions LineOptions = new() { WriteIndented = false };
    private static readonly JsonSerializerOptions StateOptions = new() { WriteIndented = true };

    private readonly FileStream _games;
    private readonly object _gate = new();
    private readonly Dictionary<int, ArmGameRecord> _completed;

    public string Directory { get; }
    public AbRunState State { get; }

    /// <summary>True when this store picked up an existing run rather than starting one.</summary>
    public bool Resumed { get; }

    /// <summary>Games already finished, keyed by schedule index.</summary>
    public IReadOnlyDictionary<int, ArmGameRecord> Completed
    {
        get { lock (_gate) return new Dictionary<int, ArmGameRecord>(_completed); }
    }

    private AbRunStore(string directory, AbRunState state, Dictionary<int, ArmGameRecord> completed, bool resumed)
    {
        Directory  = directory;
        State      = state;
        Resumed    = resumed;
        _completed = completed;

        _games = new FileStream(Path.Combine(directory, GamesFileName),
            FileMode.Append, FileAccess.Write, FileShare.Read);
    }

    /// <summary>
    /// Opens the run in <paramref name="directory"/>, resuming it when one is already there and
    /// its fingerprint matches.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// The directory holds a different run. Refused rather than overwritten or silently
    /// continued: both would destroy a measurement, and the caller can always choose another
    /// directory.
    /// </exception>
    public static AbRunStore OpenOrCreate(string directory, AbRunState state)
    {
        System.IO.Directory.CreateDirectory(directory);

        string statePath = Path.Combine(directory, StateFileName);
        var completed = new Dictionary<int, ArmGameRecord>();

        if (File.Exists(statePath))
        {
            var existing = JsonSerializer.Deserialize<AbRunState>(File.ReadAllText(statePath))
                ?? throw new InvalidOperationException($"{statePath} could not be read as a run state.");

            if (existing.Fingerprint != state.Fingerprint)
                throw new InvalidOperationException(
                    $"{directory} holds a different run (fingerprint {Short(existing.Fingerprint)} " +
                    $"vs {Short(state.Fingerprint)}). An arm binary, its options, the openings, the " +
                    "budget, the game count or the SPRT parameters changed, and resuming across that " +
                    "would splice two experiments into one result. Use a new output directory.");

            foreach (var record in ReadGames(Path.Combine(directory, GamesFileName)))
                completed[record.GameIndex] = record;

            return new AbRunStore(directory, existing, completed, resumed: true);
        }

        File.WriteAllText(statePath, JsonSerializer.Serialize(state, StateOptions));
        return new AbRunStore(directory, state, completed, resumed: false);
    }

    /// <summary>
    /// Reads the finished games, tolerating a truncated last line. A process killed mid-write
    /// leaves a partial record, and refusing to read the file because of it would throw away
    /// every game before it — the exact loss the log exists to prevent.
    /// </summary>
    public static List<ArmGameRecord> ReadGames(string path)
    {
        var records = new List<ArmGameRecord>();
        if (!File.Exists(path)) return records;

        // Opened sharing read AND write, because the run that is producing the file still holds
        // it open for appending. File.ReadLines asks for exclusive-of-writers sharing and fails
        // outright against a live run — which would make the log unreadable exactly while it is
        // most worth reading.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);

        while (reader.ReadLine() is string line)
        {
            if (line.Length == 0) continue;
            try
            {
                var record = JsonSerializer.Deserialize<ArmGameRecord>(line);
                if (record is not null) records.Add(record);
            }
            catch (JsonException)
            {
                // Only the final line of a killed run can be partial; a corrupt line anywhere
                // else would be a different problem, and skipping it still loses less than
                // discarding the whole file.
            }
        }

        return records;
    }

    /// <summary>
    /// Appends one finished game and flushes it to disk before returning, so the game is durable
    /// before the next one starts.
    /// </summary>
    public void Append(ArmGameRecord record)
    {
        byte[] line = Encoding.UTF8.GetBytes(JsonSerializer.Serialize(record, LineOptions) + "\n");

        lock (_gate)
        {
            _completed[record.GameIndex] = record;
            _games.Write(line, 0, line.Length);
            _games.Flush(flushToDisk: true);
        }
    }

    /// <summary>
    /// Hashes the parts of a run that must not change across a restart. Order matters and is
    /// fixed by the caller, so the same run always produces the same fingerprint.
    /// </summary>
    public static string FingerprintOf(params string[] parts)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Join(" ", parts))))
            .ToLowerInvariant();

    private static string Short(string hash) => hash.Length >= 12 ? hash[..12] + "…" : hash;

    public void Dispose() => _games.Dispose();
}
