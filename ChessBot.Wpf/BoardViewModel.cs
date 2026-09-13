namespace ChessBot.Wpf;

using ChessBot.Engine;
using ChessBot.Engine.Types;
using ChessBot.Engine.Search;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;

/// <summary>
/// Central state manager for the chess board UI.
/// Owns the engine instance, selection logic, highlight state, and async engine search.
/// Fires PropertyChanged for info-panel bindings; exposes an Action for board repaint.
/// </summary>
public sealed class BoardViewModel : INotifyPropertyChanged, IDisposable
{
    // ── Engine ─────────────────────────────────────────────────────────────
    private readonly ChessEngine _engine = new ChessEngine();
    private CancellationTokenSource? _engineCts;

    // ── Board visual state ──────────────────────────────────────────────────────────────────────────
    private bool _isFlipped;
    private bool _autoPlay = true;  // auto-play engine move after player move
    private Square? _selectedSquare;
    private IReadOnlyList<Move> _legalMovesFromSelected = Array.Empty<Move>();
    private Move? _lastMove;
    private bool _isEngineThinking;
    private Square? _hoverSquare;   // square under cursor during drag

    // ── Callbacks (MainWindow wires these) ──────────────────────────────────
    /// <summary>Called whenever the board squares need a full visual refresh.</summary>
    public Action? BoardRepaintRequested;
    /// <summary>Called when engine thinking starts or stops (to update button state).</summary>
    public Action<bool>? EngineThinkingChanged;

    // ── Info panel bound properties ─────────────────────────────────────────
    private string _statusText = "White to move";
    private string _evalText   = "Eval: 0.00";
    private string _bestMove   = "Best move: —";
    private string _pvText     = "PV: —";
    private string _depthText  = "Depth: —";
    private string _nodesText  = "Nodes: —";
    private string _npsText    = "NPS: —";

    public string StatusText { get => _statusText; private set => Set(ref _statusText, value); }
    public string EvalText   { get => _evalText;   private set => Set(ref _evalText,   value); }
    public string BestMove   { get => _bestMove;   private set => Set(ref _bestMove,   value); }
    public string PvText     { get => _pvText;     private set => Set(ref _pvText,     value); }
    public string DepthText  { get => _depthText;  private set => Set(ref _depthText,  value); }
    public string NodesText  { get => _nodesText;  private set => Set(ref _nodesText,  value); }
    public string NpsText    { get => _npsText;    private set => Set(ref _npsText,    value); }

    public bool IsFlipped       => _isFlipped;
    public bool IsEngineThinking => _isEngineThinking;
    public bool AutoPlay         => _autoPlay;

    // ── Public board queries ────────────────────────────────────────────────

    /// <summary>Gets the piece on an internal-coordinate square (file 0-7, rank 0-7).</summary>
    public Piece GetPiece(Square sq) => _engine.GetBoardSnapshot().GetPiece(sq);

    /// <summary>Is this square the currently selected piece origin?</summary>
    public bool IsSelected(Square sq) => _selectedSquare.HasValue && _selectedSquare.Value == sq;

    /// <summary>Is this square a legal destination for the selected piece?</summary>
    public bool IsLegalTarget(Square sq) =>
        _legalMovesFromSelected.Any(m => m.To == sq);

    /// <summary>Is this square part of the last move (from or to)?</summary>
    public bool IsLastMoveSquare(Square sq) =>
        _lastMove.HasValue && (_lastMove.Value.From == sq || _lastMove.Value.To == sq);

    /// <summary>Is this square the king in check?</summary>
    public bool IsKingInCheck(Square sq)
    {
        var snap = _engine.GetBoardSnapshot();
        var piece = snap.GetPiece(sq);
        if (piece.Type != PieceType.King) return false;
        if (piece.Color != snap.ActiveColor) return false;
        // Check is only meaningful for the side to move
        return snap.IsKingInCheck(piece.Color);
    }

    /// <summary>Returns the active color in the current position.</summary>
    public Color ActiveColor => _engine.GetBoardSnapshot().ActiveColor;

    /// <summary>Sets the square currently hovered during a drag (null = no hover).</summary>
    public void SetHoverSquare(Square? sq) => _hoverSquare = sq;

    /// <summary>Is this square being hovered during a drag AND is a legal target?</summary>
    public bool IsDragHover(Square sq) =>
        _hoverSquare.HasValue && _hoverSquare.Value == sq && IsLegalTarget(sq);

    // ── Square interaction ──────────────────────────────────────────────────

