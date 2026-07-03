namespace ChessBot.Engine.Board;

using ChessBot.Engine.Types;
using ChessBot.Engine.Hashing;

/// <summary>
/// Represents the chess board state and piece placement.
/// Uses a 0x64 (8x8 mailbox) representation for simplicity and correctness.
/// The architecture is decoupled to allow future optimization (e.g., Bitboard) without interface changes.
/// 
/// Internal layout:
/// Index 0-7:    a1-h1 (White's first rank)
/// Index 8-15:   a2-h2
/// ...
/// Index 56-63:  a8-h8 (Black's first rank)
/// </summary>
public class Board
{
    /// <summary>
    /// The underlying piece array: 64 squares (0-63).
    /// Index maps directly to Square.Index via file (column) and rank (row).
    /// </summary>
    private readonly Piece[] _pieces;

    /// <summary>
    /// Tracks the position of each king for quick access during check detection.
    /// </summary>
    private readonly Square[] _kingPositions;

    /// <summary>
    /// A history of previous board states (for move undo operations).
    /// Stores: the move made, the captured piece, the game state, and the Zobrist hash before the move.
    /// Uses List (not Stack) so the Searcher can iterate by index without a ToArray() allocation.
    /// </summary>
    private readonly List<(Move move, Piece capturedPiece, GameState gameState, ulong hash)> _history;

    /// <summary>
    /// The current game state (active color, castling rights, en passant, halfmove clock, fullmove number).
    /// </summary>
    private GameState _gameState;

    // ── Incremental Zobrist hashing ───────────────────────────────────────────────────
    // Maintained by SetPiece (piece keys) + MakeMove/UndoMove (castling/EP/color keys).
    // Eliminates the O(32-per-node) cost of recomputing the hash from scratch each node.
    private readonly ZobristHasher _hasher;
    private ulong _hash;

    /// <summary>
    /// The current Zobrist hash for the position.
    /// Updated incrementally by MakeMove/UndoMove; always consistent with the board state.
    /// </summary>
    public ulong ZobristHash => _hash;

    public Board()
    {
        _pieces = new Piece[64];
        _kingPositions = new Square[2];
        _history = new List<(Move, Piece, GameState, ulong)>(128);
        _gameState = new GameState();
        _hasher = new ZobristHasher();
        _hash = 0;

        // Initialize with empty pieces
        for (int i = 0; i < 64; i++)
            _pieces[i] = Piece.Empty;
    }

    /// <summary>
    /// Gets the piece at a specific square.
    /// </summary>
    public Piece GetPiece(Square square) => _pieces[square.Index];

    /// <summary>
    /// Sets a piece on a specific square (internal use only, no validation).
    /// </summary>
    internal void SetPiece(Square square, Piece piece)
    {
        // XOR out the old piece key before replacing
        Piece old = _pieces[square.Index];
        if (old.Type != PieceType.None)
            _hash ^= _hasher.GetPieceKey(old.Color, old.Type, square);

        _pieces[square.Index] = piece;

        // XOR in the new piece key after placement
        if (piece.Type != PieceType.None)
            _hash ^= _hasher.GetPieceKey(piece.Color, piece.Type, square);

        // Track king positions for efficient check detection
        if (piece.Type == PieceType.King)
            _kingPositions[(int)piece.Color] = square;
    }

    /// <summary>
    /// Sets a piece on a specific square WITHOUT updating the Zobrist hash or king positions.
    /// Intended only for the cheap, throwaway make/restore probe used by
    /// <see cref="MoveGenerator"/>'s legality check: that probe always reverts the board to its
    /// exact prior state before returning, so paying the incremental-hash XOR cost (which exists
    /// to keep the hash valid across real, persisted moves) is pure waste. Callers are responsible
    /// for restoring the exact previous piece via a matching SetPieceRaw call.
    /// </summary>
    internal void SetPieceRaw(Square square, Piece piece)
    {
        _pieces[square.Index] = piece;
    }

    /// <summary>
    /// Directly overwrites the tracked king position for a color WITHOUT touching the board or
    /// hash. Paired with <see cref="SetPieceRaw"/> so the temporary legality probe can move a king
    /// and have <see cref="GetKingPosition"/>/<see cref="CheckDetector"/> see the probed square,
    /// then restore the original position afterward.
    /// </summary>
    internal void SetKingPositionRaw(Color color, Square square)
    {
        _kingPositions[(int)color] = square;
    }

    /// <summary>
    /// Gets the current game state (active color, castling rights, etc.).
    /// </summary>
    public GameState State => _gameState;

