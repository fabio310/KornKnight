namespace ChessBot.MatchRunner;

using System.Text.Json;

/// <summary>Everything an A/B run between two engine binaries needs.</summary>
public sealed class ArmMatchConfig
{
    public required EngineArm ArmA { get; init; }
    public required EngineArm ArmB { get; init; }

    public required OpeningSet Openings { get; init; }
    public required MoveBudget Budget   { get; init; }

    /// <summary>Games to play. Rounded up to an even number: openings are played in colour pairs.</summary>
    public required int Games { get; init; }

    public required string OutputDir { get; init; }

    /// <summary>Null to play every game rather than stopping as soon as the answer is known.</summary>
    public SprtSettings? Sprt { get; init; }

    private readonly int? _concurrency;

    /// <summary>Explicit degree of parallelism, or the budget's default when unset.</summary>
    public int Concurrency
    {
        get => _concurrency ?? ConcurrencyPolicy.DefaultFor(Budget.Kind, PairedGames);
        init => _concurrency = value < 1 ? null : value;
    }

    public bool ConcurrencyIsDefault => _concurrency is null;

    /// <summary>
    /// Pin workers to cores and raise process priority. Applies only to a timed run: with a node
    /// budget the result is identical either way, so pinning would cost scheduling freedom and
    /// buy nothing.
    /// </summary>
    public bool PinWorkers { get; init; } = true;

    public string? ReferenceEnginePath { get; init; }
    public int     ReferenceDepth      { get; init; } = 18;
    public List<UciOptionSetting> ReferenceEngineOptions { get; init; } = new();
    public int     MoveLossMaxRetries  { get; init; } = 2;

    /// <summary>Requested games rounded up to a whole number of colour-reversed pairs.</summary>
    public int PairedGames => Games % 2 == 0 ? Games : Games + 1;

    public bool PinningApplies => PinWorkers && Budget.Kind == BudgetKind.Time;
}

/// <summary>
/// Result of an A/B run, reported from arm B's point of view — B is the change under test and A
/// the baseline, so a score above 50% means the change won.
///
/// The statistics are the ones this harness has always reported: a score rate with the standard
/// error around it, and the Elo those imply with a confidence interval, because a score is not
/// evidence of anything without the interval around it. SPRT sits on top and answers a different
/// question: when to stop, not how large the difference is.
/// </summary>
public sealed class ArmMatchResult
{
    public string RunId { get; set; } = string.Empty;
    public ArmIdentity? ArmA { get; set; }
    public ArmIdentity? ArmB { get; set; }

    public int Games { get; set; }
    public int WinsB { get; set; }
    public int Draws { get; set; }
    public int WinsA { get; set; }

    /// <summary>
    /// Games that ended in a protocol failure or an illegal move. Excluded from the score
    /// entirely rather than counted as draws: scoring a crash as half a point hands the arm that
    /// crashed free rating.
    /// </summary>
    public int Aborted { get; set; }

    /// <summary>Scored games in which arm A had White. A run stopped mid-pair is not balanced.</summary>
    public int GamesArmAAsWhite { get; set; }

    public string BudgetLabel { get; set; } = string.Empty;
    public RunConditions? Conditions { get; set; }

    public bool Resumed { get; set; }
    public int  GamesFromEarlierRun { get; set; }

    public List<string> TerminationReasons { get; set; } = new();

    public SprtSettings? SprtParameters { get; set; }
    public SprtState?    SprtProgress   { get; set; }

    /// <summary>Why the run ended: an SPRT bound, the game count, or being cut short.</summary>
    public string StopReason { get; set; } = string.Empty;

    public MoveLossAggregateDto? MoveLossArmA { get; set; }
    public MoveLossAggregateDto? MoveLossArmB { get; set; }

    /// <summary>B's score rate: (wins + draws/2) / games.</summary>
    public double ScoreRateB => Games > 0 ? (WinsB + Draws / 2.0) / Games : 0;

    /// <summary>
    /// Standard error of <see cref="ScoreRateB"/>, treating each game as an independent trial
    /// scoring 1, 0.5 or 0. Reported so a 51% score over 100 games is visibly indistinguishable
    /// from 50%.
    /// </summary>
    public double ScoreRateStdError
    {
        get
        {
            if (Games <= 1) return 0;

            double mean = ScoreRateB;
            double sumSq = WinsB * Math.Pow(1 - mean, 2)
                         + Draws * Math.Pow(0.5 - mean, 2)
                         + WinsA * Math.Pow(0 - mean, 2);

            return Math.Sqrt(sumSq / (Games - 1) / Games);
        }
    }

