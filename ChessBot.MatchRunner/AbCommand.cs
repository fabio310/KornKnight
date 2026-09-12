namespace ChessBot.MatchRunner;

/// <summary>
/// The <c>--ab</c> command: compare two engine binaries.
///
/// There is no mode table any more. The old harness carried a fixed list of comparisons —
/// partial-root, lmr, threat-eval, tapered-eval — each a pair of <c>SearchSettings</c> the engine
/// had to keep a flag for, so asking a new question meant adding a flag and answering one left
/// the flag behind. Arms are two paths on the command line instead, and the question is whatever
/// the difference between those two builds happens to be. The harness never needs to know.
/// </summary>
public static class AbCommand
{
    public static async Task<int> RunAsync(string[] args, CancellationToken ct = default)
    {
        try
        {
            return await RunCoreAsync(args, ct);
        }
        catch (ArgumentException ex)
        {
            // A bad option is the operator's mistake, not a crash: say which one and stop.
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 1;
        }
    }

    private static async Task<int> RunCoreAsync(string[] args, CancellationToken ct)
    {
        if (args.Contains("--help") || args.Contains("-h"))
        {
            PrintUsage();
            return 0;
        }

        string? pathA = Arg(args, "--arm-a");
        string? pathB = Arg(args, "--arm-b");

        if (string.IsNullOrWhiteSpace(pathA) || string.IsNullOrWhiteSpace(pathB))
        {
            Console.Error.WriteLine("ERROR: --ab needs both --arm-a <path> and --arm-b <path>.");
            PrintUsage();
            return 1;
        }

        foreach (string path in new[] { pathA, pathB })
        {
            if (!File.Exists(path))
            {
                Console.Error.WriteLine($"ERROR: arm binary not found: {path}");
                return 2;
            }
        }

        string? referenceEngine = Arg(args, "--ab-reference-engine");
        if (!string.IsNullOrWhiteSpace(referenceEngine) && !File.Exists(referenceEngine))
        {
            Console.Error.WriteLine($"ERROR: reference engine not found: {referenceEngine}");
            return 2;
        }

        var armA = new EngineArm { Label = "A", EnginePath = pathA, Options = OptionsFor(args, "--arm-a-option") };
        var armB = new EngineArm { Label = "B", EnginePath = pathB, Options = OptionsFor(args, "--arm-b-option") };

        // A node budget is the default because it is the one that can use the whole machine:
        // identical at any concurrency, so the run is reproducible and every core is usable.
        // A time budget is opt-in, for the questions that are about what a change costs to
        // compute rather than what it decides.
        var budget = Arg(args, "--ab-time") is not null
            ? MoveBudget.Time(Integer(args, "--ab-time", 100, minimum: 1))
            : MoveBudget.Nodes(Integer64(args, "--ab-nodes", 50_000, minimum: 1));

        OpeningSet openings;
        try
        {
            openings = ResolveOpenings(args);
        }
        catch (Exception ex) when (ex is FileNotFoundException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 2;
        }

        var cfg = new ArmMatchConfig
        {
            ArmA      = armA,
            ArmB      = armB,
            Openings  = openings,
            Budget    = budget,
            Games     = Integer(args, "--ab-games", 200, minimum: 2),
            OutputDir = Arg(args, "--ab-out") ?? "ab_runs/latest",
            Sprt      = ResolveSprt(args),
            PinWorkers = !args.Contains("--ab-no-pin"),
            ReferenceEnginePath = referenceEngine,
            ReferenceDepth = Integer(args, "--ab-reference-depth", 18, minimum: 1),
            ReferenceEngineOptions = OptionsFor(args, "--ab-reference-option"),
            MoveLossMaxRetries = Integer(args, "--moveloss-retries", 2, minimum: 0),
        };

        if (Arg(args, "--ab-concurrency") is not null)
            cfg = CloneWithConcurrency(cfg, Integer(args, "--ab-concurrency", 1, minimum: 1));

        cfg.Sprt?.Validate();

        ArmMatchResult result;
        try
        {
            result = await ArmMatch.RunAsync(cfg, args, ct);
        }
        catch (InvalidOperationException ex)
        {
            // The usual case is an output directory holding a different run, which is refused
            // rather than overwritten.
            Console.Error.WriteLine($"ERROR: {ex.Message}");
            return 3;
        }

        Console.WriteLine();
        Console.WriteLine(ArmMatch.BuildTextReport(cfg, result));
        Console.WriteLine($"Reports written to: {Path.GetFullPath(cfg.OutputDir)}");

        // A conclusive SPRT is the only exit code that means anything to a script: 0 for "adopt
        // B", 4 for "discard B", 5 for "still unknown".
        return result.SprtProgress?.Verdict switch
        {
            SprtVerdict.AcceptH1 => 0,
            SprtVerdict.AcceptH0 => 4,
            SprtVerdict.Continue => 5,
            _                    => 0,
        };
    }