    /// <summary>
    /// Handle a click on the square with the given internal index (0 = a1, 63 = h8).
    /// Manages selection, deselection, and move execution.
    /// </summary>
    /// <summary>
    /// Handles a click on a square: selects, re-selects, deselects, or plays a move.
    /// Returns true only when a move was actually executed, so the caller can trigger
    /// the engine reply for auto-play without firing on a mere selection click.
    /// </summary>
    public bool OnSquareClicked(int squareIndex)
    {
        if (_isEngineThinking) return false;

        var clickedSq = new Square(squareIndex);
        var snap = _engine.GetBoardSnapshot();

        // If a piece is already selected, attempt to make a move
        if (_selectedSquare.HasValue)
        {
            var candidates = _legalMovesFromSelected
                .Where(m => m.To == clickedSq)
                .ToList();

            if (candidates.Count > 0)
            {
                // Handle promotion: prefer Queen by default
                Move move = candidates.FirstOrDefault(m => m.PromotionType == PieceType.Queen,
                                                       candidates[0]);
                ExecuteMove(move);
                return true;
            }

            // Clicked on own piece — switch selection
            var clickedPiece = snap.GetPiece(clickedSq);
            if (!clickedPiece.IsEmpty && clickedPiece.Color == snap.ActiveColor)
            {
                SelectSquare(clickedSq);
                return false;
            }

            // Clicked elsewhere — deselect
            ClearSelection();
            RequestRepaint();
            return false;
        }

        // Nothing selected yet — try to select a piece
        var piece = snap.GetPiece(clickedSq);
        if (!piece.IsEmpty && piece.Color == snap.ActiveColor)
        {
            SelectSquare(clickedSq);
        }

        return false;
    }

    /// <summary>
    /// Returns all promotion move candidates to the given target square from the currently selected piece.
    /// Used by UI to show promotion dialog.
    /// </summary>
    public Move[] GetPromotionMovesTo(Square targetSq)
    {
        return _legalMovesFromSelected
            .Where(m => m.To == targetSq && m.PromotionType != PieceType.None)
            .ToArray();
    }

    /// <summary>
    /// Directly applies a move (used by UI after user selects promotion piece).
    /// </summary>
    public void ApplyMove(Move move)
    {
        ExecuteMove(move);
    }

    /// <summary>
    /// Selects a square for drag regardless of which color's turn it is.
    /// Allows the user to drag pieces for either side.
    /// </summary>
    public void SelectSquareForDrag(int squareIndex)
    {
        var sq = new Square(squareIndex);
        var snap = _engine.GetBoardSnapshot();
        if (snap.GetPiece(sq).IsEmpty) return;
        SelectSquare(sq);
    }

    /// <summary>
    /// Applies the move from srcIdx to dstIdx if it exists in the currently
    /// selected piece's legal-move list. Used by the drag-drop handler.
    /// Returns true if a move was made.
    /// </summary>
    public bool ApplyMoveFromDrag(int srcIdx, int dstIdx)
    {
        var toSq = new Square(dstIdx);
        if (!_selectedSquare.HasValue || _selectedSquare.Value.Index != srcIdx)
            return false;

        var candidates = _legalMovesFromSelected
            .Where(m => m.To == toSq)
            .ToList();

        if (candidates.Count == 0) return false;

        // Prefer queen promotion; caller handles promotion dialog for >1 candidate with promotion
        Move move = candidates.FirstOrDefault(m => m.PromotionType == PieceType.Queen,
                                               candidates[0]);
        ExecuteMove(move);
        return true;
    }

    /// <summary>
    /// Public method to clear selection (used by UI when canceling promotion).
    /// </summary>
    public void ClearSelectionPublic()
    {
        ClearSelection();
        RequestRepaint();
    }

    private void SelectSquare(Square sq)
    {
        _selectedSquare = sq;
        // Pre-compute legal moves from this square for highlight drawing
        _legalMovesFromSelected = _engine.GetLegalMoves()
            .Where(m => m.From == sq)
            .ToList();
        RequestRepaint();
    }

    private void ClearSelection()
    {
        _selectedSquare = null;
        _legalMovesFromSelected = Array.Empty<Move>();
    }

    private void ExecuteMove(Move move)
    {
        _engine.MakeMove(move);
        _lastMove = move;
        ClearSelection();
        UpdateStatusText();
        RequestRepaint();
    }

    // ── Game controls ───────────────────────────────────────────────────────

    public void NewGame()
    {
        StopEngine();
        _engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
        _lastMove = null;
        ClearSelection();
        ClearInfoPanel();
        UpdateStatusText();
        RequestRepaint();
    }

    public void ResetToStartingPosition() => NewGame();