    /// <summary>
    /// Elo difference implied by the score rate, for B relative to A. Undefined at a clean sweep
    /// in either direction, where the logistic estimate is infinite.
    /// </summary>
    public double? EloDifference => Games == 0 ? null : Sprt.ScoreToElo(ScoreRateB);

    /// <summary>Half-width of the roughly 95% confidence interval on the Elo estimate.</summary>
    public double? EloMarginOfError
    {
        get
        {
            double p = ScoreRateB;
            double se = ScoreRateStdError;
            if (Games == 0 || se <= 0 || p <= 0 || p >= 1) return null;

            double lo = Math.Clamp(p - 1.96 * se, 1e-6, 1 - 1e-6);
            double hi = Math.Clamp(p + 1.96 * se, 1e-6, 1 - 1e-6);

            return (Sprt.ScoreToElo(hi)!.Value - Sprt.ScoreToElo(lo)!.Value) / 2;
        }
    }

    /// <summary>True when the 95% interval on the score rate excludes 50%.</summary>
    public bool IsSignificant => Games > 1 && Math.Abs(ScoreRateB - 0.5) > 1.96 * ScoreRateStdError;

    /// <summary>True when the scored games split colours evenly.</summary>
    public bool IsColourBalanced => Games - GamesArmAAsWhite == GamesArmAAsWhite;
}

/// <summary>
/// Plays an A/B run between two engine binaries: paired openings, a fixed degree of parallelism,
/// every finished game written to disk before the next one starts, and an optional SPRT that
/// stops the run as soon as it is conclusive.
/// </summary>
public static class ArmMatch
{
    /// <summary>
    /// One concurrent game's pair of engine processes, reused across the games that run in this
    /// slot. Starting an engine costs more than a short game does, so the processes outlive the
    /// game and are reset with "ucinewgame" between them.
    /// </summary>
    private sealed class ArmSlot : IDisposable
    {
        private readonly EngineArm _armA;
        private readonly EngineArm _armB;

        public UciAdapter? A { get; private set; }
        public UciAdapter? B { get; private set; }

        public ArmSlot(EngineArm armA, EngineArm armB)
        {
            _armA = armA;
            _armB = armB;
        }

        /// <summary>
        /// Starts either engine that is not running. An engine that has died must not take the
        /// rest of the run with it, so the slot restarts it rather than failing every later game
        /// scheduled onto it.
        /// </summary>
        public async Task EnsureStartedAsync(CancellationToken ct)
        {
            A = await EnsureAsync(A, _armA, ct);
            B = await EnsureAsync(B, _armB, ct);
        }

        private static async Task<UciAdapter> EnsureAsync(UciAdapter? existing, EngineArm arm, CancellationToken ct)
        {
            if (existing is { IsRunning: true }) return existing;

            existing?.Dispose();
            var adapter = new UciAdapter(arm.EnginePath);
            await adapter.InitializeAsync(ct);
            await arm.ApplyOptionsAsync(adapter, ct);
            return adapter;
        }

        /// <summary>
        /// Throws both engines away so the next game in this slot starts fresh ones.
        ///
        /// Used when a game is abandoned mid-search. The engine was told to "go" and nobody read
        /// its "bestmove", so a stray answer is still queued on its stdout — and because this
        /// engine answers "isready" while it is still searching, even a "ucinewgame"/"readyok"
        /// resynchronisation can complete *before* that stray line arrives. The next game would
        /// then read the previous game's move as its own, in a position where it is very likely
        /// illegal. Restarting costs a handshake at the end of a run and removes the hazard
        /// entirely.
        /// </summary>
        public void Discard()
        {
            A?.Dispose();
            B?.Dispose();
            A = null;
            B = null;
        }

        public void Dispose() => Discard();
    }