    /// <summary>
    /// Concurrency is an init-only property, so an explicit value is applied by rebuilding the
    /// config rather than by leaving a settable knob that could change mid-run.
    /// </summary>
    private static ArmMatchConfig CloneWithConcurrency(ArmMatchConfig cfg, int concurrency) => new()
    {
        ArmA = cfg.ArmA,
        ArmB = cfg.ArmB,
        Openings = cfg.Openings,
        Budget = cfg.Budget,
        Games = cfg.Games,
        OutputDir = cfg.OutputDir,
        Sprt = cfg.Sprt,
        Concurrency = concurrency,
        PinWorkers = cfg.PinWorkers,
        ReferenceEnginePath = cfg.ReferenceEnginePath,
        ReferenceDepth = cfg.ReferenceDepth,
        ReferenceEngineOptions = cfg.ReferenceEngineOptions,
        MoveLossMaxRetries = cfg.MoveLossMaxRetries,
    };

    private static OpeningSet ResolveOpenings(string[] args)
    {
        if (args.Contains("--start-position-only")) return OpeningBook.StartPositionOnly;

        string? path = Arg(args, "--openings");
        if (path is null) return OpeningBook.Standard;

        return OpeningBook.Load(path, Integer(args, "--opening-plies", OpeningBook.DefaultPlies, minimum: 0));
    }

    private static SprtSettings? ResolveSprt(string[] args)
    {
        bool requested = args.Contains("--sprt") ||
                         args.Any(a => a.StartsWith("--sprt-", StringComparison.Ordinal));
        if (!requested) return null;

        return new SprtSettings
        {
            Elo0     = Number(args, "--sprt-elo0", 0),
            Elo1     = Number(args, "--sprt-elo1", 5),
            Alpha    = Number(args, "--sprt-alpha", 0.05),
            Beta     = Number(args, "--sprt-beta", 0.05),
        };
    }

    /// <summary>
    /// Parses a decimal option under the invariant culture.
    ///
    /// Never the ambient culture. On a machine whose locale uses "," as the decimal separator,
    /// <c>double.Parse("0.05")</c> reads the dot as a group separator and returns 5 — so
    /// <c>--sprt-alpha 0.05</c> silently became an alpha of 5, which is not a probability and
    /// made the SPRT bounds NaN. A command-line number means the same thing on every machine.
    /// </summary>
    private static double Number(string[] args, string flag, double fallback)
    {
        string? raw = Arg(args, flag);
        if (raw is null) return fallback;

        if (!double.TryParse(raw, System.Globalization.NumberStyles.Float,
                             System.Globalization.CultureInfo.InvariantCulture, out double value))
            throw new ArgumentException($"{flag} expects a number, got '{raw}'.");

        return value;
    }

    private static int Integer(string[] args, string flag, int fallback, int minimum = int.MinValue)
    {
        string? raw = Arg(args, flag);
        if (raw is null) return fallback;

        if (!int.TryParse(raw, System.Globalization.NumberStyles.Integer,
                          System.Globalization.CultureInfo.InvariantCulture, out int value))
            throw new ArgumentException($"{flag} expects a whole number, got '{raw}'.");

        return Math.Max(minimum, value);
    }

    private static long Integer64(string[] args, string flag, long fallback, long minimum = long.MinValue)
    {
        string? raw = Arg(args, flag);
        if (raw is null) return fallback;

        if (!long.TryParse(raw, System.Globalization.NumberStyles.Integer,
                           System.Globalization.CultureInfo.InvariantCulture, out long value))
            throw new ArgumentException($"{flag} expects a whole number, got '{raw}'.");

        return Math.Max(minimum, value);
    }