    /// <summary>
    /// Gets the side whose turn it is to move.
    /// </summary>
    public Color ActiveColor => _gameState.ActiveColor;

    /// <summary>
    /// Gets the castling rights available in the current position.
    /// </summary>
    public CastlingRights CastlingRights => _gameState.CastlingRights;

    /// <summary>
    /// Gets the en passant target square (if any).
    /// </summary>
    public Square EnPassantTarget => _gameState.EnPassantTarget;

    /// <summary>
    /// <summary>
    /// Gets the move history for repetition detection.
    /// Entries are (move, capturedPiece, previousState, hashBeforeMove); index 0 = oldest, Count-1 = newest.
    /// </summary>
    internal List<(Move move, Piece capturedPiece, GameState gameState, ulong hash)> History => _history;

    /// <summary>
    /// Gets the position of the specified color's king.
    /// </summary>
    public Square GetKingPosition(Color color) => _kingPositions[(int)color];

    /// <summary>
    /// Returns true if the specified color's king is currently in check.
    /// </summary>
    public bool IsKingInCheck(Color color)
    {
        var detector = new CheckDetector(this);
        return detector.IsInCheck(color);
    }

    /// <summary>
    /// Makes a move on the board, updating all state (piece positions, castling rights, en passant, halfmove clock, fullmove number).
    /// Pushes the move and previous state to the undo stack for move reversal.
    /// </summary>
    public void MakeMove(Move move)
    {
        // Save the current state for undo (including hash for O(1) restoration)
        Piece capturedPiece = GetPiece(move.To);
        GameState preMoveState = _gameState;
        _history.Add((move, capturedPiece, _gameState, _hash));

        // Get the moving piece
        Piece movingPiece = GetPiece(move.From);
        if (movingPiece.IsEmpty)
            throw new InvalidOperationException($"Cannot make move: no piece on {move.From}.");

        // Move the piece
        SetPiece(move.From, Piece.Empty);
        SetPiece(move.To, movingPiece);

        // Handle special moves
        if ((move.MoveType & MoveType.Castling) != 0)
        {
            HandleCastling(move);
        }

        if ((move.MoveType & MoveType.EnPassant) != 0)
        {
            HandleEnPassantCapture(move);
        }

        // Handle pawn promotion
        if ((move.MoveType & MoveType.Promotion) != 0)
        {
            SetPiece(move.To, new Piece(movingPiece.Color, move.PromotionType));
        }

        // Update castling rights — pass capturedPiece (saved before move) so detection is correct
        UpdateCastlingRights(movingPiece, move.From, move.To, capturedPiece);

        // Update en passant target
        Square newEnPassantTarget = new Square(0);  // Default: no en passant
        if ((move.MoveType & MoveType.DoublePawnPush) != 0)
        {
            // Set en passant target to the square behind the pawn
            int epRank = movingPiece.Color == Color.White ? move.To.Rank - 1 : move.To.Rank + 1;
            newEnPassantTarget = new Square(move.To.File, epRank);
        }

        // Update halfmove clock (reset on pawn moves or captures)
        int newHalfmoveClock = _gameState.HalfmoveClock + 1;
        if (movingPiece.Type == PieceType.Pawn || (move.MoveType & MoveType.Capture) != 0 || (move.MoveType & MoveType.EnPassant) != 0)
            newHalfmoveClock = 0;

        // Update fullmove number (increment after Black's move)
        int newFullmoveNumber = _gameState.FullmoveNumber;
        if (_gameState.ActiveColor == Color.Black)
            newFullmoveNumber++;

        // Update game state
        _gameState = new GameState(
            _gameState.ActiveColor.Opposite(),
            _gameState.CastlingRights,
            newEnPassantTarget,
            newHalfmoveClock,
            newFullmoveNumber
        );

        // Update Zobrist hash for non-piece state changes.
        // Piece-level XORs are handled automatically by SetPiece calls above.
        _hash ^= _hasher.GetCastlingKey(preMoveState.CastlingRights.Mask);
        _hash ^= _hasher.GetCastlingKey(_gameState.CastlingRights.Mask);
        if (preMoveState.EnPassantTarget.Index != 0)
            _hash ^= _hasher.GetEnPassantKey(preMoveState.EnPassantTarget.File);
        if (_gameState.EnPassantTarget.Index != 0)
            _hash ^= _hasher.GetEnPassantKey(_gameState.EnPassantTarget.File);
        _hash ^= _hasher.GetColorKey(Color.Black); // Always toggle; Black key encodes whose turn it is
    }

