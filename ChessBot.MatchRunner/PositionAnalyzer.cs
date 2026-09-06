namespace ChessBot.MatchRunner;

/// <summary>
/// A detected blunder in a game.
/// </summary>
public record BlunderRecord(
    int    MoveNumber,
    bool   IsWhiteMove,
    string Move,
    string Fen,          // FEN before the move
    int    ScoreBefore,  // engine score before the move (side-to-move positive)
    int    ScoreAfter,   // engine score after the move (side-to-move positive, flipped)
    int    SwingCp);     // positive = blunder hurt the mover

/// <summary>
/// Result of game analysis.
/// </summary>
public class GameAnalysis
{
    public int GameNumber      { get; init; }
    public List<BlunderRecord> Blunders { get; } = new();
    public BlunderRecord? FirstMajorSwing { get; set; }
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
    /// Analyzes a game record to find blunders and first major eval swings.
    /// Uses the stored ScoreCp values from each move.
    /// A "blunder" is when the score swings significantly against the mover.
    /// </summary>
    public GameAnalysis Analyze(GameResult game)
    {
        var analysis = new GameAnalysis { GameNumber = game.GameNumber };
        if (game.Moves.Count < 2) return analysis;

        // We track the score from each mover's perspective.
        // A blunder is when a player's position deteriorates sharply after their move
        // (i.e., the opponent's score right after is much higher than expected).

        for (int i = 1; i < game.Moves.Count; i++)
        {
            var prev = game.Moves[i - 1];
            var curr = game.Moves[i];

            // Score convention in SearchResult: positive = good for side-to-move.
            // After White's move, it's Black to move. Black's score is the negative of White's advantage.
            // A big positive scoreCp for the opponent after your move = you blundered.

            // Detect when ChessBot blundered: previous move was ChessBot's, current score is bad
            bool prevWasChessBot = (prev.IsWhiteMove == game.ChessBotIsWhite);
            if (!prevWasChessBot) continue;

            // The score before ChessBot's move (from ChessBot's perspective)
            int scoreBefore = prev.ScoreCp;
            // After ChessBot's move, the opponent responds; curr.ScoreCp is from opponent's view
            // A big positive curr score = bad for ChessBot
            int scoreAfter = curr.ScoreCp;

            // Swing: how much did ChessBot's position deteriorate?
            // scoreBefore > 0 = ChessBot was winning, scoreAfter > 0 = opponent is winning
            int swing = scoreBefore + scoreAfter; // both are "good for mover" so positive sum = position flipped

            if (swing >= _cfg.BlunderThresholdCp)
            {
                var blunder = new BlunderRecord(
                    MoveNumber:  prev.MoveNumber,
                    IsWhiteMove: prev.IsWhiteMove,
                    Move:        prev.UciMove,
                    Fen:         prev.Fen,
                    ScoreBefore: scoreBefore,
                    ScoreAfter:  scoreAfter,
                    SwingCp:     swing);

                analysis.Blunders.Add(blunder);
                analysis.RegressionFens.Add(prev.Fen);

                if (analysis.FirstMajorSwing is null && swing >= _cfg.FirstSwingThresholdCp)
                    analysis.FirstMajorSwing = blunder;
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

            // Skip already-logged blunders
            if (analysis.Blunders.Any(b => b.MoveNumber == prev.MoveNumber && b.IsWhiteMove == prev.IsWhiteMove))
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
        w.WriteLine($"  {"Blunders detected",-28} {analysis.Blunders.Count(b => b.IsWhiteMove == game.ChessBotIsWhite),-22} {analysis.Blunders.Count(b => b.IsWhiteMove != game.ChessBotIsWhite),-22}");

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

            // Raw UCI info lines (external engine only)
            if (m.RawUciLines.Count > 0)
            {
                w.WriteLine($"  UCI output  : ({m.RawUciLines.Count} info lines)");
                foreach (var line in m.RawUciLines)
                    w.WriteLine($"    {line}");
            }
        }

        // ── Blunder analysis ──────────────────────────────────────────────────
        w.WriteLine();
        if (analysis.Blunders.Count == 0)
        {
            w.WriteLine("=== Blunder Analysis: no blunders detected ===");
        }
        else
        {
            w.WriteLine($"=== Blunder Analysis ({analysis.Blunders.Count} blunder(s)) ===");
            for (int i = 0; i < analysis.Blunders.Count; i++)
            {
                var b   = analysis.Blunders[i];
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