    public static async Task<ArmMatchResult> RunAsync(
        ArmMatchConfig cfg, IEnumerable<string> commandLineArgs, CancellationToken ct = default)
    {
        Directory.CreateDirectory(cfg.OutputDir);

        // Identities first. A run that dies during its first game should still have both arms
        // recorded, and the fingerprint that makes the run resumable is built out of them.
        Console.WriteLine("Identifying arms…");
        var identityA = await cfg.ArmA.IdentifyAsync(ct);
        var identityB = await cfg.ArmB.IdentifyAsync(ct);
        Console.WriteLine("  A (baseline) " + identityA.Describe());
        Console.WriteLine("  B (change)   " + identityB.Describe());

        if (identityA.BuiltFromDirtyTree || identityB.BuiltFromDirtyTree)
            Console.Error.WriteLine(
                "WARNING: an arm was built from a working tree with uncommitted changes, so its " +
                "result is not traceable to the commit it reports.");

        if (identityA.BinarySha256.Length > 0 && identityA.BinarySha256 == identityB.BinarySha256 &&
            cfg.ArmA.Fingerprint == cfg.ArmB.Fingerprint)
            Console.WriteLine(
                "NOTE: both arms are the same binary with the same options — a calibration run. " +
                "The expected result is 50%; anything else is the harness, not the engine.");

        string runId = DateTime.Now.ToString("yyyyMMdd_HHmmss");
        int concurrency = Math.Max(1, cfg.Concurrency);

        string oversubscription = ConcurrencyPolicy.WarnIfOversubscribed(cfg.Budget.Kind, concurrency);
        string processPriority = cfg.PinningApplies
            ? MachineTopology.RaiseProcessPriority()
            : System.Diagnostics.Process.GetCurrentProcess().PriorityClass.ToString();

        var conditions = new RunConditions
        {
            Budget                  = cfg.Budget.Kind.ToString(),
            Concurrency             = concurrency,
            ConcurrencySource       = cfg.ConcurrencyIsDefault ? "default" : "explicit",
            PhysicalCores           = MachineTopology.PhysicalCoreCount,
            LogicalProcessors       = MachineTopology.LogicalProcessorCount,
            CoreCountSource         = MachineTopology.CoreCountSource,
            ProcessPriority         = processPriority,
            CorePinningRequested    = cfg.PinningApplies,
            OversubscriptionWarning = oversubscription,
        };

        var state = new AbRunState
        {
            RunId      = runId,
            StartedUtc = DateTime.UtcNow.ToString("o"),
            ArmA       = identityA,
            ArmB       = identityB,
            Openings   = OpeningSetDto.From(cfg.Openings),
            Conditions = conditions,
            Budget     = cfg.Budget.ToString(),
            TotalGames = cfg.PairedGames,
            Sprt       = cfg.Sprt,
            CommandLineArgs = commandLineArgs.ToList(),
            Fingerprint = AbRunStore.FingerprintOf(
                cfg.ArmA.Fingerprint,
                cfg.ArmB.Fingerprint,
                cfg.Openings.Sha256,
                cfg.Budget.ToString(),
                cfg.PairedGames.ToString(),
                cfg.Sprt?.ToString() ?? "(no sprt)"),
        };

        using var store = AbRunStore.OpenOrCreate(cfg.OutputDir, state);

        int alreadyPlayed = store.Completed.Count;
        if (store.Resumed)
            Console.WriteLine($"Resuming run {store.State.RunId}: {alreadyPlayed} of {cfg.PairedGames} " +
                              "games were already played and are kept.");

        Console.WriteLine($"A/B run: {cfg.PairedGames} games at {cfg.Budget}, " +
                          $"{ConcurrencyPolicy.Describe(cfg.Budget.Kind, concurrency)}" +
                          (cfg.PinningApplies ? ", workers pinned" : "") + ".");
        Console.WriteLine($"Openings: {cfg.Openings}");
        if (cfg.Sprt is SprtSettings sprt) Console.WriteLine($"SPRT: {sprt}");

        var pending = Enumerable.Range(0, cfg.PairedGames)
            .Where(i => !store.Completed.ContainsKey(i))
            .ToList();

        using var stopping = CancellationTokenSource.CreateLinkedTokenSource(ct);
        string stopReason = $"played all {cfg.PairedGames} games";
        var stopLock = new object();

        // Exactly `concurrency` workers, each taking the next game from a shared queue in
        // schedule order. Not one task per game: queueing every game at once puts them all on
        // the thread pool, and the order in which they reach the gate is then whatever the pool
        // felt like — so even a concurrency of 1 played the games in an arbitrary order. That
        // made an SPRT run stop on an arbitrary subset of games, and "stopped after 12 games"
        // was not reproducible from the same command. The queue fixes the order; the worker
        // count fixes the parallelism.
        var queue = new Queue<int>(pending);
        var queueLock = new object();

        var slots = Enumerable.Range(0, concurrency).Select(_ => new ArmSlot(cfg.ArmA, cfg.ArmB)).ToArray();

        PinnedWorkerPool? workers = cfg.PinningApplies
            ? new PinnedWorkerPool(concurrency, raiseThreadPriority: true)
            : null;

        try
        {
            var running = new Task[concurrency];

            for (int w = 0; w < concurrency; w++)
            {
                var slot = slots[w];
                running[w] = Task.Run(async () =>
                {
                    while (!stopping.IsCancellationRequested)
                    {
                        int gameIndex;
                        lock (queueLock)
                        {
                            if (queue.Count == 0) return;
                            gameIndex = queue.Dequeue();
                        }

                        try
                        {
                            var record = workers is null
                                ? await PlayOneAsync(cfg, slot, gameIndex, stopping.Token)
                                : await workers.RunAsync(
                                    () => PlayOneAsync(cfg, slot, gameIndex, stopping.Token), CancellationToken.None);

                            store.Append(record);

                            var snapshot = Summarize(cfg, store, identityA, identityB, conditions, runId);
                            WriteReports(cfg, snapshot);

                            Console.WriteLine(
                                $"[{snapshot.Games + snapshot.Aborted}/{cfg.PairedGames}] game {gameIndex + 1}: " +
                                $"{record.Outcome} — {record.Termination}, {record.Plies} plies, {record.Opening}  |  " +
                                $"B {snapshot.ScoreRateB:P1} (+{snapshot.WinsB}={snapshot.Draws}-{snapshot.WinsA})" +
                                (snapshot.SprtProgress is SprtState s ? $"  LLR {s.Llr:F2}" : ""));

                            // Only ever at a colour-balanced point. Stopping mid-pair would end
                            // the run on a game count where one arm had White more often than
                            // the other, and that bias would land in the reported score.
                            if (snapshot.SprtProgress is { IsConclusive: true } conclusive && snapshot.IsColourBalanced)
                            {
                                lock (stopLock) stopReason = conclusive.Explanation;
                                stopping.Cancel();
                                return;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            // Stopped by SPRT or by the caller. Games already appended are kept;
                            // this one did not finish and is replayed if the run is resumed. Its
                            // engines are thrown away rather than reused: they were abandoned
                            // mid-search and still owe an answer nobody read.
                            slot.Discard();
                            return;
                        }
                    }
                }, CancellationToken.None);
            }

            await Task.WhenAll(running);
        }
        finally
        {
            foreach (var slot in slots) slot.Dispose();
            workers?.Dispose();
        }

        var result = Summarize(cfg, store, identityA, identityB, conditions, runId);
        result.Resumed = store.Resumed;
        result.GamesFromEarlierRun = store.Resumed ? alreadyPlayed : 0;

        if (result.Conditions is not null)
        {
            result.Conditions.CorePinningGranted = workers?.AllPinned ?? false;
            result.Conditions.PinnedCores        = workers?.Cores.ToList() ?? new List<int>();
        }

        int finished = result.Games + result.Aborted;
        if (ct.IsCancellationRequested)
            stopReason = $"cancelled after {finished} games; every finished game was kept and the run can be resumed";
        else if (finished < cfg.PairedGames && result.SprtProgress is not { IsConclusive: true })
            stopReason = $"stopped at {finished} of {cfg.PairedGames} games";

        result.StopReason = stopReason;
        WriteReports(cfg, result);
        return result;
    }

    private static async Task<ArmGameRecord> PlayOneAsync(
        ArmMatchConfig cfg, ArmSlot slot, int gameIndex, CancellationToken ct)
    {
        await slot.EnsureStartedAsync(ct);

        var (opening, armAIsWhite) = cfg.Openings.ScheduleFor(gameIndex);

        var game = await ArmGame.PlayAsync(
            white: armAIsWhite ? slot.A! : slot.B!,
            black: armAIsWhite ? slot.B! : slot.A!,
            cfg.ArmA, cfg.ArmB, armAIsWhite, opening, cfg.Budget,
            gameNumber: gameIndex + 1, cfg.OutputDir, ct);

        var record = new ArmGameRecord
        {
            GameIndex   = gameIndex,
            Opening     = opening.Name,
            ArmAIsWhite = armAIsWhite,
            Outcome     = game.Outcome switch
            {
                GameOutcome.ChessBotWin  => ArmOutcome.ArmAWin,
                GameOutcome.ChessBotLoss => ArmOutcome.ArmBWin,
                GameOutcome.Draw         => ArmOutcome.Draw,
                _                        => ArmOutcome.Aborted,
            },
            Termination = game.TerminationReason ?? "unknown",
            Plies       = game.Moves.Count,
            Pgn         = Path.GetFileName(game.PgnPath),
            FinishedUtc = DateTime.UtcNow.ToString("o"),
        };

        // Reference-engine move loss, after the game rather than during it, and once per arm: a
        // run can then say not only which arm won but how much each of them threw away.
        if (!string.IsNullOrWhiteSpace(cfg.ReferenceEnginePath))
        {
            using var reference = new UciAdapter(cfg.ReferenceEnginePath);
            await reference.InitializeAsync(ct);

            foreach (var option in cfg.ReferenceEngineOptions)
                await reference.SetOptionAsync(option.Name, option.Value);
            if (cfg.ReferenceEngineOptions.Count > 0)
                await reference.SyncAsync(ct: ct);

            var lossA = await MoveLossAnalyzer.AnalyzeGameAsync(
                game, reference, cfg.ReferenceDepth, m => m.IsChessBotMove, cfg.MoveLossMaxRetries, ct);
            var lossB = await MoveLossAnalyzer.AnalyzeGameAsync(
                game, reference, cfg.ReferenceDepth, m => !m.IsChessBotMove, cfg.MoveLossMaxRetries, ct);

            record.MoveLossArmA = MoveLossAggregateDto.From(lossA);
            record.MoveLossArmB = MoveLossAggregateDto.From(lossB);

            MoveLossAnalyzer.WriteReport(
                game, lossA.Concat(lossB).ToList(),
                Path.ChangeExtension(game.PgnPath, ".moveloss.log"),
                referenceEngineName: reference.EngineName,
                referenceEngineDepth: cfg.ReferenceDepth,
                referenceEngineOptions: cfg.ReferenceEngineOptions);
        }

        return record;
    }

    /// <summary>
    /// Folds the finished games into a result — always in schedule order and always from the
    /// durable log, so the numbers never depend on the order the workers happened to finish in,
    /// and a resumed run counts its earlier games exactly once.
    /// </summary>
    private static ArmMatchResult Summarize(
        ArmMatchConfig cfg, AbRunStore store,
        ArmIdentity identityA, ArmIdentity identityB, RunConditions conditions, string runId)
    {
        var result = new ArmMatchResult
        {
            RunId          = runId,
            ArmA           = identityA,
            ArmB           = identityB,
            BudgetLabel    = cfg.Budget.ToString(),
            Conditions     = conditions,
            SprtParameters = cfg.Sprt,
        };

        var lossA = new List<MoveLossAggregateDto>();
        var lossB = new List<MoveLossAggregateDto>();

        foreach (var record in store.Completed.OrderBy(kv => kv.Key).Select(kv => kv.Value))
        {
            result.TerminationReasons.Add(record.Termination);

            if (record.Outcome == ArmOutcome.Aborted)
            {
                result.Aborted++;
                continue;
            }

            result.Games++;
            if (record.ArmAIsWhite) result.GamesArmAAsWhite++;

            switch (record.Outcome)
            {
                case ArmOutcome.ArmAWin: result.WinsA++; break;
                case ArmOutcome.ArmBWin: result.WinsB++; break;
                default:                 result.Draws++; break;
            }

            if (record.MoveLossArmA is not null) lossA.Add(record.MoveLossArmA);
            if (record.MoveLossArmB is not null) lossB.Add(record.MoveLossArmB);
        }

        result.MoveLossArmA = MoveLossAggregateDto.Combine(lossA);
        result.MoveLossArmB = MoveLossAggregateDto.Combine(lossB);

        if (cfg.Sprt is SprtSettings settings)
            result.SprtProgress = Sprt.Evaluate(settings, result.WinsB, result.Draws, result.WinsA);

        return result;
    }

    private static readonly JsonSerializerOptions ReportJson = new() { WriteIndented = true };
    private static readonly object ReportLock = new();

    /// <summary>
    /// Rewrites the reports after every game, so a killed run leaves a readable result rather
    /// than only a log of games.
    ///
    /// Written to a temporary file and moved into place. A rewrite-in-place is exactly what a
    /// kill truncates, and a half-written report next to a complete game log would look like a
    /// corrupt run. <c>run_state.json</c> is never rewritten at all: it is what a resume depends
    /// on, so it is written once and then only read.
    /// </summary>
    private static void WriteReports(ArmMatchConfig cfg, ArmMatchResult result)
    {
        lock (ReportLock)
        {
            WriteAtomic(Path.Combine(cfg.OutputDir, "ab_result.json"),
                JsonSerializer.Serialize(result, ReportJson));
            WriteAtomic(Path.Combine(cfg.OutputDir, "ab_report.log"),
                BuildTextReport(cfg, result));
        }
    }

    private static void WriteAtomic(string path, string content)
    {
        string temp = path + ".tmp";
        File.WriteAllText(temp, content);
        File.Move(temp, path, overwrite: true);
    }

    public static string BuildTextReport(ArmMatchConfig cfg, ArmMatchResult r)
    {
        var w = new StringWriter();

        w.WriteLine("=== ChessBot A/B run: two builds, one question ===");
        w.WriteLine($"Run ID     : {r.RunId}");
        w.WriteLine($"Generated  : {DateTime.UtcNow:o}");
        w.WriteLine($"Budget     : {r.BudgetLabel}");
        w.WriteLine($"Conditions : {ConcurrencyPolicy.Describe(cfg.Budget.Kind, r.Conditions?.Concurrency ?? 1)}");
        w.WriteLine($"Openings   : {cfg.Openings}");
        if (r.Resumed) w.WriteLine($"Resumed    : yes — {r.GamesFromEarlierRun} games came from an earlier run");
        w.WriteLine();

        w.WriteLine("=== Arms ===");
        w.WriteLine($"  A (baseline) {r.ArmA?.Describe()}");
        w.WriteLine($"  B (change)   {r.ArmB?.Describe()}");
        if (r.ArmA?.BuiltFromDirtyTree == true || r.ArmB?.BuiltFromDirtyTree == true)
            w.WriteLine("  WARNING: an arm was built from a dirty tree and is not traceable to its commit.");
        w.WriteLine();

        w.WriteLine("=== Result (B against A) ===");
        w.WriteLine($"  Games            : {r.Games}" + (r.Aborted > 0 ? $"   ({r.Aborted} aborted, excluded from the score)" : ""));
        w.WriteLine($"  B result         : +{r.WinsB} ={r.Draws} -{r.WinsA}");
        w.WriteLine($"  B score rate     : {r.ScoreRateB:P2} ± {1.96 * r.ScoreRateStdError:P2} (95%)");
        w.WriteLine($"  Implied Elo (B-A): {(r.EloDifference?.ToString("+0.0;-0.0;0") ?? "n/a")}" +
                    (r.EloMarginOfError is double moe ? $" ± {moe:F1}" : ""));
        w.WriteLine($"  Significant      : {(r.IsSignificant ? "yes" : "no — the interval includes 50%")}");
        w.WriteLine($"  Colour balance   : A had White in {r.GamesArmAAsWhite} of {r.Games} scored games" +
                    (r.IsColourBalanced ? "" : "   (UNBALANCED — the run ended mid-pair)"));
        w.WriteLine();

        if (r.SprtParameters is SprtSettings settings)
        {
            w.WriteLine("=== SPRT ===");
            w.WriteLine($"  {settings}");
            if (r.SprtProgress is SprtState s)
            {
                w.WriteLine($"  LLR     : {s.Llr:F4}");
                w.WriteLine($"  Verdict : {s.Verdict}");
                w.WriteLine($"  {s.Explanation}");
            }
            w.WriteLine();
        }

        w.WriteLine($"Stopped because: {r.StopReason}");
        w.WriteLine();

        w.WriteLine("=== How games ended ===");
        foreach (var group in r.TerminationReasons.GroupBy(x => x).OrderByDescending(g => g.Count()))
            w.WriteLine($"  {group.Key,-34} {group.Count()}");
        w.WriteLine();

        if (r.MoveLossArmA is not null || r.MoveLossArmB is not null)
        {
            w.WriteLine("=== Reference-engine move loss ===");
            w.WriteLine($"  A: {r.MoveLossArmA?.ToString() ?? "(not measured)"}");
            w.WriteLine($"  B: {r.MoveLossArmB?.ToString() ?? "(not measured)"}");
            w.WriteLine("  Median and 95th percentile are per game only: an order statistic cannot be");
            w.WriteLine("  combined across games, so the per-game reports hold them instead.");
            w.WriteLine();
        }

        w.WriteLine("Game results are the objective. The score and its interval above are the only");
        w.WriteLine("numbers here that speak to strength; everything else is evidence for them.");

        return w.ToString();
    }
}
