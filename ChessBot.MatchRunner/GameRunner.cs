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

    // ── Search-shape instrumentation (ChessBot moves only; 0 for external-engine moves) ──────
    /// <summary>Main-search nodes only. <see cref="Nodes"/> is main + quiescence.</summary>
    public long   MainNodes             { get; init; }
    public long   QNodes                { get; init; }
    public long   EvaluationCalls       { get; init; }
    public long   MovesGenerated        { get; init; }
    public long   BetaCutoffs           { get; init; }
    public double FirstMoveCutoffRate   { get; init; }
    public long   NullMoveAttempts      { get; init; }
    public long   NullMoveCutoffs       { get; init; }
    public long   LmrReductions         { get; init; }
    public long   LmrReSearches         { get; init; }
    public long   FutilitySkips         { get; init; }
    public long   PvsReSearches         { get; init; }
    public long   AspirationFailLow     { get; init; }
    public long   AspirationFailHigh    { get; init; }
    public long   RepetitionDraws       { get; init; }
    /// <summary>
    /// Ratio of the last completed iteration's own node count to the previous one's — the
    /// standard effective-branching-factor estimate. 0 when fewer than two iterations
    /// completed. Replaces the former "EffectiveBranchingFactor", which raised the cumulative
    /// node total (several completed iterations plus an unfinished one) to the power 1/depth
    /// and had no branching-factor meaning.
    /// </summary>
    public double IterationNodeRatio    { get; init; }
    /// <summary>Nodes spent by the last completed iteration alone.</summary>
    public long   LastIterationNodes    { get; init; }
    /// <summary>Nodes spent inside aspiration re-searches — the cost of too-narrow windows.</summary>
    public long   AspirationRetryNodes  { get; init; }
    /// <summary>Total plies removed by LMR (sum of reductions), not the count of reduced moves.</summary>
    public long   LmrPliesSaved         { get; init; }
    /// <summary>LMR reductions bucketed by remaining depth (index = depth, capped).</summary>
    public long[] LmrReductionsByDepth      { get; init; } = Array.Empty<long>();
    /// <summary>LMR reductions bucketed by move number at the node (index = move number, capped).</summary>
    public long[] LmrReductionsByMoveNumber { get; init; } = Array.Empty<long>();
    /// <summary>
    /// True when the budget did not allow even depth 1 to finish, so the move is an
    /// unevaluated legal fallback rather than a search result.
    /// </summary>
    public bool   IsUnsearchedFallbackMove { get; init; }
    public bool   UsedPartialRootResult { get; init; }
    public int    PartialDepth          { get; init; }
    public long   BetaCutoffsFirstMove  { get; init; }
    // ── Partial-root coverage (reported for every cancelled iteration, even when the
    //    partial candidate itself was not selected as the reported move) ───────────────────
    public int    RootMovesCompleted    { get; init; }
    public int    RootMoveCount         { get; init; }
    public double RootCoveragePercent   { get; init; }
    public bool   PartialScoreIsExact   { get; init; }
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

        // Threefold repetition was not adjudicated here at all, so a repeated position ran on
        // until the fifty-move clock or the 300-move cap. Keyed on the position without the move
        // counters, which is what "the same position" means for the rule.
        var seenPositions = new Dictionary<string, int>();

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

            long   qNodes                = 0;
            long   evaluationCalls       = 0;
            long   movesGenerated        = 0;
            long   betaCutoffs           = 0;
            double firstMoveCutoffRate   = 0;
            long   nullMoveAttempts      = 0;
            long   nullMoveCutoffs       = 0;
            long   lmrReductions         = 0;
            long   lmrReSearches         = 0;
            long   futilitySkips         = 0;
            long   pvsReSearches         = 0;
            long   aspirationFailLow     = 0;
            long   aspirationFailHigh    = 0;
            long   repetitionDraws       = 0;
            double iterationNodeRatio    = 0;
            long   mainNodes            = 0;
            long   lastIterationNodes   = 0;
            long   aspirationRetryNodes = 0;
            long   lmrPliesSaved        = 0;
            long[] lmrByDepth           = Array.Empty<long>();
            long[] lmrByMoveNumber      = Array.Empty<long>();
            bool   isUnsearchedFallback = false;
            bool   usedPartialRootResult = false;
            int    partialDepth          = 0;
            long   betaCutoffsFirstMove  = 0;
            int    rootMovesCompleted    = 0;
            int    rootMoveCount         = 0;
            double rootCoveragePercent   = 0;
            bool   partialScoreIsExact   = false;

            if (chessBotMoves)
            {
                // ChessBot's turn
                var sw = System.Diagnostics.Stopwatch.StartNew();
                var settings = new SearchSettings
                {
                    MaxTimeMs             = _cfg.MoveTimeMs,
                    Verbose               = false,
                    UsePartialRootResult  = _cfg.UsePartialRootResult
                };
                var searchResult = chessBotEngine.FindBestMove(settings, ct);
                sw.Stop();

                if (searchResult.BestMove == default)
                {
                    result.TerminationReason = "ChessBot has no legal moves";
                    break;
                }

                uciMove   = searchResult.BestMove.ToString();

                // The search returns a ply-relative mate score inside the mate band, which is not
                // a centipawn value and must not be recorded as one: every consumer that averages
                // ScoreCp, or diffs it against the opponent's, would be reading a 100,000-unit
                // constant as an evaluation. Same conversion the UCI layer uses.
                var reported = SearchScores.ToReported(searchResult.Evaluation);
                scoreCp   = reported.Cp;
                scoreMate = reported.MateInMoves;
                depth     = searchResult.DepthAchieved;
                selDepth  = searchResult.SelDepth;
                nodes     = searchResult.NodesSearched;
                nps       = (long)searchResult.NodesPerSecond;
                hashFull  = searchResult.HashFull;
                tbHits    = 0;    // no tablebases
                pv        = string.Join(' ', searchResult.PrincipalVariation.Select(m => m.ToString()));
                elapsedMs = sw.ElapsedMilliseconds;
                rawLines  = Array.Empty<string>();

                qNodes                 = searchResult.QNodesSearched;
                evaluationCalls        = searchResult.EvaluationCalls;
                movesGenerated         = searchResult.MovesGenerated;
                betaCutoffs            = searchResult.BetaCutoffs;
                firstMoveCutoffRate    = searchResult.FirstMoveCutoffRate;
                nullMoveAttempts       = searchResult.NullMoveAttempts;
                nullMoveCutoffs        = searchResult.NullMoveCutoffs;
                lmrReductions          = searchResult.LmrReductions;
                lmrReSearches          = searchResult.LmrReSearches;
                futilitySkips          = searchResult.FutilitySkips;
                pvsReSearches          = searchResult.PvsReSearches;
                aspirationFailLow      = searchResult.AspirationFailLow;
                aspirationFailHigh     = searchResult.AspirationFailHigh;
                repetitionDraws        = searchResult.RepetitionDraws;
                iterationNodeRatio     = searchResult.IterationNodeRatio;
                mainNodes              = searchResult.MainNodes;
                lastIterationNodes     = searchResult.LastIterationNodes;
                aspirationRetryNodes   = searchResult.AspirationRetryNodes;
                lmrPliesSaved          = searchResult.LmrPliesSaved;
                lmrByDepth             = searchResult.LmrReductionsByDepth;
                lmrByMoveNumber        = searchResult.LmrReductionsByMoveNumber;
                isUnsearchedFallback   = searchResult.IsUnsearchedFallbackMove;
                usedPartialRootResult  = searchResult.UsedPartialRootResult;
                partialDepth           = searchResult.PartialDepth;
                betaCutoffsFirstMove   = searchResult.BetaCutoffsFirstMove;
                rootMovesCompleted     = searchResult.RootMovesCompleted;
                rootMoveCount          = searchResult.RootMoveCount;
                rootCoveragePercent    = searchResult.RootCoveragePercent;
                partialScoreIsExact    = searchResult.PartialScoreIsExact;

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
                QNodes                   = qNodes,
                EvaluationCalls          = evaluationCalls,
                MovesGenerated           = movesGenerated,
                BetaCutoffs              = betaCutoffs,
                FirstMoveCutoffRate      = firstMoveCutoffRate,
                NullMoveAttempts         = nullMoveAttempts,
                NullMoveCutoffs          = nullMoveCutoffs,
                LmrReductions            = lmrReductions,
                LmrReSearches            = lmrReSearches,
                FutilitySkips            = futilitySkips,
                PvsReSearches            = pvsReSearches,
                AspirationFailLow        = aspirationFailLow,
                AspirationFailHigh       = aspirationFailHigh,
                RepetitionDraws          = repetitionDraws,
                IterationNodeRatio       = iterationNodeRatio,
                MainNodes                = mainNodes,
                LastIterationNodes       = lastIterationNodes,
                AspirationRetryNodes     = aspirationRetryNodes,
                LmrPliesSaved            = lmrPliesSaved,
                LmrReductionsByDepth      = lmrByDepth,
                LmrReductionsByMoveNumber = lmrByMoveNumber,
                IsUnsearchedFallbackMove = isUnsearchedFallback,
                UsedPartialRootResult    = usedPartialRootResult,
                PartialDepth             = partialDepth,
                BetaCutoffsFirstMove     = betaCutoffsFirstMove,
                RootMovesCompleted       = rootMovesCompleted,
                RootMoveCount            = rootMoveCount,
                RootCoveragePercent      = rootCoveragePercent,
                PartialScoreIsExact      = partialScoreIsExact,
            });

            // Apply the move before recording it. The history is what the external engine is
            // replayed from, so a move that will not apply must never enter it — and the report
            // below has to show the history as it stood when the move was produced.
            bool applied = TryApplyMove(chessBotEngine, uciMove);
            if (!applied)
            {
                new RejectedMoveReport
                {
                    Player        = chessBotMoves ? "ChessBot" : _externalEngine.EngineName,
                    Colour        = whiteToMove ? "white" : "black",
                    MoveText      = uciMove,
                    FenBeforeMove = currentFen,
                    StartFen      = StartFen,
                    MoveHistory   = moveHistory.ToArray(),
                    LegalMoves    = chessBotEngine.GetLegalMoves().Select(m => m.ToString()).ToArray(),
                    MoveNumber    = moveNumber,
                    Depth         = depth,
                    Score         = scoreMate is int m ? $"mate {m}" : $"{scoreCp} cp",
                    Nodes         = nodes,
                    ElapsedMs     = elapsedMs,
                    BudgetMs      = _cfg.MoveTimeMs,
                    // Only an external engine has a protocol conversation to show; ChessBot is
                    // called directly, so its side is deliberately empty rather than faked.
                    MoverTrace    = chessBotMoves ? Array.Empty<string>() : _externalEngine.ProtocolTrace,
                    OpponentTrace = chessBotMoves ? _externalEngine.ProtocolTrace : Array.Empty<string>(),
                }
                .Save(Path.Combine(_cfg.PgnOutputDir, "rejected-moves.txt"));

                result.TerminationReason = $"Illegal move: {uciMove}";
                result.Outcome = GameOutcome.Aborted;
                break;
            }

            moveHistory.Add(uciMove);

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

            if (chessBotEngine.GetBoardSnapshot().HasInsufficientMaterial)
            {
                result.Outcome = GameOutcome.Draw;
                result.TerminationReason = "Insufficient material";
                break;
            }

            string repetitionKey = string.Join(' ', currentFen.Split(' ').Take(4));
            seenPositions[repetitionKey] = seenPositions.GetValueOrDefault(repetitionKey) + 1;
            if (seenPositions[repetitionKey] >= 3)
            {
                result.Outcome = GameOutcome.Draw;
                result.TerminationReason = "Threefold repetition";
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
