# ChessBot Architecture Diagrams & Visual Guides

## Solution Dependency Graph

```
┌─────────────────────────────────────────────────────────────────┐
│                      ChessBot.sln                               │
├─────────────────────────────────────────────────────────────────┤
│                                                                   │
│  ┌──────────────────┐    ┌──────────────────┐  ┌──────────────┐ │
│  │  ChessBot.Wpf    │    │ ChessBot.Engine  │  │ ChessBot.Tests
│  │  (WPF App)       │───▶│  (Class Library) │◀─┤  (xUnit)    │ │
│  │                  │    │                  │  │              │ │
│  │ net8.0-windows   │    │ net8.0           │  │ net8.0       │ │
│  │ UseWPF: true     │    │ No Dependencies  │  │ Ref: Engine  │ │
│  │                  │    │                  │  │              │ │
│  └──────────────────┘    └──────────────────┘  └──────────────┘ │
│                                │                                  │
│                   (Project Reference)                             │
│                                                                   │
└─────────────────────────────────────────────────────────────────┘
```

## Type System Hierarchy

```
						 CORE PRIMITIVES
						(Value Types Only)
							  │
		┌─────────────────────┼─────────────────────┐
		│                     │                     │
	┌───▼────┐        ┌───────▼────────┐      ┌────▼────┐
	│ Colors │        │  Piece Types   │      │ Squares │
	├────────┤        ├────────────────┤      ├─────────┤
	│ White  │        │ None           │      │ 0-63    │
	│ Black  │        │ Pawn, Knight   │      │ File/Rank
	│        │        │ Bishop, Rook   │      │ Algebraic
	│Exts:   │        │ Queen, King    │      │         │
	│Opposite│        │                │      │Exts:    │
	│Sign    │        │Exts:           │      │Algebraic│
	│PawnDir │        │MaterialValue   │      │ToString │
	└────────┘        │IsSlider, IsMajor     └─────────┘
					  │CanPromote             AllSquares
					  └────────────────┘

		 ┌─────────────────────┬──────────────────────┐
		 │                     │                      │
	┌────▼────┐          ┌─────▼──────┐      ┌───────▼────┐
	│  PIECE  │          │ MOVE TYPE  │      │  MOVE      │
	├─────────┤          ├────────────┤      ├────────────┤
	│ Color   │          │ Quiet      │      │ From Sq    │
	│ PieceType          │ Capture    │      │ To Sq      │
	│ IsEmpty │          │ DoublePush │      │ MoveType   │
	│         │          │ Castling   │      │ PromotionPc│
	│Exts:    │          │ EnPassant  │      │            │
	│Material │          │ Promotion  │      │Exts:       │
	│ToFenChar│          │ Check      │      │Algebraic   │
	│FromFen  │          │ Checkmate  │      │ToString    │
	└─────────┘          │            │      │Detailed    │
						 │Exts:       │      └────────────┘
						 │IsCapture   │
						 │IsPromotion │
						 │IsTactical  │
						 │IsCastling  │
						 └────────────┘

		 ┌──────────────────────────────────┐
		 │  CASTLING RIGHTS (4-bit mask)    │
		 ├──────────────────────────────────┤
		 │ WhiteKingSide (bit 0)            │
		 │ WhiteQueenSide (bit 1)           │
		 │ BlackKingSide (bit 2)            │
		 │ BlackQueenSide (bit 3)           │
		 │                                  │
		 │ Exts: CanCastle, Revoke          │
		 │       ToFenString, FromFenString │
		 └──────────────────────────────────┘

		 ┌──────────────────────────────────┐
		 │     GAME STATE (FEN Metadata)    │
		 ├──────────────────────────────────┤
		 │ ActiveColor                      │
		 │ CastlingRights                   │
		 │ EnPassantTarget (or Square(0))   │
		 │ HalfmoveClock (50-move rule)     │
		 │ FullmoveNumber                   │
		 │                                  │
		 │ Exts: FromFenState, ToFenState   │
		 │       IsFiftyMoveRuleDraw        │
		 └──────────────────────────────────┘
```

## Board Representation (Mailbox 0x64)

```
			  Files (Columns)
			  ┌─────────────────────────────────┐
			  │ a   b   c   d   e   f   g   h   │
			┌─┼─────────────────────────────────┤
			8 │ 56  57  58  59  60  61  62  63  │  ◀─ Black's 1st Rank
			7 │ 48  49  50  51  52  53  54  55  │
			6 │ 40  41  42  43  44  45  46  47  │
			5 │ 32  33  34  35  36  37  38  39  │
			4 │ 24  25  26  27  28  29  30  31  │
			3 │ 16  17  18  19  20  21  22  23  │
			2 │ 8   9   10  11  12  13  14  15  │
			1 │ 0   1   2   3   4   5   6   7   │  ◀─ White's 1st Rank
			▲ └─────────────────────────────────┤
		  Ranks │
		 (Rows)  └─ e4 is at index 28 (file 4, rank 3)

Index = (Rank * 8) + File
File  = Index % 8
Rank  = Index / 8

Example: e4 
  File = 4 (a=0, b=1, c=2, d=3, e=4, f=5, g=6, h=7)
  Rank = 3 (1=0, 2=1, 3=2, 4=3)
  Index = (3 * 8) + 4 = 28
```

