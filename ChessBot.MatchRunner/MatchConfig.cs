namespace ChessBot.MatchRunner;

/// <summary>
/// A single UCI option to send to the external engine before play starts.
/// </summary>
/// <param name="Name">UCI option name, e.g. "UCI_Elo".</param>
/// <param name="Value">UCI option value, e.g. "1500".</param>
public readonly record struct UciOptionSetting(string Name, string Value);

/// <summary>
/// Configuration for a match between ChessBot and an external UCI engine.
/// </summary>
public class MatchConfig
{
    /// <summary>Path to the external UCI engine executable.</summary>
    public string ExternalEnginePath { get; set; } = string.Empty;

    /// <summary>Milliseconds per move for both engines.</summary>
    public int MoveTimeMs { get; set; } = 1000;

    /// <summary>
    /// Exact total number of games to play, both colors combined. This is the authoritative
    /// count: <c>--games n</c> plays exactly n games, odd values included. Colors alternate
    /// starting with ChessBot as White, so an odd total gives ChessBot one extra White game;
    /// <see cref="ColorImbalance"/> reports that so it is never silent.
    /// </summary>
    public int TotalGames { get; set; } = 2;

    /// <summary>
    /// Per-color view of <see cref="TotalGames"/>. Setting it requests n games as White plus n
    /// as Black, i.e. a total of 2n. Reading it returns the number of games ChessBot plays as
    /// White, which for an odd total is the larger half.
    /// </summary>
    public int GamesPerSide
    {
        get => (TotalGames + 1) / 2;
        set => TotalGames = value * 2;
    }

    /// <summary>
    /// Difference between the number of games played as White and as Black: 0 for an even
    /// total, 1 for an odd one. Reported rather than corrected, because an odd total is a
    /// legitimate request for a diagnostic run.
    /// </summary>
    public int ColorImbalance => TotalGames % 2;

    /// <summary>
    /// When true, propagated into every <see cref="ChessBot.Engine.Search.SearchSettings"/>
    /// created for ChessBot's moves. Mirrors <see cref="ChessBot.Engine.Search.SearchSettings.UsePartialRootResult"/>;
    /// defaults to false (matches the engine default) because this is a strength-affecting
    /// heuristic that has not yet been validated by a controlled A/B comparison.
    /// </summary>
    public bool UsePartialRootResult { get; set; } = false;

    /// <summary>Directory to save PGN files.</summary>
    public string PgnOutputDir { get; set; } = "pgns";

    /// <summary>Centipawn swing that counts as a blunder.</summary>
    public int BlunderThresholdCp { get; set; } = 200;

    /// <summary>Minimum engine score difference for first-blunder detection.</summary>
    public int FirstSwingThresholdCp { get; set; } = 150;

    /// <summary>Whether to emit verbose move-by-move output to console.</summary>
    public bool Verbose { get; set; } = true;

    /// <summary>
    /// When set, the external engine is capped to this Elo via the standard UCI options
    /// "UCI_LimitStrength" (true) and "UCI_Elo". Null = no strength limit (full strength).
    /// </summary>
    public int? EngineElo { get; set; }

    /// <summary>
    /// Extra UCI options sent to the external engine right after the handshake.
    /// Applied after the <see cref="EngineElo"/> options, so they can override them.
    /// </summary>
    public List<UciOptionSetting> EngineOptions { get; } = new();

    /// <summary>
    /// Path to a reference engine (normally full-strength Stockfish) used for post-game
    /// move-loss analysis (see <see cref="MoveLossAnalyzer"/>). Null/empty = analysis skipped.
    /// This is always run after the timed game completes, never during play, so it cannot
    /// affect move-time budgets or results.
    /// </summary>
    public string? ReferenceEnginePath { get; set; }

    /// <summary>Fixed search depth used for reference-engine move-loss analysis.</summary>
    public int ReferenceEngineDepth { get; set; } = 18;

    /// <summary>
    /// UCI options applied to the reference engine before analysis (e.g. Threads, Hash).
    /// Recorded verbatim in the move-loss report header so results are attributable to a
    /// specific, reproducible reference-engine configuration.
    /// </summary>
    public List<UciOptionSetting> ReferenceEngineOptions { get; } = new();

    /// <summary>
    /// Maximum number of deterministic re-search attempts (at increasing depth) the move-loss
    /// analyzer will make for a single position before giving up and marking the sample
    /// ineligible for exact aggregates. Applies to both bound-score resolution and negative
    /// analysis-inconsistency resolution.
    /// </summary>
    public int MoveLossMaxRetries { get; set; } = 2;

    /// <summary>
    /// Sends the configured UCI options to an already-initialized engine.
    /// Must be called after <see cref="UciAdapter.InitializeAsync"/> and before the
    /// first "position"/"go" command. A no-op when nothing is configured.
    /// </summary>
    public async Task ApplyEngineOptionsAsync(UciAdapter engine, CancellationToken ct = default)
    {
        if (EngineElo is null && EngineOptions.Count == 0)
            return;

        if (EngineElo is int elo)
        {
            await engine.SetOptionAsync("UCI_LimitStrength", "true");
            await engine.SetOptionAsync("UCI_Elo", elo.ToString(System.Globalization.CultureInfo.InvariantCulture));
        }

        foreach (var opt in EngineOptions)
            await engine.SetOptionAsync(opt.Name, opt.Value);

        // Make sure the engine has digested the options before the first search.
        await engine.SyncAsync(ct: ct);
    }

