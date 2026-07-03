using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using ChessBot.EloEvaluator.Models;

namespace ChessBot.EloEvaluator.Parsing;

/// <summary>
/// Parses per-game .log files written by PositionAnalyzer.WriteGameLog().
///
/// Sections parsed:
///   === ChessBot Game Log ===   → header fields
///   === Game Statistics ===     → aggregate stats table (ChessBot column)
///   === Move List ===           → per-move records
///   === Blunder Analysis ===    → blunder count
///   === Regression FENs ===     → ignored
/// </summary>
public static class LogFileParser
{
    private enum Section { None, Header, Stats, MoveList, CurrentMove, BlunderAnalysis, RegressionFens }

    // ── Temporary move fields ────────────────────────────────────────────────
    private struct MoveBuf
    {
        public int    MoveNum, Depth, SelDepth, ScoreCp;
        public int?   ScoreMate;
        public long   Nodes, Nps, ElapsedMs;
        public bool   IsWhiteMove, IsChessBotMove, HasValidScore;
        public string UciMove, FenBefore, Pv, ScoreBound;

        public void Reset()
        {
            MoveNum = Depth = SelDepth = ScoreCp = 0;
            ScoreMate = null;
            Nodes = Nps = ElapsedMs = 0;
            IsWhiteMove = IsChessBotMove = HasValidScore = false;
            UciMove = FenBefore = Pv = string.Empty;
            ScoreBound = "exact";
        }

        public readonly bool HasData => UciMove.Length > 0;
    }

    public static ParsedGame Parse(string filePath)
    {
        var game = new ParsedGame { SourceFile = filePath, SourceType = "log" };

        string[] lines;
        try
        {
            lines = File.ReadAllLines(filePath, Encoding.UTF8);
        }
        catch (Exception ex)
        {
            game.IsValid = false;
            game.ValidationErrors.Add($"Cannot read file: {ex.Message}");
            return game;
        }

        var section  = Section.None;
        var buf      = new MoveBuf();
        buf.Reset();

        foreach (var rawLine in lines)
        {
            // ── Section transitions ──────────────────────────────────────────
            if (rawLine.StartsWith("=== ChessBot Game Log ==="))
            {
                section = Section.Header;
                continue;
            }
            if (rawLine.StartsWith("=== Game Statistics ==="))
            {
                section = Section.Stats;
                continue;
            }
            if (rawLine.StartsWith("=== Move List ==="))
            {
                section = Section.MoveList;
                continue;
            }
            if (rawLine.StartsWith("=== Blunder Analysis"))
            {
                FinalizePendingMove(game, ref buf);
                section = Section.BlunderAnalysis;
                ParseBlunderAnalysisHeader(rawLine, game);
                continue;
            }
            if (rawLine.StartsWith("=== Regression FENs ==="))
            {
                FinalizePendingMove(game, ref buf);
                section = Section.RegressionFens;
                continue;
            }

            // ── Content dispatch ────────────────────────────────────────────
            switch (section)
            {
                case Section.Header:
                    ParseHeaderLine(game, rawLine);
                    break;

                case Section.Stats:
                {
                    var t = rawLine.TrimStart();
                    if (t.StartsWith("Metric") || t.StartsWith('-') || t.Length == 0)
                        break;
                    ParseStatsLine(game, rawLine);
                    break;
                }

                case Section.MoveList:
                case Section.CurrentMove:
                    if (TryParseMoveHeader(rawLine,
                            out int mn, out bool iw, out bool icb))
                    {
                        FinalizePendingMove(game, ref buf);
                        buf.Reset();
                        buf.MoveNum        = mn;
                        buf.IsWhiteMove    = iw;
                        buf.IsChessBotMove = icb;
                        section = Section.CurrentMove;
                    }
                    else if (section == Section.CurrentMove)
                    {
                        ParseMovePropertyLine(rawLine, ref buf);
                    }
                    break;
            }
        }

        // Finalize last pending move at EOF
        FinalizePendingMove(game, ref buf);

        // Derive per-move aggregate stats if stats table didn't parse them
        DeriveStatsFromMoves(game);

        return game;
    }

