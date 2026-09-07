namespace ChessBot.Engine;

using ChessBot.Engine.Types;
using ChessBot.Engine.Board;
using ChessBot.Engine.Search;
using ChessBot.Engine.Evaluation;
using ChessBot.Engine.Hashing;

/// <summary>
/// The primary public API for the ChessBot engine.
/// Exposes all core functionality: board management, move generation, search, and evaluation.
/// Fully thread-safe for concurrent analysis scenarios.
/// </summary>
public class ChessEngine
{
    private readonly Board.Board _board;
    private readonly object _boardLock = new object();
    private readonly Evaluator _evaluator;
    private readonly ZobristHasher _zobristHasher;
    private Searcher? _searcher;

    /// <summary>
    /// Creates a new ChessEngine initialized to the standard starting position.
    /// </summary>
    public ChessEngine()
    {
        _board = new Board.Board();
        _board.ResetToStartingPosition();
        _evaluator = new Evaluator();
        _zobristHasher = new ZobristHasher();
        _searcher = new Searcher(_board, _evaluator, _zobristHasher);
    }

    /// <summary>
    /// Loads a position from FEN notation.
    /// </summary>
    public void LoadFen(string fen)
    {
        lock (_boardLock)
        {
            _board.LoadFromFen(fen);
        }
    }

    /// <summary>
    /// Exports the current position to FEN notation.
    /// </summary>
    public string ExportFen()
    {
        lock (_boardLock)
        {
            return _board.ExportToFen();
        }
    }

    /// <summary>
    /// The side to move in the current position. Exposed separately from
    /// <see cref="GetBoardSnapshot"/> because a caller that only needs the side to move
    /// (a clock allocator deciding which side's remaining time applies, for instance)
    /// should not have to copy the whole board to get it.
    /// </summary>
    public Color SideToMove
    {
        get
        {
            lock (_boardLock)
            {
                return _board.State.ActiveColor;
            }
        }
    }

    /// <summary>
    /// Resets the engine for a new, unrelated game: starting position, empty move history,
    /// and cleared search tables. Without the table reset, a match harness that reuses one
    /// engine instance across games carries the previous game's transposition entries and
    /// move-ordering history into the next one, which makes games in a series
    /// non-independent and results non-reproducible.
    /// </summary>
    public void NewGame()
    {
        lock (_boardLock)
        {
            _board.ResetToStartingPosition();
            _searcher ??= new Searcher(_board, _evaluator, _zobristHasher);
            _searcher.ClearTables();
        }
    }

    /// <summary>
    /// Gets all legal moves in the current position.
    /// </summary>
    public IReadOnlyList<Move> GetLegalMoves()
    {
        lock (_boardLock)
        {
            var generator = new MoveGenerator(_board);
            return generator.GenerateLegalMoves().AsReadOnly();
        }
    }

    /// <summary>
    /// Makes a move on the board, validating that it is legal.
    /// </summary>
    public void MakeMove(Move move)
    {
        lock (_boardLock)
        {
            _board.MakeMove(move);
        }
    }

    /// <summary>
    /// Undoes the last move made (if any).
    /// </summary>
    public void UndoMove()
    {
        lock (_boardLock)
        {
            _board.UndoMove();
        }
    }

    /// <summary>
    /// Finds the best move in the current position given the specified search settings.
    /// </summary>
    public SearchResult FindBestMove(SearchSettings settings, CancellationToken ct = default)
    {
        lock (_boardLock)
        {
            if (_searcher == null)
                _searcher = new Searcher(_board, _evaluator, _zobristHasher);

            return _searcher.Search(settings, ct);
        }
    }

    /// <summary>
    /// Evaluates the current position without search.
    /// </summary>
    public EvaluationResult Evaluate()
    {
        lock (_boardLock)
        {
            // _evaluator.Evaluate() returns score relative to side-to-move (negamax convention).
            // Public API converts back to White-positive for display.
            int stmScore = _evaluator.Evaluate(_board);
            int sign     = _board.State.ActiveColor == Color.White ? 1 : -1;
            int score    = stmScore * sign;

            var result = new EvaluationResult
            {
                Score = score
            };

            // Calculate material balance
            int whiteMaterial = 0;
            int blackMaterial = 0;

            foreach (var (_, piece) in _board.GetAllPieces())
            {
                if (!piece.IsEmpty && piece.Type != PieceType.King)
                {
                    int materialValue = piece.Type.MaterialValue();
                    if (piece.Color == Color.White)
                        whiteMaterial += materialValue;
                    else
                        blackMaterial += materialValue;
                }
            }

            result.MaterialBalance.WhiteMaterial = whiteMaterial;
            result.MaterialBalance.BlackMaterial = blackMaterial;

            return result;
        }
    }

