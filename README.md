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
| `ChessBot.Tests` | xUnit test project (net8.0) | Perft, move generation, FEN, evaluation, search, UCI protocol, Zobrist hashing, and tactical/regression tests. Deep perft runs are marked `Category=Slow`. |

### ChessBot.Engine

- **Board**: 8x8 mailbox (`Piece[64]`), with per-color piece lists for O(pieceCount) iteration, a preallocated undo-state stack for O(1) make/unmake, and incrementally maintained Zobrist hash + material/PST evaluation state.
- **Move generation** (`Board/MoveGenerator.cs`): generates fully legal moves directly in one pass (no pseudo-legal filtering step), using bitboards internally for checkers/pins/attacked squares. Handles check evasion, pins, castling, en passant (including the discovered-check edge case).
- **Search** (`Search/Searcher.cs`): negamax with alpha-beta pruning, iterative deepening, a transposition table, quiescence search, null-move pruning, late move reductions, aspiration windows, and check extensions. Move ordering via TT move → PV move → MVV-LVA/SEE captures → killer moves → history/counter-move heuristics.
- **Evaluation** (`Evaluation/Evaluator.cs`): material, piece-square tables, pawn structure, hanging-piece/threat detection, development and endgame king centralization; both a full recompute path and an incremental fast path used during search.
- **API**: `ChessEngine` is the public, thread-safe facade — `LoadFen`/`ExportFen`, `GetLegalMoves`, `MakeMove`/`UndoMove`, `FindBestMove`, `Evaluate`, `RunPerft`/`RunPerftDivide`, `NewGame`. The engine core itself does not speak UCI; `ChessBot.Uci` wraps this API in the protocol, and `ChessBot.MatchRunner` uses UCI only to talk to an *external* opponent engine.

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

### Move-generation correctness (perft)

`ChessBot.Tests/PerftTests.cs` runs the six standard positions from
[chessprogramming.org](https://www.chessprogramming.org/Perft_Results). Depths 1–4 are in the
fast set; depth 5 (and depth 6 for the start position, position 3 and Kiwipete) carry
`[Trait("Category", "Slow")]`, and the eight-billion-node Kiwipete depth 6 additionally carries
`Category=VerySlow`. All counts are exact; the deepest are:

| Position | Depth | Nodes | Release time |
|---|---:|---:|---:|
| Start position | 6 | 119,060,324 | 2.1 s |
| Kiwipete | 5 | 193,690,690 | 2.1 s |
| Kiwipete | 6 | 8,031,647,685 | 103 s |
| Position 3 | 6 | 11,030,083 | 0.3 s |
| Position 4 (and mirrored) | 5 | 15,833,292 | 0.2 s |
| Position 5 | 5 | 89,941,194 | 1.0 s |
| Position 6 | 5 | 164,075,551 | 1.9 s |

`ChessEngine.RunPerftDivide(depth)` splits a perft by root move (`e2e4: 8902`), which is how a
mismatch is localised: diff the divide against a reference engine to find the root move whose
subtree diverges, play it, and repeat until a single position is left.

## Building and running

```powershell
dotnet build ChessBot.sln

dotnet test ChessBot.Tests

# Fast set only — skips the deep perft runs (recommended for the inner loop)
dotnet test ChessBot.Tests --filter "Category!=Slow"

# Everything except the eight-billion-node Kiwipete depth-6 run
dotnet test ChessBot.Tests --filter "Category!=VerySlow"

dotnet run --project ChessBot.Wpf

dotnet run --project ChessBot.Uci

dotnet run --project ChessBot.MatchRunner -- --engine <path-to-uci-engine> [--time <ms>] [--games <n>] [--pgn-dir <dir>] [--disagreement-threshold <cp>] [--reference-engine <path>]

dotnet run --project ChessBot.EloEvaluator -- --pgn-dir pgns --out-dir elo-reports
```

### A/B harness

`ChessBot.MatchRunner --ab-harness` compares two search configurations under identical
conditions, without an external engine. Modes: `partial-root`, `lmr`, `threat-eval`.

```powershell
# Search-shape comparison at a fixed node budget (reproducible; isolates decisions)
dotnet run -c Release --project ChessBot.MatchRunner -- --ab-harness --ab-mode threat-eval `
  --ab-corpus-size 400 --ab-depth 12 --ab-nodes 200000

# Same corpus at a fixed time budget — the only way a cheaper evaluation shows up as depth
dotnet run -c Release --project ChessBot.MatchRunner -- --ab-harness --ab-mode threat-eval `
  --ab-corpus-size 400 --ab-depth 30 --ab-time 500

# Head-to-head games between the two configurations (both colours per opening)
dotnet run -c Release --project ChessBot.MatchRunner -- --ab-harness --ab-mode threat-eval `
  --ab-games 200 --ab-game-ms 100 --ab-concurrency 10
```

Games are independent and run in parallel by default (half the logical processors); the two
colour-reversed games of an opening always run together on one worker. `--ab-concurrency 1`
measures at full machine speed, which matters for timed games: concurrent games leave each
engine less CPU per millisecond.

The same applies to real matches and to the Elo sweep, whose rounds are independent games:

```powershell
dotnet run -c Release --project ChessBot.MatchRunner -- --engine <path> --games 20 --concurrency 8
dotnet run -c Release --project ChessBot.EloEvaluator -- sweep --games-per-round 2 --concurrency 2
```

Matches and sweeps both play in parallel by default: as many games at once as the machine can
usefully take — the match's game count, capped at half the logical processors and at 10. Both
say what they used, because the games are timed and concurrent games leave each engine less CPU
per move: the contest stays fair, but the strength measured is strength at that speed. Pass
`--concurrency 1` to measure at the machine's full speed.

A node budget makes runs reproducible but gives an expensive evaluation its cost back for
free; a time budget charges for it. Both readings are needed, and each report states which
budget produced it. Verdicts stay INCONCLUSIVE unless games or a reference engine
adjudicate — node counts and depth alone never establish a strength change.

`Chess.slnx` at the repo root is currently empty and not used for building; use `ChessBot.sln`.

## License

This project uses a custom source-available license.

The source code is publicly visible for personal, educational, and non-commercial use, but the project is not open source.

You may view, clone, and run the project privately, but you may not reuse, redistribute, sell, relicense, or publish modified versions of the code without written permission.

Copyright (c) 2026 Fabio Kornfeld. All rights reserved.
