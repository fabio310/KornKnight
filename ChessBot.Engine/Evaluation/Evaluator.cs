namespace ChessBot.Engine.Evaluation;

using ChessBot.Engine.Types;
using ChessBot.Engine.Board;

/// <summary>
/// Static board evaluator. Scores positions from White's perspective (positive = White advantage).
/// Features: Material balance, Piece-Square Tables, pawn structure, king safety scaling.
/// All scores are in centipawns (1 pawn = 100cp).
/// </summary>
internal class Evaluator
{
    // Cached CheckDetector reused across Evaluate() calls (see EvaluateThreats). The Evaluator
    // is always paired 1:1 with a single, persistent Board instance for its lifetime, so it is
    // safe to lazily bind this once instead of allocating a new CheckDetector on every node.
    private CheckDetector? _threatDetector;
    private Board?         _threatDetectorBoard;

    // Reusable buffer for Board.GetAllPiecesInto — avoids the per-call heap allocation that
    // Board.GetAllPieces() incurs (it's a `yield return` iterator, so every invocation
    // allocates a new enumerator). Evaluate() runs at every leaf/quiescence node, so this
    // was the single hottest allocation in the engine. Max 32 pieces on a legal board.
    private readonly (Square sq, Piece p)[] _pieceBuffer = new (Square, Piece)[32];

    /// <summary>
    /// Evaluates the current position statically (without search).
    /// Positive score favors White; negative favors Black.
    /// </summary>
    /// <param name="board">Position to evaluate.</param>
    /// <param name="useThreatEval">
    /// Include the hanging-piece term (see <see cref="EvaluateThreats"/>). Defaults to true so
    /// that every existing caller keeps the behaviour it had; the search passes
    /// <c>SearchSettings.UseThreatEval</c> so the term can be measured rather than assumed.
    /// It must be passed identically to <see cref="Evaluate"/> and <see cref="EvaluateFast"/>,
    /// which are required to return the same score for the same position.
    /// </param>
    public int Evaluate(Board board, bool useThreatEval = true)
    {
        int score = 0;
        int totalMaterial = 0;

        // Single-pass over all pieces: material + PST + pawn file counts
        Span<int> whitePawnFiles = stackalloc int[8];
        Span<int> blackPawnFiles = stackalloc int[8];

        int pieceCount = board.GetAllPiecesInto(_pieceBuffer);
        for (int i = 0; i < pieceCount; i++)
        {
            var (square, piece) = _pieceBuffer[i];
            if (piece.Type == PieceType.King) continue;

            int matVal = piece.Type.MaterialValue();
            totalMaterial += matVal;
            int sign = piece.Color == Color.White ? 1 : -1;
            score += sign * matVal;

            // PST
            int rank = piece.Color == Color.White ? square.Rank : 7 - square.Rank;
            int file = piece.Color == Color.White ? square.File : 7 - square.File;
            score += sign * PieceSquareTables.TableFor(piece.Type)[rank][file];

            // Track pawn files for structure evaluation
            if (piece.Type == PieceType.Pawn)
            {
                if (piece.Color == Color.White) whitePawnFiles[square.File]++;
                else                            blackPawnFiles[square.File]++;
            }
        }

        // Pawn structure
        score += EvaluatePawnStructureFromCounts(whitePawnFiles, blackPawnFiles);

        // Threat detection: hanging pieces
        if (useThreatEval)
            score += EvaluateThreats(board, pieceCount);

        // Opening development and king safety
        score += EvaluateOpeningDevelopment(board);

        // King safety (endgame centralization)
        if (totalMaterial < 1000)
            score += EvaluateKingCentrality(board);

        // Negamax convention: return score relative to the side to move.
        int sideSign = board.State.ActiveColor == Color.White ? 1 : -1;
        return score * sideSign;
    }