## Starting Position Layout

```
Black's Perspective (if board is flipped):
		┌───┬───┬───┬───┬───┬───┬───┬───┐
		│ r │ n │ b │ q │ k │ b │ n │ r │  (Rank 8)
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ p │ p │ p │ p │ p │ p │ p │ p │  (Rank 7)
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ . │ . │ . │ . │ . │ . │ . │ . │  (Rank 6)
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ . │ . │ . │ . │ . │ . │ . │ . │  (Rank 5)
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ . │ . │ . │ . │ . │ . │ . │ . │  (Rank 4)
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ . │ . │ . │ . │ . │ . │ . │ . │  (Rank 3)
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ P │ P │ P │ P │ P │ P │ P │ P │  (Rank 2)
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ R │ N │ B │ Q │ K │ B │ N │ R │  (Rank 1)
		└───┴───┴───┴───┴───┴───┴───┴───┘
		  h   g   f   e   d   c   b   a

Standard Notation (White's Perspective):
		┌───┬───┬───┬───┬───┬───┬───┬───┐
		│ r │ n │ b │ q │ k │ b │ n │ r │  (Rank 8) - Indices 56-63
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ p │ p │ p │ p │ p │ p │ p │ p │  (Rank 7) - Indices 48-55
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ . │ . │ . │ . │ . │ . │ . │ . │  (Rank 6) - Indices 40-47
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ . │ . │ . │ . │ . │ . │ . │ . │  (Rank 5) - Indices 32-39
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ . │ . │ . │ . │ . │ . │ . │ . │  (Rank 4) - Indices 24-31
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ . │ . │ . │ . │ . │ . │ . │ . │  (Rank 3) - Indices 16-23
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ P │ P │ P │ P │ P │ P │ P │ P │  (Rank 2) - Indices 8-15
		├───┼───┼───┼───┼───┼───┼───┼───┤
		│ R │ N │ B │ Q │ K │ B │ N │ R │  (Rank 1) - Indices 0-7
		└───┴───┴───┴───┴───┴───┴───┴───┘
		  a   b   c   d   e   f   g   h

Indices Legend:
- Indices 0-7 = a1-h1 (White's back rank)
- Indices 56-63 = a8-h8 (Black's back rank)
```

## Data Flow: Making a Move

```
User Interface Input (Chess UI)
		   │
		   ▼
Move.FromAlgebraic("e2e4")  ◀─ Parse user input
		   │
		   ▼
ChessEngine.GetLegalMoves()  ◀─ Validate move exists
		   │                   in legal move list
		   ▼
	   Move is legal?
		   │
		┌──┴──┐
		│     │
	   No     Yes
		│      │
		│      ▼
		│  ChessEngine.MakeMove(move)
		│      │
		│      ├─ Store captured piece (if any)
		│      ├─ Store current GameState
		│      ├─ Update Board position
		│      ├─ Update GameState (turn, castling, ep, clocks)
		│      ├─ Push to history stack
		│      │
		│      ▼
		└─▶ Return to UI (illegal or made)
			   │
			   ▼
		Update board display
			   │
			   ▼
		(Optionally) Call engine.FindBestMove()
```

## Data Flow: Undoing a Move

```
User clicks "Undo"
		   │
		   ▼
ChessEngine.UndoMove()
		   │
	┌──────┴──────┐
	│             │
 History       History
 empty?        not empty?
	│             │
   No            Yes
	│             │
	▼             ▼
 Return   Pop (capturedPiece, prevGameState)
 error       │
			 ├─ Restore piece to board (if captured)
			 ├─ Restore GameState
			 │
			 ▼
		Return to UI
			 │
			 ▼
		Update display
```

## Evaluation Pipeline

```
Position (Board + GameState)
		   │
		   ▼
   ChessEngine.Evaluate()
		   │
	┌──────┴──────────────────┐
	│                         │
	▼                         ▼
Material         Positional Scores
Balance          ├─ Piece-Square Tables
├─ Count pieces  ├─ Pawn structure
└─ Calculate cp  ├─ King safety
				 └─ Endgame scaling
						│
						▼
				  Total Evaluation (cp)
						│
						▼
				EvaluationResult
				├─ Score (int)
				└─ MaterialBalance
				   ├─ WhiteMaterial
				   └─ BlackMaterial
```

## Search Framework Stack (Future)

