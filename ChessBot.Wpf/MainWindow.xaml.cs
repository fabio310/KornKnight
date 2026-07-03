namespace ChessBot.Wpf;

using ChessBot.Engine.Types;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Effects;
using System.Windows.Shapes;
using PieceColor = global::ChessBot.Engine.Types.Color;

/// <summary>
/// Canvas-only board: no Viewbox, no separate drag overlay.
/// All squares, highlights, piece glyphs, and the drag ghost live
/// directly on BoardCanvas so every coordinate is in the same space.
/// </summary>
public partial class MainWindow : Window
{
    // ── Colors ──────────────────────────────────────────────────────────────
    private static readonly SolidColorBrush BrushLight     = new(System.Windows.Media.Color.FromRgb(0xF0, 0xD9, 0xB5));
    private static readonly SolidColorBrush BrushDark      = new(System.Windows.Media.Color.FromRgb(0xB5, 0x88, 0x63));
    private static readonly SolidColorBrush BrushSelected  = new(System.Windows.Media.Color.FromArgb(0xCC, 0xF6, 0xF6, 0x69));
    private static readonly SolidColorBrush BrushLastMove  = new(System.Windows.Media.Color.FromArgb(0xAA, 0xCC, 0xD1, 0x6E));
    private static readonly SolidColorBrush BrushCheck     = new(System.Windows.Media.Color.FromArgb(0xDD, 0xE6, 0x35, 0x35));
    private static readonly SolidColorBrush BrushLegalDot  = new(System.Windows.Media.Color.FromArgb(0x55, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush BrushLegalRing = new(System.Windows.Media.Color.FromArgb(0x66, 0x00, 0x00, 0x00));
    private static readonly SolidColorBrush BrushHover     = new(System.Windows.Media.Color.FromArgb(0xBB, 0x64, 0xB5, 0xFF));

    private static readonly FontFamily PieceFont = new("Segoe UI Symbol,Arial Unicode MS");

    private static string PieceGlyph(PieceType t, PieceColor c)
    {
        bool w = c == PieceColor.White;
        return t switch
        {
            PieceType.King   => w ? "♔" : "♚",
            PieceType.Queen  => w ? "♕" : "♛",
            PieceType.Rook   => w ? "♖" : "♜",
            PieceType.Bishop => w ? "♗" : "♝",
            PieceType.Knight => w ? "♘" : "♞",
            PieceType.Pawn   => w ? "♙" : "♟",
            _                => "",
        };
    }

    // ── Per-square canvas elements (index = square index 0-63) ──────────────
    private readonly Rectangle[] _sqRects     = new Rectangle[64];
    private readonly Ellipse[]   _sqDots      = new Ellipse[64];
    private readonly Ellipse[]   _sqRings     = new Ellipse[64];
    private readonly TextBlock[] _sqPieces    = new TextBlock[64];

    private readonly BoardViewModel _vm = new();

    // ── Promotion ────────────────────────────────────────────────────────────
    private Move[]? _pendingPromotions;

    // ── Drag state ───────────────────────────────────────────────────────────
    private bool      _isDragging;
    private int       _dragSrcIdx   = -1;
    private Point     _dragStart;
    private TextBlock _ghost        = null!;
    private const double DragThreshold = 5.0;

    // ── Layout cache (set in SizeChanged) ───────────────────────────────────
    private double _cellSize   = 60;
    private double _boardOffX  = 0;
    private double _boardOffY  = 0;
    private double _boardPx    = 480;

    // ── Constructor ──────────────────────────────────────────────────────────
    public MainWindow()
    {
        InitializeComponent();
        DataContext = _vm;

        _vm.BoardRepaintRequested += RefreshBoard;
        _vm.EngineThinkingChanged += OnEngineThinkingChanged;

        BindInfoLabel(TxtEval,   nameof(_vm.EvalText));
        BindInfoLabel(TxtBest,   nameof(_vm.BestMove));
        BindInfoLabel(TxtDepth,  nameof(_vm.DepthText));
        BindInfoLabel(TxtNodes,  nameof(_vm.NodesText));
        BindInfoLabel(TxtNps,    nameof(_vm.NpsText));
        BindInfoLabel(TxtPv,     nameof(_vm.PvText));
        BindInfoLabel(TxtStatus, nameof(_vm.StatusText));

        BtnNewGame.Click    += (_, _) => _vm.NewGame();
        BtnReset.Click      += (_, _) => _vm.ResetToStartingPosition();
        BtnUndo.Click       += (_, _) => _vm.UndoMove();
        BtnFlip.Click       += (_, _) => { _vm.FlipBoard(); RefreshBoard(); };
        BtnCopyFen.Click    += OnCopyFen;
        BtnLoadFen.Click    += OnLoadFen;
        BtnEngineMove.Click += async (_, _) => await OnEngineMoveAsync();
        BtnStopEngine.Click += (_, _) => _vm.StopEngine();

        BtnPromoQueen.Click  += (_, _) => ApplyPromotion(PieceType.Queen);
        BtnPromoRook.Click   += (_, _) => ApplyPromotion(PieceType.Rook);
        BtnPromoBishop.Click += (_, _) => ApplyPromotion(PieceType.Bishop);
        BtnPromoKnight.Click += (_, _) => ApplyPromotion(PieceType.Knight);
        BtnPromoCancel.Click += (_, _) => CancelPromotion();

        BtnApplyMove.Click += OnApplyMoveClicked;
        TxtFrom.KeyDown += (_, e) => { if (e.Key == Key.Enter) TxtTo.Focus(); };
        TxtTo.KeyDown   += (_, e) => { if (e.Key == Key.Enter) OnApplyMoveClicked(null!, null!); };

        BuildCanvasElements();
    }

    // ── Build static canvas elements once ───────────────────────────────────
    private void BuildCanvasElements()
    {
        BoardCanvas.Children.Clear();

        for (int i = 0; i < 64; i++)
        {
            // background square
            var rect = new Rectangle { IsHitTestVisible = false };
            Canvas.SetZIndex(rect, 0);
            BoardCanvas.Children.Add(rect);
            _sqRects[i] = rect;

            // legal-move ring (for squares that have a piece)
            var ring = new Ellipse
            {
                Stroke = BrushLegalRing, StrokeThickness = 4,
                Fill = Brushes.Transparent,
                IsHitTestVisible = false, Visibility = Visibility.Hidden
            };
            Canvas.SetZIndex(ring, 1);
            BoardCanvas.Children.Add(ring);
            _sqRings[i] = ring;

            // legal-move dot (for empty squares)
            var dot = new Ellipse
            {
                Fill = BrushLegalDot,
                IsHitTestVisible = false, Visibility = Visibility.Hidden
            };
            Canvas.SetZIndex(dot, 1);
            BoardCanvas.Children.Add(dot);
            _sqDots[i] = dot;

            // piece glyph
            var txt = new TextBlock
            {
                FontFamily = PieceFont,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment   = VerticalAlignment.Center,
                IsHitTestVisible    = false
            };
            Canvas.SetZIndex(txt, 2);
            BoardCanvas.Children.Add(txt);
            _sqPieces[i] = txt;
        }
    }

    // ── Layout ───────────────────────────────────────────────────────────────
    private void BoardCanvas_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        double w = BoardCanvas.ActualWidth;
        double h = BoardCanvas.ActualHeight;
        double side = Math.Min(w, h) - 16; // 8-px padding each side
        if (side < 80) side = 80;
        _boardPx  = side;
        _cellSize = side / 8.0;
        _boardOffX = (w - side) / 2.0;
        _boardOffY = (h - side) / 2.0;
        RefreshBoard();
    }

    // ── Refresh all visual elements ──────────────────────────────────────────
    private void RefreshBoard()
    {
        if (_cellSize <= 0) return;
        for (int idx = 0; idx < 64; idx++)
        {
            var sq      = new Square(idx);
            // Normal: white at bottom — rank 0 is at the bottom (dispRow 7), file 0 is left
            // Flipped: black at bottom — rank 0 is at the top (dispRow 0), file 0 is right
            int dispRow = _vm.IsFlipped ? sq.Rank       : (7 - sq.Rank);
            int dispCol = _vm.IsFlipped ? (7 - sq.File) : sq.File;

            double x = _boardOffX + dispCol * _cellSize;
            double y = _boardOffY + dispRow * _cellSize;
            double cs = _cellSize;

            // ── square background color ──
            bool isLight = (sq.File + sq.Rank) % 2 == 1;
            var bgBrush = isLight ? BrushLight : BrushDark;

            if (_vm.IsSelected(sq))          bgBrush = BrushSelected;
            else if (_vm.IsLastMoveSquare(sq)) bgBrush = BrushLastMove;
            else if (_vm.IsKingInCheck(sq))  bgBrush = BrushCheck;
            else if (_vm.IsDragHover(sq))    bgBrush = BrushHover;

            var rect = _sqRects[idx];
            rect.Fill   = bgBrush;
            rect.Width  = cs;
            rect.Height = cs;
            Canvas.SetLeft(rect, x);
            Canvas.SetTop (rect, y);

            // ── legal move indicators ──
            bool isLegal = _vm.IsLegalTarget(sq);
            var piece = _vm.GetPiece(sq);

            var ring = _sqRings[idx];
            var dot  = _sqDots[idx];

            if (isLegal && piece.Type != PieceType.None)
            {
                // ring around occupied squares
                double margin = cs * 0.08;
                ring.Width  = cs - margin * 2;
                ring.Height = cs - margin * 2;
                Canvas.SetLeft(ring, x + margin);
                Canvas.SetTop (ring, y + margin);
                ring.Visibility = Visibility.Visible;
                dot.Visibility  = Visibility.Hidden;
            }
            else if (isLegal)
            {
                // dot on empty squares
                double ds = cs * 0.32;
                dot.Width  = ds;
                dot.Height = ds;
                Canvas.SetLeft(dot, x + (cs - ds) / 2);
                Canvas.SetTop (dot, y + (cs - ds) / 2);
                dot.Visibility  = Visibility.Visible;
                ring.Visibility = Visibility.Hidden;
            }
            else
            {
                ring.Visibility = Visibility.Hidden;
                dot.Visibility  = Visibility.Hidden;
            }

            // ── piece glyph ──
            var txt = _sqPieces[idx];
            if (_isDragging && idx == _dragSrcIdx)
            {
                txt.Text = "";
            }
            else if (piece.Type != PieceType.None)
            {
                txt.Text       = PieceGlyph(piece.Type, piece.Color);
                txt.FontSize   = cs * 0.80;
                txt.Foreground = piece.Color == PieceColor.White ? Brushes.White : Brushes.Black;
                txt.Effect     = piece.Color == PieceColor.White
                    ? new DropShadowEffect { ShadowDepth = 1, BlurRadius = 4, Color = Colors.Black, Opacity = 0.9 }
                    : new DropShadowEffect { ShadowDepth = 1, BlurRadius = 3, Color = Colors.White, Opacity = 0.6 };
                txt.Width  = cs;
                txt.Height = cs;
                txt.TextAlignment = TextAlignment.Center;
                Canvas.SetLeft(txt, x);
                Canvas.SetTop (txt, y);
                txt.Visibility = Visibility.Visible;
            }
            else
            {
                txt.Text = "";
            }
        }

        // FEN label
        TxtFen.Text = _vm.GetCurrentFen();
    }

    // ── Hit testing: canvas point -> square index ────────────────────────────
    private int HitSquare(Point pt)
    {
        double bx = pt.X - _boardOffX;
        double by = pt.Y - _boardOffY;
        if (bx < 0 || by < 0 || bx >= _boardPx || by >= _boardPx) return -1;

        int dispCol = (int)(bx / _cellSize);
        int dispRow = (int)(by / _cellSize);
        dispCol = Math.Clamp(dispCol, 0, 7);
        dispRow = Math.Clamp(dispRow, 0, 7);

        return _vm.DisplayToSquare(dispRow, dispCol).Index;
    }

    // ── Mouse events ─────────────────────────────────────────────────────────
    private void BoardCanvas_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        if (_pendingPromotions != null) { CancelPromotion(); return; }
        if (_vm.IsEngineThinking) return;

        Point pt  = e.GetPosition(BoardCanvas);
        int   idx = HitSquare(pt);
        if (idx < 0) return;

        var sq    = new Square(idx);
        var piece = _vm.GetPiece(sq);

        if (piece.Type != PieceType.None)
        {
            // start potential drag from any piece
            _dragSrcIdx = idx;
            _dragStart  = pt;
            _isDragging = false;
            BoardCanvas.CaptureMouse();
            _vm.SelectSquareForDrag(idx);
            RefreshBoard();
        }
        else if (_vm.IsLegalTarget(sq))
        {
            HandleMoveOrPromotion(idx);
        }
        else
        {
            _vm.ClearSelectionPublic();
            RefreshBoard();
        }

        e.Handled = true;
    }

