# ChessBot Architecture Quick Reference

## Type System at a Glance

| Type | Category | Purpose | Key Methods |
|------|----------|---------|------------|
| `Color` | Enum | Side-to-move (White/Black) | `.Opposite()`, `.Sign()`, `.PawnDirection()` |
| `PieceType` | Enum | Piece classification | `.MaterialValue()`, `.IsSlider()`, `.IsMajor()` |
| `Piece` | Struct | Piece + Color combo | `.ToFenChar()`, `.FromFenChar()` |
| `Square` | Struct | Board square (0-63) | `.FromAlgebraic()`, `.ToString()` |
| `Move` | Struct | Source + Dest + Type + Promotion | `.FromAlgebraic()`, `.ToDetailedString()` |
| `MoveType` | Flags Enum | Move classification | `.IsCapture()`, `.IsPromotion()`, `.IsTactical()` |
| `CastlingRights` | Struct | Castling availability (4-bit) | `.CanCastle()`, `.Revoke()`, `.ToFenString()` |
| `GameState` | Struct | Turn + Castling + EP + Clocks | `.FromFenState()`, `.ToFenState()` |

## Board Representation

```
Index:  0-7 = Rank 1 (White's back rank)
		8-15 = Rank 2
		...
		56-63 = Rank 8 (Black's back rank)

Square Mapping:
  a1 = Index 0    b1 = Index 1    ...    h1 = Index 7
  a2 = Index 8    b2 = Index 9    ...    h2 = Index 15
  ...
  a8 = Index 56   b8 = Index 57   ...    h8 = Index 63

File (Column): index % 8 = 0-7 (a-h)
Rank (Row):    index / 8 = 0-7 (1-8)
```

## Core API Structure

### ChessEngine (Main Entry Point)
```csharp
var engine = new ChessEngine();  // Starts at standard position

// FEN I/O
engine.LoadFen("rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1");
string fen = engine.ExportFen();

// Move Management
IReadOnlyList<Move> moves = engine.GetLegalMoves();
engine.MakeMove(move);
engine.UndoMove();

// Analysis
var searchResult = engine.FindBestMove(new SearchSettings { MaxDepth = 20 }, cts.Token);
var evalResult = engine.Evaluate();

// Verification
long nodeCount = engine.RunPerft(4);  // Should be 197,281 from start
```

### SearchSettings Configuration
```csharp
var settings = new SearchSettings
{
	MaxDepth = 20,                    // Depth limit
	MaxTimeMs = 5000,                 // 5 seconds
	MinNodeTarget = 300_000,          // Min nodes to search
	UseIterativeDeepening = true,     // Deepen gradually
	Verbose = true                    // Debug output
};
```

### SearchResult Output
```csharp
searchResult.BestMove               // The best move found
searchResult.Evaluation             // Score in centipawns (cp)
searchResult.PrincipalVariation     // Line of best play
searchResult.DepthAchieved          // Depth reached
searchResult.NodesSearched          // Total nodes evaluated
searchResult.NodesPerSecond         // Performance metric
searchResult.ElapsedTimeMs          // Time taken
searchResult.IsCheckmate            // Mate found?
searchResult.IsStalemate            // Stalemate?
```

## Square Algebraic Notation

```csharp
// Creating squares
var e4 = Square.FromAlgebraic("e4");
var h1 = new Square(7, 0);           // File 7 (h), Rank 0 (1)
var a8 = new Square(0, 7);           // File 0 (a), Rank 7 (8)

// Using squares
int index = e4.Index;                // 0-63
int file = e4.File;                  // 0-7 (a-h)
int rank = e4.Rank;                  // 0-7 (1-8)
char fileLetter = e4.FileLetter;     // 'e'
int rankNumber = e4.RankNumber;      // 4

string notation = e4.ToString();     // "e4"
```

## Move Notation & Types

```csharp
// Creating moves
var move = new Move(from, to, MoveType.Quiet);
var promotion = new Move(from, to, MoveType.Promotion, PieceType.Queen);
var castle = new Move(from, to, MoveType.Castling);

// Algebraic notation
var e2e4 = Move.FromAlgebraic("e2e4");
var e7e8q = Move.FromAlgebraic("e7e8q");  // With promotion

// Using moves
string notation = move.ToString();           // "e2e4"
string detailed = move.ToDetailedString();   // "e2e4 (capture)"
bool isCapture = move.MoveType.IsCapture();
bool isPromotion = move.IsPromotion;
```

## FEN String Components

Example: `rnbqkbnr/pppppppp/8/8/8/8/PPPPPPPP/RNBQKBNR w KQkq - 0 1`

