using System.Diagnostics;
using ChessBot.EloEvaluator.Analysis;
using ChessBot.EloEvaluator.Parsing;
using ChessBot.EloEvaluator.Reporting;
using ChessBot.MatchRunner;

namespace ChessBot.EloEvaluator.Sweep;

/// <summary>
/// Plays rounds against a local UCI engine whose strength is capped via
/// UCI_LimitStrength + UCI_Elo, raising the cap every round until ChessBot stops
/// scoring above 50%. The games themselves are played by
/// <see cref="MatchExecutor"/> — no game logic is duplicated here.
/// </summary>
public sealed class SweepRunner
{
    private readonly SweepConfig _cfg;

    public SweepRunner(SweepConfig cfg) => _cfg = cfg;

    public async Task<SweepResult> RunAsync(CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();

        var result = new SweepResult
        {
            RunId         = DateTime.Now.ToString("yyyyMMdd_HHmmss"),
            Timestamp     = DateTime.Now,
            EnginePath    = _cfg.EnginePath,
            StartElo      = _cfg.StartElo,
            EloStep       = _cfg.EloStep,
            MaxElo        = _cfg.MaxElo,
            GamesPerRound = _cfg.GamesPerRound,
            MoveTimeMs    = _cfg.MoveTimeMs,
            OutDir        = Path.GetFullPath(_cfg.OutDir)
        };

        Directory.CreateDirectory(_cfg.OutDir);

        PrintHeader(result);

        int roundNumber = 0;
        SweepRoundResult? firstFailed = null;
        int? highestPassed = null;
        int currentElo = _cfg.StartElo;

        // ── Linear sweep ──────────────────────────────────────────────────────
        while (currentElo <= _cfg.MaxElo)
        {
            ct.ThrowIfCancellationRequested();

            var round = await PlayRoundAsync(++roundNumber, currentElo, isRefinement: false, result, ct);
            result.Rounds.Add(round);

            if (!round.Passed)
            {
                firstFailed = round;
                break;
            }

            highestPassed = currentElo;

            if (currentElo == _cfg.MaxElo)
                break;

            currentElo = Math.Min(currentElo + _cfg.EloStep, _cfg.MaxElo);
        }

        // ── Optional bisection between the last passed and the first failed level ──
        if (_cfg.Refine && firstFailed is not null && highestPassed is int passed)
        {
            int lo = passed;             // known: ChessBot scores > 50%
            int hi = firstFailed.StockfishElo;  // known: ChessBot scores <= 50%

            while (hi - lo > _cfg.RefineStep)
            {
                ct.ThrowIfCancellationRequested();

                int mid = lo + (hi - lo) / 2;
                if (mid <= lo || mid >= hi)
                    break;

                var round = await PlayRoundAsync(++roundNumber, mid, isRefinement: true, result, ct);
                result.Rounds.Add(round);

                if (round.Passed)
                {
                    lo = mid;
                    highestPassed = mid;
                }
                else
                {
                    hi = mid;
                    firstFailed = round;
                }
            }
        }

        // ── Verdict ───────────────────────────────────────────────────────────
        result.HighestPassedElo = highestPassed;
        result.DurationSeconds  = sw.Elapsed.TotalSeconds;

        if (firstFailed is null)
        {
            result.ReachedMaxElo = true;
            result.EstimatedElo  = null;
            var last = result.Rounds.LastOrDefault();
            result.PerformanceElo = last?.EstimatedElo;
            result.Verdict =
                $"ChessBot still scored above 50% at the highest tested level " +
                $"(Stockfish Elo {highestPassed ?? _cfg.MaxElo}). Its strength is at least " +
                $"Stockfish Elo {highestPassed ?? _cfg.MaxElo} (UCI_Elo) — raise --max-elo to narrow it down.";
            result.ReliabilityNote = last?.ReliabilityNote ?? string.Empty;
        }
        else
        {
            result.EstimatedElo   = firstFailed.StockfishElo;
            result.PerformanceElo = firstFailed.EstimatedElo;
            result.FailedAtStart  = highestPassed is null;

            if (result.FailedAtStart)
            {
                result.Verdict =
                    $"ChessBot's estimated strength ≈ Stockfish Elo {firstFailed.StockfishElo} (UCI_Elo) — " +
                    $"it already failed the first level, so its true strength is at or below that. " +
                    $"Round performance Elo: {firstFailed.EstimatedElo:F0}. Lower --start-elo to bracket it.";
            }
            else
            {
                result.Verdict =
                    $"ChessBot's estimated strength ≈ Stockfish Elo {firstFailed.StockfishElo} (UCI_Elo) — " +
                    $"it beat Stockfish up to Elo {highestPassed} and stopped scoring above 50% at " +
                    $"Elo {firstFailed.StockfishElo}. Round performance Elo: {firstFailed.EstimatedElo:F0}.";
            }

            result.ReliabilityNote = firstFailed.ReliabilityNote;
        }

        return result;
    }

