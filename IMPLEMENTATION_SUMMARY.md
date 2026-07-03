# ChessBot: Foundation Architecture & Core Type System

## Overview

The ChessBot chess engine foundation has been successfully established across three .NET 8 projects. This document provides a complete summary of the project structure, type system, and baseline architectural shell.

---

## Solution Structure

```
ChessBot/
├── ChessBot.sln
├── ChessBot.Engine/
│   ├── ChessBot.Engine.csproj
│   ├── Types/
│   │   ├── Color.cs                    (Enum + Extensions)
│   │   ├── PieceType.cs                (Enum + Extensions)
│   │   ├── Piece.cs                    (Readonly Struct)
│   │   ├── Square.cs                   (Readonly Struct, 0x64 Mailbox)
│   │   ├── Move.cs                     (Readonly Struct + Parser)
│   │   ├── MoveType.cs                 (Flags Enum + Extensions)
│   │   ├── CastlingRights.cs           (Readonly Struct, Bitmask)
│   │   └── GameState.cs                (Readonly Struct, FEN-serializable)
│   ├── Board/
│   │   ├── Board.cs                    (State + Move/Undo Engine)
│   │   ├── MoveGenerator.cs            (Legal Move Generation)
│   │   └── CheckDetector.cs            (King Safety Detection)
│   ├── Search/
│   │   ├── SearchDefinitions.cs        (SearchSettings, SearchResult, EvaluationResult)
│   │   ├── Searcher.cs                 (Negamax + Alpha-Beta + ID)
│   │   └── MoveOrdering.cs             (PV, MVV-LVA, Killer Heuristics)
│   ├── Evaluation/
│   │   └── Evaluator.cs                (Static Position Evaluation)
│   ├── Hashing/
│   │   ├── ZobristHasher.cs            (Zobrist Hashing)
│   │   └── TranspositionTable.cs       (TT Caching)
│   └── ChessEngine.cs                  (Public API + Thread-Safety)
│
├── ChessBot.Wpf/
│   ├── ChessBot.Wpf.csproj
│   ├── App.xaml & App.xaml.cs
│   ├── MainWindow.xaml & MainWindow.xaml.cs
│   └── [MVVM & UI Infrastructure] (to be implemented in next iteration)
│
└── ChessBot.Tests/
	├── ChessBot.Tests.csproj
	├── PerftTests.cs
	├── MoveGenerationTests.cs
	├── FenParsingTests.cs
	├── SearchTests.cs
	├── ZobristHashTests.cs
	└── EvaluationTests.cs
```

---

## Core Type System

### 1. **Color** (`Types/Color.cs`)

Represents the side-to-move in chess. Two values: `White` (0) and `Black` (1).

**Key Extensions:**
- `Opposite()` → Returns opposite color
- `Sign()` → Returns 1 (White) or -1 (Black) for evaluation math
- `PawnDirection()` → Returns +1 (White, rank 8) or -1 (Black, rank 1)

---

### 2. **PieceType** (`Types/PieceType.cs`)

Represents the six chess piece types (excluding color):
- `None` (0) – Empty square
- `Pawn` (1) – 1 point
- `Knight` (2) – 3 points
- `Bishop` (3) – 3 points
- `Rook` (4) – 5 points
- `Queen` (5) – 9 points
- `King` (6) – Infinite (not traded)

**Key Extensions:**
- `MaterialValue()` → Returns centipawn value (100 cp = 1 pawn)
- `IsSlider()` → Returns true for Bishop, Rook, Queen
- `IsMinor()` → Knight or Bishop
- `IsMajor()` → Rook or Queen

---

### 3. **Piece** (`Types/Piece.cs`)

A readonly struct combining `Color` and `PieceType`. Represents a single piece on the board.

**Key Properties & Methods:**
- `Color` → The piece's color
- `Type` → The piece's type
- `IsEmpty` → True if it's an empty square
- `MaterialValue` → Total value of this piece
- `ToFenChar()` / `FromFenChar()` → FEN serialization (e.g., 'K', 'k', 'P', 'p')

---

