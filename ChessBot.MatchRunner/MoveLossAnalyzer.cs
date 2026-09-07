namespace ChessBot.MatchRunner;

/// <summary>
/// Real per-move centipawn-loss measurement for ChessBot's moves, computed with a single
/// reference engine (normally Stockfish) analysing the *same* position twice:
///
///   1. Unrestricted search from the pre-move FEN — the reference engine's own best score
///      for the position, i.e. what it considers the best available move to be worth.
///   2. The same search restricted to the move ChessBot actually played, via UCI "searchmoves"
///      (<see cref="UciAdapter.AnalyzeFenAsync"/>).
///
/// Centipawn loss = (unrestricted best score) − (score of ChessBot's played move), both from
/// the same engine, same position, same side-to-move perspective. This is the standard
/// same-engine move-loss definition and is not comparable to <see cref="PositionAnalyzer"/>'s
/// <see cref="CrossEngineEvaluationDisagreementRecord"/>, which mixes two different engines'
/// scores from two different searches and is not a loss measurement.
///
/// Must be run *after* the timed game completes (never during play), since these are deep,
/// slow analysis searches that would otherwise interfere with move-time budgets.
/// </summary>
public static class MoveLossAnalyzer
{
    /// <summary>
    /// Why a sample is (in)eligible for the exact CP-vs-CP aggregate. A sample is only ever
    /// "Exact" when both the unrestricted best score and the restricted played score resolved
    /// to non-mate, non-bound (exact) centipawn values with best >= played.
    /// </summary>
    public enum SampleEligibility
    {
        /// <summary>Both scores are exact centipawns and best &gt;= played; safe to average.</summary>
        Exact,
        /// <summary>One or both sides involved a forced mate; reported as a mate category instead.</summary>
        MateInvolved,
        /// <summary>
        /// A bound (lowerbound/upperbound) score could not be resolved to exact within the
        /// configured retry budget. Reported as an interval instead of a point estimate.
        /// </summary>
        UnresolvedBound,
        /// <summary>
        /// The restricted (played-move) search scored *higher* than the unrestricted best-move
        /// search from the same engine at the same depth — a real inconsistency (different
        /// move ordering / TT state / search instability between the two searches), not simply
        /// "no loss". Silently clamping this to zero would hide the inconsistency. Excluded
        /// from exact aggregates until resolved by a deterministic retry at greater depth.
        /// </summary>
        Inconsistent,
        /// <summary>
        /// The reference engine failed for this position (process died, timed out, returned
        /// nothing usable). Recorded per position so one failure excludes one sample instead of
        /// aborting the analysis and losing the whole match's artifacts.
        /// </summary>
        AnalysisError
    }

    /// <summary>Mate-outcome category for a move, independent of any centipawn measurement.</summary>
    public enum MateCategory
    {
        None,
        /// <summary>The unrestricted search found a forced mate that the played move missed.</summary>
        MissedForcedMate,
        /// <summary>The played move itself walks into (or deepens) being forced-mated.</summary>
        EnteredForcedMate,
        /// <summary>
        /// Both sides report a forced mate for the same side and at the same distance — the
        /// played move is as good as the best move. Distinct from
        /// <see cref="MateDistanceChanged"/>, which previously absorbed this case and reported
        /// an equal outcome as a change.
        /// </summary>
        MateUnchanged,
        /// <summary>Both best and played are mate scores for the same side, but distance changed.</summary>
        MateDistanceChanged,
        /// <summary>
        /// The played move escapes a forced mate the unrestricted search reported — the two
        /// analyses contradict each other rather than describing a better or worse move.
        /// </summary>
        EscapedForcedMate,
        /// <summary>
        /// The two searches disagree about which side is being mated. Cannot be a move-quality
        /// statement; kept separate so it is investigated rather than aggregated.
        /// </summary>
        ContradictoryMate
    }

