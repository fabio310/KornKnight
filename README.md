# ChessBot

A C# / .NET 8 chess engine with a WPF playing/analysis UI, plus tooling to benchmark it against external UCI engines and turn the results into Elo ratings.

## Projects

The solution (`ChessBot.sln`) contains five projects:

| Project | Type | Purpose |
|---|---|---|
| `ChessBot.Engine` | class library (net8.0) | The chess engine itself: board, move generation, search, evaluation. No external dependencies. |
| `ChessBot.Wpf` | WPF app (net8.0-windows) | Desktop UI to play against the engine or watch it analyze, with a live eval/PV/depth/nodes panel. |
| `ChessBot.MatchRunner` | console app (net8.0) | Plays automated games between `ChessBot.Engine` and an external UCI engine (e.g. Stockfish) for regression/strength testing, writes PGNs and flags blunders. |
| `ChessBot.EloEvaluator` | console app (net8.0) | Reads the logs/PGNs produced by `ChessBot.MatchRunner` and computes Elo ratings and comparison reports. |
| `ChessBot.Tests` | xUnit test project (net8.0) | Perft, move generation, FEN, evaluation, search, Zobrist hashing, and tactical/regression tests (~10 test files). |

### ChessBot.Engine

- **Board**: 8x8 mailbox (`Piece[64]`), with per-color piece lists for O(pieceCount) iteration, a preallocated undo-state stack for O(1) make/unmake, and incrementally maintained Zobrist hash + material/PST evaluation state.
- **Move generation** (`Board/MoveGenerator.cs`): generates fully legal moves directly in one pass (no pseudo-legal filtering step), using bitboards internally for checkers/pins/attacked squares. Handles check evasion, pins, castling, en passant (including the discovered-check edge case).
- **Search** (`Search/Searcher.cs`): negamax with alpha-beta pruning, iterative deepening, a transposition table, quiescence search, null-move pruning, late move reductions, aspiration windows, and check extensions. Move ordering via TT move → PV move → MVV-LVA/SEE captures → killer moves → history/counter-move heuristics.
- **Evaluation** (`Evaluation/Evaluator.cs`): material, piece-square tables, pawn structure, hanging-piece/threat detection, development and endgame king centralization; both a full recompute path and an incremental fast path used during search.
- **API**: `ChessEngine` is the public, thread-safe facade — `LoadFen`/`ExportFen`, `GetLegalMoves`, `MakeMove`/`UndoMove`, `FindBestMove`, `Evaluate`, `RunPerft`. The engine is driven directly through this C# API; it does not speak the UCI protocol itself (`ChessBot.MatchRunner` only uses UCI to talk to the *external* opponent engine).

## Building and running

```powershell
dotnet build ChessBot.sln

dotnet test ChessBot.Tests

dotnet run --project ChessBot.Wpf

dotnet run --project ChessBot.MatchRunner -- --engine <path-to-uci-engine> [--time <ms>] [--games <n>] [--pgn-dir <dir>] [--blunder <cp>]

dotnet run --project ChessBot.EloEvaluator -- --pgn-dir pgns --out-dir elo-reports
```

`Chess.slnx` at the repo root is currently empty and not used for building; use `ChessBot.sln`.

## License

This project uses a custom source-available license.

The source code is publicly visible for personal, educational, and non-commercial use, but the project is not open source.

You may view, clone, and run the project privately, but you may not reuse, redistribute, sell, relicense, or publish modified versions of the code without written permission.

Copyright (c) 2026 Fabio Kornfeld. All rights reserved.
