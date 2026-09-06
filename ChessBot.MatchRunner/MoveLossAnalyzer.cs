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
    /// <summary>Score value used for a "mate for me" result, so it always dominates any centipawn score.</summary>
    private const int MateScoreValue = 100_000;

    /// <summary>One measured ChessBot move's centipawn loss, as judged by the reference engine.</summary>
    public record MoveLossRecord(
        int    MoveNumber,
        bool   IsWhiteMove,
        string Move,
        string Fen,
        int?   BestScoreCp,     // reference engine's unrestricted best score (side-to-move positive); null if mate
        int?   BestScoreMate,   // mate-in-N for the unrestricted best move; null if not mate
        string BestScoreBound,  // "exact" | "lowerbound" | "upperbound"
        int?   PlayedScoreCp,   // reference engine's score restricted to the move played; null if mate
        int?   PlayedScoreMate, // mate-in-N for the played move; null if not mate
        string PlayedScoreBound,
        int    CentipawnLoss);  // always >= 0; BestScore - PlayedScore, clamped at 0

    /// <summary>
    /// Analyses every ChessBot move in <paramref name="game"/> with <paramref name="referenceEngine"/>
    /// (already initialized) at a fixed <paramref name="depth"/>, and returns one
    /// <see cref="MoveLossRecord"/> per ChessBot move.
    /// </summary>
    public static async Task<List<MoveLossRecord>> AnalyzeGameAsync(
        GameResult game,
        UciAdapter referenceEngine,
        int depth,
        CancellationToken ct = default)
    {
        var records = new List<MoveLossRecord>();

        foreach (var move in game.Moves)
        {
            if (!move.IsChessBotMove) continue;
            ct.ThrowIfCancellationRequested();

            // Unrestricted: the reference engine's own opinion of the best move here.
            var best = await referenceEngine.AnalyzeFenAsync(move.Fen, depth, restrictToMove: null, ct);

            // Restricted to the move ChessBot actually played, so the score reflects the value
            // of that specific move rather than of the position as a whole.
            var played = await referenceEngine.AnalyzeFenAsync(move.Fen, depth, restrictToMove: move.UciMove, ct);

            int bestValue    = ToComparableValue(best);
            int playedValue  = ToComparableValue(played);

            // Both values are from the same engine, same position, same side-to-move
            // perspective, so the difference is a real move-loss figure. The best move can
            // never be worth less than the move actually played, but defensive clamping
            // guards against a restricted search settling on a slightly different score at
            // shallow depth (e.g. from different move-ordering / TT state).
            int loss = Math.Max(0, bestValue - playedValue);

            records.Add(new MoveLossRecord(
                MoveNumber:       move.MoveNumber,
                IsWhiteMove:      move.IsWhiteMove,
                Move:             move.UciMove,
                Fen:              move.Fen,
                BestScoreCp:      best.ScoreMate.HasValue ? null : best.ScoreCp,
                BestScoreMate:    best.ScoreMate,
                BestScoreBound:   best.ScoreBound,
                PlayedScoreCp:    played.ScoreMate.HasValue ? null : played.ScoreCp,
                PlayedScoreMate:  played.ScoreMate,
                PlayedScoreBound: played.ScoreBound,
                CentipawnLoss:    loss));
        }

        return records;
    }

    /// <summary>
    /// Converts a UCI score (which may be a mate score, or a centipawn score with a bound
    /// qualifier) into a single comparable integer. Mate-for-me collapses to a large constant
    /// (nearer mates. are not distinguished from each other — both dominate any centipawn score
    /// equally, which is all that is needed for a loss comparison). Mate-against-me collapses
    /// to the negative of that constant. Bound qualifiers ("lowerbound"/"upperbound") mean the
    /// true score could be even more extreme than reported; since loss is computed as
    /// best-minus-played, treating the reported value as exact is the conservative (never
    /// overstates loss) choice given only a single search per side.
    /// </summary>
    private static int ToComparableValue(UciMoveResult result)
    {
        if (result.ScoreMate.HasValue)
        {
            int mateIn = result.ScoreMate.Value;
            return mateIn >= 0 ? MateScoreValue - mateIn : -MateScoreValue - mateIn;
        }

        return result.ScoreCp;
    }

    /// <summary>
    /// Writes a move-loss report to <paramref name="path"/>: per-move centipawn loss plus
    /// median/95th-percentile summary statistics, split by color relative to ChessBot.
    /// </summary>
    public static void WriteReport(GameResult game, IReadOnlyList<MoveLossRecord> records, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var w = new StreamWriter(path, append: false);

        w.WriteLine("=== ChessBot Move-Loss Report (same-engine, two-search method) ===");
        w.WriteLine($"Game   : {game.GameNumber}");
        w.WriteLine($"Moves analyzed: {records.Count}");
        w.WriteLine();

        if (records.Count == 0)
        {
            w.WriteLine("No ChessBot moves to analyze.");
            return;
        }

        var losses = records.Select(r => (double)r.CentipawnLoss).OrderBy(v => v).ToList();
        w.WriteLine($"Median centipawn loss     : {Percentile(losses, 0.50):F1}");
        w.WriteLine($"95th percentile cp loss   : {Percentile(losses, 0.95):F1}");
        w.WriteLine($"Mean centipawn loss       : {losses.Average():F1}");
        w.WriteLine($"Max centipawn loss        : {losses.Max():F1}");
        w.WriteLine();

        w.WriteLine("=== Per-Move Detail ===");
        foreach (var r in records)
        {
            w.WriteLine();
            w.WriteLine($"Move {r.MoveNumber} ({(r.IsWhiteMove ? "White" : "Black")}): {r.Move}");
            w.WriteLine($"  Best (unrestricted) : {FormatScore(r.BestScoreCp, r.BestScoreMate, r.BestScoreBound)}");
            w.WriteLine($"  Played (searchmoves): {FormatScore(r.PlayedScoreCp, r.PlayedScoreMate, r.PlayedScoreBound)}");
            w.WriteLine($"  Centipawn loss      : {r.CentipawnLoss}");
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
