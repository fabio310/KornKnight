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
            return PerftImpl(depth);
        }
    }

    /// <summary>
    /// Internal Perft implementation (recursive).
    /// </summary>
    private long PerftImpl(int depth)
    {
        if (depth == 0)
            return 1;

        var moves = GetLegalMoves();
        long count = 0;

        if (depth == 1)
            return moves.Count;

        foreach (var move in moves)
        {
            MakeMove(move);
            count += PerftImpl(depth - 1);
            UndoMove();
        }

        return count;
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