    /// <summary>
    /// Handles rook movement for castling
    /// </summary>
    private void HandleCastling(Move move)
    {
        int rank = move.To.Rank;
        if (move.To.File == 6)  // King-side castling (king moves to g-file)
        {
            Piece rook = GetPiece(new Square(7, rank));
            SetPiece(new Square(7, rank), Piece.Empty);
            SetPiece(new Square(5, rank), rook);
        }
        else if (move.To.File == 2)  // Queen-side castling (king moves to c-file)
        {
            Piece rook = GetPiece(new Square(0, rank));
            SetPiece(new Square(0, rank), Piece.Empty);
            SetPiece(new Square(3, rank), rook);
        }
    }

    /// <summary>
    /// Handles en passant capture (removing the captured pawn from the board).
    /// </summary>
    private void HandleEnPassantCapture(Move move)
    {
        int captureRank = _gameState.ActiveColor == Color.White ? move.To.Rank - 1 : move.To.Rank + 1;
        Square captureSquare = new Square(move.To.File, captureRank);
        SetPiece(captureSquare, Piece.Empty);
    }

    /// <summary>
    /// Updates castling rights based on the move (loses rights if king or rook moves, or if a corner rook is captured).
    /// capturedPiece must be passed in from before the move was applied to the board.
    /// </summary>
    private void UpdateCastlingRights(Piece movingPiece, Square from, Square to, Piece capturedPiece)
    {
        CastlingRights rights = _gameState.CastlingRights;

        // King move: lose all castling rights for that color
        if (movingPiece.Type == PieceType.King)
        {
            rights = rights.RevokeColor(movingPiece.Color);
        }

        // Rook move: lose castling right for that side
        if (movingPiece.Type == PieceType.Rook)
        {
            if (movingPiece.Color == Color.White && from.Rank == 0)
            {
                if (from.File == 0)
                    rights = rights.Revoke(Color.White, false);  // Queen-side rook
                else if (from.File == 7)
                    rights = rights.Revoke(Color.White, true);   // King-side rook
            }
            else if (movingPiece.Color == Color.Black && from.Rank == 7)
            {
                if (from.File == 0)
                    rights = rights.Revoke(Color.Black, false);  // Queen-side rook
                else if (from.File == 7)
                    rights = rights.Revoke(Color.Black, true);   // King-side rook
            }
        }

        // Rook capture: lose castling right for the captured rook's side.
        // capturedPiece is the pre-move occupant of 'to', passed by the caller.
        if (capturedPiece.Type == PieceType.Rook)
        {
            if (capturedPiece.Color == Color.White && to.Rank == 0)
            {
                if (to.File == 0)
                    rights = rights.Revoke(Color.White, false);
                else if (to.File == 7)
                    rights = rights.Revoke(Color.White, true);
            }
            else if (capturedPiece.Color == Color.Black && to.Rank == 7)
            {
                if (to.File == 0)
                    rights = rights.Revoke(Color.Black, false);
                else if (to.File == 7)
                    rights = rights.Revoke(Color.Black, true);
            }
        }

        _gameState = new GameState(
            _gameState.ActiveColor,
            rights,
            _gameState.EnPassantTarget,
            _gameState.HalfmoveClock,
            _gameState.FullmoveNumber
        );
    }

    /// <summary>
    /// Undoes the last move made. Throws if no moves have been made.
    /// Restores all piece positions, game state, and move history.
    /// </summary>
    public void UndoMove()
    {
        if (_history.Count == 0)
            throw new InvalidOperationException("Cannot undo: no moves have been made.");

        var entry = _history[_history.Count - 1];
        _history.RemoveAt(_history.Count - 1);
        var (move, capturedPiece, previousState, savedHash) = entry;

        // Restore the game state
        _gameState = previousState;

        // Get the piece that moved (now on the destination square)
        Piece movedPiece = GetPiece(move.To);

        // Reverse the move: piece goes back to source square
        SetPiece(move.To, capturedPiece);  // Restore captured piece (or empty)

        // If it was a promotion, restore the pawn; otherwise restore the moved piece as-is
        if ((move.MoveType & MoveType.Promotion) != 0)
        {
            SetPiece(move.From, new Piece(movedPiece.Color, PieceType.Pawn));
        }
        else
        {
            SetPiece(move.From, movedPiece);
        }

        // Handle undo of special moves
        if ((move.MoveType & MoveType.Castling) != 0)
        {
            UndoCastling(move);
        }

        if ((move.MoveType & MoveType.EnPassant) != 0)
        {
            UndoEnPassantCapture(move, previousState);
        }

        // Restore the Zobrist hash to its exact pre-move value (faster than reversing all XORs)
        _hash = savedHash;
    }

