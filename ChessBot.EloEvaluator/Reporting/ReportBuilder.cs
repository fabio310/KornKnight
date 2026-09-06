using ChessBot.EloEvaluator.Analysis;
using ChessBot.EloEvaluator.Models;

namespace ChessBot.EloEvaluator.Reporting;

/// <summary>
/// Aggregates a list of <see cref="ParsedGame"/> records into an <see cref="EloReport"/>.
/// </summary>
public sealed class ReportBuilder
{
    // Blunder threshold used when re-detecting blunders from per-move score data.
    // Matches the default MatchConfig.BlunderThresholdCp = 200.
    private const int BlunderThresholdCp = 200;

    private readonly EvaluatorConfig _cfg;

    public ReportBuilder(EvaluatorConfig cfg) => _cfg = cfg;

    public EloReport Build(IReadOnlyList<ParsedGame> allGames)
    {
        var report = new EloReport
        {
            RunId        = DateTime.Now.ToString("yyyyMMdd_HHmmss"),
            Timestamp    = DateTime.Now,
            PgnDir       = _cfg.PgnDir,
            ReferenceElo = _cfg.ReferenceElo
        };

        report.TotalGames = allGames.Count;

        // ── Validation issues ─────────────────────────────────────────────────
        report.ValidationIssues = allGames
            .Where(g => g.ValidationErrors.Count > 0)
            .SelectMany(g => g.ValidationErrors.Select(e => new ValidationIssue
            {
                SourceFile = Path.GetFileName(g.SourceFile),
                GameNumber = g.GameNumber,
                Issue      = e
            }))
            .ToList();

        var validGames = allGames.Where(g => g.IsValid).ToList();

        report.ValidGames   = validGames.Count;
        report.InvalidGames = allGames.Count - validGames.Count;

        // ── Outcome counts (valid games only) ────────────────────────────────
        foreach (var g in validGames)
        {
            switch (g.Outcome)
            {
                case GameOutcomeKind.Win:     report.Wins++;    break;
                case GameOutcomeKind.Draw:    report.Draws++;   break;
                case GameOutcomeKind.Loss:    report.Losses++;  break;
                case GameOutcomeKind.Aborted: report.Aborted++; break;
            }
        }

        // Scoring games = valid + has a decisive/draw outcome
        var scoring = validGames
            .Where(g => g.Outcome is GameOutcomeKind.Win
                                  or GameOutcomeKind.Draw
                                  or GameOutcomeKind.Loss)
            .ToList();

        // ── Overall Elo stats ────────────────────────────────────────────────
        var elo = EloCalculator.Calculate(
            report.Wins, report.Draws, report.Losses, _cfg.ReferenceElo);

        report.ScoreRate            = elo.ScoreRate;
        report.EloDiff              = elo.EloDiff;
        report.EstimatedElo         = elo.EstimatedElo;
        report.ConfidenceInterval95 = elo.ConfidenceInterval95;
        report.LOS                  = elo.LOS;
        report.IsReliable           = elo.IsReliable;
        report.ReliabilityNote      = elo.ReliabilityNote;

        // ── Color breakdown ──────────────────────────────────────────────────
        report.AsWhite = BuildColorStats(scoring.Where(g => g.ChessBotColor == "White").ToList());
        report.AsBlack = BuildColorStats(scoring.Where(g => g.ChessBotColor == "Black").ToList());

        // ── Engine performance (from log files that have stats) ───────────────
        report.EnginePerf = BuildEnginePerf(scoring);

        // ── Phase stats (from per-move data in log files) ────────────────────
        var withMoves = scoring.Where(g => g.Moves.Count > 0).ToList();
        report.Opening    = BuildPhaseStats(withMoves, "Opening",    m => m.MoveNumber <= 10);
        report.Middlegame = BuildPhaseStats(withMoves, "Middlegame", m => m.MoveNumber is > 10 and <= 30);
        report.Endgame    = BuildPhaseStats(withMoves, "Endgame",    m => m.MoveNumber > 30);

        // ── Cross-engine evaluation disagreement stats (NOT blunder/CP-loss stats) ──────────
        // Deliberately not mirrored into the legacy BlunderStats object: copying the numbers
        // there kept the old name alive as a parallel, serialized source of truth and invited
        // consumers to keep reading a disagreement count as if it were measured move loss.
        report.CrossEngineDisagreementStats = BuildDisagreementStats(scoring);

        // ── Per-game summaries ───────────────────────────────────────────────
        report.Games = allGames.Select(g => new GameSummary
        {
            GameNumber    = g.GameNumber,
            SourceFile    = Path.GetFileName(g.SourceFile),
            ChessBotColor = g.ChessBotColor,
            Result        = g.ResultTag,
            Outcome       = g.Outcome.ToString(),
            Termination   = g.TerminationReason,
            TotalPlies    = g.TotalPlies,
            AvgDepth      = g.CbAvgDepth,
            AvgNps        = g.CbAvgNps,
            PeakNps       = g.CbPeakNps,
            CrossEngineDisagreements = g.CrossEngineDisagreementsDetected,
            IsValid       = g.IsValid,
            Date          = g.Date
        }).ToList();

        return report;
    }

    // ── Helpers ───────────────────────────────────────────────────────────────