    /// <summary>
    /// Fast static evaluation used on the search hot path (leaf, stand-pat, null-move staticEval).
    /// Numerically identical to <see cref="Evaluate"/>, but the material, piece-square-table, and
    /// pawn-file terms are read from the Board's incrementally maintained make/unmake eval state
    /// instead of being recomputed by a full piece scan every node. The remaining terms
    /// (hanging-piece threats, opening development, endgame king centrality) still require board
    /// context and are computed the same way as <see cref="Evaluate"/>.
    /// </summary>
    /// <param name="board">Position to evaluate.</param>
    /// <param name="useThreatEval">See <see cref="Evaluate"/>; must match what that call is given.</param>
    public int EvaluateFast(Board board, bool useThreatEval = true)
    {
        // Only the threat pass needs the piece list; with the term off, the whole board scan
        // goes away too, which is most of what disabling it saves.
        int pieceCount = useThreatEval ? board.GetAllPiecesInto(_pieceBuffer) : 0;

        // Material + PST come straight from the incremental White-positive accumulators.
        int score = board.IncrementalMaterialScore + board.IncrementalPstScore;

        // Pawn structure from the incrementally maintained per-file pawn counts.
        score += EvaluatePawnStructureFromCounts(board.WhitePawnFileCounts, board.BlackPawnFileCounts);

        // Threat detection: hanging pieces
        if (useThreatEval)
            score += EvaluateThreats(board, pieceCount);

        // Opening development and king safety
        score += EvaluateOpeningDevelopment(board);

        // King safety (endgame centralization)
        if (board.IncrementalTotalMaterial < 1000)
            score += EvaluateKingCentrality(board);

        // Negamax convention: return score relative to the side to move.
        int sideSign = board.State.ActiveColor == Color.White ? 1 : -1;
        return score * sideSign;
    }

    /// <summary>
    /// Evaluates pawn structure (doubled, isolated) from pre-computed file counts.
    /// </summary>
    private static int EvaluatePawnStructureFromCounts(ReadOnlySpan<int> whitePawnFiles, ReadOnlySpan<int> blackPawnFiles)
    {
        int score = 0;

        for (int file = 0; file < 8; file++)
        {
            // Doubled pawns penalty
            if (whitePawnFiles[file] > 1) score -= 20 * (whitePawnFiles[file] - 1);
            if (blackPawnFiles[file] > 1) score += 20 * (blackPawnFiles[file] - 1);

            // Isolated pawn penalty
            if (whitePawnFiles[file] > 0)
            {
                bool support = (file > 0 && whitePawnFiles[file - 1] > 0) ||
                               (file < 7 && whitePawnFiles[file + 1] > 0);
                if (!support) score -= 10 * whitePawnFiles[file];
            }
            if (blackPawnFiles[file] > 0)
            {
                bool support = (file > 0 && blackPawnFiles[file - 1] > 0) ||
                               (file < 7 && blackPawnFiles[file + 1] > 0);
                if (!support) score += 10 * blackPawnFiles[file];
            }
        }

        return score;
    }

    /// <summary>
    /// Evaluates pawn structure (doubled, isolated) — kept for direct callers.
    /// </summary>
    private int EvaluatePawnStructure(Board board)
    {
        Span<int> whitePawnFiles = stackalloc int[8];
        Span<int> blackPawnFiles = stackalloc int[8];

        foreach (var (square, piece) in board.GetAllPieces())
        {
            if (piece.Type != PieceType.Pawn) continue;
            if (piece.Color == Color.White) whitePawnFiles[square.File]++;
            else                            blackPawnFiles[square.File]++;
        }

        return EvaluatePawnStructureFromCounts(whitePawnFiles, blackPawnFiles);
    }

    /// <summary>
    /// Evaluates material balance: sum of all pieces' values.
    /// </summary>
    private int EvaluateMaterial(Board board)
    {
        int score = 0;
        foreach (var (square, piece) in board.GetAllPieces())
        {
            if (piece.IsEmpty) continue;
            int materialValue = piece.Type.MaterialValue();
            if (piece.Color == Color.White) score += materialValue;
            else                            score -= materialValue;
        }
        return score;
    }