    /// <summary>
    /// Makes a null move: passes the turn to the opponent without moving a piece.
    /// Used by null-move pruning in the search. Call UndoNullMove() to revert.
    /// </summary>
    public void MakeNullMove()
    {
        // Save current state so we can restore it (including hash)
        var savedState = _gameState;
        _history.Add((default, Piece.Empty, savedState, _hash));

        // Update hash: XOR out old EP (if any), toggle color; castling is unchanged
        if (savedState.EnPassantTarget.Index != 0)
            _hash ^= _hasher.GetEnPassantKey(savedState.EnPassantTarget.File);
        _hash ^= _hasher.GetColorKey(Color.Black);

        // Switch side to move and reset en passant (a null move cannot create en passant)
        _gameState = new GameState(
            savedState.ActiveColor.Opposite(),
            savedState.CastlingRights,
            default,                       // no en passant after null move
            savedState.HalfmoveClock + 1,
            savedState.FullmoveNumber
        );
    }

    /// <summary>
    /// Undoes a null move made with MakeNullMove().
    /// </summary>
    public void UndoNullMove()
    {
        if (_history.Count == 0)
            throw new InvalidOperationException("Cannot undo null move: history is empty.");

        var nullEntry = _history[_history.Count - 1];
        _history.RemoveAt(_history.Count - 1);
        var (_, _, previousState, savedHash) = nullEntry;
        _gameState = previousState;
        _hash = savedHash;
    }

    /// <summary>
    /// Undoes a castling move by restoring the rook to its original corner.
    /// </summary>
    private void UndoCastling(Move move)
    {
        int rank = move.To.Rank;
        if (move.To.File == 6)  // King-side castling (undo: rook from f-file back to h-file)
        {
            Piece rook = GetPiece(new Square(5, rank));
            SetPiece(new Square(5, rank), Piece.Empty);
            SetPiece(new Square(7, rank), rook);
        }
        else if (move.To.File == 2)  // Queen-side castling (undo: rook from d-file back to a-file)
        {
            Piece rook = GetPiece(new Square(3, rank));
            SetPiece(new Square(3, rank), Piece.Empty);
            SetPiece(new Square(0, rank), rook);
        }
    }

    /// <summary>
    /// Undoes an en passant capture by restoring the captured pawn to the board.
    /// </summary>
    private void UndoEnPassantCapture(Move move, GameState previousState)
    {
        // The captured pawn was on the source rank, same file as destination
        int captureRank = previousState.ActiveColor == Color.White ? move.To.Rank - 1 : move.To.Rank + 1;
        Square captureSquare = new Square(move.To.File, captureRank);
        Color capturedColor = previousState.ActiveColor.Opposite();
        SetPiece(captureSquare, new Piece(capturedColor, PieceType.Pawn));
    }

