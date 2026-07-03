namespace ChessBot.Engine.Board;

using ChessBot.Engine.Types;
using System.Collections.Generic;

/// <summary>
/// Generates all pseudo-legal and legal moves for a given position.
/// Pseudo-legal moves are checked for legality (king safety) before being returned.
/// </summary>
internal class MoveGenerator
{
    private readonly Board _board;
    private readonly CheckDetector _checkDetector;
    private readonly List<Move> _moves;

    // Pre-allocated buffer for fast piece iteration — avoids IEnumerable heap allocations.
    // A side can have at most 16 pieces (1 king, 8 pawns, and up to 7 other pieces after promotion).
    private readonly (Square sq, Piece p)[] _pieceBuffer = new (Square, Piece)[16];

    // Precomputed direction/offset tables shared by the move generators below.
    // These used to be re-allocated as local array literals on every single Generate*Moves
    // call (once per node in the search hot path). Caching them as static readonly fields
    // makes move generation allocation-free.
    private static readonly int[] KnightFileOffsets   = { -2, -2, -1, -1, 1, 1, 2, 2 };
    private static readonly int[] KnightRankOffsets   = { -1, 1, -2, 2, -2, 2, -1, 1 };
    private static readonly int[] BishopFileOffsets   = { -1, -1, 1, 1 };
    private static readonly int[] BishopRankOffsets   = { -1, 1, -1, 1 };
    private static readonly int[] RookFileOffsets     = { -1, 1, 0, 0 };
    private static readonly int[] RookRankOffsets     = { 0, 0, -1, 1 };
    private static readonly int[] QueenFileOffsets    = { -1, -1, 1, 1, -1, 1, 0, 0 };
    private static readonly int[] QueenRankOffsets    = { -1, 1, -1, 1, 0, 0, -1, 1 };
    private static readonly int[] KingFileOffsets     = { -1, -1, -1, 0, 0, 1, 1, 1 };
    private static readonly int[] KingRankOffsets     = { -1, 0, 1, -1, 1, -1, 0, 1 };

    public MoveGenerator(Board board)
    {
        _board = board;
        _checkDetector = new CheckDetector(board);
        _moves = new List<Move>();
    }

    /// <summary>
    /// Generates all legal moves for the side whose turn it is to move.
    /// </summary>
    public List<Move> GenerateLegalMoves()
    {
        var output = new List<Move>();
        GenerateLegalMovesInto(output);
        return output;
    }

    /// <summary>
    /// Generates all legal moves into a caller-supplied list (which is cleared first).
    /// Using a pre-allocated output list avoids per-node heap allocation in the search hot-path.
    /// </summary>
    public void GenerateLegalMovesInto(List<Move> output)
    {
        output.Clear();
        _moves.Clear();
        Color activeColor = _board.ActiveColor;

        GeneratePawnMoves(activeColor);
        GenerateKnightMoves(activeColor);
        GenerateBishopMoves(activeColor);
        GenerateRookMoves(activeColor);
        GenerateQueenMoves(activeColor);
        GenerateKingMoves(activeColor);
        GenerateCastlingMoves(activeColor);

        foreach (var move in _moves)
        {
            if (IsMoveLegal(move))
                output.Add(move);
        }
    }

    /// <summary>
    /// Checks if a move is legal (doesn't leave/place the king in check).
    /// </summary>
    private bool IsMoveLegal(Move move)
    {
        // Make the move temporarily
        Piece captured = _board.GetPiece(move.To);
        Piece moving = _board.GetPiece(move.From);

        _board.SetPiece(move.From, Piece.Empty);
        _board.SetPiece(move.To, moving);

        // Handle en passant capture (remove the captured pawn)
        if ((move.MoveType & MoveType.EnPassant) != 0)
        {
            int captureRankOffset = _board.ActiveColor == Color.White ? -1 : 1;
            Square captureSquare = new Square(move.To.File, move.To.Rank + captureRankOffset);
            _board.SetPiece(captureSquare, Piece.Empty);
        }

        // Check if the king is still in check
        bool kingInCheck = _checkDetector.IsInCheck(_board.ActiveColor);

        // Undo the move
        _board.SetPiece(move.From, moving);
        _board.SetPiece(move.To, captured);
        if ((move.MoveType & MoveType.EnPassant) != 0)
        {
            int captureRankOffset = _board.ActiveColor == Color.White ? -1 : 1;
            Square captureSquare = new Square(move.To.File, move.To.Rank + captureRankOffset);
            Color oppositeColor = _board.ActiveColor.Opposite();
            _board.SetPiece(captureSquare, new Piece(oppositeColor, PieceType.Pawn));
        }

        return !kingInCheck;
    }

