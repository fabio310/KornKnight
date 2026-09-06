using System.Diagnostics;
using ChessBot.EloEvaluator.Analysis;
using ChessBot.EloEvaluator.Parsing;
using ChessBot.EloEvaluator.Reporting;
using ChessBot.MatchRunner;

namespace ChessBot.EloEvaluator.Sweep;

/// <summary>
/// Finds ChessBot's playing strength by binary-searching the opponent's UCI_Elo.
///
/// The ladder walks in the direction of the last result — win, climb; loss, drop —
/// and halves its step every time the direction reverses, so the levels probed
/// converge on the crossover point instead of marching past it:
///
///   1320 win  → +100 → 1420 win → +100 → 1520 loss → reverse, step 50
///   1470 win  → +50  → 1520 …            (bracket 1470–1520, estimate 1495)
///
/// The opponent is capped via UCI_LimitStrength + UCI_Elo; the games themselves are
/// played by <see cref="MatchExecutor"/> — no game logic is duplicated here.
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
            MinStep       = _cfg.MinStep,
            MaxElo        = _cfg.MaxElo,
            MinElo        = _cfg.MinElo,
            GamesPerRound = _cfg.GamesPerRound,
            MoveTimeMs    = _cfg.MoveTimeMs,
            OutDir        = Path.GetFullPath(_cfg.OutDir)
        };

        Directory.CreateDirectory(_cfg.OutDir);

        PrintHeader(result);

        // ── Adaptive ladder ───────────────────────────────────────────────────
        // Win  → climb by `step`. Lose → drop by `step`. Every time the direction
        // reverses, the answer is bracketed more tightly, so the step is halved.
        // The ladder stops once the step would fall below --min-step: at that point
        // the highest level beaten and the lowest level failed are less than one
        // step apart and the midpoint is the estimate.
        int roundNumber   = 0;
        int elo           = Math.Clamp(_cfg.StartElo, _cfg.MinElo, _cfg.MaxElo);
        int step          = _cfg.EloStep;
        int lastDirection = 0;              // +1 = climbing, -1 = dropping
        int? highestPassed = null;
        int? lowestFailed  = null;
        SweepRoundResult? decidingRound = null;

        while (roundNumber < _cfg.MaxRounds)
        {
            ct.ThrowIfCancellationRequested();

            var round = await PlayRoundAsync(++roundNumber, elo, step, result, ct);
            result.Rounds.Add(round);

            int direction = round.Passed ? +1 : -1;

            if (round.Passed)
            {
                if (highestPassed is null || elo > highestPassed) highestPassed = elo;
            }
            else
            {
                if (lowestFailed is null || elo < lowestFailed) { lowestFailed = elo; decidingRound = round; }
            }

            // Direction reversal ⇒ the answer is bracketed ⇒ search finer.
            if (lastDirection != 0 && direction != lastDirection)
            {
                int halved = step / 2;
                if (halved < _cfg.MinStep)
                {
                    result.StopReason =
                        $"converged — step would fall below --min-step {_cfg.MinStep}";
                    Console.WriteLine();
                    Console.WriteLine($"    Ladder converged: step {step} → below --min-step {_cfg.MinStep}. Stopping.");
                    break;
                }

                step = halved;
                Console.WriteLine($"    Direction reversed → step halved to {step}.");
            }

            lastDirection = direction;

            int next = Math.Clamp(elo + direction * step, _cfg.MinElo, _cfg.MaxElo);

            if (next == elo)
            {
                // Clamped against the engine's own Elo range — cannot probe further.
                if (direction > 0)
                {
                    result.ReachedMaxElo = true;
                    result.StopReason    = $"reached the engine's maximum Elo ({_cfg.MaxElo})";
                }
                else
                {
                    result.ReachedMinElo = true;
                    result.StopReason    = $"reached the engine's minimum Elo ({_cfg.MinElo})";
                }
                Console.WriteLine();
                Console.WriteLine($"    {result.StopReason}. Stopping.");
                break;
            }

            round.NextElo = next;
            Console.WriteLine($"    Ladder: {elo} → {next}  (step ±{step})");
            elo = next;

            if (roundNumber >= _cfg.MaxRounds)
                result.StopReason = $"hit --max-rounds {_cfg.MaxRounds}";
        }

        if (string.IsNullOrEmpty(result.StopReason))
            result.StopReason = $"hit --max-rounds {_cfg.MaxRounds}";

        // ── Verdict ───────────────────────────────────────────────────────────
        result.HighestPassedElo = highestPassed;
        result.LowestFailedElo  = lowestFailed;
        result.FinalStep        = step;
        result.PerformanceElo   = decidingRound?.EstimatedElo;
        result.DurationSeconds  = sw.Elapsed.TotalSeconds;
        result.ReliabilityNote  = (decidingRound ?? result.Rounds.LastOrDefault())?.ReliabilityNote ?? string.Empty;

        var verdict = SweepVerdict.Build(highestPassed, lowestFailed,
                                         _cfg.GamesPerRound,
                                         atEngineFloor: _cfg.MinElo <= SweepConfig.StockfishMinElo);
        result.EstimatedElo = verdict.EstimatedElo;
        result.BracketWidth = verdict.BracketWidth;
        result.Verdict      = verdict.Verdict;

        return result;
    }
    // ── One round ─────────────────────────────────────────────────────────────

    private async Task<SweepRoundResult> PlayRoundAsync(int roundNumber, int elo, int step,
                                                        SweepResult sweep, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        Console.WriteLine();
        Console.WriteLine($"=== Round {roundNumber}: Stockfish Elo {elo} ===  (step ±{step})");

        string roundDir = ResolveRoundDir(elo, sweep.RunId);
        Directory.CreateDirectory(roundDir);

        Console.WriteLine($"    {_cfg.GamesPerRound} games @ {_cfg.MoveTimeMs}ms/move → {roundDir}");

        var matchCfg = new MatchConfig
        {
            ExternalEnginePath = _cfg.EnginePath,
            MoveTimeMs         = _cfg.MoveTimeMs,
            GamesPerSide       = _cfg.GamesPerSide,
            PgnOutputDir       = roundDir,
            DisagreementThresholdCp = _cfg.DisagreementThresholdCp,
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
            Step                 = step,
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
        // gets the full .txt/.json/.csv report (depth, NPS, disagreements, per-game table).
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
            round.Disagreements = report.CrossEngineDisagreementStats.TotalDisagreements;
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
        Console.WriteLine($"Start Elo     : {r.StartElo}   (bounds {r.MinElo}–{r.MaxElo})");
        Console.WriteLine($"Step          : ±{r.EloStep}, halved on every direction change, " +
                          $"down to ±{r.MinStep}");
        Console.WriteLine($"Games/round   : {r.GamesPerRound}  ({_cfg.GamesPerSide} per color)");
        Console.WriteLine($"Move time     : {r.MoveTimeMs}ms");
        Console.WriteLine($"Max rounds    : {_cfg.MaxRounds}");
        Console.WriteLine($"Output dir    : {r.OutDir}");
        Console.WriteLine();
        Console.WriteLine("Ladder        : win → climb by the step, loss → drop by the step.");
        Console.WriteLine("                Each reversal halves the step until the bracket is");
        Console.WriteLine($"                narrower than ±{r.MinStep}; the midpoint is the estimate.");
    }

    private static void PrintRoundResult(SweepRoundResult r)
    {
        // Deliberately does not name the next level: the step may still be halved by the
        // caller before the ladder moves, so announcing it here would print the wrong one.
        string verdict = r.Passed ? "WIN — climbing" : $"LOSS — dropping ({r.StopReason})";

        Console.WriteLine();
        Console.WriteLine($"=== Round {r.RoundNumber}: Stockfish Elo {r.StockfishElo} === " +
                          $"Result: {r.Record} " +
                          $"(score {r.ScoreRate:P0}, EloDiff {r.EloDiff:+0;-0;0}) — {verdict}");
        Console.WriteLine($"    performance Elo {r.EstimatedElo:F0} ±{r.ConfidenceInterval95:F0}, " +
                          $"LOS {r.LOS:P0}, {r.DurationSeconds:F0}s — {r.ReliabilityNote}");
    }
}