    /// <summary>
    /// Resets the board to the starting position.
    /// </summary>
    public void ResetToStartingPosition()
    {
        // Clear the board
        for (int i = 0; i < 64; i++)
            _pieces[i] = Piece.Empty;
        _history.Clear();
        _hash = 0; // Reset before SetPiece calls accumulate piece keys

        // Set up White pieces
        SetPiece(Square.FromAlgebraic("a1"), new Piece(Color.White, PieceType.Rook));
        SetPiece(Square.FromAlgebraic("b1"), new Piece(Color.White, PieceType.Knight));
        SetPiece(Square.FromAlgebraic("c1"), new Piece(Color.White, PieceType.Bishop));
        SetPiece(Square.FromAlgebraic("d1"), new Piece(Color.White, PieceType.Queen));
        SetPiece(Square.FromAlgebraic("e1"), new Piece(Color.White, PieceType.King));
        SetPiece(Square.FromAlgebraic("f1"), new Piece(Color.White, PieceType.Bishop));
        SetPiece(Square.FromAlgebraic("g1"), new Piece(Color.White, PieceType.Knight));
        SetPiece(Square.FromAlgebraic("h1"), new Piece(Color.White, PieceType.Rook));

        for (int file = 0; file < 8; file++)
            SetPiece(new Square(file, 1), new Piece(Color.White, PieceType.Pawn));

        // Set up Black pieces
        SetPiece(Square.FromAlgebraic("a8"), new Piece(Color.Black, PieceType.Rook));
        SetPiece(Square.FromAlgebraic("b8"), new Piece(Color.Black, PieceType.Knight));
        SetPiece(Square.FromAlgebraic("c8"), new Piece(Color.Black, PieceType.Bishop));
        SetPiece(Square.FromAlgebraic("d8"), new Piece(Color.Black, PieceType.Queen));
        SetPiece(Square.FromAlgebraic("e8"), new Piece(Color.Black, PieceType.King));
        SetPiece(Square.FromAlgebraic("f8"), new Piece(Color.Black, PieceType.Bishop));
        SetPiece(Square.FromAlgebraic("g8"), new Piece(Color.Black, PieceType.Knight));
        SetPiece(Square.FromAlgebraic("h8"), new Piece(Color.Black, PieceType.Rook));

        for (int file = 0; file < 8; file++)
            SetPiece(new Square(file, 6), new Piece(Color.Black, PieceType.Pawn));

        // Reset game state to starting position
        _gameState = new GameState(
            Color.White,
            CastlingRights.FromFenString("KQkq"),
            new Square(0),  // No en passant
            0,
            1
        );

        // Finalize hash: pieces are already XOR'd in by SetPiece; add state components.
        // White to move → no color XOR. No en passant. KQkq castling.
        _hash ^= _hasher.GetCastlingKey(_gameState.CastlingRights.Mask);
    }

    /// <summary>
    /// Returns a copy of the current board for independent manipulation or analysis.
    /// </summary>
    public Board Copy()
    {
        var copy = new Board();
        Array.Copy(_pieces, copy._pieces, 64);
        Array.Copy(_kingPositions, copy._kingPositions, 2);
        copy._gameState = _gameState;
        copy._hash = _hash; // Copy the incremental hash
        // Note: History is not copied; the copy starts fresh
        return copy;
    }