| Part | Index | Example | Purpose |
|------|-------|---------|---------|
| Piece Placement | 0 | `rnbqkbnr/...` | Board state |
| Active Color | 1 | `w` or `b` | Whose turn |
| Castling Rights | 2 | `KQkq` | What's available |
| En Passant | 3 | `-` or `e3` | Capture target |
| Halfmove Clock | 4 | `0` | 50-move counter |
| Fullmove Number | 5 | `1` | Move count |

## Centipawn (cp) Evaluation Scale

```
 1.00 = +100 cp (slight White advantage)
 3.00 = +300 cp (minor piece advantage)
 5.00 = +500 cp (rook advantage)
 9.00 = +900 cp (queen advantage)
±5.00 (±500 cp) = likely decisive
Mate-in-N (special handling in search)
```

## Material Values

```
Pawn   = 100 cp (1 point)
Knight = 300 cp (3 points)
Bishop = 300 cp (3 points)
Rook   = 500 cp (5 points)
Queen  = 900 cp (9 points)
King   = Infinite (not traded)
```

## Piece FEN Characters

```
White: P (Pawn), N (Knight), B (Bishop), R (Rook), Q (Queen), K (King)
Black: p (Pawn), n (Knight), b (Bishop), r (Rook), q (Queen), k (King)
Empty: . (dot) or digit 1-8 (number of empty squares)
```

## Perft Benchmark Targets

From the standard starting position:

| Depth | Expected Nodes | Purpose |
|-------|---------------|----|
| 1 | 20 | Single move count |
| 2 | 400 | Two half-moves |
| 3 | 8,902 | Three plies |
| 4 | 197,281 | **Phase 4 validation target** |
| 5 | 4,865,609 | Full verification |
| 6 | 119,060,324 | Deep analysis validation |

## Thread Safety Model

```csharp
// ChessEngine is thread-safe via internal locking
// Multiple threads can call methods concurrently:

var task1 = Task.Run(() => engine.RunPerft(3));
var task2 = Task.Run(() => engine.FindBestMove(settings, cts.Token));
var task3 = Task.Run(() => engine.Evaluate());

// All operations are serialized internally, no external locking needed
```

## Placeholder Implementations (To Be Done)

These methods currently throw `NotImplementedException`:

| Method | Phase | Complexity |
|--------|-------|-----------|
| `Board.LoadFromFen()` | Phase 5 | Medium |
| `Board.ExportToFen()` | Phase 5 | Medium |
| `ChessEngine.GetLegalMoves()` | Phase 3 | High |
| `ChessEngine.MakeMove()` | Phase 3 | Medium |
| `ChessEngine.UndoMove()` | Phase 3 | Medium |
| `ChessEngine.FindBestMove()` | Phase 7 | Very High |
| `ChessEngine.Evaluate()` | Phase 6 | High |
| `ChessEngine.RunPerft()` | Phase 4 | Medium |

## Design Principles Applied

1. **Zero Allocations:** All core types are value types (struct/enum)
2. **Immutability:** All types are read-only (state changes via new instances)
3. **Strong Typing:** No raw integers or implicit conversions
4. **Decoupled Board:** 0x64 Mailbox now, Bitboard later without API changes
5. **FEN-Native:** All types understand FEN serialization
6. **Thread-Safe:** ChessEngine synchronizes all access
7. **Testable:** Pure functions + no side effects (except UI)

## Common Patterns

### Iterating All Squares
```csharp
foreach (var square in Square.AllSquares)
{
	var piece = board.GetPiece(square);
	if (!piece.IsEmpty)
	{
		// Process piece
	}
}
```

### Checking Colors & Piece Types
```csharp
if (piece.Color == Color.White && piece.Type == PieceType.Knight)
{
	// Process White knight
}

if (piece.Type.IsSlider())  // Bishop, Rook, Queen
{
	// Sliding piece logic
}
```

### Working with Castling Rights
```csharp
var castling = board.CastlingRights;

if (castling.CanCastle(Color.White, kingSide: true))
{
	// White can castle kingside
}

var updated = castling.Revoke(Color.White, kingSide: true);
```

### Move Filtering
```csharp
var legalMoves = engine.GetLegalMoves();
var captures = legalMoves.Where(m => m.MoveType.IsCapture()).ToList();
var promotions = legalMoves.Where(m => m.IsPromotion).ToList();
var tactics = legalMoves.Where(m => m.MoveType.IsTactical()).ToList();
```

## Building & Running

```powershell
# Build all projects
dotnet build

# Run tests
dotnet test ChessBot.Tests

# Run WPF app
dotnet run --project ChessBot.Wpf
```

---

This quick reference should accelerate development of Phase 3 onwards. Refer to FOUNDATION_SUMMARY.md for architectural details.
