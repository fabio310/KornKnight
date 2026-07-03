namespace ChessBot.MatchRunner;

using ChessBot.Engine;
using ChessBot.Engine.Types;
using ChessBot.Engine.Search;

/// <summary>
/// Outcome of a game from ChessBot's perspective.
/// </summary>
public enum GameOutcome
{
    ChessBotWin,
    ChessBotLoss,
    Draw,
    Aborted
}

/// <summary>
/// Complete record of a single move during a game.
/// Stores every metric available from ChessBot's SearchResult or the external UCI engine.
/// </summary>
public record MoveRecord
{
    // ── Identity ──────────────────────────────────────────────────────────────
    public required int    MoveNumber     { get; init; }
    public required bool   IsWhiteMove    { get; init; }
    public required bool   IsChessBotMove { get; init; }
    public required string UciMove        { get; init; }
    public required string Fen            { get; init; } // position *before* the move
    // ── Search depth ─────────────────────────────────────────────────────────
    public required int    Depth          { get; init; }
    public          int    SelDepth       { get; init; } // 0 = not reported
    // ── Score ────────────────────────────────────────────────────────────────
    public required int    ScoreCp        { get; init; } // centipawns, side-to-move positive
    public          int?   ScoreMate      { get; init; } // mate in N; null if not a mate score
    public          string ScoreBound     { get; init; } = "exact"; // "exact"|"lowerbound"|"upperbound"
    // ── Search stats ─────────────────────────────────────────────────────────
    public required long   Nodes          { get; init; }
    public required long   Nps            { get; init; }
    public          int    HashFull       { get; init; } = -1; // TT fill per-mille; -1 = not reported
    public          long   TbHits         { get; init; }
    public required long   ElapsedMs      { get; init; }
    // ── Variation ────────────────────────────────────────────────────────────
    public required string Pv             { get; init; }
    // ── Raw engine output (external engine only) ─────────────────────────────
    public IReadOnlyList<string> RawUciLines { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Complete record of a played game.
/// </summary>
public class GameResult
{
    public int GameNumber       { get; init; }
    public bool ChessBotIsWhite { get; init; }
    public GameOutcome Outcome  { get; set; } = GameOutcome.Aborted;
    public List<MoveRecord> Moves { get; } = new();
    public string InitialFen      { get; init; } = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    public string? TerminationReason { get; set; }
    /// <summary>Path of the PGN file written for this game.</summary>
    public string PgnPath { get; set; } = string.Empty;
}

/// <summary>
/// Plays a full game between ChessBot (internal engine) and an external UCI engine.
/// Saves FEN snapshots before each move, logs depth/score/nodes/NPS/PV/time, writes PGN.
/// </summary>
public class GameRunner
{
    private const string StartFen = "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1";
    private const int MaxMoves = 300;

    private readonly UciAdapter _externalEngine;
    private readonly MatchConfig _cfg;

    public GameRunner(UciAdapter externalEngine, MatchConfig cfg)
    {
        _externalEngine = externalEngine;
        _cfg = cfg;
    }

    public async Task<GameResult> PlayGameAsync(bool chessBotIsWhite, int gameNumber, CancellationToken ct = default)
    {
        var result = new GameResult
        {
            GameNumber      = gameNumber,
            ChessBotIsWhite = chessBotIsWhite,
            InitialFen      = StartFen
        };

        var chessBotEngine = new ChessEngine();
        chessBotEngine.LoadFen(StartFen);
        await _externalEngine.NewGameAsync();

        var moveHistory = new List<string>();
        string currentFen = StartFen;
        bool whiteToMove = true;

        if (_cfg.Verbose)
        {
            Console.WriteLine($"  ChessBot plays {(chessBotIsWhite ? "White" : "Black")}");
            Console.WriteLine($"  Move time: {_cfg.MoveTimeMs}ms per move");
        }

        for (int plyCount = 0; plyCount < MaxMoves * 2; plyCount++)
        {
            ct.ThrowIfCancellationRequested();

            int moveNumber = plyCount / 2 + 1;
            bool chessBotMoves = (whiteToMove == chessBotIsWhite);
            string uciMove;
            int    scoreCp    = 0;
            int?   scoreMate  = null;
            string scoreBound = "exact";
            int    depth      = 0;
            int    selDepth   = 0;
            long   nodes      = 0;
            long   nps        = 0;
            int    hashFull   = -1;
            long   tbHits     = 0;
            string pv         = string.Empty;
            long   elapsedMs  = 0;
            IReadOnlyList<string> rawLines = Array.Empty<string>();

            if (chessBotMoves)
            {
                // ChessBot's turn
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var settings = new SearchSettings
                {
                    MaxTimeMs = _cfg.MoveTimeMs,
                    Verbose   = false
                };
                var searchResult = chessBotEngine.FindBestMove(settings, ct);
                sw.Stop();

                if (searchResult.BestMove == default)
                {
                    result.TerminationReason = "ChessBot has no legal moves";
                    break;
                }

                uciMove   = searchResult.BestMove.ToString();
                scoreCp   = searchResult.Evaluation;
                scoreMate = null; // ChessBot reports mate via high cp value, not UCI mate score
                depth     = searchResult.DepthAchieved;
                selDepth  = searchResult.SelDepth;
                nodes     = searchResult.NodesSearched;
                nps       = (long)searchResult.NodesPerSecond;
                hashFull  = searchResult.HashFull;
                tbHits    = 0;    // no tablebases
                pv        = string.Join(' ', searchResult.PrincipalVariation.Select(m => m.ToString()));
                elapsedMs = sw.ElapsedMilliseconds;
                rawLines  = Array.Empty<string>();

                if (_cfg.Verbose)
                    Console.WriteLine($"  {(whiteToMove ? "W" : "B")} move {moveNumber,-3}: {uciMove,-8} " +
                                      $"depth={depth} score={scoreCp:+#;-#;0}cp " +
                                      $"nodes={nodes} nps={nps:N0} time={elapsedMs}ms");
            }
            else
            {
                // External engine's turn.
                // Always send the initial FEN plus the full move list so the engine
                // reconstructs the position from scratch. Passing currentFen (the
                // already-advanced position) together with moveHistory would cause the
                // moves to be applied twice, producing an illegal board state.
                var uciResult = await _externalEngine.GetBestMoveAsync(StartFen, moveHistory, _cfg.MoveTimeMs, ct);

                if (string.IsNullOrEmpty(uciResult.BestMove))
                {
                    result.TerminationReason = "External engine has no legal moves";
                    break;
                }

                uciMove    = uciResult.BestMove;
                scoreCp    = uciResult.ScoreCp;
                scoreMate  = uciResult.ScoreMate;
                scoreBound = uciResult.ScoreBound;
                depth      = uciResult.Depth;
                selDepth   = uciResult.SelDepth;
                nodes      = uciResult.Nodes;
                nps        = uciResult.Nps;
                hashFull   = uciResult.HashFull;
                tbHits     = uciResult.TbHits;
                pv         = uciResult.Pv;
                elapsedMs  = uciResult.TimeMs;
                rawLines   = uciResult.InfoLines.AsReadOnly();

                if (_cfg.Verbose)
                    Console.WriteLine($"  {(whiteToMove ? "W" : "B")} move {moveNumber,-3}: {uciMove,-8} " +
                                      $"depth={depth} score={scoreCp:+#;-#;0}cp " +
                                      $"nodes={nodes} nps={nps:N0} time={elapsedMs}ms");
            }

            // Record the move with all available metrics
            result.Moves.Add(new MoveRecord
            {
                MoveNumber     = moveNumber,
                IsWhiteMove    = whiteToMove,
                IsChessBotMove = chessBotMoves,
                UciMove        = uciMove,
                Fen            = currentFen,
                Depth          = depth,
                SelDepth       = selDepth,
                ScoreCp        = scoreCp,
                ScoreMate      = scoreMate,
                ScoreBound     = scoreBound,
                Nodes          = nodes,
                Nps            = nps,
                HashFull       = hashFull,
                TbHits         = tbHits,
                Pv             = pv,
                ElapsedMs      = elapsedMs,
                RawUciLines    = rawLines,
            });

            // Apply move to both ChessBot board and move history
            moveHistory.Add(uciMove);

            bool applied = TryApplyMove(chessBotEngine, uciMove);
            if (!applied)
            {
                result.TerminationReason = $"Illegal move: {uciMove}";
                result.Outcome = GameOutcome.Aborted;
                break;
            }

            currentFen = chessBotEngine.ExportFen();
            whiteToMove = !whiteToMove;

            // Check terminal conditions
            var legalMoves = chessBotEngine.GetLegalMoves();
            if (legalMoves.Count == 0)
            {
                bool inCheck = chessBotEngine.GetBoardSnapshot().IsKingInCheck(
                    whiteToMove ? Color.White : Color.Black);

                if (inCheck)
                {
                    result.Outcome = DetermineCheckmateOutcome(whiteToMove, chessBotIsWhite);
                    result.TerminationReason = $"Checkmate — {(whiteToMove ? "Black" : "White")} wins";
                }
                else
                {
                    result.Outcome = GameOutcome.Draw;
                    result.TerminationReason = "Stalemate";
                }
                break;
            }

            if (chessBotEngine.GetBoardSnapshot().State.IsFiftyMoveRuleDraw)
            {
                result.Outcome = GameOutcome.Draw;
                result.TerminationReason = "50-move rule";
                break;
            }
        }

        if (result.Outcome == GameOutcome.Aborted && string.IsNullOrEmpty(result.TerminationReason))
        {
            result.Outcome = GameOutcome.Draw;
            result.TerminationReason = "Max moves reached";
        }

        // Save PGN
        string pgnPath = Path.Combine(_cfg.PgnOutputDir,
            $"game{gameNumber}_{DateTime.Now:yyyyMMdd_HHmmss}.pgn");
        PgnWriter.Write(pgnPath, result);
        result.PgnPath = pgnPath;

        if (_cfg.Verbose)
            Console.WriteLine($"  PGN saved: {pgnPath}");

        return result;
    }

    private static bool TryApplyMove(ChessEngine engine, string uciMove)
    {
        try
        {
            var legalMoves = engine.GetLegalMoves();
            Move? matchedMove = null;

            foreach (var m in legalMoves)
            {
                if (m.ToString().Equals(uciMove, StringComparison.OrdinalIgnoreCase))
                {
                    matchedMove = m;
                    break;
                }
            }

            if (matchedMove is null)
                return false;

            engine.MakeMove(matchedMove.Value);
            return true;
        }
        catch
        {
            return false;
        }
    }

    private static GameOutcome DetermineCheckmateOutcome(bool whiteJustGotMated, bool chessBotIsWhite)
    {
        // whiteJustGotMated = it was White's turn and White is in checkmate
        bool chessBotLost = (whiteJustGotMated && chessBotIsWhite) ||
                            (!whiteJustGotMated && !chessBotIsWhite);
        return chessBotLost ? GameOutcome.ChessBotLoss : GameOutcome.ChessBotWin;
    }
}