    /// <summary>
    /// Renders the configuration that the arguments actually parsed to, so a run's manifest can
    /// state what was in effect rather than leaving it to be inferred from the command line.
    /// </summary>
    public Dictionary<string, string> Describe() => new()
    {
        ["ExternalEnginePath"]    = ExternalEnginePath,
        ["MoveTimeMs"]            = MoveTimeMs.ToString(),
        ["TotalGames"]            = TotalGames.ToString(),
        ["GamesAsWhite"]          = ((TotalGames + 1) / 2).ToString(),
        ["GamesAsBlack"]          = (TotalGames / 2).ToString(),
        ["ColorImbalance"]        = ColorImbalance.ToString(),
        ["PgnOutputDir"]          = PgnOutputDir,
        ["DisagreementThresholdCp"] = BlunderThresholdCp.ToString(),
        ["EngineElo"]             = EngineElo?.ToString() ?? "(unset — full strength)",
        ["EngineOptions"]         = EngineOptions.Count == 0
            ? "(none)"
            : string.Join(", ", EngineOptions.Select(o => $"{o.Name}={o.Value}")),
        ["ReferenceEnginePath"]   = string.IsNullOrWhiteSpace(ReferenceEnginePath)
            ? "(unset — move-loss analysis skipped)" : ReferenceEnginePath,
        ["ReferenceEngineDepth"]  = ReferenceEngineDepth.ToString(),
        ["ReferenceEngineOptions"] = ReferenceEngineOptions.Count == 0
            ? "(none)"
            : string.Join(", ", ReferenceEngineOptions.Select(o => $"{o.Name}={o.Value}")),
        ["MoveLossMaxRetries"]    = MoveLossMaxRetries.ToString(),
        ["UsePartialRootResult"]  = UsePartialRootResult.ToString(),
        ["Verbose"]               = Verbose.ToString(),
    };

    /// <summary>
    /// Parse command-line arguments into a MatchConfig.
    /// Supported flags:
    ///   --engine &lt;path&gt;
    ///   --time &lt;ms&gt;
    ///   --games &lt;n&gt;              (TOTAL games played, both colors combined; must be even)
    ///   --games-per-side &lt;n&gt;    (unambiguous alternative: n games as White + n as Black)
    ///   --pgn-dir &lt;dir&gt;
    ///   --blunder &lt;cp&gt;
    ///   --engine-elo &lt;elo&gt;
    ///   --engine-option &lt;name=value&gt;   (repeatable)
    ///   --reference-engine &lt;path&gt;
    ///   --reference-depth &lt;depth&gt;
    ///   --reference-option &lt;name=value&gt;   (repeatable; e.g. Threads=1, Hash=128)
    ///   --moveloss-retries &lt;n&gt;   (deterministic re-search attempts before marking a sample ineligible)
    ///   --use-partial-root-result       (enable UsePartialRootResult; off by default)
    ///   --quiet
    ///
    /// NOTE on --games: earlier versions of this tool multiplied the value by 2 internally
    /// ("games per side"), so "--games 56" silently played 112 games. --games is now the
    /// TOTAL game count (split evenly between colors); use --games-per-side for the old,
    /// unambiguous per-color meaning.
    /// </summary>
    public static MatchConfig Parse(string[] args)
    {
        var cfg = new MatchConfig();
        for (int i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--engine" when i + 1 < args.Length:
                    cfg.ExternalEnginePath = args[++i];
                    break;
                case "--time" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int ms)) cfg.MoveTimeMs = ms;
                    break;
                case "--games" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int g))
                    {
                        // --games means exactly this many games. It previously halved the value
                        // and rounded down, so "--games 5" silently played 4 and "--games 1"
                        // played none at all — the requested number must be honoured instead.
                        if (g < 1)
                            throw new ArgumentException(
                                $"--games must be at least 1; got {g}.");

                        cfg.TotalGames = g;
                        if (g % 2 != 0)
                            Console.Error.WriteLine(
                                $"NOTE: --games {g} is odd, so colors cannot be split evenly: " +
                                $"ChessBot plays {(g + 1) / 2} game(s) as White and {g / 2} as Black. " +
                                "All requested games are played.");
                    }
                    break;
                case "--games-per-side" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int gps))
                    {
                        if (gps < 1)
                            throw new ArgumentException(
                                $"--games-per-side must be at least 1; got {gps}.");
                        cfg.GamesPerSide = gps;
                    }
                    break;
                case "--pgn-dir" when i + 1 < args.Length:
                    cfg.PgnOutputDir = args[++i];
                    break;
                case "--blunder" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int b)) cfg.BlunderThresholdCp = b;
                    break;
                case "--engine-elo" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int elo)) cfg.EngineElo = elo;
                    break;
                case "--use-partial-root-result":
                    cfg.UsePartialRootResult = true;
                    break;
                case "--engine-option" when i + 1 < args.Length:
                    {
                        string raw = args[++i];
                        int eq = raw.IndexOf('=');
                        if (eq > 0)
                            cfg.EngineOptions.Add(new UciOptionSetting(
                                raw[..eq].Trim(), raw[(eq + 1)..].Trim()));
                        break;
                    }
                case "--reference-engine" when i + 1 < args.Length:
                    cfg.ReferenceEnginePath = args[++i];
                    break;
                case "--reference-depth" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int rd)) cfg.ReferenceEngineDepth = rd;
                    break;
                case "--reference-option" when i + 1 < args.Length:
                    {
                        string raw = args[++i];
                        int eq = raw.IndexOf('=');
                        if (eq > 0)
                            cfg.ReferenceEngineOptions.Add(new UciOptionSetting(
                                raw[..eq].Trim(), raw[(eq + 1)..].Trim()));
                        break;
                    }
                case "--moveloss-retries" when i + 1 < args.Length:
                    if (int.TryParse(args[++i], out int mlr)) cfg.MoveLossMaxRetries = mlr;
                    break;
                case "--quiet":
                    cfg.Verbose = false;
                    break;
            }
        }
        return cfg;
    }
}
