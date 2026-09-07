# ChessBot

A C# / .NET 8 chess engine with a WPF playing/analysis UI, plus tooling to benchmark it against external UCI engines and turn the results into Elo ratings.

## Projects

The solution (`ChessBot.sln`) contains six projects:

| Project | Type | Purpose |
|---|---|---|
| `ChessBot.Engine` | class library (net8.0) | The chess engine itself: board, move generation, search, evaluation. No external dependencies. |
| `ChessBot.Wpf` | WPF app (net8.0-windows) | Desktop UI to play against the engine or watch it analyze, with a live eval/PV/depth/nodes panel. |
| `ChessBot.Uci` | console app (net8.0) | UCI front end: drives `ChessBot.Engine` over stdin/stdout so the engine can be run by any UCI host (cutechess-cli, Arena, lichess-bot). |
| `ChessBot.MatchRunner` | console app (net8.0) | Plays automated games between `ChessBot.Engine` and an external UCI engine (e.g. Stockfish) for regression/strength testing, writes PGNs, flags cross-engine evaluation disagreements, and (with `--reference-engine`) measures Stockfish-referenced move loss.  |
| `ChessBot.EloEvaluator` | console app (net8.0) | Reads the logs/PGNs produced by `ChessBot.MatchRunner` and computes Elo ratings and comparison reports. |
| `ChessBot.Tests` | xUnit test project (net8.0) | Perft, move generation, FEN, evaluation, search, Zobrist hashing, and tactical/regression tests (~10 test files). |

### ChessBot.Engine

- **Board**: 8x8 mailbox (`Piece[64]`), with per-color piece lists for O(pieceCount) iteration, a preallocated undo-state stack for O(1) make/unmake, and incrementally maintained Zobrist hash + material/PST evaluation state.
- **Move generation** (`Board/MoveGenerator.cs`): generates fully legal moves directly in one pass (no pseudo-legal filtering step), using bitboards internally for checkers/pins/attacked squares. Handles check evasion, pins, castling, en passant (including the discovered-check edge case).
- **Search** (`Search/Searcher.cs`): negamax with alpha-beta pruning, iterative deepening, a transposition table, quiescence search, null-move pruning, late move reductions, aspiration windows, and check extensions. Move ordering via TT move → PV move → MVV-LVA/SEE captures → killer moves → history/counter-move heuristics.
- **Evaluation** (`Evaluation/Evaluator.cs`): material, piece-square tables, pawn structure, hanging-piece/threat detection, development and endgame king centralization; both a full recompute path and an incremental fast path used during search.
- **API**: `ChessEngine` is the public, thread-safe facade — `LoadFen`/`ExportFen`, `GetLegalMoves`, `MakeMove`/`UndoMove`, `FindBestMove`, `Evaluate`, `RunPerft`, `NewGame`. The engine core itself does not speak UCI; `ChessBot.Uci` wraps this API in the protocol, and `ChessBot.MatchRunner` uses UCI only to talk to an *external* opponent engine.

### ChessBot.Uci

A UCI front end over `ChessEngine`, so the engine's strength can be measured by tools this
project does not control.

- **Commands**: `uci`, `isready`, `ucinewgame`, `position startpos|fen [moves ...]`, `go`, `stop`, `quit`.
- **`go` operands**: `wtime`, `btime`, `winc`, `binc`, `movestogo`, `movetime`, `depth`, `nodes`, `infinite`.
- **Time management** (`UciTimeManager`): a move gets `remaining / movestogo` (30 assumed when
  the GUI sends none) plus the increment, capped at half the remaining clock and reduced by a
  fixed move overhead, so a small clock cannot be overdrawn by a large increment.
- **Output**: one `info` line per completed iteration (`depth`, `seldepth`, `score cp|mate`,
  `nodes`, `nps`, `time`, `pv`) and one `bestmove` per `go`, in long algebraic notation.
- The search runs on a background thread so `stop` and `isready` are answered while it is
  running; `stop` returns the best move found so far.

```powershell
# Play the engine against itself in cutechess-cli
cutechess-cli -engine cmd=ChessBot.Uci/bin/Release/net8.0/ChessBot.Uci.exe `
              -engine cmd=ChessBot.Uci/bin/Release/net8.0/ChessBot.Uci.exe `
              -each proto=uci tc=10+0.1 -games 10
```

## Building and running

```powershell
dotnet build ChessBot.sln

dotnet test ChessBot.Tests

dotnet run --project ChessBot.Wpf

dotnet run --project ChessBot.Uci

dotnet run --project ChessBot.MatchRunner -- --engine <path-to-uci-engine> [--time <ms>] [--games <n>] [--pgn-dir <dir>] [--disagreement-threshold <cp>] [--reference-engine <path>]

dotnet run --project ChessBot.EloEvaluator -- --pgn-dir pgns --out-dir elo-reports
```

`Chess.slnx` at the repo root is currently empty and not used for building; use `ChessBot.sln`.

## License

This project uses a custom source-available license.

The source code is publicly visible for personal, educational, and non-commercial use, but the project is not open source.

You may view, clone, and run the project privately, but you may not reuse, redistribute, sell, relicense, or publish modified versions of the code without written permission.

Copyright (c) 2026 Fabio Kornfeld. All rights reserved.
