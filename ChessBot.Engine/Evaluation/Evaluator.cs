namespace ChessBot.Engine.Evaluation;

using ChessBot.Engine.Types;
using ChessBot.Engine.Board;

/// <summary>
/// Static board evaluator. Scores positions from White's perspective (positive = White advantage).
/// Features: Material balance, Piece-Square Tables (tapered, kings included), pawn structure,
/// passed pawns, the bishop pair and rook placement.
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

        // Single-pass over all pieces: material + PST + pawn file counts + pawn and rook occupancy
        // + bishop counts. Everything the terms below need, gathered in the one scan the Board's
        // incremental accumulators reproduce move by move.
        Span<int> whitePawnFiles = stackalloc int[8];
        Span<int> blackPawnFiles = stackalloc int[8];
        ulong whitePawns = 0UL;
        ulong blackPawns = 0UL;
        ulong whiteRooks = 0UL;
        ulong blackRooks = 0UL;
        int whiteBishops = 0;
        int blackBishops = 0;

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
                if (piece.Color == Color.White) { whitePawnFiles[square.File]++; whitePawns |= 1UL << square.Index; }
                else                            { blackPawnFiles[square.File]++; blackPawns |= 1UL << square.Index; }
            }
            else if (piece.Type == PieceType.Rook)
            {
                if (piece.Color == Color.White) whiteRooks |= 1UL << square.Index;
                else                            blackRooks |= 1UL << square.Index;
            }
            else if (piece.Type == PieceType.Bishop)
            {
                if (piece.Color == Color.White) whiteBishops++;
                else                            blackBishops++;
            }
        }

        // Material + PST, blended on the phase.
        score += PieceSquareTables.Interpolate(midgame, endgame, phase);

        // Pawn structure
        score += EvaluatePawnStructureFromCounts(whitePawnFiles, blackPawnFiles, phase);

        // Passed pawns, from the occupancy this scan just built.
        score += PassedPawns.Evaluate(whitePawns, blackPawns, phase);

        // Bishop pair and rook placement, from the counts and occupancy this scan just built.
        score += BishopPair(whiteBishops, blackBishops, phase);
        score += RookFiles.Evaluate(whiteRooks, blackRooks, whitePawns, blackPawns, phase);

        // Negamax convention: return score relative to the side to move.
        int sideSign = board.State.ActiveColor == Color.White ? 1 : -1;
        return score * sideSign;
    }

    /// <summary>
    /// Fast static evaluation used on the search hot path (leaf, stand-pat, null-move staticEval).
    /// Numerically identical to <see cref="Evaluate"/>, but every term is read from the Board's
    /// incrementally maintained make/unmake eval state instead of being recomputed by a full piece
    /// scan every node.
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
        score += EvaluatePawnStructureFromCounts(board.WhitePawnFileCounts, board.BlackPawnFileCounts,
                                                 board.IncrementalPhase);

        // Passed pawns from the incrementally maintained pawn occupancy — the same two bitboards
        // Evaluate() rebuilds by scanning, so the two agree by construction.
        score += PassedPawns.Evaluate(board.WhitePawnBitboard, board.BlackPawnBitboard,
                                      board.IncrementalPhase);

        // Bishop pair and rook placement, from the incrementally maintained counts and rook
        // occupancy — again the same quantities Evaluate() rebuilds by scanning.
        score += BishopPair(board.WhiteBishopCount, board.BlackBishopCount, board.IncrementalPhase);
        score += RookFiles.Evaluate(board.WhiteRookBitboard, board.BlackRookBitboard,
                                    board.WhitePawnBitboard, board.BlackPawnBitboard,
                                    board.IncrementalPhase);

        // Negamax convention: return score relative to the side to move.
        int sideSign = board.State.ActiveColor == Color.White ? 1 : -1;
        return score * sideSign;
    }

    /// <summary>Doubled pawn penalty, per extra pawn on a file, on each scale.</summary>
    private const int DoubledMidgame = 15;
    private const int DoubledEndgame = 30;

    /// <summary>Isolated pawn penalty, per pawn with no friendly pawn on either neighbouring file.</summary>
    private const int IsolatedMidgame = 8;
    private const int IsolatedEndgame = 18;

    /// <summary>
    /// Evaluates pawn structure (doubled, isolated) from pre-computed file counts, blended on the
    /// phase like every other term.
    ///
    /// Both penalties were flat before — one number for the whole game — which is the wrong shape
    /// for the thing they describe. A doubled pawn in a middlegame is a structural blemish paid
    /// for by an open file and a piece on it; the same pawn in a king-and-pawn endgame is a pawn
    /// that cannot create a passer and a file that cannot be defended. The same goes for an
    /// isolated pawn, which in the middlegame has pieces to shield it and in the endgame does not.
    ///
    /// The midgame weights are slightly below the old flat values and the endgame weights well
    /// above, so the change is to the shape rather than to the average magnitude. Nothing in the
    /// piece-square tables says anything about doubling or isolation — a pawn's table value is the
    /// same whatever stands beside or behind it — so unlike the passed pawn case there is no table
    /// here to double.
    /// </summary>
    /// <param name="phase">The position's 24-point material phase (see <see cref="GamePhase"/>).</param>
    private static int EvaluatePawnStructureFromCounts(ReadOnlySpan<int> whitePawnFiles,
                                                       ReadOnlySpan<int> blackPawnFiles,
                                                       int phase)
    {
        int midgame = 0;
        int endgame = 0;

        for (int file = 0; file < 8; file++)
        {
            // Doubled pawns penalty
            if (whitePawnFiles[file] > 1)
            {
                midgame -= DoubledMidgame * (whitePawnFiles[file] - 1);
                endgame -= DoubledEndgame * (whitePawnFiles[file] - 1);
            }
            if (blackPawnFiles[file] > 1)
            {
                midgame += DoubledMidgame * (blackPawnFiles[file] - 1);
                endgame += DoubledEndgame * (blackPawnFiles[file] - 1);
            }

            // Isolated pawn penalty
            if (whitePawnFiles[file] > 0)
            {
                bool support = (file > 0 && whitePawnFiles[file - 1] > 0) ||
                               (file < 7 && whitePawnFiles[file + 1] > 0);
                if (!support)
                {
                    midgame -= IsolatedMidgame * whitePawnFiles[file];
                    endgame -= IsolatedEndgame * whitePawnFiles[file];
                }
            }
            if (blackPawnFiles[file] > 0)
            {
                bool support = (file > 0 && blackPawnFiles[file - 1] > 0) ||
                               (file < 7 && blackPawnFiles[file + 1] > 0);
                if (!support)
                {
                    midgame += IsolatedMidgame * blackPawnFiles[file];
                    endgame += IsolatedEndgame * blackPawnFiles[file];
                }
            }
        }

        return PieceSquareTables.Interpolate(midgame, endgame, phase);
    }

    /// <summary>Bishop pair bonus, on each scale.</summary>
    private const int BishopPairMidgame = 25;
    private const int BishopPairEndgame = 40;

    /// <summary>
    /// The bishop pair: two bishops cover both colour complexes, which no other piece combination
    /// does and which nothing else in this evaluation can express. Material values a bishop the
    /// same whether it is the first or the second, and the bishop piece-square tables are among
    /// the flattest in the set — a spread of 35 in the midgame and 20 in the endgame — so unlike
    /// the passed pawn case there is genuinely room here rather than a table to amplify.
    ///
    /// Worth more as the board opens, which the phase stands in for. The ramp is deliberately
    /// shallow because part of that is already paid: a bishop's endgame material value is 340
    /// against a midgame 320, while a knight's falls from 320 to 310, so a bishop already gains on
    /// a knight as pieces come off. What is left for this term is the pair as such.
    /// </summary>
    /// <param name="phase">The position's 24-point material phase (see <see cref="GamePhase"/>).</param>
    private static int BishopPair(int whiteBishops, int blackBishops, int phase)
    {
        int count = (whiteBishops >= 2 ? 1 : 0) - (blackBishops >= 2 ? 1 : 0);
        if (count == 0) return 0;

        return PieceSquareTables.Interpolate(count * BishopPairMidgame,
                                             count * BishopPairEndgame, phase);
    }

}
