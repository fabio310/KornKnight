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
    /// <summary>
    /// Piece-Square Tables for all piece types, from White's perspective.
    /// Index [0] = rank 1, [7] = rank 8 (White's side to Black's side).
    /// File order: a-h (left to right).
    /// </summary>
    private int[][] _pstPawn;
    private int[][] _pstKnight;
    private int[][] _pstBishop;
    private int[][] _pstRook;
    private int[][] _pstQueen;
    private int[][] _pstKing;

    // Cached CheckDetector reused across Evaluate() calls (see EvaluateThreats). The Evaluator
    // is always paired 1:1 with a single, persistent Board instance for its lifetime, so it is
    // safe to lazily bind this once instead of allocating a new CheckDetector on every node.
    private CheckDetector? _threatDetector;

    // Reusable buffer for Board.GetAllPiecesInto — avoids the per-call heap allocation that
    // Board.GetAllPieces() incurs (it's a `yield return` iterator, so every invocation
    // allocates a new enumerator). Evaluate() runs at every leaf/quiescence node, so this
    // was the single hottest allocation in the engine. Max 32 pieces on a legal board.
    private readonly (Square sq, Piece p)[] _pieceBuffer = new (Square, Piece)[32];

    public Evaluator()
    {
        _pstPawn = Array.Empty<int[]>();
        _pstKnight = Array.Empty<int[]>();
        _pstBishop = Array.Empty<int[]>();
        _pstRook = Array.Empty<int[]>();
        _pstQueen = Array.Empty<int[]>();
        _pstKing = Array.Empty<int[]>();
        InitializePieceTables();
    }

    /// <summary>
    /// Evaluates the current position statically (without search).
    /// Positive score favors White; negative favors Black.
    /// </summary>
    public int Evaluate(Board board)
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
            int[][] pst = piece.Type switch
            {
                PieceType.Pawn   => _pstPawn,
                PieceType.Knight => _pstKnight,
                PieceType.Bishop => _pstBishop,
                PieceType.Rook   => _pstRook,
                PieceType.Queen  => _pstQueen,
                _                => _pstPawn
            };
            score += sign * pst[rank][file];

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
    /// Evaluates pawn structure (doubled, isolated) from pre-computed file counts.
    /// </summary>
    private static int EvaluatePawnStructureFromCounts(Span<int> whitePawnFiles, Span<int> blackPawnFiles)
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
    {
        // For Black pieces, flip the square (rotate 180 degrees)
        int rank = piece.Color == Color.White ? square.Rank : 7 - square.Rank;
        int file = piece.Color == Color.White ? square.File : 7 - square.File;

        int[][] pst = piece.Type switch
        {
            PieceType.Pawn => _pstPawn,
            PieceType.Knight => _pstKnight,
            PieceType.Bishop => _pstBishop,
            PieceType.Rook => _pstRook,
            PieceType.Queen => _pstQueen,
            PieceType.King => _pstKing,
            _ => _pstPawn  // Fallback
        };

        return pst[rank][file];
    }

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
        int distFromCenter = 3 - Math.Min(3, Math.Max(-3, square.File - 3)) +
                            3 - Math.Min(3, Math.Max(-3, square.Rank - 3));

        return Math.Max(0, 3 - distFromCenter) * 3;  // ~9 bonus at exact center, ~0 at edges
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
        var cd = _threatDetector ??= new CheckDetector(board);

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

    /// <summary>
    /// Initializes Piece-Square Tables with standard values.
    /// Tables are from White's perspective (rank 0 = bottom, rank 7 = top).
    /// </summary>
    private void InitializePieceTables()
    {
        // Pawn PST (pawns advance forward, prefer central files)
        _pstPawn = new int[8][]
        {
            new int[] { 0, 0, 0, 0, 0, 0, 0, 0 },
            new int[] { 5, 5, 5, 10, 10, 5, 5, 5 },
            new int[] { 5, 5, 10, 20, 20, 10, 5, 5 },
            new int[] { 5, 5, 10, 25, 25, 10, 5, 5 },
            new int[] { 5, 5, 10, 20, 20, 10, 5, 5 },
            new int[] { 5, 5, 5, 10, 10, 5, 5, 5 },
            new int[] { 30, 30, 30, 30, 30, 30, 30, 30 },
            new int[] { 0, 0, 0, 0, 0, 0, 0, 0 }
        };

        // Knight PST (centralize knights)
        _pstKnight = new int[8][]
        {
            new int[] { -50, -40, -30, -30, -30, -30, -40, -50 },
            new int[] { -40, -20, 0, 5, 5, 0, -20, -40 },
            new int[] { -30, 5, 10, 15, 15, 10, 5, -30 },
            new int[] { -30, 5, 15, 20, 20, 15, 5, -30 },
            new int[] { -30, 5, 15, 20, 20, 15, 5, -30 },
            new int[] { -30, 5, 10, 15, 15, 10, 5, -30 },
            new int[] { -40, -20, 0, 5, 5, 0, -20, -40 },
            new int[] { -50, -40, -30, -30, -30, -30, -40, -50 }
        };

        // Bishop PST (control center and long diagonals)
        _pstBishop = new int[8][]
        {
            new int[] { -20, -10, -10, -10, -10, -10, -10, -20 },
            new int[] { -10, 5, 5, 5, 5, 5, 5, -10 },
            new int[] { -10, 5, 10, 10, 10, 10, 5, -10 },
            new int[] { -10, 5, 10, 15, 15, 10, 5, -10 },
            new int[] { -10, 5, 10, 15, 15, 10, 5, -10 },
            new int[] { -10, 5, 10, 10, 10, 10, 5, -10 },
            new int[] { -10, 5, 5, 5, 5, 5, 5, -10 },
            new int[] { -20, -10, -10, -10, -10, -10, -10, -20 }
        };

        // Rook PST (control files, especially open files)
        _pstRook = new int[8][]
        {
            new int[] { 0, 0, 0, 5, 5, 0, 0, 0 },
            new int[] { 5, 10, 10, 10, 10, 10, 10, 5 },
            new int[] { 5, 10, 10, 10, 10, 10, 10, 5 },
            new int[] { 5, 10, 10, 10, 10, 10, 10, 5 },
            new int[] { 5, 10, 10, 10, 10, 10, 10, 5 },
            new int[] { 5, 10, 10, 10, 10, 10, 10, 5 },
            new int[] { 5, 10, 10, 10, 10, 10, 10, 5 },
            new int[] { 0, 0, 0, 5, 5, 0, 0, 0 }
        };

        // Queen PST (centralize slightly)
        _pstQueen = new int[8][]
        {
            new int[] { -20, -10, -10, -5, -5, -10, -10, -20 },
            new int[] { -10, 0, 5, 5, 5, 5, 0, -10 },
            new int[] { -10, 5, 5, 5, 5, 5, 5, -10 },
            new int[] { -5, 5, 5, 5, 5, 5, 5, -5 },
            new int[] { -5, 5, 5, 5, 5, 5, 5, -5 },
            new int[] { -10, 5, 5, 5, 5, 5, 5, -10 },
            new int[] { -10, 0, 5, 5, 5, 5, 0, -10 },
            new int[] { -20, -10, -10, -5, -5, -10, -10, -20 }
        };

        // King PST (keep safe in opening/middlegame, centralize in endgame)
        _pstKing = new int[8][]
        {
            new int[] { 20, 30, 10, 0, 0, 10, 30, 20 },
            new int[] { 20, 20, 0, 0, 0, 0, 20, 20 },
            new int[] { -10, -20, -20, -20, -20, -20, -20, -10 },
            new int[] { -20, -30, -30, -40, -40, -30, -30, -20 },
            new int[] { -20, -30, -30, -40, -40, -30, -30, -20 },
            new int[] { -10, -20, -20, -20, -20, -20, -20, -10 },
            new int[] { 20, 20, 0, 0, 0, 0, 20, 20 },
            new int[] { 20, 30, 10, 0, 0, 10, 30, 20 }
        };
    }
}