    /// <summary>
    /// Generates all pawn moves (quiet moves, double pushes, captures, promotions, en passant).
    /// </summary>
    private void GeneratePawnMoves(Color color)
    {
        int pawnDirection = color.PawnDirection();
        int startingRank = color == Color.White ? 1 : 6;
        int promotionRank = color == Color.White ? 7 : 0;

        int pieceCount = _board.GetPiecesOf(color, _pieceBuffer);
        for (int pi = 0; pi < pieceCount; pi++)
        {
            var (square, piece) = _pieceBuffer[pi];
            if (piece.Type != PieceType.Pawn) continue;

            // Single push forward
            int targetRank = square.Rank + pawnDirection;
            if (targetRank >= 0 && targetRank <= 7)
            {
                Square targetSquare = new Square(square.File, targetRank);
                if (_board.GetPiece(targetSquare).IsEmpty)
                {
                    if (targetRank == promotionRank)
                    {
                        // Promotion moves
                        _moves.Add(new Move(square, targetSquare, MoveType.Promotion, PieceType.Queen));
                        _moves.Add(new Move(square, targetSquare, MoveType.Promotion, PieceType.Rook));
                        _moves.Add(new Move(square, targetSquare, MoveType.Promotion, PieceType.Bishop));
                        _moves.Add(new Move(square, targetSquare, MoveType.Promotion, PieceType.Knight));
                    }
                    else
                    {
                        _moves.Add(new Move(square, targetSquare, MoveType.Quiet));
                    }

                    // Double push from starting position
                    if (square.Rank == startingRank)
                    {
                        int doubleTargetRank = square.Rank + 2 * pawnDirection;
                        Square doubleTargetSquare = new Square(square.File, doubleTargetRank);
                        if (_board.GetPiece(doubleTargetSquare).IsEmpty)
                        {
                            _moves.Add(new Move(square, doubleTargetSquare, MoveType.DoublePawnPush));
                        }
                    }
                }
            }

            // Pawn captures (left and right)
            for (int fileOffset = -1; fileOffset <= 1; fileOffset += 2)
            {
                int captureFile = square.File + fileOffset;
                int captureRank = square.Rank + pawnDirection;

                if (captureFile >= 0 && captureFile < 8 && captureRank >= 0 && captureRank <= 7)
                {
                    Square captureSquare = new Square(captureFile, captureRank);
                    Piece targetPiece = _board.GetPiece(captureSquare);

                    // Normal capture
                    if (!targetPiece.IsEmpty && targetPiece.Color != color)
                    {
                        if (captureRank == promotionRank)
                        {
                            // Promotion captures — From is always the pawn's current square
                            _moves.Add(new Move(square, captureSquare, MoveType.Capture | MoveType.Promotion, PieceType.Queen));
                            _moves.Add(new Move(square, captureSquare, MoveType.Capture | MoveType.Promotion, PieceType.Rook));
                            _moves.Add(new Move(square, captureSquare, MoveType.Capture | MoveType.Promotion, PieceType.Bishop));
                            _moves.Add(new Move(square, captureSquare, MoveType.Capture | MoveType.Promotion, PieceType.Knight));
                        }
                        else
                        {
                            _moves.Add(new Move(square, captureSquare, MoveType.Capture));
                        }
                    }

                    // En passant capture
                    if (_board.State.HasEnPassant && _board.EnPassantTarget == captureSquare)
                    {
                        _moves.Add(new Move(square, captureSquare, MoveType.EnPassant));
                    }
                }
            }
        }
    }

    /// <summary>
    /// Generates all knight moves.
    /// </summary>
    private void GenerateKnightMoves(Color color)
    {
        int pieceCount = _board.GetPiecesOf(color, _pieceBuffer);
        for (int pi = 0; pi < pieceCount; pi++)
        {
            var (square, piece) = _pieceBuffer[pi];
            if (piece.Type != PieceType.Knight) continue;

            for (int i = 0; i < 8; i++)
            {
                int targetFile = square.File + KnightFileOffsets[i];
                int targetRank = square.Rank + KnightRankOffsets[i];

                if (targetFile >= 0 && targetFile < 8 && targetRank >= 0 && targetRank < 8)
                {
                    Square targetSquare = new Square(targetFile, targetRank);
                    Piece targetPiece = _board.GetPiece(targetSquare);

                    if (targetPiece.IsEmpty)
                        _moves.Add(new Move(square, targetSquare, MoveType.Quiet));
                    else if (targetPiece.Color != color)
                        _moves.Add(new Move(square, targetSquare, MoveType.Capture));
                }
            }
        }
    }

    /// <summary>
    /// Generates all bishop moves (diagonals).
    /// </summary>
    private void GenerateBishopMoves(Color color)
    {
        int pieceCount = _board.GetPiecesOf(color, _pieceBuffer);
        for (int pi = 0; pi < pieceCount; pi++)
        {
            var (square, piece) = _pieceBuffer[pi];
            if (piece.Type != PieceType.Bishop) continue;

            for (int dir = 0; dir < 4; dir++)
            {
                GenerateSlidingMoves(square, BishopFileOffsets[dir], BishopRankOffsets[dir], color);
            }
        }
    }

    /// <summary>
    /// Generates all rook moves (orthogonal).
    /// </summary>
    private void GenerateRookMoves(Color color)
    {
        int pieceCount = _board.GetPiecesOf(color, _pieceBuffer);
        for (int pi = 0; pi < pieceCount; pi++)
        {
            var (square, piece) = _pieceBuffer[pi];
            if (piece.Type != PieceType.Rook) continue;

            for (int dir = 0; dir < 4; dir++)
            {
                GenerateSlidingMoves(square, RookFileOffsets[dir], RookRankOffsets[dir], color);
            }
        }
    }

