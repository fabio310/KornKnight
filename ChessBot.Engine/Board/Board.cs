namespace ChessBot.Engine.Board;

using ChessBot.Engine.Types;
using ChessBot.Engine.Hashing;
using ChessBot.Engine.Evaluation;

/// <summary>
/// A single undo-history entry capturing everything needed to reverse one make/unmake in O(1)
/// without recomputation. Stores the move made, the piece that moved (its pre-promotion identity),
/// the captured piece and the exact square it stood on (which differs from the move's target for en
/// passant), the rook's from/to squares for castling, the game state and Zobrist hash before the
/// move, and a snapshot of the incremental evaluation accumulators (material, PST, total material)
/// so those are restored exactly on undo. Backing store for Board's preallocated undo stack.
/// </summary>
internal readonly struct UndoState
{
    public readonly Move Move;
    public readonly Piece MovingPiece;
    public readonly Piece CapturedPiece;
    public readonly Square CapturedSquare;
    public readonly Square RookFrom;
    public readonly Square RookTo;
    public readonly GameState GameState;
    public readonly ulong Hash;
    public readonly int EvalMaterial;
    public readonly int EvalPst;
    public readonly int EvalTotalMaterial;

    public UndoState(
        Move move,
        Piece movingPiece,
        Piece capturedPiece,
        Square capturedSquare,
        Square rookFrom,
        Square rookTo,
        GameState gameState,
        ulong hash,
        int evalMaterial,
        int evalPst,
        int evalTotalMaterial)
    {
        Move = move;
        MovingPiece = movingPiece;
        CapturedPiece = capturedPiece;
        CapturedSquare = capturedSquare;
        RookFrom = rookFrom;
        RookTo = rookTo;
        GameState = gameState;
        Hash = hash;
        EvalMaterial = evalMaterial;
        EvalPst = evalPst;
        EvalTotalMaterial = evalTotalMaterial;
    }
}

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

    // ── Compact per-color piece lists (hot-path optimization) ──────────────────────────────
    // GetPiecesOf/GetAllPiecesInto used to scan all 64 squares on every call; both are invoked
    // once per node by MoveGenerator and Evaluator respectively. These lists are maintained
    // incrementally by SetPiece (never by SetPieceRaw, which is a throwaway probe — see its
    // doc comment) so piece iteration becomes O(pieceCount) instead of O(64). A side can have
    // at most 16 pieces on a legal board. Index 0 = White, 1 = Black.
    private const int MaxPiecesPerColor = 16;
    private readonly Square[][] _pieceListSquares;
    private readonly Piece[][] _pieceListPieces;
    private readonly int[] _pieceListCount;

    /// <summary>
    /// Maps a square index (0-63) to its slot within the occupying piece's color list
    /// (_pieceListSquares[color]/_pieceListPieces[color]), or -1 if the square is empty.
    /// Enables O(1) removal (via swap-remove) when a piece leaves a square.
    /// </summary>
    private readonly int[] _squareToListSlot;

    /// <summary>
    /// A history of previous board states (for move undo operations), backed by a preallocated
    /// array + stack pointer instead of a List{T} to avoid List mutation overhead on every
    /// make/undo call. Stores: the move made, the captured piece, the game state, and the
    /// Zobrist hash before the move. Grows (doubles) on overflow; indexed access for the
    /// Searcher's repetition detection is exposed via HistoryCount/GetHistoryEntry.
    /// </summary>
    private UndoState[] _history;
    private int _historyCount;

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

    // ── Incremental evaluation state (hot-path optimization) ───────────────────────────────
    // White-positive accumulators mirrored on every AddPiece/RemovePiece so Evaluator.EvaluateFast
    // can skip the per-node full-board material/PST/pawn scan. Kings contribute nothing to material
    // or PST (the full Evaluate scan skips them), so they are excluded here too. Initialized from a
    // full scan by RecomputeIncrementalEval after ResetToStartingPosition/LoadFromFen and then kept
    // exactly consistent through the direct-mutation MakeMove/UndoMove path.
    private int _evalMaterial;        // Σ sign * MaterialValue over non-king pieces (White +, Black -)
    private int _evalPst;             // Σ sign * PST[rank][file] over non-king pieces
    private int _evalTotalMaterial;   // Σ MaterialValue over non-king pieces (unsigned)
    private readonly int[] _whitePawnFiles = new int[8];
    private readonly int[] _blackPawnFiles = new int[8];

    /// <summary>White-positive incremental material score (non-king). Consumed by Evaluator.EvaluateFast.</summary>
    internal int IncrementalMaterialScore => _evalMaterial;

    /// <summary>White-positive incremental piece-square-table score (non-king). Consumed by Evaluator.EvaluateFast.</summary>
    internal int IncrementalPstScore => _evalPst;

    /// <summary>Total (unsigned) non-king material on the board, used for the endgame phase test.</summary>
    internal int IncrementalTotalMaterial => _evalTotalMaterial;

    /// <summary>Per-file White pawn counts (index 0 = a-file), maintained incrementally.</summary>
    internal ReadOnlySpan<int> WhitePawnFileCounts => _whitePawnFiles;

    /// <summary>Per-file Black pawn counts (index 0 = a-file), maintained incrementally.</summary>
    internal ReadOnlySpan<int> BlackPawnFileCounts => _blackPawnFiles;

    public Board()
    {
        _pieces = new Piece[64];
        _kingPositions = new Square[2];

        _pieceListSquares = new Square[2][] { new Square[MaxPiecesPerColor], new Square[MaxPiecesPerColor] };
        _pieceListPieces  = new Piece[2][]  { new Piece[MaxPiecesPerColor],  new Piece[MaxPiecesPerColor] };
        _pieceListCount   = new int[2];
        _squareToListSlot = new int[64];
        Array.Fill(_squareToListSlot, -1);

        _history = new UndoState[128];
        _historyCount = 0;

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
        {
            _hash ^= _hasher.GetPieceKey(old.Color, old.Type, square);
            RemoveFromPieceList(old.Color, square);
        }

        _pieces[square.Index] = piece;

        // XOR in the new piece key after placement
        if (piece.Type != PieceType.None)
        {
            _hash ^= _hasher.GetPieceKey(piece.Color, piece.Type, square);
            AddToPieceList(piece.Color, square, piece);
        }

        // Track king positions for efficient check detection
        if (piece.Type == PieceType.King)
            _kingPositions[(int)piece.Color] = square;
    }

    /// <summary>
    /// Adds a piece to its color's compact piece list and records the square's slot,
    /// enabling O(1) later removal. Called only from SetPiece.
    /// </summary>
    private void AddToPieceList(Color color, Square square, Piece piece)
    {
        int colorIdx = (int)color;
        int slot = _pieceListCount[colorIdx]++;
        _pieceListSquares[colorIdx][slot] = square;
        _pieceListPieces[colorIdx][slot] = piece;
        _squareToListSlot[square.Index] = slot;
    }

    /// <summary>
    /// Removes the piece occupying <paramref name="square"/> from <paramref name="color"/>'s
    /// compact piece list via swap-remove (move the last entry into the freed slot), keeping
    /// the list dense with O(1) cost. Called only from SetPiece.
    /// </summary>
    private void RemoveFromPieceList(Color color, Square square)
    {
        int colorIdx = (int)color;
        int slot = _squareToListSlot[square.Index];
        int lastSlot = --_pieceListCount[colorIdx];

        if (slot != lastSlot)
        {
            Square movedSquare = _pieceListSquares[colorIdx][lastSlot];
            Piece movedPiece = _pieceListPieces[colorIdx][lastSlot];
            _pieceListSquares[colorIdx][slot] = movedSquare;
            _pieceListPieces[colorIdx][slot] = movedPiece;
            _squareToListSlot[movedSquare.Index] = slot;
        }

        _squareToListSlot[square.Index] = -1;
    }

    /// <summary>
    /// Resets both colors' piece lists to empty and marks every square as unoccupied in the
    /// slot map. Must be called before bulk-clearing _pieces directly (i.e. not through
    /// SetPiece) in ResetToStartingPosition/LoadFromFen, since those bypass RemoveFromPieceList.
    /// </summary>
    private void ClearPieceLists()
    {
        _pieceListCount[0] = 0;
        _pieceListCount[1] = 0;
        Array.Fill(_squareToListSlot, -1);
    }

    // ── Direct-mutation primitives (make/unmake hot path) ──────────────────────────────────
    // AddPiece/RemovePiece/MovePiece are the single mutation path used by MakeMove/UndoMove. Each
    // keeps _pieces, the compact piece list, king positions, the Zobrist hash, and the incremental
    // evaluation accumulators consistent in one pass, replacing the previous chain of SetPiece
    // calls (which re-derived nothing incrementally). Preconditions are asserted by contract:
    // AddPiece requires an empty target; RemovePiece requires an occupied source.

    /// <summary>
    /// Places <paramref name="piece"/> on an empty square, updating the piece array, Zobrist hash,
    /// compact piece list, king position, and incremental eval accumulators. The target must be empty.
    /// </summary>
    private void AddPiece(Square square, Piece piece)
    {
        _pieces[square.Index] = piece;
        _hash ^= _hasher.GetPieceKey(piece.Color, piece.Type, square);
        AddToPieceList(piece.Color, square, piece);

        if (piece.Type == PieceType.King)
            _kingPositions[(int)piece.Color] = square;

        AddPieceEval(piece.Color, piece.Type, square);
    }

    /// <summary>
    /// Removes and returns the piece occupying <paramref name="square"/>, updating the piece array,
    /// Zobrist hash, compact piece list, and incremental eval accumulators. The square must be occupied.
    /// </summary>
    private Piece RemovePiece(Square square)
    {
        Piece piece = _pieces[square.Index];
        _hash ^= _hasher.GetPieceKey(piece.Color, piece.Type, square);
        RemoveFromPieceList(piece.Color, square);
        _pieces[square.Index] = Piece.Empty;

        RemovePieceEval(piece.Color, piece.Type, square);
        return piece;
    }

    /// <summary>
    /// Moves the piece on <paramref name="from"/> to the empty square <paramref name="to"/> and
    /// returns it. Composed from RemovePiece + AddPiece so every derived structure stays consistent.
    /// </summary>
    private Piece MovePiece(Square from, Square to)
    {
        Piece piece = RemovePiece(from);
        AddPiece(to, piece);
        return piece;
    }

    /// <summary>
    /// Applies the White-positive incremental-eval contribution of a piece entering the board.
    /// Kings are excluded (they contribute no material and are skipped by the PST scan), matching
    /// Evaluator.Evaluate exactly so EvaluateFast returns identical scores.
    /// </summary>
    private void AddPieceEval(Color color, PieceType type, Square square)
    {
        if (type == PieceType.King) return;

        int sign = color == Color.White ? 1 : -1;
        int matVal = type.MaterialValue();
        _evalMaterial += sign * matVal;
        _evalTotalMaterial += matVal;
        _evalPst += sign * PieceSquareTables.Value(color, type, square);

        if (type == PieceType.Pawn)
        {
            if (color == Color.White) _whitePawnFiles[square.File]++;
            else                      _blackPawnFiles[square.File]++;
        }
    }

    /// <summary>
    /// Reverses <see cref="AddPieceEval"/> for a piece leaving the board.
    /// </summary>
    private void RemovePieceEval(Color color, PieceType type, Square square)
    {
        if (type == PieceType.King) return;

        int sign = color == Color.White ? 1 : -1;
        int matVal = type.MaterialValue();
        _evalMaterial -= sign * matVal;
        _evalTotalMaterial -= matVal;
        _evalPst -= sign * PieceSquareTables.Value(color, type, square);

        if (type == PieceType.Pawn)
        {
            if (color == Color.White) _whitePawnFiles[square.File]--;
            else                      _blackPawnFiles[square.File]--;
        }
    }

    /// <summary>
    /// Rebuilds the incremental evaluation accumulators (material, PST, total material, per-file
    /// pawn counts) from a full scan of the compact piece lists. Called after
    /// ResetToStartingPosition/LoadFromFen populate the board, so the incremental state is exactly
    /// consistent with the position before any make/unmake occurs.
    /// </summary>
    private void RecomputeIncrementalEval()
    {
        _evalMaterial = 0;
        _evalPst = 0;
        _evalTotalMaterial = 0;
        Array.Clear(_whitePawnFiles, 0, 8);
        Array.Clear(_blackPawnFiles, 0, 8);

        for (int color = 0; color < 2; color++)
        {
            int count = _pieceListCount[color];
            Square[] squares = _pieceListSquares[color];
            Piece[] pieces = _pieceListPieces[color];
            for (int i = 0; i < count; i++)
                AddPieceEval(pieces[i].Color, pieces[i].Type, squares[i]);
        }
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
    /// Gets the number of entries in the undo history (for repetition detection).
    /// </summary>
    internal int HistoryCount => _historyCount;

    /// <summary>
    /// Gets the undo-history entry at <paramref name="index"/> (0 = oldest, HistoryCount-1 = newest).
    /// Provides indexed access equivalent to a List{T} without exposing a mutable List{T}.
    /// </summary>
    internal UndoState GetHistoryEntry(int index) => _history[index];

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
    /// Makes a move on the board, updating all state (piece positions, castling rights, en passant,
    /// halfmove clock, fullmove number, Zobrist hash, and incremental eval) through a single direct
    /// mutation path (AddPiece/RemovePiece/MovePiece) instead of repeated SetPiece calls. Pushes a
    /// full undo snapshot so UndoMove can restore everything in O(1).
    /// </summary>
    public void MakeMove(Move move)
    {
        Piece movingPiece = _pieces[move.From.Index];
        if (movingPiece.IsEmpty)
            throw new InvalidOperationException($"Cannot make move: no piece on {move.From}.");

        GameState preMoveState = _gameState;
        ulong preHash = _hash;
        Color us = preMoveState.ActiveColor;

        bool isCastling   = (move.MoveType & MoveType.Castling) != 0;
        bool isEnPassant  = (move.MoveType & MoveType.EnPassant) != 0;
        bool isPromotion  = (move.MoveType & MoveType.Promotion) != 0;
        bool isDoublePush = (move.MoveType & MoveType.DoublePawnPush) != 0;

        // Resolve the captured piece and the exact square it occupies. For en passant the victim
        // sits on the moving pawn's rank (not on move.To); otherwise it is whatever stands on move.To.
        Square capturedSquare;
        Piece capturedPiece;
        if (isEnPassant)
        {
            int captureRank = us == Color.White ? move.To.Rank - 1 : move.To.Rank + 1;
            capturedSquare = new Square(move.To.File, captureRank);
            capturedPiece = _pieces[capturedSquare.Index];
        }
        else
        {
            capturedSquare = move.To;
            capturedPiece = _pieces[move.To.Index];
        }

        // Resolve the rook relocation for castling (from corner to the king's transit square).
        Square rookFrom = default;
        Square rookTo = default;
        if (isCastling)
        {
            int rank = move.To.Rank;
            if (move.To.File == 6) { rookFrom = new Square(7, rank); rookTo = new Square(5, rank); }
            else                   { rookFrom = new Square(0, rank); rookTo = new Square(3, rank); }
        }

        // Snapshot everything needed to reverse this move before mutating the board.
        PushHistory(new UndoState(
            move, movingPiece, capturedPiece, capturedSquare, rookFrom, rookTo,
            preMoveState, preHash, _evalMaterial, _evalPst, _evalTotalMaterial));

        // ── Apply piece movement through the single mutation path ──
        RemovePiece(move.From);
        if (!capturedPiece.IsEmpty)
            RemovePiece(capturedSquare);

        Piece placedPiece = isPromotion ? new Piece(movingPiece.Color, move.PromotionType) : movingPiece;
        AddPiece(move.To, placedPiece);

        if (isCastling)
            MovePiece(rookFrom, rookTo);

        // ── Compute new game state ──
        CastlingRights newRights =
            ComputeCastlingRights(preMoveState.CastlingRights, movingPiece, move.From, move.To, capturedPiece);

        Square newEnPassantTarget = default;  // no en passant unless a double pawn push occurs
        if (isDoublePush)
        {
            int epRank = us == Color.White ? move.To.Rank - 1 : move.To.Rank + 1;
            newEnPassantTarget = new Square(move.To.File, epRank);
        }

        // Halfmove clock resets on a pawn move or any capture (en passant included, since its victim
        // is non-empty above); otherwise it increments.
        int newHalfmoveClock = preMoveState.HalfmoveClock + 1;
        if (movingPiece.Type == PieceType.Pawn || !capturedPiece.IsEmpty)
            newHalfmoveClock = 0;

        int newFullmoveNumber = preMoveState.FullmoveNumber;
        if (us == Color.Black)
            newFullmoveNumber++;

        _gameState = new GameState(
            us.Opposite(),
            newRights,
            newEnPassantTarget,
            newHalfmoveClock,
            newFullmoveNumber
        );

        // ── Update Zobrist hash for non-piece state changes (piece keys handled by the primitives) ──
        _hash ^= _hasher.GetCastlingKey(preMoveState.CastlingRights.Mask);
        _hash ^= _hasher.GetCastlingKey(newRights.Mask);
        if (preMoveState.EnPassantTarget.Index != 0)
            _hash ^= _hasher.GetEnPassantKey(preMoveState.EnPassantTarget.File);
        if (newEnPassantTarget.Index != 0)
            _hash ^= _hasher.GetEnPassantKey(newEnPassantTarget.File);
        _hash ^= _hasher.GetColorKey(Color.Black); // Always toggle; Black key encodes whose turn it is
    }

    /// <summary>
    /// Computes the castling rights that result from a move, given the rights before it. Pure
    /// function (does not mutate board state): a king move revokes both of that color's rights, a
    /// rook move revokes that side's right, and capturing a corner rook revokes the captured side's
    /// right. capturedPiece is the pre-move occupant of the captured square.
    /// </summary>
    private static CastlingRights ComputeCastlingRights(
        CastlingRights rights, Piece movingPiece, Square from, Square to, Piece capturedPiece)
    {
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

        return rights;
    }

    /// <summary>
    /// Undoes the last move made. Throws if no moves have been made.
    /// Restores all piece positions, game state, Zobrist hash, and incremental eval in O(1)
    /// using the snapshot pushed by MakeMove, through the same direct-mutation primitives.
    /// </summary>
    public void UndoMove()
    {
        if (_historyCount == 0)
            throw new InvalidOperationException("Cannot undo: no moves have been made.");

        UndoState undo = PopHistory();
        Move move = undo.Move;

        bool isCastling = (move.MoveType & MoveType.Castling) != 0;

        // Reverse castling rook relocation first (mirror of MakeMove's order).
        if (isCastling)
            MovePiece(undo.RookTo, undo.RookFrom);

        // Lift the (possibly promoted) piece off the destination square.
        RemovePiece(move.To);

        // Restore the mover on its origin square as its pre-promotion identity (MovingPiece is the
        // pawn for a promotion, so no special-casing is needed here).
        AddPiece(move.From, undo.MovingPiece);

        // Restore any captured piece on its exact original square (differs from move.To for en passant).
        if (!undo.CapturedPiece.IsEmpty)
            AddPiece(undo.CapturedSquare, undo.CapturedPiece);

        // Restore snapshotted state in O(1) (cheaper and exact vs. reversing every XOR/accumulator).
        _gameState = undo.GameState;
        _hash = undo.Hash;
        _evalMaterial = undo.EvalMaterial;
        _evalPst = undo.EvalPst;
        _evalTotalMaterial = undo.EvalTotalMaterial;
    }

    /// <summary>
    /// Makes a null move: passes the turn to the opponent without moving a piece.
    /// Used by null-move pruning in the search. Call UndoNullMove() to revert.
    /// </summary>
    public void MakeNullMove()
    {
        // Save current state so we can restore it (including hash and incremental eval accumulators).
        var savedState = _gameState;
        PushHistory(new UndoState(
            default, Piece.Empty, Piece.Empty, default, default, default,
            savedState, _hash, _evalMaterial, _evalPst, _evalTotalMaterial));

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
        if (_historyCount == 0)
            throw new InvalidOperationException("Cannot undo null move: history is empty.");

        UndoState undo = PopHistory();
        _gameState = undo.GameState;
        _hash = undo.Hash;
        // A null move makes no piece changes, so the incremental eval accumulators are unchanged;
        // restoring them from the snapshot is a harmless no-op that keeps the code uniform.
        _evalMaterial = undo.EvalMaterial;
        _evalPst = undo.EvalPst;
        _evalTotalMaterial = undo.EvalTotalMaterial;
    }

    /// <summary>
    /// Pushes a new undo-history entry, growing the backing array (doubling) if full.
    /// Replaces List{T}.Add to avoid List mutation overhead on every move made.
    /// </summary>
    private void PushHistory(in UndoState state)
    {
        if (_historyCount == _history.Length)
            Array.Resize(ref _history, _history.Length * 2);

        _history[_historyCount++] = state;
    }

    /// <summary>
    /// Pops and returns the most recent undo-history entry. Caller must check HistoryCount > 0.
    /// </summary>
    private UndoState PopHistory()
    {
        return _history[--_historyCount];
    }

    /// <summary>
    /// Resets the board to the starting position.
    /// </summary>
    public void ResetToStartingPosition()
    {
        // Clear the board
        for (int i = 0; i < 64; i++)
            _pieces[i] = Piece.Empty;
        ClearPieceLists();
        _historyCount = 0;
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

        // Initialize incremental eval accumulators from the freshly placed pieces.
        RecomputeIncrementalEval();
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

        // Copy the compact piece lists and square->slot map so GetPiecesOf/GetAllPiecesInto
        // remain correct on the copy (cheap: at most 16 entries per color, not a 64-square scan).
        Array.Copy(_pieceListSquares[0], copy._pieceListSquares[0], MaxPiecesPerColor);
        Array.Copy(_pieceListSquares[1], copy._pieceListSquares[1], MaxPiecesPerColor);
        Array.Copy(_pieceListPieces[0], copy._pieceListPieces[0], MaxPiecesPerColor);
        Array.Copy(_pieceListPieces[1], copy._pieceListPieces[1], MaxPiecesPerColor);
        copy._pieceListCount[0] = _pieceListCount[0];
        copy._pieceListCount[1] = _pieceListCount[1];
        Array.Copy(_squareToListSlot, copy._squareToListSlot, 64);

        // Copy the incremental evaluation accumulators so EvaluateFast is correct on the copy
        // without a rebuild (the copy also starts with a fresh, empty history).
        copy._evalMaterial = _evalMaterial;
        copy._evalPst = _evalPst;
        copy._evalTotalMaterial = _evalTotalMaterial;
        Array.Copy(_whitePawnFiles, copy._whitePawnFiles, 8);
        Array.Copy(_blackPawnFiles, copy._blackPawnFiles, 8);

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
    /// possible pieces on a legal board). Copies directly from the compact per-color piece
    /// lists instead of scanning all 64 squares, which matters because static evaluation
    /// runs this at every leaf and quiescence stand-pat node — the majority of nodes in the
    /// search tree.
    /// </summary>
    internal int GetAllPiecesInto((Square sq, Piece p)[] buffer)
    {
        int count = 0;
        for (int color = 0; color < 2; color++)
        {
            int colorCount = _pieceListCount[color];
            Square[] squares = _pieceListSquares[color];
            Piece[] pieces = _pieceListPieces[color];
            for (int i = 0; i < colorCount; i++)
                buffer[count++] = (squares[i], pieces[i]);
        }
        return count;
    }

    /// <summary>
    /// Fills <paramref name="buffer"/> with all pieces belonging to <paramref name="color"/>
    /// and returns how many were written.  The buffer must have at least 16 elements.
    /// Copies directly from the compact per-color piece list instead of scanning all 64
    /// squares, avoiding both the heap allocation and the full-board scan in hot search paths.
    /// </summary>
    internal int GetPiecesOf(Color color, (Square sq, Piece p)[] buffer)
    {
        int colorIdx = (int)color;
        int count = _pieceListCount[colorIdx];
        Square[] squares = _pieceListSquares[colorIdx];
        Piece[] pieces = _pieceListPieces[colorIdx];
        for (int i = 0; i < count; i++)
            buffer[i] = (squares[i], pieces[i]);
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
        ClearPieceLists();
        _historyCount = 0;
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

        // Initialize incremental eval accumulators from the freshly placed pieces.
        RecomputeIncrementalEval();
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