```
				FindBestMove(settings, cancelToken)
						│
						▼
			Iterative Deepening Loop
						│
			┌───────────┼───────────┐
			│           │           │
		 Depth 1    Depth 2     Depth 3 ... until limit
			│           │           │
			▼           ▼           ▼
		NegamaxAB   NegamaxAB   NegamaxAB
			│           │           │
			├─ Alpha-Beta pruning
			├─ Transposition Table lookup
			├─ Move ordering (PV, MVV-LVA, Killer)
			├─ Quiescence search for captures
			│
			▼
		Static Evaluation (leaf nodes)
			│
	┌───────┴───────┐
	│               │
 Quiet or     Tactical
 checkmate?   position?
	│               │
	▼               ▼
Return          Quiescence
evaluation      Search
					│
					▼
				Return evaluation
					│
					└─▶ Propagate up tree
							│
							▼
					   Select best move
							│
							▼
					   Update PV
							│
							▼
		SearchResult (BestMove, Evaluation, PV, Telemetry)
```

## Zobrist Hashing (For Transposition Table)

```
Position Hash = Piece_Hashes ⊕ SideToMove_Hash ⊕ Castling_Hash ⊕ EP_Hash

For each piece on the board:
  - XOR with piece-specific random 64-bit number
  - Incorporates: piece type, color, square

XOR properties:
  - Make move: XOR out old, XOR in new
  - Undo move: XOR out new, XOR in old (reversible)
  - Same position always produces same hash

Result: O(1) position hashing for TT lookup
```

## Type Usage Example: Making a Move

```csharp
// Input: User clicks e2, then e4
Square fromSquare = Square.FromAlgebraic("e2");  // Index 12
Square toSquare = Square.FromAlgebraic("e4");    // Index 28

// Retrieve pieces
Piece movingPiece = board.GetPiece(fromSquare);  // White Pawn
Piece targetPiece = board.GetPiece(toSquare);    // Empty

// Determine move type
MoveType moveType = targetPiece.IsEmpty 
	? MoveType.Quiet 
	: MoveType.Capture;

// Create move
Move move = new Move(fromSquare, toSquare, moveType);

// Validate legality
IReadOnlyList<Move> legalMoves = engine.GetLegalMoves();
if (!legalMoves.Contains(move))
	return false;  // Illegal move

// Execute
engine.MakeMove(move);

// Result: Board state updated, GameState advanced
// - activeColor switched to Black
// - halfmoveClock incremented (not a capture)
// - fullmoveNumber unchanged (Black hasn't moved yet)
```

## FEN Parsing Example

```
Input FEN: "rnbqkbnr/pppppppp/8/8/4P3/8/PPPP1PPP/RNBQKBNR b KQkq e3 0 2"

Components:
┌─────────────────────────────┬──────┬──────┬──────┬──────┬──────┐
│ Piece Placement             │ Turn │Castl │ EP   │ HM   │ FM   │
├─────────────────────────────┼──────┼──────┼──────┼──────┼──────┤
│rnbqkbnr/pppppppp/8/8/4P3  │  b   │KQkq  │ e3   │  0   │  2   │
│/8/PPPP1PPP/RNBQKBNR        │Black │Both  │Capt. │Clock │Move  │
└─────────────────────────────┴──────┴──────┴──────┴──────┴──────┘
				  │               │      │      │     │      │
		 Board parsing         Turn = Black
							  Castling = All rights
							  EnPassant = e3 (if pawn advanced 2 squares)
							  Halfmove = 0 (last was capture or pawn move)
							  Fullmove = 2 (Black's 2nd move)

Result: Complete Position Loaded
  ├─ Board array initialized with all pieces
  ├─ GameState set (activeColor=Black, castling=KQkq, etc.)
  └─ Ready for move generation and analysis
```

## Perft Execution Tree (Depth 2 Example)

```
Starting Position
		│
		├─ 20 possible first moves (e.g., a3, a4, b3, ...)
		│
		├─ a3 ─┬─ 20 Black responses
		│      ├─ a6, a5, b6, ...
		│      └─ Subtree: ~20 nodes
		│
		├─ a4 ─┬─ 20 Black responses
		│      └─ Subtree: ~20 nodes
		│
		├─ ... (18 more white moves)
		│
		└─ TOTAL = 20 * 20 = 400 nodes ✓
```

---

## Data Structure Memory Footprint

```
Type                Size (bytes)    Notes
─────────────────────────────────────────────
Color               1               (byte enum)
PieceType           1               (byte enum)
Piece               2               (2 bytes: Color + Type)
Square              4               (int, 0-63)
Move                8               (Square + Square + Type + Promotion)
MoveType            1               (byte flags)
CastlingRights      1               (byte bitmask)
GameState           24              (Color + CastlingRights + Square + 2 ints)
Piece[64]           128             (Board array)
Square[2]           8               (King positions cache)

Total for empty Board: ~160 bytes (plus stack)
Legal moves list: 8 bytes + (moves * 8) = ~312 bytes (typical ~35 moves)
```

This architecture achieves zero-allocation move generation and ultra-fast board updates.