    public void UndoMove()
    {
        if (_isEngineThinking) return;
        try
        {
            _engine.UndoMove();
            _lastMove = null;
            ClearSelection();
            UpdateStatusText();
            RequestRepaint();
        }
        catch { /* no moves to undo */ }
    }

    public void FlipBoard()
    {
        _isFlipped = !_isFlipped;
        RequestRepaint();
    }

    public void ToggleAutoPlay()
    {
        _autoPlay = !_autoPlay;
    }

    /// <summary>
    /// Sets auto-play explicitly. Preferred over <see cref="ToggleAutoPlay"/> when driven by
    /// a ToggleButton: WPF has already flipped IsChecked by the time Click fires, so flipping
    /// a second, independent flag here can only ever drift out of sync with what the UI shows.
    /// </summary>
    public void SetAutoPlay(bool enabled)
    {
        _autoPlay = enabled;
    }

    public string GetCurrentFen() => _engine.ExportFen();

    /// <summary>
    /// Tries to apply a move given algebraic square names, e.g. "e2" → "e4".
    /// Returns true on success; sets error to a human-readable message on failure.
    /// Automatically handles promotion (defaults to Queen).
    /// </summary>
    public bool TryMoveBySquareName(string from, string to, out string error)
    {
        error = "";
        if (_isEngineThinking) { error = "Engine is thinking."; return false; }

        from = from.Trim().ToLowerInvariant();
        to   = to.Trim().ToLowerInvariant();

        if (!TryParseSquare(from, out var fromSq))
        { error = $"Invalid square: \"{from}\""; return false; }
        if (!TryParseSquare(to, out var toSq))
        { error = $"Invalid square: \"{to}\""; return false; }

        var legal = _engine.GetLegalMoves()
            .Where(m => m.From == fromSq && m.To == toSq)
            .ToList();

        if (legal.Count == 0)
        { error = $"No legal move from {from} to {to}."; return false; }

        // Prefer queen promotion when there are multiple candidates (pawn promotion)
        Move move = legal.FirstOrDefault(m => m.PromotionType == PieceType.Queen, legal[0]);
        ExecuteMove(move);
        return true;
    }

    private static bool TryParseSquare(string s, out Square sq)
    {
        sq = default;
        if (s.Length != 2) return false;
        int file = s[0] - 'a';
        int rank = s[1] - '1';
        if (file < 0 || file > 7 || rank < 0 || rank > 7) return false;
        sq = new Square(file, rank);
        return true;
    }

    public bool TryLoadFen(string fen)
    {
        try
        {
            StopEngine();
            _engine.LoadFen(fen);
            _lastMove = null;
            ClearSelection();
            ClearInfoPanel();
            UpdateStatusText();
            RequestRepaint();
            return true;
        }
        catch
        {
            return false;
        }
    }

    // ── Engine search ───────────────────────────────────────────────────────

    /// <summary>
    /// Starts an async engine search for the best move and applies it when done.
    /// maxTimeMs controls the maximum thinking time.
    /// </summary>
    public async Task LetEngineMoveAsync(int maxTimeMs = 3000)
    {
        if (_isEngineThinking) return;

        var legalMoves = _engine.GetLegalMoves();
        if (legalMoves.Count == 0) return;

        SetEngineThinking(true);
        StatusText = "Engine thinking…";

        _engineCts = new CancellationTokenSource();
        var ct = _engineCts.Token;

        // Capture the current FEN so the background thread has a stable copy
        string fen = _engine.ExportFen();

        SearchResult? result = null;
        try
        {
            result = await Task.Run(() =>
            {
                // Each search gets its own engine instance to avoid thread-lock contention
                var bgEngine = new ChessEngine();
                bgEngine.LoadFen(fen);
                var settings = new SearchSettings
                {
                    MaxDepth  = 64,
                    MaxTimeMs = maxTimeMs,
                    MinNodeTarget = null,  // time-controlled
                    UseIterativeDeepening = true
                };
                return bgEngine.FindBestMove(settings, ct);
            }, ct);
        }
        catch (OperationCanceledException) { }
        finally
        {
            _engineCts?.Dispose();
            _engineCts = null;
        }

        if (result != null && !ct.IsCancellationRequested)
        {
            UpdateInfoPanel(result);

            // Verify the move is still legal (position shouldn't have changed)
            var currentLegal = _engine.GetLegalMoves();
            var matchingMove = currentLegal.FirstOrDefault(m =>
                m.From == result.BestMove.From &&
                m.To   == result.BestMove.To   &&
                (result.BestMove.PromotionType == PieceType.None ||
                 m.PromotionType == result.BestMove.PromotionType));

            if (matchingMove != default)
            {
                ExecuteMove(matchingMove);
            }
        }

        SetEngineThinking(false);
        UpdateStatusText();
    }