    /// <summary>
    /// Evaluates piece placement using Piece-Square Tables.
    /// </summary>
    private int EvaluatePiecePlacement(Board board)
    {
        int score = 0;
        foreach (var (square, piece) in board.GetAllPieces())
        {
            if (piece.IsEmpty || piece.Type == PieceType.King) continue;
            int pstValue = GetPieceSquareTableValue(piece, square);
            if (piece.Color == Color.White) score += pstValue;
            else                            score -= pstValue;
        }
        return score;
    }

    /// <summary>
    /// Gets the PST value for a piece on a specific square.
    /// </summary>
    private int GetPieceSquareTableValue(Piece piece, Square square)
        => PieceSquareTables.Value(piece.Color, piece.Type, square);

    /// <summary>
    /// Evaluates opening-phase development quality (moves 1–20):
    ///   • Penalises minor pieces still on their home squares (encourages developing ALL pieces)
    ///   • Penalises the king lingering in the centre after move 10 (encourages castling)
    ///
    /// The evaluation is symmetric: equal positions score 0.
    /// Penalties are intentionally mild so that tactical play still dominates.
    /// </summary>
    private static int EvaluateOpeningDevelopment(Board board)
    {
        int moveNum = board.State.FullmoveNumber;
        if (moveNum > 20) return 0;

        int score = 0;

        // — Undeveloped minor pieces on home squares —
        // Each piece still on its starting square gets a fixed penalty.
        // This rewards developing ALL minor pieces, not just repeatedly moving one.

        // White minor pieces (rank 0)
        Piece wb1 = board.GetPiece(new Square(1, 0)); // b1 knight
        Piece wg1 = board.GetPiece(new Square(6, 0)); // g1 knight
        Piece wc1 = board.GetPiece(new Square(2, 0)); // c1 bishop
        Piece wf1 = board.GetPiece(new Square(5, 0)); // f1 bishop

        if (wb1.Color == Color.White && wb1.Type == PieceType.Knight) score -= 30;
        if (wg1.Color == Color.White && wg1.Type == PieceType.Knight) score -= 30;
        if (wc1.Color == Color.White && wc1.Type == PieceType.Bishop) score -= 20;
        if (wf1.Color == Color.White && wf1.Type == PieceType.Bishop) score -= 20;

        // Black minor pieces (rank 7)
        Piece bb8 = board.GetPiece(new Square(1, 7)); // b8 knight
        Piece bg8 = board.GetPiece(new Square(6, 7)); // g8 knight
        Piece bc8 = board.GetPiece(new Square(2, 7)); // c8 bishop
        Piece bf8 = board.GetPiece(new Square(5, 7)); // f8 bishop

        if (bb8.Color == Color.Black && bb8.Type == PieceType.Knight) score += 30;
        if (bg8.Color == Color.Black && bg8.Type == PieceType.Knight) score += 30;
        if (bc8.Color == Color.Black && bc8.Type == PieceType.Bishop) score += 20;
        if (bf8.Color == Color.Black && bf8.Type == PieceType.Bishop) score += 20;

        // — King safety: penalise uncastled king in the centre after move 10 —
        if (moveNum >= 10)
        {
            Square wKing = board.GetKingPosition(Color.White);
            Square bKing = board.GetKingPosition(Color.Black);

            // King on e-file and on its original rank = still on starting square, not castled
            if (wKing.File == 4 && wKing.Rank == 0) score -= 40;
            if (bKing.File == 4 && bKing.Rank == 7) score += 40;
        }

        return score;
    }

    /// <summary>
    /// Evaluates king safety and endgame considerations (kept for callers that invoke it directly).
    /// </summary>
    private int EvaluateKingSafety(Board board)
    {
        int materialCount = CountMaterial(board);
        return materialCount < 1000 ? EvaluateKingCentrality(board) : 0;
    }