    /// <summary>One measured ChessBot move's centipawn loss, as judged by the reference engine.</summary>
    public sealed record MoveLossRecord(
        int    MoveNumber,
        bool   IsWhiteMove,
        string Move,
        string Fen,
        int?   BestScoreCp,       // reference engine's unrestricted best score (side-to-move positive); null if mate
        int?   BestScoreMate,     // mate-in-N for the unrestricted best move; null if not mate
        string BestScoreBound,    // "exact" | "lowerbound" | "upperbound" — after retry resolution
        int?   PlayedScoreCp,     // reference engine's score restricted to the move played; null if mate
        int?   PlayedScoreMate,   // mate-in-N for the played move; null if not mate
        string PlayedScoreBound,
        int?   CentipawnLoss,     // only set (and only meaningful) when Eligibility == Exact
        SampleEligibility Eligibility,
        MateCategory      Mate,
        int    RetriesUsed,       // deterministic re-search attempts consumed resolving this sample
        string? IntervalNote);    // human-readable interval/inconsistency note when not Exact

    /// <summary>
    /// Analyses every ChessBot move in <paramref name="game"/> with <paramref name="referenceEngine"/>
    /// (already initialized) at a fixed <paramref name="depth"/>, and returns one
    /// <see cref="MoveLossRecord"/> per ChessBot move.
    ///
    /// Each position is reconstructed from <see cref="GameResult.InitialFen"/> plus the full move
    /// history preceding it (not from the standalone per-move FEN), so the reference engine sees
    /// the same repetition history the game itself had — a bare FEN cannot encode how many times
    /// a position was already reached.
    /// </summary>
    public static async Task<List<MoveLossRecord>> AnalyzeGameAsync(
        GameResult game,
        UciAdapter referenceEngine,
        int depth,
        int maxRetries = 2,
        CancellationToken ct = default)
    {
        var records = new List<MoveLossRecord>();
        var history  = new List<string>();

        foreach (var move in game.Moves)
        {
            ct.ThrowIfCancellationRequested();

            if (!move.IsChessBotMove)
            {
                // Still need to advance the reconstructed history for later ChessBot moves,
                // even though this move itself isn't analyzed.
                history.Add(move.UciMove);
                continue;
            }

            var record = await AnalyzeOnePositionAsync(
                game, move, history, referenceEngine, depth, maxRetries, ct);
            records.Add(record);

            history.Add(move.UciMove);
        }

        return records;
    }