    /// <summary>
    /// Returns an enumerable of all pieces on the board with their positions.
    /// </summary>
    public IEnumerable<(Square square, Piece piece)> GetAllPieces()
    {
        for (int i = 0; i < 64; i++)
        {
            if (_pieces[i].Type != PieceType.None)
                yield return (new Square(i), _pieces[i]);
        }
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> with every piece on the board (both colors) and
    /// returns how many were written. The buffer must have at least 32 elements (max
    /// possible pieces on a legal board). This avoids the heap allocation that the
    /// GetAllPieces() iterator incurs, which matters because static evaluation runs
    /// this scan at every leaf and quiescence stand-pat node — the majority of nodes
    /// in the search tree.
    /// </summary>
    internal int GetAllPiecesInto((Square sq, Piece p)[] buffer)
    {
        int count = 0;
        for (int i = 0; i < 64; i++)
        {
            Piece p = _pieces[i];
            if (p.Type != PieceType.None)
                buffer[count++] = (new Square(i), p);
        }
        return count;
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> with all pieces belonging to <paramref name="color"/>
    /// and returns how many were written.  The buffer must have at least 16 elements.
    /// This avoids heap allocation in hot search paths.
    /// </summary>
    internal int GetPiecesOf(Color color, (Square sq, Piece p)[] buffer)
    {
        int count = 0;
        for (int i = 0; i < 64; i++)
        {
            Piece p = _pieces[i];
            if (p.Type != PieceType.None && p.Color == color)
                buffer[count++] = (new Square(i), p);
        }
        return count;
    }

    /// <summary>
    /// Loads a position from FEN notation.
    /// Format: "rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1"
    /// </summary>
    /// <exception cref="ArgumentException">Thrown if FEN is malformed.</exception>
    public void LoadFromFen(string fen)
    {
        if (string.IsNullOrWhiteSpace(fen))
            throw new ArgumentException("FEN string cannot be null or empty.");

        string[] parts = fen.Split(' ');
        if (parts.Length < 6)
            throw new ArgumentException($"FEN must have 6 parts separated by spaces, got {parts.Length}.");

        // Clear the board and history
        for (int i = 0; i < 64; i++)
            _pieces[i] = Piece.Empty;
        _history.Clear();
        _hash = 0; // Reset before SetPiece calls accumulate piece keys

        // Part 0: Piece placement (from rank 8 to rank 1)
        string piecePlacement = parts[0];
        string[] ranks = piecePlacement.Split('/');
        if (ranks.Length != 8)
            throw new ArgumentException($"Piece placement must have 8 ranks, got {ranks.Length}.");

        for (int rankIdx = 0; rankIdx < 8; rankIdx++)
        {
            string rank = ranks[rankIdx];
            int fileIdx = 0;

            foreach (char c in rank)
            {
                if (char.IsDigit(c))
                {
                    // Skip empty squares
                    fileIdx += (c - '0');
                }
                else
                {
                    // Place a piece
                    Piece piece = Piece.FromFenChar(c);
                    if (piece.Type == PieceType.None)
                        throw new ArgumentException($"Invalid piece character: '{c}'.");

                    if (fileIdx >= 8)
                        throw new ArgumentException($"Rank {8 - rankIdx} has too many files.");

                    // Rank in FEN goes 8->1, so rank index is 7 - rankIdx
                    int squareIndex = (7 - rankIdx) * 8 + fileIdx;
                    SetPiece(new Square(squareIndex), piece);
                    fileIdx++;
                }
            }

            if (fileIdx != 8)
                throw new ArgumentException($"Rank {8 - rankIdx} has {fileIdx} files, expected 8.");
        }

        // Part 1: Active color
        Color activeColor = parts[1].ToLower() == "w" ? Color.White : Color.Black;

        // Part 2: Castling rights
        CastlingRights castlingRights = CastlingRights.FromFenString(parts[2]);

        // Part 3: En passant target
        Square enPassantTarget = new Square(0);
        if (parts[3] != "-")
        {
            try
            {
                enPassantTarget = Square.FromAlgebraic(parts[3]);
            }
            catch
            {
                throw new ArgumentException($"Invalid en passant target: '{parts[3]}'.");
            }
        }

        // Part 4: Halfmove clock
        if (!int.TryParse(parts[4], out int halfmoveClock))
            throw new ArgumentException($"Invalid halfmove clock: '{parts[4]}'.");

        // Part 5: Fullmove number
        if (!int.TryParse(parts[5], out int fullmoveNumber))
            throw new ArgumentException($"Invalid fullmove number: '{parts[5]}'.");

        // Update game state
        _gameState = new GameState(activeColor, castlingRights, enPassantTarget, halfmoveClock, fullmoveNumber);

        // Finalize hash: pieces are already XOR'd in by SetPiece; add state components.
        if (_gameState.ActiveColor == Color.Black)
            _hash ^= _hasher.GetColorKey(Color.Black);
        _hash ^= _hasher.GetCastlingKey(_gameState.CastlingRights.Mask);
        if (_gameState.EnPassantTarget.Index != 0)
            _hash ^= _hasher.GetEnPassantKey(_gameState.EnPassantTarget.File);
    }

    /// <summary>
    /// Exports the current board position to FEN notation.
    /// </summary>
    public string ExportToFen()
    {
        var fenBuilder = new System.Text.StringBuilder();

        // Part 0: Piece placement (from rank 8 to rank 1)
        for (int rank = 7; rank >= 0; rank--)
        {
            int emptyCount = 0;

            for (int file = 0; file < 8; file++)
            {
                Piece piece = GetPiece(new Square(file, rank));

                if (piece.IsEmpty)
                {
                    emptyCount++;
                }
                else
                {
                    if (emptyCount > 0)
                    {
                        fenBuilder.Append(emptyCount);
                        emptyCount = 0;
                    }
                    fenBuilder.Append(piece.ToFenChar());
                }
            }

            if (emptyCount > 0)
                fenBuilder.Append(emptyCount);

            if (rank > 0)
                fenBuilder.Append('/');
        }

        fenBuilder.Append(' ');

        // Part 1: Active color
        fenBuilder.Append(_gameState.ActiveColor == Color.White ? 'w' : 'b');
        fenBuilder.Append(' ');

        // Part 2: Castling rights
        fenBuilder.Append(_gameState.CastlingRights.ToFenString());
        fenBuilder.Append(' ');

        // Part 3: En passant target
        fenBuilder.Append(_gameState.HasEnPassant ? _gameState.EnPassantTarget.ToString() : "-");
        fenBuilder.Append(' ');

        // Part 4: Halfmove clock
        fenBuilder.Append(_gameState.HalfmoveClock);
        fenBuilder.Append(' ');

        // Part 5: Fullmove number
        fenBuilder.Append(_gameState.FullmoveNumber);

        return fenBuilder.ToString();
    }
}

/// <summary>
/// Extension method to create a Square from string notation.
/// </summary>
internal static class SquareCreationExtensions
{
    public static Square FromNotation(this string notation) => Square.FromAlgebraic(notation);
}