    /// <summary>
    /// Counts total material on board (excluding kings).
    /// </summary>
    private static int CountMaterial(Board board)
    {
        int count = 0;
        foreach (var (_, piece) in board.GetAllPieces())
        {
            if (piece.Type != PieceType.King && piece.Type != PieceType.None)
                count += piece.Type.MaterialValue();
        }
        return count;
    }

    /// <summary>
    /// Bonus for king centralization in endgame.
    /// </summary>
    private int EvaluateKingCentrality(Board board)
    {
        int score = 0;

        Square whiteKing = board.GetKingPosition(Color.White);
        Square blackKing = board.GetKingPosition(Color.Black);

        // Bonus for centralization: closer to center (d4, e4, d5, e5) is better
        int whiteKingCentrality = CalculateCentralityBonus(whiteKing);
        int blackKingCentrality = CalculateCentralityBonus(blackKing);

        score += whiteKingCentrality - blackKingCentrality;

        return score;
    }

    /// <summary>
    /// Calculates centrality bonus for a square (bonus for center squares).
    /// </summary>
    private int CalculateCentralityBonus(Square square)
    {
        // Distance to the nearer edge on each axis: 0 on the rim, 3 on the four centre squares.
        //
        // The previous form computed `3 - clamp(File - 3)`, which yields 6 on file a and 0 on
        // file h — a gradient towards the h8 corner rather than towards the centre. Because the
        // same function scored both kings, mirroring a position changed the White-minus-Black
        // difference, so the evaluation was not colour-symmetric and the engine judged the two
        // colours differently in endgames (the only phase where this term is active).
        int fileDist = Math.Min(square.File, 7 - square.File);
        int rankDist = Math.Min(square.Rank, 7 - square.Rank);

        return Math.Max(0, fileDist + rankDist - 3) * 3;  // 9 at the centre, 0 at the rim
    }

    /// <summary>
    /// Penalizes fully undefended pieces (hanging pieces attacked with no defender).
    /// Uses fast early-exit IsSquareAttackedBy checks instead of the more expensive
    /// GetMinAttackerValue ray scans to keep the hot eval path affordable.
    /// Reuses the piece buffer already populated by Evaluate() (via <paramref name="pieceCount"/>)
    /// instead of re-scanning the whole board a second time.
    /// Returns a White-positive score adjustment.
    /// </summary>
    private int EvaluateThreats(Board board, int pieceCount)
    {
        int score = 0;
        // Reuse a single CheckDetector per Evaluator instance instead of allocating a new
        // one on every Evaluate() call — Evaluate() runs at every leaf/quiescence node,
        // so this was a major per-node heap allocation in the hot path.
        // The detector is cached to avoid a per-node allocation on the hot path, but it binds
        // to the Board it was constructed with. Caching it unconditionally meant that calling
        // the same Evaluator with a *different* Board silently kept reading the first one and
        // returned threat scores for the wrong position. Rebinding when the board instance
        // changes keeps the allocation saving (the search reuses one Board throughout) while
        // removing the silent-wrong-answer case.
        if (_threatDetector is null || !ReferenceEquals(_threatDetectorBoard, board))
        {
            _threatDetector      = new CheckDetector(board);
            _threatDetectorBoard = board;
        }
        var cd = _threatDetector;

        for (int i = 0; i < pieceCount; i++)
        {
            var (square, piece) = _pieceBuffer[i];
            if (piece.Type == PieceType.None
                || piece.Type == PieceType.Pawn
                || piece.Type == PieceType.King)
                continue;

            Color enemy = piece.Color.Opposite();

            // Fast exit: not attacked at all
            if (!cd.IsSquareAttackedBy(square, enemy))
                continue;

            int pieceValue = piece.Type.MaterialValue();
            int sign       = piece.Color == Color.White ? 1 : -1;

            if (!cd.IsSquareAttackedBy(square, piece.Color))
            {
                // Completely undefended and attacked: half-value penalty
                score -= sign * pieceValue / 2;
            }
            // Defended pieces may still be in a losing exchange, but quiescence
            // search handles those cases; omitting it here avoids expensive SEE.
        }

        return score;
    }
}