    private static ColorStats BuildColorStats(List<ParsedGame> games)
    {
        int w = games.Count(g => g.Outcome == GameOutcomeKind.Win);
        int d = games.Count(g => g.Outcome == GameOutcomeKind.Draw);
        int l = games.Count(g => g.Outcome == GameOutcomeKind.Loss);
        var e = EloCalculator.Calculate(w, d, l);
        return new ColorStats
        {
            Games           = games.Count,
            Wins            = w,
            Draws           = d,
            Losses          = l,
            ScoreRate       = e.ScoreRate,
            EloDiff         = e.EloDiff,
            IsReliable      = e.IsReliable,
            ReliabilityNote = e.ReliabilityNote
        };
    }

    private static EnginePerf BuildEnginePerf(List<ParsedGame> games)
    {
        var withNps   = games.Where(g => g.CbAvgNps.HasValue && g.CbAvgNps > 0).ToList();
        var withPeak  = games.Where(g => g.CbPeakNps.HasValue && g.CbPeakNps > 0).ToList();
        var withDepth = games.Where(g => g.CbAvgDepth.HasValue).ToList();
        var withNodes = games.Where(g => g.CbAvgNodesPerMove.HasValue).ToList();
        var withTime  = games.Where(g => g.CbAvgTimeMsPerMove.HasValue).ToList();
        var withTotal = games.Where(g => g.CbTotalNodes.HasValue).ToList();

        return new EnginePerf
        {
            AvgDepth        = withDepth.Count > 0 ? withDepth.Average(g => g.CbAvgDepth!.Value) : 0,
            AvgNps          = withNps.Count   > 0 ? withNps.Average(g => g.CbAvgNps!.Value)     : 0,
            PeakNps         = withPeak.Count  > 0 ? withPeak.Max(g => g.CbPeakNps!.Value)       : 0,
            AvgNodesPerMove = withNodes.Count > 0 ? withNodes.Average(g => g.CbAvgNodesPerMove!.Value) : 0,
            AvgTimeMsPerMove= withTime.Count  > 0 ? withTime.Average(g => g.CbAvgTimeMsPerMove!.Value) : 0,
            TotalNodes      = withTotal.Count > 0 ? withTotal.Sum(g => g.CbTotalNodes!.Value)    : 0
        };
    }

    private static PhaseStats BuildPhaseStats(
        List<ParsedGame> games, string name, Func<ParsedMoveData, bool> inPhase)
    {
        var cbMoves = games
            .SelectMany(g => g.Moves)
            .Where(m => m.IsChessBotMove && inPhase(m))
            .ToList();

        if (cbMoves.Count == 0)
            return new PhaseStats { PhaseName = name };

        // Re-detect cross-engine evaluation disagreements for this phase using per-move scores.
        // NOTE: this is a same-side (ChessBot-internal) score-swing heuristic, not the same-
        // engine move-loss measurement produced by MoveLossAnalyzer.
        int disagreements = 0;
        foreach (var g in games)
        {
            var phase = g.Moves.Where(m => inPhase(m)).ToList();
            for (int i = 1; i < phase.Count; i++)
            {
                var prev = phase[i - 1];
                var curr = phase[i];
                if (!prev.IsChessBotMove) continue;
                // Score swing = prev score (side-to-move positive, good for ChessBot)
                //             + curr score (now from opponent's side-to-move, also positive)
                // Large positive sum = ChessBot's position collapsed.
                if (prev.HasValidScore && curr.HasValidScore &&
                    prev.ScoreMate == null && curr.ScoreMate == null)
                {
                    int swing = prev.ScoreCp + curr.ScoreCp;
                    if (swing >= BlunderThresholdCp) disagreements++;
                }
            }
        }

        var scoredMoves = cbMoves.Where(m => m.HasValidScore && m.ScoreMate == null).ToList();
        var npsPositive = cbMoves.Where(m => m.Nps > 0).ToList();

#pragma warning disable CS0618
        return new PhaseStats
        {
            PhaseName   = name,
            MoveCount   = cbMoves.Count,
            AvgDepth    = cbMoves.Average(m => (double)m.Depth),
            AvgNps      = npsPositive.Count > 0 ? npsPositive.Average(m => (double)m.Nps) : 0,
            AvgScoreCp  = scoredMoves.Count > 0 ? scoredMoves.Average(m => (double)m.ScoreCp) : 0,
            CrossEngineDisagreements     = disagreements,
            CrossEngineDisagreementRate  = cbMoves.Count > 0 ? disagreements * 10.0 / cbMoves.Count : 0
        };
#pragma warning restore CS0618
    }

    private static CrossEngineDisagreementStats BuildDisagreementStats(List<ParsedGame> games)
    {
        int total  = games.Sum(g => g.CrossEngineDisagreementsDetected);
        int cbMoves = games.Sum(g => g.ChessBotPlies);
        int withDisagreements = games.Count(g => g.CrossEngineDisagreementsDetected > 0);

        return new CrossEngineDisagreementStats
        {
            TotalDisagreements     = total,
            TotalChessBotMoves     = cbMoves,
            DisagreementRate       = cbMoves > 0 ? total * 10.0 / cbMoves : 0,
            GamesWithDisagreements = withDisagreements
        };
    }
}