    // ── One round ─────────────────────────────────────────────────────────────

    private async Task<SweepRoundResult> PlayRoundAsync(int roundNumber, int elo, bool isRefinement,
                                                        SweepResult sweep, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        Console.WriteLine();
        Console.WriteLine($"=== Round {roundNumber}: Stockfish Elo {elo} ==={(isRefinement ? "  [refinement]" : "")}");

        string roundDir = ResolveRoundDir(elo, sweep.RunId);
        Directory.CreateDirectory(roundDir);

        Console.WriteLine($"    {_cfg.GamesPerRound} games @ {_cfg.MoveTimeMs}ms/move → {roundDir}");

        var matchCfg = new MatchConfig
        {
            ExternalEnginePath = _cfg.EnginePath,
            MoveTimeMs         = _cfg.MoveTimeMs,
            GamesPerSide       = _cfg.GamesPerSide,
            PgnOutputDir       = roundDir,
            BlunderThresholdCp = _cfg.BlunderThresholdCp,
            Verbose            = _cfg.Verbose,
            EngineElo          = elo
        };

        var outcome = await MatchExecutor.RunAsync(matchCfg, ct);

        if (!string.IsNullOrWhiteSpace(outcome.OpponentName) && outcome.OpponentName != "Unknown")
            sweep.OpponentName = outcome.OpponentName;

        // referenceElo = the level just played, so EloDiff > 0 means "stronger than this level"
        var stats = EloCalculator.Calculate(outcome.Wins, outcome.Draws, outcome.Losses,
                                            referenceElo: elo);

        var round = new SweepRoundResult
        {
            RoundNumber          = roundNumber,
            StockfishElo         = elo,
            IsRefinement         = isRefinement,
            Games                = stats.N,
            Wins                 = stats.Wins,
            Draws                = stats.Draws,
            Losses               = stats.Losses,
            ScoreRate            = stats.ScoreRate,
            EloDiff              = stats.EloDiff,
            EstimatedElo         = stats.EstimatedElo,
            ConfidenceInterval95 = stats.ConfidenceInterval95,
            LOS                  = stats.LOS,
            ReliabilityNote      = stats.ReliabilityNote,
            RoundDir             = roundDir,
            Passed               = stats.ScoreRate > 0.5 && stats.Wins > 0,
            DurationSeconds      = sw.Elapsed.TotalSeconds
        };

        if (!round.Passed)
        {
            round.StopReason = stats.Wins == 0
                ? $"no wins in {stats.N} game(s)"
                : $"score rate {stats.ScoreRate:P1} ≤ 50%";
        }

        // Run the normal report pipeline over the round directory so each level also
        // gets the full .txt/.json/.csv report (depth, NPS, blunders, per-game table).
        AttachRoundReport(round, roundDir, elo);

        PrintRoundResult(round);

        return round;
    }