    private static async Task<MoveLossRecord> AnalyzeOnePositionAsync(
        GameResult game,
        MoveRecord move,
        List<string> historyBeforeMove,
        UciAdapter referenceEngine,
        int depth,
        int maxRetries,
        CancellationToken ct)
    {
        int retries = 0;
        UciMoveResult best, played;

        // A reference-engine failure must cost one sample, not the whole match: the remaining
        // games and their artifacts are still worth producing.
        try
        {
            // Unrestricted: the reference engine's own opinion of the best move here.
            best = await AnalyzeWithHistoryAsync(referenceEngine, game.InitialFen, historyBeforeMove, depth, null, ct);

            // Restricted to the move ChessBot actually played, so the score reflects the value
            // of that specific move rather than of the position as a whole.
            played = await AnalyzeWithHistoryAsync(referenceEngine, game.InitialFen, historyBeforeMove, depth, move.UciMove, ct);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return MakeErrorRecord(move, retries, ex);
        }

        // ── Resolve bound scores BEFORE classifying the mate outcome ─────────────────────
        // A bounded score is not a value, it is a one-sided constraint, and that applies to
        // mate scores too: "mate in at most N" from a fail-high is not evidence of a forced
        // mate. Classifying first turned such a bound into a final mate category that no later
        // retry could revise. Both sides are always re-run at the same new depth — re-running
        // only the bounded one compared an unrestricted score at one depth against a restricted
        // score at another, which is not a comparison of the same position at all.
        int extraDepth = 0;
        try
        {
            while ((best.ScoreBound != "exact" || played.ScoreBound != "exact") && retries < maxRetries)
            {
                retries++;
                extraDepth += 4;
                int retryDepth = depth + extraDepth;

                best   = await AnalyzeWithHistoryAsync(referenceEngine, game.InitialFen, historyBeforeMove, retryDepth, null, ct);
                played = await AnalyzeWithHistoryAsync(referenceEngine, game.InitialFen, historyBeforeMove, retryDepth, move.UciMove, ct);
            }
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return MakeErrorRecord(move, retries, ex);
        }

        // ── Mate categorization, on resolved scores ──────────────────────────────────────
        // Mate scores are never mixed into centipawn aggregates.
        var mateCategory = ClassifyMate(best, played);
        if (mateCategory != MateCategory.None)
        {
            string label = best.ScoreBound == "exact" && played.ScoreBound == "exact"
                ? "mate category"
                : "mate category (from an unresolved bound — treat as provisional)";
            return MakeMateRecord(move, best, played, mateCategory, retries, label);
        }

        if (best.ScoreBound != "exact" || played.ScoreBound != "exact")
        {
            return new MoveLossRecord(
                move.MoveNumber, move.IsWhiteMove, move.UciMove, move.Fen,
                best.ScoreCp, best.ScoreMate, best.ScoreBound,
                played.ScoreCp, played.ScoreMate, played.ScoreBound,
                CentipawnLoss: null, Eligibility: SampleEligibility.UnresolvedBound, Mate: MateCategory.None,
                RetriesUsed: retries, IntervalNote: DescribeBoundInterval(best, played));
        }

        // ── Both exact. If played scores higher than best, this is a real inconsistency
        // between two searches of the same engine (different move ordering / TT state), not
        // simply "zero loss" — silently clamping it would hide the inconsistency. Retry
        // deterministically at greater depth before giving up.
        int loss = best.ScoreCp - played.ScoreCp;
        while (loss < 0 && retries < maxRetries)
        {
            retries++;
            extraDepth += 4;
            int retryDepth = depth + extraDepth;

            try
            {
                best   = await AnalyzeWithHistoryAsync(referenceEngine, game.InitialFen, historyBeforeMove, retryDepth, null, ct);
                played = await AnalyzeWithHistoryAsync(referenceEngine, game.InitialFen, historyBeforeMove, retryDepth, move.UciMove, ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return MakeErrorRecord(move, retries, ex);
            }

            var remateCategory = ClassifyMate(best, played);
            if (remateCategory != MateCategory.None)
                return MakeMateRecord(move, best, played, remateCategory, retries, "mate category found on inconsistency retry");

            if (best.ScoreBound != "exact" || played.ScoreBound != "exact")
            {
                return new MoveLossRecord(
                    move.MoveNumber, move.IsWhiteMove, move.UciMove, move.Fen,
                    best.ScoreCp, best.ScoreMate, best.ScoreBound,
                    played.ScoreCp, played.ScoreMate, played.ScoreBound,
                    CentipawnLoss: null, Eligibility: SampleEligibility.UnresolvedBound, Mate: MateCategory.None,
                    RetriesUsed: retries, IntervalNote: DescribeBoundInterval(best, played));
            }
            loss = best.ScoreCp - played.ScoreCp;
        }

        if (loss < 0)
        {
            return new MoveLossRecord(
                move.MoveNumber, move.IsWhiteMove, move.UciMove, move.Fen,
                best.ScoreCp, best.ScoreMate, best.ScoreBound,
                played.ScoreCp, played.ScoreMate, played.ScoreBound,
                CentipawnLoss: null, Eligibility: SampleEligibility.Inconsistent, Mate: MateCategory.None,
                RetriesUsed: retries,
                IntervalNote: $"played ({played.ScoreCp}cp) scored above best ({best.ScoreCp}cp) after {retries} retries — unresolved analysis inconsistency, excluded from exact aggregates");
        }

        return new MoveLossRecord(
            move.MoveNumber, move.IsWhiteMove, move.UciMove, move.Fen,
            best.ScoreCp, null, "exact",
            played.ScoreCp, null, "exact",
            CentipawnLoss: loss, Eligibility: SampleEligibility.Exact, Mate: MateCategory.None,
            RetriesUsed: retries, IntervalNote: null);
    }

    private static MoveLossRecord MakeMateRecord(
        MoveRecord move, UciMoveResult best, UciMoveResult played, MateCategory category, int retries, string noteLabel)
        => new(
            move.MoveNumber, move.IsWhiteMove, move.UciMove, move.Fen,
            best.ScoreMate.HasValue ? null : best.ScoreCp, best.ScoreMate, best.ScoreBound,
            played.ScoreMate.HasValue ? null : played.ScoreCp, played.ScoreMate, played.ScoreBound,
            CentipawnLoss: null, Eligibility: SampleEligibility.MateInvolved, Mate: category,
            RetriesUsed: retries, IntervalNote: $"{noteLabel}: {category}");

    private static Task<UciMoveResult> AnalyzeWithHistoryAsync(
        UciAdapter engine, string initialFen, IReadOnlyList<string> history, int depth,
        string? restrictToMove, CancellationToken ct)
        => engine.AnalyzeFenAsync(initialFen, depth, restrictToMove, ct, moveHistory: history);

    /// <summary>
    /// Classifies a mate-involved sample. Never converts mate distance into artificial
    /// centipawns for averaging — mate outcomes are reported as separate categories instead.
    /// </summary>
    private static MateCategory ClassifyMate(UciMoveResult best, UciMoveResult played)
    {
        // Mate scores are side-to-move relative: positive = the mover delivers mate,
        // negative = the mover is being mated.
        int? b = best.ScoreMate;
        int? p = played.ScoreMate;

        if (b is null && p is null) return MateCategory.None;

        if (b is int bm && p is int pm)
        {
            // Same side mating and the same distance: the played move is exactly as good as
            // the best move. This used to fall through to MateDistanceChanged, reporting an
            // identical outcome as a change.
            if (bm == pm) return MateCategory.MateUnchanged;

            // Both deliver mate, or both get mated, but at a different distance.
            if (Math.Sign(bm) == Math.Sign(pm)) return MateCategory.MateDistanceChanged;

            // Unrestricted says the mover mates, the played move gets mated instead.
            if (bm > 0 && pm < 0) return MateCategory.EnteredForcedMate;

            // Restricted claims a mate the unrestricted search did not find. A restricted
            // search cannot legitimately beat the unrestricted one on the same position, so
            // this is an analysis contradiction, not a move-quality statement.
            return MateCategory.ContradictoryMate;
        }

        if (b is int bestMate)
        {
            // A mate was available and the played move does not mate at all.
            if (bestMate > 0) return MateCategory.MissedForcedMate;

            // Unrestricted says the mover is forcibly mated, yet the played move has an
            // ordinary score — the two analyses disagree about whether mate is forced.
            return MateCategory.EscapedForcedMate;
        }

        // Only the played move produced a mate score.
        int playedMate = p!.Value;

        // The played move walks into being mated.
        if (playedMate < 0) return MateCategory.EnteredForcedMate;

        // The restricted search mates while the unrestricted one did not find it: again a
        // contradiction between the two searches rather than a property of the move.
        return MateCategory.ContradictoryMate;
    }

    /// <summary>
    /// Records a per-position reference-engine failure. The sample is excluded from every
    /// aggregate, but the match keeps producing artifacts.
    /// </summary>
    private static MoveLossRecord MakeErrorRecord(MoveRecord move, int retries, Exception ex)
        => new(
            move.MoveNumber, move.IsWhiteMove, move.UciMove, move.Fen,
            null, null, "exact", null, null, "exact",
            CentipawnLoss: null, Eligibility: SampleEligibility.AnalysisError, Mate: MateCategory.None,
            RetriesUsed: retries,
            IntervalNote: $"reference engine failed for this position: {ex.GetType().Name}: {ex.Message}");

    private static string DescribeBoundInterval(UciMoveResult best, UciMoveResult played)
    {
        string bestStr   = FormatScore(best.ScoreCp, best.ScoreMate, best.ScoreBound);
        string playedStr = FormatScore(played.ScoreCp, played.ScoreMate, played.ScoreBound);
        return $"unresolved bound after retries — best: {bestStr}, played: {playedStr}. " +
               "Interval, not a point estimate: excluded from exact aggregates.";
    }

    /// <summary>
    /// Writes a move-loss report to <paramref name="path"/>: per-move centipawn loss plus
    /// median/95th-percentile summary statistics for the *exact-eligible* subset only, plus
    /// separate counts for mate categories, unresolved bounds, and analysis inconsistencies.
    /// Also records the reference engine's identity/configuration so results are attributable.
    /// </summary>
    public static void WriteReport(
        GameResult game,
        IReadOnlyList<MoveLossRecord> records,
        string path,
        string? referenceEngineName = null,
        int? referenceEngineDepth = null,
        IReadOnlyList<UciOptionSetting>? referenceEngineOptions = null)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var w = new StreamWriter(path, append: false);

        w.WriteLine("=== ChessBot Move-Loss Report (same-engine, two-search method) ===");
        w.WriteLine($"Game   : {game.GameNumber}");
        w.WriteLine($"Reference engine: {referenceEngineName ?? "(unknown)"}");
        if (referenceEngineDepth.HasValue)
            w.WriteLine($"Reference depth : {referenceEngineDepth}");
        if (referenceEngineOptions is { Count: > 0 })
            foreach (var opt in referenceEngineOptions)
                w.WriteLine($"Reference option: {opt.Name}={opt.Value}");
        w.WriteLine($"Moves analyzed: {records.Count}");
        w.WriteLine();

        if (records.Count == 0)
        {
            w.WriteLine("No ChessBot moves to analyze.");
            return;
        }

        var exact        = records.Where(r => r.Eligibility == SampleEligibility.Exact).ToList();
        var mateInvolved = records.Where(r => r.Eligibility == SampleEligibility.MateInvolved).ToList();
        var boundIssues  = records.Where(r => r.Eligibility == SampleEligibility.UnresolvedBound).ToList();
        var inconsistent = records.Where(r => r.Eligibility == SampleEligibility.Inconsistent).ToList();

        w.WriteLine("=== Sample Eligibility ===");
        w.WriteLine($"  Exact CP-vs-CP samples      : {exact.Count}");
        w.WriteLine($"  Mate-involved (separate cat): {mateInvolved.Count}");
        w.WriteLine($"  Unresolved bound (excluded) : {boundIssues.Count}");
        w.WriteLine($"  Analysis inconsistencies    : {inconsistent.Count}");
        w.WriteLine();

        if (exact.Count > 0)
        {
            var losses = exact.Select(r => (double)r.CentipawnLoss!.Value).OrderBy(v => v).ToList();
            w.WriteLine("=== Exact Centipawn-Loss Aggregate (mate/bound/inconsistent samples excluded) ===");
            w.WriteLine($"  Median centipawn loss     : {Percentile(losses, 0.50):F1}");
            w.WriteLine($"  95th percentile cp loss   : {Percentile(losses, 0.95):F1}");
            w.WriteLine($"  Mean centipawn loss       : {losses.Average():F1}");
            w.WriteLine($"  Max centipawn loss        : {losses.Max():F1}");
        }
        else
        {
            w.WriteLine("=== Exact Centipawn-Loss Aggregate ===");
            w.WriteLine("  No exact-eligible samples in this game.");
        }
        w.WriteLine();

        if (mateInvolved.Count > 0)
        {
            w.WriteLine("=== Mate Categories ===");
            foreach (var cat in Enum.GetValues<MateCategory>())
            {
                if (cat == MateCategory.None) continue;
                int n = mateInvolved.Count(r => r.Mate == cat);
                if (n > 0) w.WriteLine($"  {cat}: {n}");
            }
            w.WriteLine();
        }

        w.WriteLine("=== Per-Move Detail ===");
        foreach (var r in records)
        {
            w.WriteLine();
            w.WriteLine($"Move {r.MoveNumber} ({(r.IsWhiteMove ? "White" : "Black")}): {r.Move}  [{r.Eligibility}]");
            w.WriteLine($"  Best (unrestricted) : {FormatScore(r.BestScoreCp, r.BestScoreMate, r.BestScoreBound)}");
            w.WriteLine($"  Played (searchmoves): {FormatScore(r.PlayedScoreCp, r.PlayedScoreMate, r.PlayedScoreBound)}");
            if (r.Eligibility == SampleEligibility.Exact)
                w.WriteLine($"  Centipawn loss      : {r.CentipawnLoss}");
            if (r.IntervalNote != null)
                w.WriteLine($"  Note                : {r.IntervalNote}");
            if (r.RetriesUsed > 0)
                w.WriteLine($"  Retries used        : {r.RetriesUsed}");
            w.WriteLine($"  FEN before          : {r.Fen}");
        }
    }

    private static string FormatScore(int? cp, int? mate, string bound)
    {
        string baseStr = mate.HasValue ? $"mate {mate}" : $"{cp:+#;-#;0}cp";
        return bound == "exact" ? baseStr : $"{baseStr} [{bound}]";
    }

    private static double Percentile(IReadOnlyList<double> sortedValues, double p)
    {
        if (sortedValues.Count == 0) return 0;
        if (sortedValues.Count == 1) return sortedValues[0];
        double rank = p * (sortedValues.Count - 1);
        int lo = (int)Math.Floor(rank);
        int hi = (int)Math.Ceiling(rank);
        if (lo == hi) return sortedValues[lo];
        double frac = rank - lo;
        return sortedValues[lo] + (sortedValues[hi] - sortedValues[lo]) * frac;
    }
}