    /// <summary>
    /// Runs the Perft (performance test) algorithm to count leaf nodes at a given depth.
    /// Useful for validating move generation correctness.
    /// </summary>
    public long RunPerft(int depth)
    {
        lock (_boardLock)
        {
            if (depth <= 0) return 1;

            return PerftImpl(new MoveGenerator(_board), AllocatePerftBuffers(depth), depth);
        }
    }

    /// <summary>
    /// Perft split by root move: the node count each legal move contributes to the total.
    ///
    /// This is the tool for localising a move-generation bug. A wrong total says only that
    /// something is wrong somewhere in the tree; comparing per-root-move counts against a
    /// reference engine identifies which move's subtree diverges, and recursing into that move
    /// narrows it to a single position — which a bare total can never do.
    ///
    /// Entries are in move-generation order. At depth 1 every entry counts 1, which makes the
    /// listing a plain enumeration of the legal moves.
    /// </summary>
    public IReadOnlyList<PerftDivideEntry> RunPerftDivide(int depth)
    {
        lock (_boardLock)
        {
            var entries = new List<PerftDivideEntry>();
            if (depth <= 0) return entries;

            var generator = new MoveGenerator(_board);
            var buffers   = AllocatePerftBuffers(depth);

            var rootMoves = buffers[depth - 1];
            generator.GenerateLegalMovesInto(rootMoves, out int rootMoveCount);

            for (int i = 0; i < rootMoveCount; i++)
            {
                Move move = rootMoves[i];

                _board.MakeMove(move);
                long nodes = PerftImpl(generator, buffers, depth - 1);
                _board.UndoMove();

                entries.Add(new PerftDivideEntry(move, nodes));
            }

            return entries;
        }
    }

    /// <summary>
    /// One move buffer per ply of the search, allocated once per perft run. Perft at the depths
    /// that actually prove move generation correct visits billions of nodes, so anything
    /// allocated per node — a generator, a List of moves — dominates the run time.
    /// </summary>
    private static Move[][] AllocatePerftBuffers(int depth)
    {
        var buffers = new Move[depth][];
        for (int i = 0; i < depth; i++)
            buffers[i] = new Move[MoveGenerator.MaxMoves];

        return buffers;
    }

    /// <summary>
    /// Recursive perft over the shared generator and per-ply buffers. Counts the moves at the
    /// last ply in bulk instead of making and unmaking each one, which is the standard
    /// definition and leaves the totals identical.
    /// </summary>
    private long PerftImpl(MoveGenerator generator, Move[][] buffers, int depth)
    {
        if (depth == 0) return 1;

        var moves = buffers[depth - 1];
        generator.GenerateLegalMovesInto(moves, out int moveCount);

        if (depth == 1) return moveCount;

        long nodes = 0;
        for (int i = 0; i < moveCount; i++)
        {
            _board.MakeMove(moves[i]);
            nodes += PerftImpl(generator, buffers, depth - 1);
            _board.UndoMove();
        }

        return nodes;
    }

    /// <summary>
    /// Gets the current board state for read-only inspection.
    /// </summary>
    public Board.Board GetBoardSnapshot()
    {
        lock (_boardLock)
        {
            return _board.Copy();
        }
    }
}

/// <summary>
/// One root move of a perft divide and the number of leaf nodes its subtree contains.
/// </summary>
/// <param name="Move">The root move.</param>
/// <param name="Nodes">Leaf nodes below it at the requested depth.</param>
public readonly record struct PerftDivideEntry(Move Move, long Nodes)
{
    /// <summary>
    /// Formats the entry the way every perft tool prints it ("e2e4: 8902"), so output can be
    /// diffed directly against a reference engine's divide.
    /// </summary>
    public override string ToString() => $"{Move}: {Nodes}";
}