### 4. **Square** (`Types/Square.cs`)

A readonly struct representing a single square using 0x64 (8×8 Mailbox) indexing.

**Internal Layout:**
- Index 0 = a1 (bottom-left, White's perspective)
- Index 63 = h8 (top-right)
- Conversion: `index = rank * 8 + file`

**Key Properties:**
- `Index` (0–63) → Internal position
- `File` (0–7) → Horizontal position (a=0, h=7)
- `Rank` (0–7) → Vertical position (1st rank=0, 8th rank=7)
- `FileLetter` → 'a' to 'h'
- `RankNumber` → 1 to 8

**Key Methods:**
- `FromAlgebraic(string)` → Create from "e4", "h1", etc.
- `ToString()` → Returns algebraic notation (e.g., "e4")
- `AllSquares` → Enumerable of all 64 squares

---

### 5. **MoveType** (`Types/MoveType.cs`)

A flags enum representing the type/classification of a move. Multiple flags can be combined (e.g., a promotion capture is both `Capture` and `Promotion`).

**Flags:**
- `Quiet` (0) – No special action
- `Capture` (1 << 0) – Captures an opponent piece
- `DoublePawnPush` (1 << 1) – Pawn moves 2 squares from start
- `Castling` (1 << 2) – King-side or Queen-side castle
- `EnPassant` (1 << 3) – En passant capture
- `Promotion` (1 << 4) – Pawn promotes to another piece
- `Check` (1 << 5) – Move puts opponent in check
- `Checkmate` (1 << 6) – Move delivers checkmate

**Key Extensions:**
- `IsCapture()` → Includes both `Capture` and `EnPassant`
- `IsPromotion()` → Check `Promotion` flag
- `IsTactical()` → `IsCapture()` or `IsPromotion()`
- `IsCastling()` → Check `Castling` flag

---

### 6. **Move** (`Types/Move.cs`)

A readonly struct representing a complete chess move with full context.

**Properties:**
- `From` → Source square
- `To` → Destination square
- `MoveType` → Classification (quiet, capture, castling, etc.)
- `PromotionType` → If this is a promotion, the target piece type

**Key Methods:**
- `FromAlgebraic(string)` → Create from "e2e4" or "e7e8q" (promotion)
- `ToString()` → Returns algebraic notation
- `ToDetailedString()` → Includes move type description
- `IsPromotion` → True if `PromotionType != None`

---

### 7. **CastlingRights** (`Types/CastlingRights.cs`)

A readonly struct tracking castling rights for both sides using bitmask flags.

**Bitmask Layout:**
```
Bit 0: White King-side (0-0)
Bit 1: White Queen-side (0-0-0)
Bit 2: Black King-side (0-0)
Bit 3: Black Queen-side (0-0-0)
```

**Key Properties:**
- `WhiteKingSide` → Bit 0
- `WhiteQueenSide` → Bit 1
- `BlackKingSide` → Bit 2
- `BlackQueenSide` → Bit 3

**Key Methods:**
- `FromFenString(string)` → Parse "KQkq", "K-", "-", etc.
- `ToFenString()` → Export to FEN format
- `CanCastle(Color)` → Any rights for this color?
- `CanCastle(Color, bool kingSide)` → Specific side?
- `Revoke(Color, bool kingSide)` → Remove specific right
- `RevokeColor(Color)` → Remove all rights for a color
- `IsEmpty` → No rights remain

---

### 8. **GameState** (`Types/GameState.cs`)

A readonly struct representing the non-board portion of the chess position (all FEN metadata).

**Properties:**
- `ActiveColor` → Whose turn it is (White or Black)
- `CastlingRights` → Available castling for both sides
- `EnPassantTarget` → Target square for en passant (index 0 if none)
- `HalfmoveClock` → Plies since last capture or pawn move (50-move rule)
- `FullmoveNumber` → Total completed moves (increments after Black's move)

**Key Methods:**
- `FromFenState(string)` → Parse FEN state portion (e.g., "w KQkq - 0 1")
- `ToFenState()` → Export to FEN format
- `IsFiftyMoveRuleDraw` → Halfmove clock ≥ 100?
- `HasEnPassant` → Is en passant available this turn?

---

## Board & State Management

### 9. **Board** (`Board/Board.cs`)

Manages the complete board state: piece positions, game metadata, and move history.

**Internal Layout:**
- `_pieces[64]` → Mailbox array (index = Square.Index)
- `_kingPositions[2]` → Cached king positions for check detection
- `_history` → Stack of (Move, CapturedPiece, GameState) for undo

**Key Methods:**
- `GetPiece(Square)` / `SetPiece(Square, Piece)` → Board access
- `MakeMove(Move)` → Execute a move, update all state
- `UndoMove()` → Revert the last move
- `ResetToStartingPosition()` → Initialize standard chess starting position
- `LoadFromFen(string)` → Parse full FEN and populate board
- `ExportToFen()` → Export board + state to FEN
- `Copy()` → Deep copy for independent analysis
- `GetAllPieces()` → Enumerate all non-empty squares
- `GetKingPosition(Color)` → Quick king lookup

**MakeMove Logic:**
1. Save current state to undo stack
2. Move piece from source to destination
3. Handle special moves (castling, en passant, promotion)
4. Update castling rights based on piece movements
5. Update en passant target (if double pawn push)
6. Update halfmove clock (reset on pawn move or capture)
7. Update fullmove number (increment after Black's move)
8. Switch active color

**UndoMove Logic:**
1. Pop the move, captured piece, and previous state from history
2. Restore all pieces to their original positions
3. Restore the full game state (color, castling, etc.)

---

## Search & Evaluation Framework (Architectural Shell)

### 10. **SearchDefinitions** (`Search/SearchDefinitions.cs`)

Defines the input and output structures for the search engine.

**SearchSettings:**
- `MaxDepth` (int?) → Maximum search depth (plies)
- `MaxTimeMs` (int?) → Maximum time allowed (milliseconds)
- `MinNodeTarget` (long?) → Minimum nodes to search (default: 300,000)
- `UseIterativeDeepening` (bool) → Enable iterative deepening
- `Verbose` (bool) → Enable debug output

**SearchResult:**
- `BestMove` → The best move found
- `Evaluation` (int) → Centipawn score (positive = White advantage)
- `PrincipalVariation` → Line of best play
- `DepthAchieved` → Search depth reached
- `NodesSearched` (long) → Total nodes evaluated
- `NodesPerSecond` (double) → Performance metric
- `ElapsedTimeMs` (long) → Elapsed time
- `IsCheckmate` / `IsStalemate` → Special conditions

**EvaluationResult:**
- `Score` → Static evaluation in centipawns
- `MaterialBalance` → White vs. Black material count

---

### 11. **ChessEngine** (`ChessEngine.cs`)

The public API facade. All operations are thread-safe via internal locking.

**Key Methods:**
- `LoadFen(string)` → Load position from FEN
- `ExportFen()` → Export position to FEN
- `GetLegalMoves()` → List of all legal moves
- `MakeMove(Move)` → Execute a move (validated)
- `UndoMove()` → Revert the last move
- `FindBestMove(SearchSettings, CancellationToken)` → Run search
- `Evaluate()` → Static position evaluation
- `RunPerft(int depth)` → Perft (performance test) algorithm
- `GetBoardSnapshot()` → Read-only board copy

---

## Project Configuration

### ChessBot.Engine.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
	<TargetFramework>net8.0</TargetFramework>
	<LangVersion>latest</LangVersion>
	<Nullable>enable</Nullable>
	<ImplicitUsings>enable</ImplicitUsings>
	<GenerateDocumentationFile>true</GenerateDocumentationFile>
  </PropertyGroup>

  <!-- No external dependencies -->
  <!-- InternalsVisibleTo: ChessBot.Tests -->
</Project>
```

**Key Features:**
- Zero external dependencies (pure .NET)
- XML documentation enabled
- Test project has internal visibility
- Nullable reference types enabled
- Latest C# language features

---

### ChessBot.Wpf.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk.WindowsDesktop">
  <PropertyGroup>
	<OutputType>WinExe</OutputType>
	<TargetFramework>net8.0-windows</TargetFramework>
	<UseWPF>true</UseWPF>
	<LangVersion>latest</LangVersion>
	<Nullable>enable</Nullable>
  </PropertyGroup>

  <!-- References ChessBot.Engine -->
</Project>
```

---

### ChessBot.Tests.csproj

```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
	<TargetFramework>net8.0</TargetFramework>
	<IsTestProject>true</IsTestProject>
	<Nullable>enable</Nullable>
  </PropertyGroup>

  <!-- xUnit test framework -->
  <!-- References ChessBot.Engine -->
</Project>
```

---

## Architecture Highlights

### Design Principles

1. **Zero Framework Dependencies in Engine**
   - ChessBot.Engine contains no UI, database, or external package references
   - Pure algorithmic and data structure logic
   - Allows engine reuse in console, web, mobile, or other contexts

2. **Strict Separation of Concerns**
   - **Engine** = Logic (board, moves, search, evaluation)
   - **WPF** = Presentation (UI, visualization, user interaction)
   - **Tests** = Validation (unit tests, integration tests, Perft verification)

3. **Immutable Value Types**
   - `Color`, `PieceType`, `Piece`, `Square`, `Move`, `CastlingRights`, `GameState` are all readonly structs or enums
   - Zero-allocation, high-performance, stack-allocated by default
   - Thread-safe at the value level

4. **Mailbox 0x64 Architecture**
   - Simple, correct, and easy to debug
   - Designed for future drop-in replacement (Bitboard, 0x88, etc.)
   - No performance penalties for this iteration

5. **Thread-Safe Public API**
   - `ChessEngine` uses internal lock for all state modifications
   - Supports concurrent analysis and UI responsiveness
   - CancellationToken support for graceful search interruption

6. **Complete State Restoration**
   - Move undo via history stack (Move, CapturedPiece, GameState)
   - Enables efficient search with reversible moves
   - No board cloning needed for negamax search

---

## Next Steps (Beyond This Iteration)

### Step 3: Move Generation & Validation
- Implement `MoveGenerator.GenerateLegalMoves()`
- Pseudo-legal move generation for all piece types
- `CheckDetector` for king safety validation
- En passant and castling legality checks

### Step 4: Perft Testing Framework
- Implement recursive `RunPerft()` validation
- Target milestone verification:
  - Depth 1: 20 nodes
  - Depth 2: 400 nodes
  - Depth 3: 8,902 nodes
  - Depth 4: 197,281 nodes

### Step 5: Search Engine (Negamax + Alpha-Beta + ID)
- Implement `Searcher.Search()` with alpha-beta pruning
- Iterative deepening for time management
- Principal Variation tracking
- Transposition table integration

### Step 6: Evaluation & Move Ordering
- Static board evaluation (material, PST, pawn structure)
- MVV-LVA capture ordering
- Killer move heuristics
- Zobrist hashing integration

### Step 7: WPF User Interface
- 8×8 board grid with piece rendering
- Click-to-move interaction
- Legal move highlighting
- Real-time search telemetry display
- Game control buttons (New, Undo, Let Engine Move)

---

## Compilation Status

✅ **Build Status: SUCCESS**

All projects compile cleanly with no errors or warnings. The foundation is ready for incremental implementation of move generation, search, and UI layers.

---

## Summary

The ChessBot foundation provides:

1. ✅ **Complete Type System** – All core primitives implemented as zero-allocation value types
2. ✅ **Board State Management** – Full move/undo mechanics with FEN serialization
3. ✅ **Thread-Safe Public API** – Clean ChessEngine facade with internal locking
4. ✅ **Architectural Decoupling** – Engine independent, WPF autonomous, tests isolated
5. ✅ **Performance-First Design** – Readonly structs, bitmasks, cached king positions
6. ✅ **Future-Ready Architecture** – Board representation abstraction for optimization upgrades

The next iteration will focus on **Steps 3 & 4**: move generation validation and Perft testing framework.