    private void BoardCanvas_MouseMove(object sender, MouseEventArgs e)
    {
        if (_dragSrcIdx < 0 || !BoardCanvas.IsMouseCaptured) return;

        Point pt   = e.GetPosition(BoardCanvas);
        var   diff = pt - _dragStart;

        if (!_isDragging)
        {
            if (Math.Abs(diff.X) < DragThreshold && Math.Abs(diff.Y) < DragThreshold) return;

            // Commit to drag: create ghost glyph directly on the canvas
            _isDragging = true;
            var piece = _vm.GetPiece(new Square(_dragSrcIdx));

            _ghost = new TextBlock
            {
                Text             = PieceGlyph(piece.Type, piece.Color),
                FontSize         = _cellSize * 0.90,
                FontFamily       = PieceFont,
                IsHitTestVisible = false,
                TextAlignment    = TextAlignment.Center
            };

            if (piece.Color == PieceColor.White)
            {
                _ghost.Foreground = Brushes.White;
                _ghost.Effect = new DropShadowEffect { ShadowDepth = 3, BlurRadius = 12, Color = Colors.Black, Opacity = 1.0 };
            }
            else
            {
                _ghost.Foreground = Brushes.Black;
                _ghost.Effect = new DropShadowEffect { ShadowDepth = 3, BlurRadius = 8,  Color = Colors.White, Opacity = 0.8 };
            }

            Canvas.SetZIndex(_ghost, 10);
            BoardCanvas.Children.Add(_ghost);

            // Hide the source piece text
            _sqPieces[_dragSrcIdx].Text = "";
        }

        // Position ghost centred on cursor
        _ghost.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        double gw = Math.Max(_ghost.DesiredSize.Width,  _cellSize);
        double gh = Math.Max(_ghost.DesiredSize.Height, _cellSize);
        Canvas.SetLeft(_ghost, pt.X - gw / 2);
        Canvas.SetTop (_ghost, pt.Y - gh * 0.55);

        // Update hover highlight
        int hoverIdx = HitSquare(pt);
        _vm.SetHoverSquare(hoverIdx >= 0 ? new Square(hoverIdx) : (Square?)null);
        RefreshBoard();
    }