    /// <summary>
    /// Generates all queen moves (both diagonals and orthogonal).
    /// </summary>
    private void GenerateQueenMoves(Color color)
    {
        int pieceCount = _board.GetPiecesOf(color, _pieceBuffer);
        for (int pi = 0; pi < pieceCount; pi++)
        {
            var (square, piece) = _pieceBuffer[pi];
            if (piece.Type != PieceType.Queen) continue;

            for (int dir = 0; dir < 8; dir++)
            {
                GenerateSlidingMoves(square, QueenFileOffsets[dir], QueenRankOffsets[dir], color);
            }
        }
    }

    /// <summary>
    /// Generates moves for a sliding piece (bishop, rook, queen) in a specific direction.
    /// </summary>
    private void GenerateSlidingMoves(Square square, int fileDir, int rankDir, Color color)
    {
        int file = square.File + fileDir;
        int rank = square.Rank + rankDir;

        while (file >= 0 && file < 8 && rank >= 0 && rank < 8)
        {
            Square targetSquare = new Square(file, rank);
            Piece targetPiece = _board.GetPiece(targetSquare);

            if (targetPiece.IsEmpty)
            {
                _moves.Add(new Move(square, targetSquare, MoveType.Quiet));
            }
            else
            {
                if (targetPiece.Color != color)
                    _moves.Add(new Move(square, targetSquare, MoveType.Capture));
                break;  // Path is blocked
            }

            file += fileDir;
            rank += rankDir;
        }
    }

    /// <summary>
    /// Generates all king moves (adjacent squares).
    /// </summary>
    private void GenerateKingMoves(Color color)
    {
        int pieceCount = _board.GetPiecesOf(color, _pieceBuffer);
        for (int pi = 0; pi < pieceCount; pi++)
        {
            var (square, piece) = _pieceBuffer[pi];
            if (piece.Type != PieceType.King) continue;

            for (int i = 0; i < 8; i++)
            {
                int targetFile = square.File + KingFileOffsets[i];
                int targetRank = square.Rank + KingRankOffsets[i];

                if (targetFile >= 0 && targetFile < 8 && targetRank >= 0 && targetRank < 8)
                {
                    Square targetSquare = new Square(targetFile, targetRank);
                    Piece targetPiece = _board.GetPiece(targetSquare);

                    if (targetPiece.IsEmpty)
                        _moves.Add(new Move(square, targetSquare, MoveType.Quiet));
                    else if (targetPiece.Color != color)
                        _moves.Add(new Move(square, targetSquare, MoveType.Capture));
                }
            }
        }
    }

    /// <summary>
    /// Generates all castling moves (king-side and queen-side).
    /// </summary>
    private void GenerateCastlingMoves(Color color)
    {
        // King must be on its starting square
        int kingRank = color == Color.White ? 0 : 7;
        Square kingSquare = new Square(4, kingRank);

        if (_board.GetPiece(kingSquare).Type != PieceType.King)
            return;  // King is not on starting square

        // King-side castling
        if (_board.CastlingRights.CanCastle(color, true))
        {
            Square rookSquare = new Square(7, kingRank);
            if (_board.GetPiece(rookSquare).Type == PieceType.Rook &&
                _board.GetPiece(rookSquare).Color == color)
            {
                // Check that f and g files are empty
                if (_board.GetPiece(new Square(5, kingRank)).IsEmpty &&
                    _board.GetPiece(new Square(6, kingRank)).IsEmpty)
                {
                    // King cannot be in check, and cannot move through check
                    if (!_checkDetector.IsInCheck(color) &&
                        !_checkDetector.IsSquareAttackedBy(new Square(5, kingRank), color.Opposite()))
                    {
                        Square castleKingTarget = new Square(6, kingRank);
                        _moves.Add(new Move(kingSquare, castleKingTarget, MoveType.Castling));
                    }
                }
            }
        }

        // Queen-side castling
        if (_board.CastlingRights.CanCastle(color, false))
        {
            Square rookSquare = new Square(0, kingRank);
            if (_board.GetPiece(rookSquare).Type == PieceType.Rook &&
                _board.GetPiece(rookSquare).Color == color)
            {
                // Check that b, c, d files are empty
                if (_board.GetPiece(new Square(1, kingRank)).IsEmpty &&
                    _board.GetPiece(new Square(2, kingRank)).IsEmpty &&
                    _board.GetPiece(new Square(3, kingRank)).IsEmpty)
                {
                    // King cannot be in check, and cannot move through check
                    if (!_checkDetector.IsInCheck(color) &&
                        !_checkDetector.IsSquareAttackedBy(new Square(3, kingRank), color.Opposite()))
                    {
                        Square castleKingTarget = new Square(2, kingRank);
                        _moves.Add(new Move(kingSquare, castleKingTarget, MoveType.Castling));
                    }
                }
            }
        }
    }
}
