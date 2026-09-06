namespace ChessBot.MatchRunner;

/// <summary>
/// A detected cross-engine evaluation disagreement: ChessBot's own pre-move score plus the
/// opponent's post-move score summed together. This mixes two different engines' evaluations
/// from two different searches and is NOT a measurement of centipawn loss — it only flags that
/// the two engines disagreed sharply about the resulting position. Never call this a "blunder".
/// Real move-loss must be computed by analysing the same position with the same engine twice
/// (see <see cref="MoveLossAnalyzer"/>).
/// </summary>
public record CrossEngineEvaluationDisagreementRecord(
    int    MoveNumber,
    bool   IsWhiteMove,
    string Move,
    string Fen,          // FEN before the move
    int    ScoreBefore,  // ChessBot's own score before the move (side-to-move positive)
    int    ScoreAfter,   // opponent's score after the move (side-to-move positive, flipped)
    int    SwingCp);     // positive = the two engines' evaluations disagree in ChessBot's disfavor

/// <summary>
/// Result of game analysis.
/// </summary>
public class GameAnalysis
{
    public int GameNumber      { get; init; }
    public List<CrossEngineEvaluationDisagreementRecord> CrossEngineEvaluationDisagreements { get; } = new();
    public CrossEngineEvaluationDisagreementRecord? FirstMajorSwing { get; set; }
    public List<string> RegressionFens   { get; } = new();
}

/// <summary>
/// Analyzes a completed game to detect blunders, eval swings,
/// and produces FEN regression test candidates from bad positions.
/// </summary>
public class PositionAnalyzer
{
    private readonly MatchConfig _cfg;

    public PositionAnalyzer(MatchConfig cfg)
    {
        _cfg = cfg;
    }

    /// <summary>
    /// Analyzes a game record to find cross-engine evaluation disagreements and first major eval
    /// swings. Uses the stored ScoreCp values from each move. This is a cheap same-game heuristic,
    /// NOT a centipawn-loss measurement: it mixes ChessBot's own score with the opponent engine's
    /// score from a different search, so it is renamed CrossEngineEvaluationDisagreement and must
    /// never be reported as a blunder or as move loss. For real move loss, see
    /// <see cref="MoveLossAnalyzer"/>, which re-analyzes the position with one engine twice.
    /// </summary>
    public GameAnalysis Analyze(GameResult game)
    {
        var analysis = new GameAnalysis { GameNumber = game.GameNumber };
        if (game.Moves.Count < 2) return analysis;

        // We track the score from each mover's perspective.
        // A disagreement is flagged when a player's position appears to deteriorate sharply
        // after their move (i.e., the opponent's score right after is much higher than expected).

        for (int i = 1; i < game.Moves.Count; i++)
        {
            var prev = game.Moves[i - 1];
            var curr = game.Moves[i];

            // Score convention in SearchResult: positive = good for side-to-move.
            // After White's move, it's Black to move. Black's score is the negative of White's advantage.
            // A big positive scoreCp for the opponent after your move = disagreement flag for ChessBot.

            // Detect when ChessBot's move looks bad in hindsight: previous move was ChessBot's,
            // current (opponent-engine) score is bad for ChessBot.
            bool prevWasChessBot = (prev.IsWhiteMove == game.ChessBotIsWhite);
            if (!prevWasChessBot) continue;

            // The score before ChessBot's move (from ChessBot's own engine, ChessBot's perspective)
            int scoreBefore = prev.ScoreCp;
            // After ChessBot's move, the opponent responds; curr.ScoreCp is from the opponent
            // engine's view. A big positive curr score = looks bad for ChessBot.
            int scoreAfter = curr.ScoreCp;

            // Swing: how much did the two engines' assessments of ChessBot's position disagree?
            // scoreBefore > 0 = ChessBot's own engine thought it was winning,
            // scoreAfter > 0 = the opponent engine thinks it is winning post-move.
            int swing = scoreBefore + scoreAfter; // both are "good for mover" so positive sum = disagreement

            if (swing >= _cfg.BlunderThresholdCp)
            {
                var disagreement = new CrossEngineEvaluationDisagreementRecord(
                    MoveNumber:  prev.MoveNumber,
                    IsWhiteMove: prev.IsWhiteMove,
                    Move:        prev.UciMove,
                    Fen:         prev.Fen,
                    ScoreBefore: scoreBefore,
                    ScoreAfter:  scoreAfter,
                    SwingCp:     swing);

                analysis.CrossEngineEvaluationDisagreements.Add(disagreement);
                analysis.RegressionFens.Add(prev.Fen);

                if (analysis.FirstMajorSwing is null && swing >= _cfg.FirstSwingThresholdCp)
                    analysis.FirstMajorSwing = disagreement;
            }
        }

        // Also flag positions where ChessBot missed a tactic: high-value piece hanging unprotected
        // (heuristic: any position where score went from +100 to -100 or worse)
        DetectEvalCollapses(game, analysis);

        return analysis;
    }