    private void BoardCanvas_MouseUp(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;
        BoardCanvas.ReleaseMouseCapture();

        Point pt      = e.GetPosition(BoardCanvas);
        int   srcIdx  = _dragSrcIdx;
        _dragSrcIdx   = -1;
        _vm.SetHoverSquare(null);

        if (!_isDragging)
        {
            // Pure click: selection already done in MouseDown
            // If clicking same square twice, deselect
            RefreshBoard();
            return;
        }

        // Remove ghost
        _isDragging = false;
        if (_ghost != null)
        {
            BoardCanvas.Children.Remove(_ghost);
            _ghost = null!;
        }

        int dropIdx = HitSquare(pt);

        if (dropIdx >= 0 && dropIdx != srcIdx && _vm.IsLegalTarget(new Square(dropIdx)))
        {
            var dropSq = new Square(dropIdx);
            if (IsPromotionTarget(dropSq, out var promos))
            {
                _pendingPromotions = promos;
                ShowPromotionPanel();
                RefreshBoard();
                return;
            }
            _vm.ApplyMoveFromDrag(srcIdx, dropIdx);
        }
        else
        {
            _vm.ClearSelectionPublic();
        }

        RefreshBoard();
    }

    // ── Click-to-move ────────────────────────────────────────────────────────
    private void HandleMoveOrPromotion(int squareIndex)
    {
        var toSq = new Square(squareIndex);
        if (IsPromotionTarget(toSq, out var promos))
        {
            _pendingPromotions = promos;
            ShowPromotionPanel();
            return;
        }
        _vm.OnSquareClicked(squareIndex);
        RefreshBoard();
    }