    // ── Header parsing ────────────────────────────────────────────────────────
    private static void ParseHeaderLine(ParsedGame game, string line)
    {
        int colon = line.IndexOf(':');
        if (colon < 0) return;

        string key   = line[..colon].Trim();
        string value = line[(colon + 1)..].Trim();

        switch (key)
        {
            case "Game":
                if (int.TryParse(value, out int gn)) game.GameNumber = gn;
                break;
            case "ChessBot":
                game.ChessBotColor = value;  // "White" or "Black"
                break;
            case "Opponent":
                game.OpponentName = value;
                break;
            case "Move time":
            {
                var m = Regex.Match(value, @"(\d+)");
                if (m.Success && int.TryParse(m.Groups[1].Value, out int mt)) game.MoveTimeMs = mt;
                break;
            }
            case "Result":
                game.Outcome = value switch
                {
                    "ChessBotWin"  => GameOutcomeKind.Win,
                    "ChessBotLoss" => GameOutcomeKind.Loss,
                    "Draw"         => GameOutcomeKind.Draw,
                    "Aborted"      => GameOutcomeKind.Aborted,
                    _              => GameOutcomeKind.Unknown
                };
                game.ResultTag = (game.Outcome, game.ChessBotColor) switch
                {
                    (GameOutcomeKind.Win,  "White") => "1-0",
                    (GameOutcomeKind.Win,  "Black") => "0-1",
                    (GameOutcomeKind.Loss, "White") => "0-1",
                    (GameOutcomeKind.Loss, "Black") => "1-0",
                    (GameOutcomeKind.Draw, _)       => "1/2-1/2",
                    _                               => "*"
                };
                break;
            case "Termination":
                game.TerminationReason = value;
                break;
            case "Total plies":
            {
                // "38  (19 ChessBot, 19 opponent)"
                var m = Regex.Match(value,
                    @"(\d+)\s*\(\s*(\d+)\s+ChessBot,\s*(\d+)\s+opponent\)");
                if (m.Success)
                {
                    if (int.TryParse(m.Groups[1].Value, out int tp)) game.TotalPlies    = tp;
                    if (int.TryParse(m.Groups[2].Value, out int cp)) game.ChessBotPlies = cp;
                    if (int.TryParse(m.Groups[3].Value, out int op)) game.OpponentPlies = op;
                }
                else if (int.TryParse(value.Split('(')[0].Trim(), out int tp2))
                {
                    game.TotalPlies = tp2;
                }
                break;
            }
            case "Date":
                if (DateTime.TryParse(value, out DateTime dt)) game.Date = dt;
                break;
        }
    }

    // ── Stats table parsing ───────────────────────────────────────────────────
    // Format: "  {label,-28} {cbVal,-22} {oppVal,-22}"
    // Column 1 (cb) is ALWAYS ChessBot regardless of color.
    private static void ParseStatsLine(ParsedGame game, string line)
    {
        if (line.Length < 31) return;

        string label = line.Length >= 30 ? line.Substring(2, 28).Trim()
                                         : line[2..].Trim();
        string cbVal  = line.Length >= 53 ? line.Substring(31, 22).Trim()
                      : line.Length  > 31 ? line[31..].Trim()
                      : string.Empty;

        switch (label)
        {
            case "Avg depth":
                if (TryParseDouble(cbVal, out double ad)) game.CbAvgDepth = ad;
                break;
            case "Avg seldepth":
                if (TryParseDouble(cbVal, out double asd)) game.CbAvgSelDepth = asd;
                break;
            case "Avg nodes / move":
                if (TryParseLong(cbVal, out long anm)) game.CbAvgNodesPerMove = anm;
                break;
            case "Avg NPS":
                if (TryParseLong(cbVal, out long an)) game.CbAvgNps = an;
                break;
            case "Avg time / move (ms)":
                if (TryParseDouble(cbVal, out double at)) game.CbAvgTimeMsPerMove = at;
                break;
            case "Total nodes":
                if (TryParseLong(cbVal, out long tn)) game.CbTotalNodes = tn;
                break;
            case "Peak NPS":
                if (TryParseLong(cbVal, out long pn)) game.CbPeakNps = pn;
                break;
            case "Blunders detected":
                // Use this if the blunder analysis header hasn't set it yet
                if (game.BlundersDetected == 0 && int.TryParse(cbVal, out int bd))
                    game.BlundersDetected = bd;
                break;
            // "Avg score (cp)" row writes "+F1"/"-F1" due to a format string bug
            // in PositionAnalyzer — skip it and derive from per-move data instead.
        }
    }