    private void DetectEvalCollapses(GameResult game, GameAnalysis analysis)
    {
        // A large single-move eval collapse (regardless of whose move it was)
        for (int i = 1; i < game.Moves.Count; i++)
        {
            var prev = game.Moves[i - 1];
            var curr = game.Moves[i];

            // Skip already-logged disagreements
            if (analysis.CrossEngineEvaluationDisagreements.Any(b => b.MoveNumber == prev.MoveNumber && b.IsWhiteMove == prev.IsWhiteMove))
                continue;

            // Score collapse: previous score was clearly positive, now strongly negative
            if (prev.ScoreCp > 50 && curr.ScoreCp > _cfg.BlunderThresholdCp)
            {
                // The opponent now has a large advantage — something went wrong
                if (!analysis.RegressionFens.Contains(prev.Fen))
                    analysis.RegressionFens.Add(prev.Fen);
            }
        }
    }

    /// <summary>
    /// Writes a comprehensive game log to <paramref name="path"/>.
    /// Sections: header · game statistics · full move list (all metrics + raw UCI output) ·
    /// blunder analysis · regression FENs.
    /// Always written, even when there are no blunders.
    /// </summary>
    public void WriteGameLog(GameResult game, GameAnalysis analysis, string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path) ?? ".");
        using var w = new StreamWriter(path, append: false);

        string cbColor   = game.ChessBotIsWhite ? "White" : "Black";
        string oppColor  = game.ChessBotIsWhite ? "Black" : "White";
        var    cbMoves   = game.Moves.Where(m => m.IsChessBotMove).ToList();
        var    oppMoves  = game.Moves.Where(m => !m.IsChessBotMove).ToList();

        // ── Header ────────────────────────────────────────────────────────────
        w.WriteLine("=== ChessBot Game Log ===");
        w.WriteLine($"Game         : {game.GameNumber}");
        w.WriteLine($"ChessBot     : {cbColor}");
        w.WriteLine($"Opponent     : {_cfg.ExternalEnginePath}");
        w.WriteLine($"Move time    : {_cfg.MoveTimeMs} ms per move");
        w.WriteLine($"Result       : {game.Outcome}");
        if (!string.IsNullOrEmpty(game.TerminationReason))
            w.WriteLine($"Termination  : {game.TerminationReason}");
        w.WriteLine($"Total plies  : {game.Moves.Count}  ({cbMoves.Count} ChessBot, {oppMoves.Count} opponent)");
        w.WriteLine($"Date         : {DateTime.Now:yyyy-MM-dd HH:mm:ss}");

        // ── Game statistics ───────────────────────────────────────────────────
        w.WriteLine();
        w.WriteLine("=== Game Statistics ===");
        w.WriteLine($"  {"Metric",-28} {cbColor + " (ChessBot)",-22} {oppColor + " (Opponent)",-22}");
        w.WriteLine(new string('-', 75));
        // Forced-mate searches terminate at artificial depths (245 plies has been observed)
        // and carry mate scores, not centipawns. Averaging either of those with normal
        // searches produces a number that describes nothing, so mate-scored moves are
        // excluded from the depth and score averages and counted on their own row.
        var cbNormal  = cbMoves.Where(m => !IsMateScored(m)).ToList();
        var oppNormal = oppMoves.Where(m => !IsMateScored(m)).ToList();

        WriteStatRow(w, "Avg depth (non-mate)",
            cbNormal.Count  > 0 ? cbNormal.Average(m => m.Depth)   : 0,
            oppNormal.Count > 0 ? oppNormal.Average(m => m.Depth)  : 0, fmt: "F1");
        WriteStatRow(w, "Median depth (non-mate)",
            Median(cbNormal.Select(m => (double)m.Depth)),
            Median(oppNormal.Select(m => (double)m.Depth)), fmt: "F1");
        WriteStatRow(w, "Avg seldepth (non-mate)",
            cbNormal.Count  > 0 ? cbNormal.Average(m => m.SelDepth)  : 0,
            oppNormal.Count > 0 ? oppNormal.Average(m => m.SelDepth) : 0, fmt: "F1");
        WriteStatRow(w, "Avg score (cp, non-mate)",
            cbNormal.Count  > 0 ? cbNormal.Average(m => (double)m.ScoreCp)  : 0,
            oppNormal.Count > 0 ? oppNormal.Average(m => (double)m.ScoreCp) : 0, fmt: "+0.0;-0.0;0.0");
        WriteStatRow(w, "Mate-scored moves",
            cbMoves.Count - cbNormal.Count,
            oppMoves.Count - oppNormal.Count, fmt: "F0");
        WriteStatRow(w, "Avg nodes / move",
            cbMoves.Count  > 0 ? cbMoves.Average(m => (double)m.Nodes)  : 0,
            oppMoves.Count > 0 ? oppMoves.Average(m => (double)m.Nodes) : 0, fmt: "N0");
        WriteStatRow(w, "Avg NPS",
            cbMoves.Count  > 0 ? cbMoves.Average(m => (double)m.Nps)  : 0,
            oppMoves.Count > 0 ? oppMoves.Average(m => (double)m.Nps) : 0, fmt: "N0");
        WriteStatRow(w, "Avg time / move (ms)",
            cbMoves.Count  > 0 ? cbMoves.Average(m => (double)m.ElapsedMs)  : 0,
            oppMoves.Count > 0 ? oppMoves.Average(m => (double)m.ElapsedMs) : 0, fmt: "F0");
        WriteStatRow(w, "Total nodes",
            cbMoves.Sum(m => (double)m.Nodes),
            oppMoves.Sum(m => (double)m.Nodes), fmt: "N0");
        WriteStatRow(w, "Peak NPS",
            cbMoves.Count  > 0 ? (double)cbMoves.Max(m => m.Nps)  : 0,
            oppMoves.Count > 0 ? (double)oppMoves.Max(m => m.Nps) : 0, fmt: "N0");
        // Hash full (per-mille → percentage; -1 = not reported by that engine)
        double cbHf  = cbMoves.Any(m => m.HashFull >= 0)
            ? cbMoves.Where(m => m.HashFull >= 0).Average(m => m.HashFull / 10.0) : -1;
        double oppHf = oppMoves.Any(m => m.HashFull >= 0)
            ? oppMoves.Where(m => m.HashFull >= 0).Average(m => m.HashFull / 10.0) : -1;
        w.WriteLine($"  {"Avg TT fill",-28} {(cbHf  < 0 ? "n/a" : $"{cbHf:F1}%"),-22} {(oppHf < 0 ? "n/a" : $"{oppHf:F1}%"),-22}");
        WriteStatRow(w, "Total TB hits",
            cbMoves.Sum(m => (double)m.TbHits),
            oppMoves.Sum(m => (double)m.TbHits), fmt: "N0");
        w.WriteLine($"  {"Cross-engine eval disagreements",-28} {analysis.CrossEngineEvaluationDisagreements.Count(b => b.IsWhiteMove == game.ChessBotIsWhite),-22} {analysis.CrossEngineEvaluationDisagreements.Count(b => b.IsWhiteMove != game.ChessBotIsWhite),-22}");

        // ── Search-shape instrumentation (ChessBot only — the external engine doesn't expose this) ──
        if (cbMoves.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("=== ChessBot Search-Shape Instrumentation ===");
            w.WriteLine($"  Total qnodes                 : {cbMoves.Sum(m => m.QNodes):N0}");
            w.WriteLine($"  Total evaluation calls       : {cbMoves.Sum(m => m.EvaluationCalls):N0}");
            w.WriteLine($"  Total moves generated        : {cbMoves.Sum(m => m.MovesGenerated):N0}");
            w.WriteLine($"  Total beta cutoffs           : {cbMoves.Sum(m => m.BetaCutoffs):N0}");
            {
                long totalBetaCutoffs = cbMoves.Sum(m => m.BetaCutoffs);
                long totalFirstMoveCutoffs = cbMoves.Sum(m => m.BetaCutoffsFirstMove);
                double firstMoveCutoffRate = totalBetaCutoffs > 0 ? (double)totalFirstMoveCutoffs / totalBetaCutoffs : 0;
                w.WriteLine($"  First-move cutoff rate (total/total): {firstMoveCutoffRate:P1}  ({totalFirstMoveCutoffs:N0} / {totalBetaCutoffs:N0})");
            }
            w.WriteLine($"  Null-move attempts/cutoffs   : {cbMoves.Sum(m => m.NullMoveAttempts):N0} / {cbMoves.Sum(m => m.NullMoveCutoffs):N0}");
            w.WriteLine($"  LMR reductions/re-searches   : {cbMoves.Sum(m => m.LmrReductions):N0} / {cbMoves.Sum(m => m.LmrReSearches):N0}");
            w.WriteLine($"  Futility skips               : {cbMoves.Sum(m => m.FutilitySkips):N0}");
            w.WriteLine($"  PVS re-searches              : {cbMoves.Sum(m => m.PvsReSearches):N0}");
            w.WriteLine($"  Aspiration fail-low/fail-high: {cbMoves.Sum(m => m.AspirationFailLow):N0} / {cbMoves.Sum(m => m.AspirationFailHigh):N0}");
            w.WriteLine($"  Repetition draws             : {cbMoves.Sum(m => m.RepetitionDraws):N0}");
            w.WriteLine($"  Aspiration retry nodes       : {cbMoves.Sum(m => m.AspirationRetryNodes):N0}");
            w.WriteLine($"  LMR plies saved              : {cbMoves.Sum(m => m.LmrPliesSaved):N0}");
            // Ratio of the last completed iteration's own nodes to the previous iteration's,
            // averaged over moves where two iterations completed. This is not the old
            // nodes^(1/depth) figure, which mixed cumulative iterations with a partial one.
            var ratios = cbMoves.Where(m => m.IterationNodeRatio > 0).Select(m => m.IterationNodeRatio).ToList();
            w.WriteLine($"  Iteration node ratio (per-move avg, {ratios.Count} moves): " +
                        $"{(ratios.Count > 0 ? ratios.Average() : 0):F2}");
            w.WriteLine($"  Moves with a cancelled iteration: {cbMoves.Count(m => m.PartialDepth > 0)}");
            w.WriteLine($"  Moves using partial root result: {cbMoves.Count(m => m.UsedPartialRootResult)}");
            w.WriteLine($"  Unsearched fallback moves    : {cbMoves.Count(m => m.IsUnsearchedFallbackMove)}");
        }

        // ── Move list ─────────────────────────────────────────────────────────
        w.WriteLine();
        w.WriteLine("=== Move List ===");

        foreach (var m in game.Moves)
        {
            w.WriteLine();
            string who   = m.IsChessBotMove ? "ChessBot" : "Opponent";
            string color = m.IsWhiteMove    ? "White"    : "Black";
            w.WriteLine($"Move {m.MoveNumber} — {color} ({who})");
            w.WriteLine($"  Move        : {m.UciMove}");

            // Score
            if (m.ScoreMate.HasValue)
                w.WriteLine($"  Score       : mate {m.ScoreMate}  [{m.ScoreBound}]");
            else
                w.WriteLine($"  Score       : {m.ScoreCp:+#;-#;0}cp  [{m.ScoreBound}]");

            // Depth
            if (m.SelDepth > 0)
                w.WriteLine($"  Depth       : {m.Depth}  /  seldepth {m.SelDepth}");
            else
                w.WriteLine($"  Depth       : {m.Depth}");

            // Search stats
            string hashStr = m.HashFull >= 0 ? $"  |  TT fill {m.HashFull / 10.0:F1}%" : string.Empty;
            string tbStr   = m.TbHits > 0    ? $"  |  TBhits {m.TbHits:N0}"             : string.Empty;
            w.WriteLine($"  Nodes       : {m.Nodes:N0}  |  NPS {m.Nps:N0}  |  Time {m.ElapsedMs} ms{hashStr}{tbStr}");

            // PV
            if (!string.IsNullOrWhiteSpace(m.Pv))
                w.WriteLine($"  PV          : {m.Pv}");

            // FEN
            w.WriteLine($"  FEN before  : {m.Fen}");

            // Instrumentation (ChessBot moves only)
            if (m.IsChessBotMove)
            {
                w.WriteLine($"  Search shape: qnodes {m.QNodes:N0}  evalCalls {m.EvaluationCalls:N0}  movesGen {m.MovesGenerated:N0}");
                w.WriteLine($"  Cutoffs     : beta {m.BetaCutoffs:N0} (firstMove {m.FirstMoveCutoffRate:P1})  nullMove {m.NullMoveAttempts:N0}/{m.NullMoveCutoffs:N0}");
                w.WriteLine($"  Reductions  : LMR {m.LmrReductions:N0}/{m.LmrReSearches:N0} re-search  futilitySkips {m.FutilitySkips:N0}  PVS re-search {m.PvsReSearches:N0}");
                w.WriteLine($"  Aspiration  : failLow {m.AspirationFailLow:N0}  failHigh {m.AspirationFailHigh:N0}  " +
                            $"retryNodes {m.AspirationRetryNodes:N0}  repetitionDraws {m.RepetitionDraws:N0}  " +
                            $"iterNodeRatio {m.IterationNodeRatio:F2}");
                w.WriteLine($"  Nodes       : main {m.MainNodes:N0}  q {m.QNodes:N0}  total {m.Nodes:N0}  " +
                            $"lastIteration {m.LastIterationNodes:N0}");

                // Partial-iteration facts are reported whenever an iteration was cancelled, not
                // only when its candidate was selected: root coverage explains how much of the
                // deeper iteration was actually seen regardless of which move was reported.
                if (m.PartialDepth > 0)
                {
                    string selection = m.UsedPartialRootResult
                        ? $"selected (score {(m.PartialScoreIsExact ? "exact" : "lower bound")})"
                        : "not selected";
                    w.WriteLine($"  Partial     : depth {m.PartialDepth} cancelled, completed depth {m.Depth}, " +
                                $"root coverage {m.RootMovesCompleted}/{m.RootMoveCount} " +
                                $"({m.RootCoveragePercent:F1}%), {selection}");
                }
                if (m.IsUnsearchedFallbackMove)
                    w.WriteLine("  Fallback    : budget too small for depth 1 — move is an unevaluated legal fallback");
            }

            // Raw UCI info lines (external engine only)
            if (m.RawUciLines.Count > 0)
            {
                w.WriteLine($"  UCI output  : ({m.RawUciLines.Count} info lines)");
                foreach (var line in m.RawUciLines)
                    w.WriteLine($"    {line}");
            }
        }

        // ── Cross-engine evaluation disagreements ─────────────────────────────
        w.WriteLine();
        if (analysis.CrossEngineEvaluationDisagreements.Count == 0)
        {
            w.WriteLine("=== Cross-Engine Evaluation Disagreement Analysis: none detected ===");
        }
        else
        {
            w.WriteLine($"=== Cross-Engine Evaluation Disagreement Analysis ({analysis.CrossEngineEvaluationDisagreements.Count} found) ===");
            w.WriteLine("NOTE: this compares ChessBot's own pre-move score with the opponent engine's");
            w.WriteLine("post-move score — two different engines, two different searches. It is NOT a");
            w.WriteLine("centipawn-loss / blunder measurement. See MoveLossAnalyzer output for that.");
            for (int i = 0; i < analysis.CrossEngineEvaluationDisagreements.Count; i++)
            {
                var b   = analysis.CrossEngineEvaluationDisagreements[i];
                var rec = game.Moves.FirstOrDefault(
                    m => m.MoveNumber == b.MoveNumber && m.IsWhiteMove == b.IsWhiteMove);

                w.WriteLine();
                w.WriteLine($"  [{i + 1}]  Move {b.MoveNumber} ({(b.IsWhiteMove ? "White" : "Black")}): {b.Move}");
                w.WriteLine($"       Score swing   : {b.SwingCp:+#;-#;0} cp");
                w.WriteLine($"       Before move   : {b.ScoreBefore:+#;-#;0} cp  (ChessBot's evaluation)");
                w.WriteLine($"       Opponent eval of the position this move created: " +
                            $"{b.ScoreAfter:+#;-#;0} cp  (opponent to move, before it replies)");
                w.WriteLine($"       FEN before    : {b.Fen}");
                if (rec != null)
                {
                    if (rec.ScoreMate.HasValue)
                        w.WriteLine($"       Score played  : mate {rec.ScoreMate}  [{rec.ScoreBound}]");
                    else
                        w.WriteLine($"       Score played  : {rec.ScoreCp:+#;-#;0} cp  [{rec.ScoreBound}]");
                    string sdStr = rec.SelDepth > 0 ? $" / seldepth {rec.SelDepth}" : "";
                    w.WriteLine($"       Search        : depth {rec.Depth}{sdStr}  nodes {rec.Nodes:N0}  NPS {rec.Nps:N0}  time {rec.ElapsedMs} ms");
                    if (rec.HashFull >= 0)
                        w.WriteLine($"       TT fill       : {rec.HashFull / 10.0:F1}%");
                    if (rec.TbHits > 0)
                        w.WriteLine($"       TB hits       : {rec.TbHits:N0}");
                    if (!string.IsNullOrWhiteSpace(rec.Pv))
                        w.WriteLine($"       PV played     : {rec.Pv}");
                }
            }
        }

        // ── Regression FENs ───────────────────────────────────────────────────
        if (analysis.RegressionFens.Count > 0)
        {
            w.WriteLine();
            w.WriteLine("=== Regression FENs ===");
            w.WriteLine("# Paste into TacticalSearchTests.cs to create regression tests");
            w.WriteLine();
            foreach (var fen in analysis.RegressionFens)
                w.WriteLine(fen);
        }
    }

    private static void WriteStatRow(StreamWriter w, string label, double cb, double opp, string fmt)
    {
        w.WriteLine($"  {label,-28} {cb.ToString(fmt),-22} {opp.ToString(fmt),-22}");
    }

    /// <summary>
    /// True when a move's score is a mate score rather than a centipawn evaluation: either the
    /// external engine reported "score mate N", or the internal engine returned a value in the
    /// mate band (it encodes mate as ±100000 minus the ply). Such values are not centipawns and
    /// must never be averaged with them.
    /// </summary>
    private static bool IsMateScored(MoveRecord m)
        => m.ScoreMate.HasValue || Math.Abs(m.ScoreCp) >= MateScoreThreshold;

    private const int MateScoreThreshold = 99_000;

    private static double Median(IEnumerable<double> values)
    {
        var sorted = values.OrderBy(v => v).ToList();
        if (sorted.Count == 0) return 0;
        int mid = sorted.Count / 2;
        return sorted.Count % 2 == 1 ? sorted[mid] : (sorted[mid - 1] + sorted[mid]) / 2.0;
    }
}
