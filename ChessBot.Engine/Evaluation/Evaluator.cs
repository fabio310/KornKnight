namespace ChessBot.Engine.Evaluation;

using ChessBot.Engine.Types;
using ChessBot.Engine.Board;

/// <summary>
/// Static board evaluator. Scores positions from White's perspective (positive = White advantage).
/// Features: Material balance, Piece-Square Tables (tapered, kings included), pawn structure.
/// All scores are in centipawns (1 pawn = 100cp).
/// </summary>
internal class Evaluator
{
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
    public int Evaluate(Board board)
    {
        int score = 0;
        int phase = 0;

        // Midgame and endgame material+PST are accumulated separately and blended once, after
        // the phase is known — the phase is only complete when the whole scan is.
        int midgame = 0;
        int endgame = 0;

        // Single-pass over all pieces: material + PST + pawn file counts
        Span<int> whitePawnFiles = stackalloc int[8];
        Span<int> blackPawnFiles = stackalloc int[8];

        int pieceCount = board.GetAllPiecesInto(_pieceBuffer);
        for (int i = 0; i < pieceCount; i++)
        {
            var (square, piece) = _pieceBuffer[i];

            // The same definition the Board's incremental accumulator maintains, which is what
            // lets EvaluateFast reproduce this score exactly.
            phase += GamePhase.WeightFor(piece.Type);

            // The king is in the accumulators like any other piece. Its material value is 0 on
            // both scales, so it contributes nothing but its piece-square term — which is the
            // whole point: the king's midgame-to-endgame transition is the largest single thing
            // a taper buys, and a king scored outside the interpolation bypasses it.
            int matVal = piece.Type.MaterialValue();
            int sign = piece.Color == Color.White ? 1 : -1;

            // PST
            int rank = piece.Color == Color.White ? square.Rank : 7 - square.Rank;
            int file = piece.Color == Color.White ? square.File : 7 - square.File;

            midgame += sign * (matVal + PieceSquareTables.TableFor(piece.Type)[rank][file]);

            endgame += sign * (PieceSquareTables.EndgameMaterialValue(piece.Type)
                             + PieceSquareTables.EndgameTableFor(piece.Type)[rank][file]);

            // Track pawn files for structure evaluation
            if (piece.Type == PieceType.Pawn)
            {
                if (piece.Color == Color.White) whitePawnFiles[square.File]++;
                else                            blackPawnFiles[square.File]++;
            }
        }

        // Material + PST, blended on the phase.
        score += PieceSquareTables.Interpolate(midgame, endgame, phase);

        // Pawn structure
        score += EvaluatePawnStructureFromCounts(whitePawnFiles, blackPawnFiles);

        // Opening development
        score += EvaluateOpeningDevelopment(board, phase);

        // Negamax convention: return score relative to the side to move.
        int sideSign = board.State.ActiveColor == Color.White ? 1 : -1;
        return score * sideSign;
    }

    /// <summary>
    /// Fast static evaluation used on the search hot path (leaf, stand-pat, null-move staticEval).
    /// Numerically identical to <see cref="Evaluate"/>, but the material, piece-square-table, and
    /// pawn-file terms are read from the Board's incrementally maintained make/unmake eval state
    /// instead of being recomputed by a full piece scan every node. Only the opening-development
    /// term still requires board context, and it is computed the same way as
    /// <see cref="Evaluate"/>.
    ///
    /// Nothing here scans the board any more. The hanging-piece term was the last caller that
    /// needed the piece list, so removing it took the per-node board scan with it.
    /// </summary>
    /// <param name="board">Position to evaluate.</param>
    public int EvaluateFast(Board board)
    {
        // Material + PST come straight from the incremental White-positive accumulators, one
        // pair per scale, both maintained by the same make/unmake bookkeeping.
        int score = PieceSquareTables.Interpolate(
            board.IncrementalMaterialScore + board.IncrementalPstScore,
            board.IncrementalEndgameMaterialScore + board.IncrementalEndgamePstScore,
            board.IncrementalPhase);

        // Pawn structure from the incrementally maintained per-file pawn counts.
        score += EvaluatePawnStructureFromCounts(board.WhitePawnFileCounts, board.BlackPawnFileCounts);

        // Opening development. The phase comes from the Board's incremental accumulator rather
        // than a scan — the same quantity Evaluate() sums piece by piece.
        score += EvaluateOpeningDevelopment(board, board.IncrementalPhase);

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
    /// Evaluates development quality:
    ///   • Penalises minor pieces still on their home squares (encourages developing ALL pieces)
    ///   • Penalises the king lingering in the centre (encourages castling)
    ///
    /// The evaluation is symmetric: equal positions score 0.
    /// Penalties are intentionally mild so that tactical play still dominates.
    ///
    /// How much the term applies is a function of the material phase: full weight with the
    /// starting array on the board, fading smoothly to nothing as pieces come off. It depends
    /// only on the position, so it survives a transposition and cannot change as a search
    /// descends.
    ///
    /// The rule this replaced read the move number instead — full value to move 20, nothing
    /// after, with the king penalty switched on at move 10 — which made the evaluation a
    /// function of something the position does not contain. The Zobrist hash does not include
    /// the move number, so a transposition entry carried a score that was only valid at the move
    /// number it was stored at; inside a search tree crossing move 20 the score jumped by up to
    /// 100 cp because plies elapsed, which paid the engine to shuffle rather than develop; and
    /// the same position reached by a longer route evaluated differently from itself.
    /// </summary>
    /// <param name="phase">The position's 24-point material phase (see <see cref="GamePhase"/>).</param>
    private static int EvaluateOpeningDevelopment(Board board, int phase)
    {
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

        // — King safety: penalise an uncastled king in the centre —
        // The rule this replaced waited until move 10 to give the engine time to castle first.
        // There is nothing to wait for here: material barely changes in ten moves, so no phase
        // threshold could reproduce that gate. The penalty simply applies while there is still an
        // army on the board to be afraid of, and fades with it.
        Square wKing = board.GetKingPosition(Color.White);
        Square bKing = board.GetKingPosition(Color.Black);

        // King on e-file and on its original rank = still on starting square, not castled
        if (wKing.File == 4 && wKing.Rank == 0) score -= 40;
        if (bKing.File == 4 && bKing.Rank == 7) score += 40;

        return GamePhase.ScaleByOpening(score, phase);
    }
}