    // ── Move header parsing ───────────────────────────────────────────────────
    // "Move 1 — White (ChessBot)" / "Move 1 — Black (Opponent)"
    // Handles em dash (U+2014), en dash (U+2013), and hyphen-minus.
    private static bool TryParseMoveHeader(string line,
        out int moveNumber, out bool isWhiteMove, out bool isChessBotMove)
    {
        moveNumber = 0; isWhiteMove = false; isChessBotMove = false;

        var m = Regex.Match(line,
            @"^Move\s+(\d+)\s+[—–\-]+\s+(White|Black)\s+\((ChessBot|Opponent)\)");
        if (!m.Success) return false;

        if (!int.TryParse(m.Groups[1].Value, out moveNumber)) return false;
        isWhiteMove    = m.Groups[2].Value.Equals("White",     StringComparison.OrdinalIgnoreCase);
        isChessBotMove = m.Groups[3].Value.Equals("ChessBot",  StringComparison.OrdinalIgnoreCase);
        return true;
    }

    // ── Per-move property parsing ─────────────────────────────────────────────
    private static void ParseMovePropertyLine(string rawLine, ref MoveBuf buf)
    {
        var t = rawLine.TrimStart();

        if (t.StartsWith("Move        :"))
        {
            buf.UciMove = t["Move        :".Length..].Trim();
        }
        else if (t.StartsWith("Score       :"))
        {
            ParseScore(t["Score       :".Length..].Trim(),
                ref buf.ScoreCp, ref buf.ScoreMate, ref buf.ScoreBound, ref buf.HasValidScore);
        }
        else if (t.StartsWith("Depth       :"))
        {
            ParseDepth(t["Depth       :".Length..].Trim(), ref buf.Depth, ref buf.SelDepth);
        }
        else if (t.StartsWith("Nodes       :"))
        {
            ParseNodeLine(t["Nodes       :".Length..].Trim(),
                ref buf.Nodes, ref buf.Nps, ref buf.ElapsedMs);
        }
        else if (t.StartsWith("PV          :"))
        {
            buf.Pv = t["PV          :".Length..].Trim();
        }
        else if (t.StartsWith("FEN before  :"))
        {
            buf.FenBefore = t["FEN before  :".Length..].Trim();
        }
        // UCI output lines, info lines, etc. are silently ignored
    }

