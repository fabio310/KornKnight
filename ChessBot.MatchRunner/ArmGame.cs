namespace ChessBot.MatchRunner;

using ChessBot.Engine;
using ChessBot.Engine.Types;

/// <summary>
/// Plays one game between two engine binaries, with a local board as arbiter.
///
/// Both sides are driven over UCI, exactly the way the external opponent in a rating-anchor
/// match already is. Neither arm is the harness's own in-process engine, so nothing about the
/// comparison depends on the two sides sharing a process, a transposition table, or a build.
///
/// The result is recorded as a <see cref="GameResult"/> whose "measured player" is arm A: the
/// outcome and <see cref="MoveRecord.IsChessBotMove"/> are relative to A, so the PGN writer, the
/// position analyzer and the reference-engine move-loss analysis all apply unchanged.
/// </summary>
public static class ArmGame
{
    /// <summary>Ply cap. A game that reaches it is scored as a draw and says so.</summary>
    public const int MaxPlies = 600;

    /// <summary>
    /// Plays arm <paramref name="white"/> against arm <paramref name="black"/> from
    /// <paramref name="opening"/>.
    /// </summary>
    /// <param name="armAIsWhite">
    /// Which of the two supplied engines is arm A. The caller has already decided the colours;
    /// this only says which side the result is reported from.
    /// </param>
    public static async Task<GameResult> PlayAsync(
        UciAdapter white,
        UciAdapter black,
        EngineArm armA,
        EngineArm armB,
        bool armAIsWhite,
        Opening opening,
        MoveBudget budget,
        int gameNumber,
        string outputDir,
        CancellationToken ct = default)
    {
        string startFen = opening.Fen;

        var result = new GameResult
        {
            GameNumber         = gameNumber,
            ChessBotIsWhite    = armAIsWhite,
            MeasuredPlayerName = armA.Label,
            OpponentPlayerName = armB.Label,
            InitialFen         = startFen,
            OpeningName        = opening.Name,
        };

        var board = new ChessEngine();
        board.LoadFen(startFen);

        await white.NewGameAsync();
        await black.NewGameAsync();

        var moveHistory = new List<string>();
        string currentFen = startFen;
        bool whiteToMove = board.SideToMove == Color.White;
        bool firstPlyIsWhite = whiteToMove;
        int firstMoveNumber = FullMoveNumberOf(startFen);

        // Threefold repetition is adjudicated here because nothing else would: two engines with
        // the same evaluation shuffle forever otherwise, and in an A/B run between two builds of
        // one engine that is the common case rather than the exception.
        var seenPositions = new Dictionary<string, int>();

        for (int ply = 0; ply < MaxPlies; ply++)
        {
            ct.ThrowIfCancellationRequested();

            var mover = whiteToMove ? white : black;
            bool isArmAMove = whiteToMove == armAIsWhite;

            // Always the start position plus the full move list, never the already-advanced FEN:
            // sending both would apply the moves twice, and the move list is also the only way
            // the engine learns the repetition history the arbiter is holding it to.
            var move = await mover.GetBestMoveAsync(startFen, moveHistory, budget, ct);

            if (string.IsNullOrEmpty(move.BestMove))
            {
                result.TerminationReason = $"{(isArmAMove ? armA.Label : armB.Label)} returned no move";
                result.Outcome = GameOutcome.Aborted;
                break;
            }

            int moveNumber = firstMoveNumber + (ply + (firstPlyIsWhite ? 0 : 1)) / 2;

            result.Moves.Add(new MoveRecord
            {
                MoveNumber     = moveNumber,
                IsWhiteMove    = whiteToMove,
                IsChessBotMove = isArmAMove,
                UciMove        = move.BestMove,
                Fen            = currentFen,
                Depth          = move.Depth,
                SelDepth       = move.SelDepth,
                ScoreCp        = move.ScoreCp,
                ScoreMate      = move.ScoreMate,
                ScoreBound     = move.ScoreBound,
                Nodes          = move.Nodes,
                Nps            = move.Nps,
                HashFull       = move.HashFull,
                TbHits         = move.TbHits,
                Pv             = move.Pv,
                ElapsedMs      = move.TimeMs,
                RawUciLines    = move.InfoLines.AsReadOnly(),
            });

            if (!TryApplyMove(board, move.BestMove))
            {
                new RejectedMoveReport
                {
                    Player        = isArmAMove ? armA.Label : armB.Label,
                    Colour        = whiteToMove ? "white" : "black",
                    MoveText      = move.BestMove,
                    FenBeforeMove = currentFen,
                    StartFen      = startFen,
                    MoveHistory   = moveHistory.ToArray(),
                    LegalMoves    = board.GetLegalMoves().Select(m => m.ToString()).ToArray(),
                    MoveNumber    = moveNumber,
                    Depth         = move.Depth,
                    Score         = move.ScoreMate is int m ? $"mate {m}" : $"{move.ScoreCp} cp",
                    Nodes         = move.Nodes,
                    ElapsedMs     = move.TimeMs,
                    BudgetMs      = budget.Kind == BudgetKind.Time ? (int)budget.Value : 0,
                    MoverTrace    = mover.ProtocolTrace,
                    OpponentTrace = (whiteToMove ? black : white).ProtocolTrace,
                }
                .Save(Path.Combine(outputDir, "rejected-moves.txt"));

                result.TerminationReason = $"Illegal move: {move.BestMove}";
                result.Outcome = GameOutcome.Aborted;
                break;
            }

            moveHistory.Add(move.BestMove);
            currentFen  = board.ExportFen();
            whiteToMove = !whiteToMove;

            var legal = board.GetLegalMoves();
            if (legal.Count == 0)
            {
                if (board.GetBoardSnapshot().IsKingInCheck(whiteToMove ? Color.White : Color.Black))
                {
                    // The side to move is mated, so the side that just moved won.
                    bool armAWon = whiteToMove != armAIsWhite;
                    result.Outcome = armAWon ? GameOutcome.ChessBotWin : GameOutcome.ChessBotLoss;
                    result.TerminationReason = $"Checkmate — {(whiteToMove ? "Black" : "White")} wins";
                }
                else
                {
                    result.Outcome = GameOutcome.Draw;
                    result.TerminationReason = "Stalemate";
                }
                break;
            }

            if (board.GetBoardSnapshot().State.IsFiftyMoveRuleDraw)
            {
                result.Outcome = GameOutcome.Draw;
                result.TerminationReason = "50-move rule";
                break;
            }

            if (board.GetBoardSnapshot().HasInsufficientMaterial)
            {
                result.Outcome = GameOutcome.Draw;
                result.TerminationReason = "Insufficient material";
                break;
            }

            // The position without the move counters, which is what "the same position" means
            // for the threefold rule.
            string key = string.Join(' ', currentFen.Split(' ').Take(4));
            seenPositions[key] = seenPositions.GetValueOrDefault(key) + 1;
            if (seenPositions[key] >= 3)
            {
                result.Outcome = GameOutcome.Draw;
                result.TerminationReason = "Threefold repetition";
                break;
            }
        }

        if (result.Outcome == GameOutcome.Aborted && string.IsNullOrEmpty(result.TerminationReason))
        {
            result.Outcome = GameOutcome.Draw;
            result.TerminationReason = $"Ply limit ({MaxPlies}) reached";
        }

        string pgnPath = Path.Combine(outputDir, $"game{gameNumber:D5}.pgn");
        PgnWriter.Write(pgnPath, result);
        result.PgnPath = pgnPath;

        return result;
    }

    private static bool TryApplyMove(ChessEngine board, string uciMove)
    {
        foreach (var candidate in board.GetLegalMoves())
        {
            if (!candidate.ToString().Equals(uciMove, StringComparison.OrdinalIgnoreCase)) continue;
            board.MakeMove(candidate);
            return true;
        }
        return false;
    }

    private static int FullMoveNumberOf(string fen)
    {
        var fields = fen.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return fields.Length >= 6 && int.TryParse(fields[5], out int n) && n >= 1 ? n : 1;
    }
}