    public void StopEngine()
    {
        _engineCts?.Cancel();
    }

    // ── Display coordinate helpers ──────────────────────────────────────────

    /// <summary>
    /// Maps a display cell (row 0-7, col 0-7) to a board Square.
    /// row=0 is rank 8 when unflipped, rank 1 when flipped.
    /// </summary>
    public Square DisplayToSquare(int displayRow, int displayCol)
    {
        int rank = _isFlipped ? displayRow       : 7 - displayRow;
        int file = _isFlipped ? 7 - displayCol   : displayCol;
        return new Square(file, rank);
    }

    /// <summary>Returns the file label character for a display column (0-7).</summary>
    public char FileLabel(int displayCol) =>
        (char)('a' + (_isFlipped ? 7 - displayCol : displayCol));

    /// <summary>Returns the rank number string for a display row (0-7).</summary>
    public string RankLabel(int displayRow) =>
        (_isFlipped ? displayRow + 1 : 8 - displayRow).ToString();

    // ── Private helpers ─────────────────────────────────────────────────────

    private void SetEngineThinking(bool thinking)
    {
        _isEngineThinking = thinking;
        EngineThinkingChanged?.Invoke(thinking);
    }

    private void RequestRepaint() => BoardRepaintRequested?.Invoke();

    private void UpdateStatusText()
    {
        var legalMoves = _engine.GetLegalMoves();
        var snap = _engine.GetBoardSnapshot();
        bool inCheck = snap.IsKingInCheck(snap.ActiveColor);

        string side = snap.ActiveColor == Color.White ? "White" : "Black";

        if (legalMoves.Count == 0)
        {
            StatusText = inCheck
                ? $"Checkmate! {(snap.ActiveColor == Color.White ? "Black" : "White")} wins."
                : "Stalemate — draw.";
        }
        else
        {
            StatusText = inCheck
                ? $"{side} to move (in check)"
                : $"{side} to move";
        }
    }

    private void UpdateInfoPanel(SearchResult result)
    {
        // result.Evaluation is side-to-move-relative (negamax convention).
        // Convert to White-positive for display: flip sign when Black moved last.
        int whiteRelative = ActiveColor == Color.White
            ? -result.Evaluation   // engine just finished Black's move; White is now to move
            : result.Evaluation;   // engine just finished White's move; result is already White-positive... 
        // Simpler: the result is from the perspective of the side that JUST searched.
        // After the engine moves for side S, White-positive = result.Evaluation * (S==White ? 1 : -1)
        // ActiveColor at this point is the side to move AFTER the engine moved, i.e. the opponent of S.
        // So White-positive = result.Evaluation * (ActiveColor==Black ? 1 : -1)
        int displayScore = result.Evaluation * (ActiveColor == Color.Black ? 1 : -1);

        // Mate detection and the ply-to-moves conversion belong to the score encoding, not to
        // the view: this used to carry its own 90,000 threshold and its own rounding, which
        // classified a 95,000 centipawn evaluation as a mate that the UCI layer called a score.
        var reported = SearchScores.ToReported(displayScore);

        string evalStr;
        if (reported.MateInMoves is int mateDist)
        {
            evalStr = mateDist > 0 ? $"Mate in {mateDist}" : $"Mated in {-mateDist}";
        }
        else
        {
            float evalPawns = reported.Cp / 100f;
            evalStr = evalPawns >= 0 ? $"+{evalPawns:F2}" : $"{evalPawns:F2}";
        }

        EvalText  = $"Eval: {evalStr}";
        BestMove  = $"Best: {result.BestMove}";
        PvText    = $"PV: {string.Join(" ", result.PrincipalVariation.Take(8).Select(m => m.ToString()))}";
        DepthText = $"Depth: {result.DepthAchieved}";
        NodesText = $"Nodes: {result.NodesSearched:N0}";
        NpsText   = $"NPS: {result.NodesPerSecond:N0}";
    }

    private void ClearInfoPanel()
    {
        EvalText  = "Eval: —";
        BestMove  = "Best: —";
        PvText    = "PV: —";
        DepthText = "Depth: —";
        NodesText = "Nodes: —";
        NpsText   = "NPS: —";
    }

    // ── INotifyPropertyChanged ───────────────────────────────────────────────

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    // ── IDisposable ──────────────────────────────────────────────────────────

    public void Dispose()
    {
        _engineCts?.Cancel();
        _engineCts?.Dispose();
    }
}