    private static List<UciOptionSetting> OptionsFor(string[] args, string flag)
    {
        var options = new List<UciOptionSetting>();

        for (int i = 0; i + 1 < args.Length; i++)
        {
            if (args[i] != flag) continue;

            string raw = args[i + 1];
            int equals = raw.IndexOf('=');
            if (equals > 0)
                options.Add(new UciOptionSetting(raw[..equals].Trim(), raw[(equals + 1)..].Trim()));
        }

        return options;
    }

    private static string? Arg(string[] args, string flag)
    {
        int index = Array.IndexOf(args, flag);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    private static void PrintUsage()
    {
        Console.WriteLine("Usage: ChessBot.MatchRunner --ab --arm-a <engine> --arm-b <engine> [options]");
        Console.WriteLine();
        Console.WriteLine("  Compares two engine BINARIES. Branch, change one thing, build both, run this,");
        Console.WriteLine("  keep the winner and delete the loser — no feature flag survives the decision.");
        Console.WriteLine("  A is the baseline, B is the change; results are reported from B's side.");
        Console.WriteLine();
        Console.WriteLine("  --arm-a <path>          Baseline engine binary (UCI)");
        Console.WriteLine("  --arm-b <path>          Changed engine binary (UCI)");
        Console.WriteLine("  --arm-a-option n=v      UCI option for arm A (repeatable)");
        Console.WriteLine("  --arm-b-option n=v      UCI option for arm B (repeatable)");
        Console.WriteLine();
        Console.WriteLine("  --ab-games <n>          Games to play; rounded up to a whole colour pair (default: 200)");
        Console.WriteLine("  --ab-nodes <n>          Node budget per move (default: 50000). Deterministic and");
        Console.WriteLine("                          identical at any concurrency, so it can use every core.");
        Console.WriteLine("  --ab-time <ms>          Time budget per move INSTEAD of a node budget. The only");
        Console.WriteLine("                          budget that charges a change for what it costs to compute,");
        Console.WriteLine("                          and the only one that needs a quiet machine.");
        Console.WriteLine("  --ab-out <dir>          Output directory (default: ab_runs/latest). An existing run");
        Console.WriteLine("                          here is RESUMED when it matches, and refused when it does not.");
        Console.WriteLine($"  --ab-concurrency <n>    Games in parallel (default: {MachineTopology.PhysicalCoreCount} on a node budget," );
        Console.WriteLine($"                          {ConcurrencyPolicy.RecommendedTimedCeiling} on a time budget, on this machine)");
        Console.WriteLine("  --ab-no-pin             Do not pin timed-game workers to cores or raise priority");
        Console.WriteLine();
        Console.WriteLine("  --openings <path>       EPD/FEN list or PGN of start positions (default: built-in 16)");
        Console.WriteLine("  --opening-plies <n>     Book depth taken from a PGN (default: 8)");
        Console.WriteLine("  --start-position-only   Every game from the initial position");
        Console.WriteLine();
        Console.WriteLine("  --sprt                  Stop as soon as the result is conclusive");
        Console.WriteLine("  --sprt-elo0 <elo>       H0: B is this much stronger (default: 0)");
        Console.WriteLine("  --sprt-elo1 <elo>       H1: B is this much stronger (default: 5)");
        Console.WriteLine("  --sprt-alpha <p>        Chance of adopting a change that is not one (default: 0.05)");
        Console.WriteLine("  --sprt-beta <p>         Chance of discarding one that is (default: 0.05)");
        Console.WriteLine();
        Console.WriteLine("  --ab-reference-engine <path>  Measure per-arm move loss with this engine after each game");
        Console.WriteLine("  --ab-reference-depth <n>      Fixed analysis depth (default: 18)");
        Console.WriteLine("  --ab-reference-option n=v     Reference-engine UCI option (repeatable)");
        Console.WriteLine();
        Console.WriteLine("  Exit codes: 0 adopt B, 4 discard B, 5 inconclusive, 1-3 usage or setup error.");
    }
}