    private bool IsPromotionTarget(Square toSq, out Move[] promos)
    {
        promos = [];
        if (!_vm.IsLegalTarget(toSq)) return false;
        var candidates = _vm.GetPromotionMovesTo(toSq);
        if (candidates.Length > 0) { promos = candidates; return true; }
        return false;
    }

    // ── Promotion ────────────────────────────────────────────────────────────
    private void ShowPromotionPanel()
    {
        PromotionPanel.Visibility = Visibility.Visible;
        var c = _vm.ActiveColor;
        bool w = c == PieceColor.White;
        BtnPromoQueen.Content  = w ? "♕" : "♛";
        BtnPromoRook.Content   = w ? "♖" : "♜";
        BtnPromoBishop.Content = w ? "♗" : "♝";
        BtnPromoKnight.Content = w ? "♘" : "♞";
    }

    private void ApplyPromotion(PieceType promoteTo)
    {
        PromotionPanel.Visibility = Visibility.Collapsed;
        if (_pendingPromotions == null) return;
        var move = _pendingPromotions.FirstOrDefault(m => m.PromotionType == promoteTo);
        _pendingPromotions = null;
        if (move != default) _vm.ApplyMove(move);
        RefreshBoard();
    }

    private void CancelPromotion()
    {
        PromotionPanel.Visibility = Visibility.Collapsed;
        _pendingPromotions = null;
        _vm.ClearSelectionPublic();
        RefreshBoard();
    }