    private static void ParseScore(string s,
        ref int scoreCp, ref int? scoreMate, ref string scoreBound, ref bool hasValidScore)
    {
        var boundM = Regex.Match(s, @"\[(\w+)\]");
        if (boundM.Success) scoreBound = boundM.Groups[1].Value;

        if (s.StartsWith("mate", StringComparison.OrdinalIgnoreCase))
        {
            var m = Regex.Match(s, @"mate\s+(-?\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int mate))
            {
                scoreMate    = mate;
                hasValidScore = true;
            }
        }
        else
        {
            // "+10cp" or "-37cp"; skip "+F1"/"-F1" (PositionAnalyzer format bug on stats table only;
            // per-move lines correctly emit numeric values)
            var m = Regex.Match(s, @"([+\-]?\d+)cp");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int cp))
            {
                scoreCp      = cp;
                hasValidScore = true;
            }
        }
    }

    private static void ParseDepth(string s, ref int depth, ref int selDepth)
    {
        // "9"  or  "23  /  seldepth 34"
        var parts = s.Split('/', StringSplitOptions.TrimEntries);
        if (parts.Length >= 1 && int.TryParse(parts[0].Trim(), out int d)) depth = d;
        if (parts.Length >= 2)
        {
            var m = Regex.Match(parts[1], @"seldepth\s+(\d+)");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int sd)) selDepth = sd;
        }
    }

    private static void ParseNodeLine(string s,
        ref long nodes, ref long nps, ref long elapsedMs)
    {
        // "41.870  |  NPS 20.285  |  Time 2065 ms  [|  TT fill 66,2%  |  TBhits …]"
        var parts = s.Split('|');
        for (int i = 0; i < parts.Length; i++)
        {
            var part = parts[i].Trim();
            if (i == 0)
            {
                // First segment = node count (no label prefix)
                if (TryParseLong(part.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0],
                        out long nd))
                    nodes = nd;
            }
            else if (part.StartsWith("NPS "))
            {
                var raw = part[4..].Trim().Split(' ')[0];
                if (TryParseLong(raw, out long n)) nps = n;
            }
            else if (part.StartsWith("Time "))
            {
                var m = Regex.Match(part, @"(\d[\d.]*)\s*ms");
                if (m.Success && TryParseLong(m.Groups[1].Value, out long t)) elapsedMs = t;
            }
            // TT fill, TBhits ignored
        }
    }

    // ── Blunder analysis header ───────────────────────────────────────────────
    private static void ParseBlunderAnalysisHeader(string line, ParsedGame game)
    {
        if (line.Contains("no blunders", StringComparison.OrdinalIgnoreCase))
        {
            game.BlundersDetected = 0;
        }
        else
        {
            var m = Regex.Match(line, @"\((\d+)\s+blunder");
            if (m.Success && int.TryParse(m.Groups[1].Value, out int bc))
                game.BlundersDetected = bc;
        }
    }

    // ── Move finalization ─────────────────────────────────────────────────────
    private static void FinalizePendingMove(ParsedGame game, ref MoveBuf buf)
    {
        if (!buf.HasData) return;

        game.Moves.Add(new ParsedMoveData(
            buf.MoveNum,
            buf.IsWhiteMove,
            buf.IsChessBotMove,
            buf.UciMove,
            buf.FenBefore,
            buf.Depth,
            buf.SelDepth,
            buf.ScoreCp,
            buf.ScoreMate,
            buf.Nodes,
            buf.Nps,
            buf.ElapsedMs,
            buf.Pv,
            buf.ScoreBound,
            buf.HasValidScore));

        buf.Reset();
    }

    // ── Post-processing: derive aggregate stats from per-move data ────────────
    private static void DeriveStatsFromMoves(ParsedGame game)
    {
        if (game.Moves.Count == 0) return;

        // Fix up ply counts if header didn't supply them
        if (game.TotalPlies    == 0) game.TotalPlies    = game.Moves.Count;
        if (game.ChessBotPlies == 0) game.ChessBotPlies = game.Moves.Count(m => m.IsChessBotMove);
        if (game.OpponentPlies == 0) game.OpponentPlies = game.Moves.Count(m => !m.IsChessBotMove);

        var cb = game.Moves.Where(m => m.IsChessBotMove).ToList();
        if (cb.Count == 0) return;

        // Depth
        if (!game.CbAvgDepth.HasValue)
            game.CbAvgDepth = cb.Average(m => (double)m.Depth);

        // NPS (skip moves where NPS == 0 to avoid cold-start distortion)
        var npsValid = cb.Where(m => m.Nps > 0).ToList();
        if (!game.CbAvgNps.HasValue && npsValid.Count > 0)
            game.CbAvgNps = npsValid.Average(m => (double)m.Nps);
        if (!game.CbPeakNps.HasValue && npsValid.Count > 0)
            game.CbPeakNps = (double)npsValid.Max(m => m.Nps);

        // Nodes
        if (!game.CbAvgNodesPerMove.HasValue)
            game.CbAvgNodesPerMove = cb.Average(m => (double)m.Nodes);
        if (!game.CbTotalNodes.HasValue)
            game.CbTotalNodes = cb.Sum(m => m.Nodes);

        // Time
        if (!game.CbAvgTimeMsPerMove.HasValue)
            game.CbAvgTimeMsPerMove = cb.Average(m => (double)m.ElapsedMs);

        // Score — only from moves with a valid numeric score
        var scored = cb.Where(m => m.HasValidScore && m.ScoreMate == null).ToList();
        if (!game.CbAvgScoreCp.HasValue && scored.Count > 0)
            game.CbAvgScoreCp = scored.Average(m => (double)m.ScoreCp);
    }

    // ── Number parsing helpers ────────────────────────────────────────────────
    // Handles both invariant ("20285") and European ("20.285", "6,6") formats.

    internal static bool TryParseDouble(string s, out double result)
    {
        s = s.Trim().TrimEnd('%');
        // Use NumberStyles.Float (no AllowThousands) so that European decimal "7,8" is NOT
        // silently parsed as 78 by treating "," as an InvariantCulture thousands separator.
        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out result))
            return true;
        // European: "." = thousands, "," = decimal  →  remove ".", replace "," with "."
        string norm = s.Replace(".", string.Empty).Replace(',', '.');
        return double.TryParse(norm, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }

    internal static bool TryParseLong(string s, out long result)
    {
        s = s.Trim();
        // Use NumberStyles.Integer (no AllowDecimalPoint) so that European-thousands "230.000"
        // is NOT silently parsed as 230 by treating "." as an InvariantCulture decimal point.
        if (long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out result))
            return true;
        // European: remove period/comma grouping separators
        string norm = s.Replace(".", string.Empty).Replace(",", string.Empty);
        return long.TryParse(norm, NumberStyles.Any, CultureInfo.InvariantCulture, out result);
    }
}