    /// <summary>
    /// "elo_1500" for a fresh level. If that directory already holds games from an earlier
    /// sweep, this run gets its own "elo_1500_&lt;runId&gt;" instead — otherwise the per-round
    /// report would be built from a mix of old and new games.
    /// </summary>
    private string ResolveRoundDir(int elo, string runId)
    {
        string dir = Path.Combine(_cfg.OutDir, $"elo_{elo}");

        if (Directory.Exists(dir) &&
            (Directory.GetFiles(dir, "*.pgn").Length > 0 ||
             Directory.GetFiles(dir, "game*.log").Length > 0))
        {
            dir = Path.Combine(_cfg.OutDir, $"elo_{elo}_{runId}");
            Console.WriteLine($"    NOTE: elo_{elo} already holds games from an earlier sweep — " +
                              $"writing this round to {Path.GetFileName(dir)} instead.");
        }

        return dir;
    }

    /// <summary>
    /// Reuses LogFileParser/PgnFileParser + ReportBuilder + ReportWriter on the round's
    /// output directory. Failures here are non-fatal — the sweep verdict comes from the
    /// games actually played, not from re-parsing them.
    /// </summary>
    private void AttachRoundReport(SweepRoundResult round, string roundDir, int elo)
    {
        try
        {
            var games = GameCollector.Collect(roundDir, verbose: false, announceCounts: false);
            if (games.Count == 0)
                return;

            foreach (var g in games)
                GameValidator.Validate(g);

            var evalCfg = EvaluatorConfig.ForDirectory(roundDir, roundDir, referenceElo: elo);
            var report  = new ReportBuilder(evalCfg).Build(games);
            new ReportWriter(roundDir).Write(report);

            round.AvgDepth = report.EnginePerf.AvgDepth > 0 ? report.EnginePerf.AvgDepth : null;
            round.AvgNps   = report.EnginePerf.AvgNps   > 0 ? report.EnginePerf.AvgNps   : null;
            round.Blunders = report.BlunderStats.TotalBlunders;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"    NOTE: per-round report could not be built: {ex.Message}");
        }
    }

    // ── Console output ────────────────────────────────────────────────────────

    private void PrintHeader(SweepResult r)
    {
        Console.WriteLine("╔══════════════════════════════════════════════════════════════╗");
        Console.WriteLine("║        ChessBot Elo Evaluator — Strength Sweep                ║");
        Console.WriteLine("╚══════════════════════════════════════════════════════════════╝");
        Console.WriteLine();
        Console.WriteLine($"Opponent      : {r.EnginePath}");
        Console.WriteLine($"Elo range     : {r.StartElo} → {r.MaxElo}  (step {r.EloStep})");
        Console.WriteLine($"Games/round   : {r.GamesPerRound}  ({_cfg.GamesPerSide} per color)");
        Console.WriteLine($"Move time     : {r.MoveTimeMs}ms");
        Console.WriteLine($"Output dir    : {r.OutDir}");
        if (_cfg.Refine)
            Console.WriteLine($"Refinement    : on (bisect down to ±{_cfg.RefineStep} Elo)");
        Console.WriteLine();
        Console.WriteLine("Stop condition: score rate ≤ 50% (EloDiff ≤ 0) or zero wins in a round.");
    }

    private static void PrintRoundResult(SweepRoundResult r)
    {
        string verdict = r.Passed ? "continuing" : $"STOP — {r.StopReason}";
        Console.WriteLine();
        Console.WriteLine($"=== Round {r.RoundNumber}: Stockfish Elo {r.StockfishElo} === " +
                          $"Result: {r.Record} " +
                          $"(score {r.ScoreRate:P0}, EloDiff {r.EloDiff:+0;-0;0}) — {verdict}");
        Console.WriteLine($"    performance Elo {r.EstimatedElo:F0} ±{r.ConfidenceInterval95:F0}, " +
                          $"LOS {r.LOS:P0}, {r.DurationSeconds:F0}s — {r.ReliabilityNote}");
    }
}