    // ── Toolbar handlers ─────────────────────────────────────────────────────
    private void OnCopyFen(object sender, RoutedEventArgs e)
    {
        try { Clipboard.SetText(_vm.GetCurrentFen()); }
        catch { }
    }

    private void OnLoadFen(object sender, RoutedEventArgs e)
    {
        var dlg = new FenInputDialog { Owner = this };
        if (dlg.ShowDialog() == true)
        {
            if (!_vm.TryLoadFen(dlg.FenText))
                MessageBox.Show("Invalid FEN string.", "Invalid FEN", MessageBoxButton.OK, MessageBoxImage.Warning);
            else
                RefreshBoard();
        }
    }

    private void OnApplyMoveClicked(object sender, RoutedEventArgs e)
    {
        string from = TxtFrom.Text.Trim();
        string to   = TxtTo.Text.Trim();
        TxtMoveError.Text = "";

        if (!_vm.TryMoveBySquareName(from, to, out string err))
        {
            TxtMoveError.Text = err;
        }
        else
        {
            TxtFrom.Text = "";
            TxtTo.Text   = "";
            RefreshBoard();
        }
    }

    private void ShowMoveError(string msg) => TxtMoveError.Text = msg;

    private async Task OnEngineMoveAsync()
    {
        await _vm.LetEngineMoveAsync();
        RefreshBoard();
    }

    private void OnEngineThinkingChanged(bool thinking)
    {
        BtnEngineMove.IsEnabled = !thinking;
        BtnStopEngine.IsEnabled = thinking;
    }

    // ── Helpers ──────────────────────────────────────────────────────────────
    private void BindInfoLabel(TextBlock tb, string propName)
    {
        var binding = new System.Windows.Data.Binding(propName)
        {
            Source = _vm,
            Mode   = System.Windows.Data.BindingMode.OneWay
        };
        tb.SetBinding(TextBlock.TextProperty, binding);
    }

    protected override void OnClosed(EventArgs e)
    {
        _vm.StopEngine();
        base.OnClosed(e);
    }
}
