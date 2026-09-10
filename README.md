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

dotnet run --project ChessBot.MatchRunner -- --engine <path-to-uci-engine> [--time <ms>] [--games <n>] [--openings <path>] [--pgn-dir <dir>] [--disagreement-threshold <cp>] [--reference-engine <path>]

dotnet run --project ChessBot.EloEvaluator -- --pgn-dir pgns --out-dir elo-reports
```

If either engine ever plays a move the board will not accept, the game is aborted and a full
report is appended to `<pgn-dir>/rejected-moves.txt` and echoed to stderr: the position the mover
was given, the move it answered with, the legal moves it was chosen from, what the engine claimed
about it (depth, score, nodes, time against the budget), and the last 40 protocol lines exchanged
with each engine. A rejected move otherwise looks like an ordinary loss in the summary, so in a
long run it would inject silent forfeits into the measurement.

### Rating anchor — what the engine is worth today

One command, fixed opponent strength and fixed openings, so this is a number to look up rather
than a run to design:

```powershell
dotnet run -c Release --project ChessBot.MatchRunner -- `
  --engine "C:\Tools\stockfish\stockfish-windows-x86-64-avx2.exe" `
  --engine-elo 2100 --time 1000 --games 200 --concurrency 1 --quiet `
  --pgn-dir matches/anchor
```

`--engine-elo 2100` and the built-in sixteen-opening set are the fixed parts: change either and
the result is not comparable to the last anchor. Everything the run needs to be reproduced —
the opponent's Elo, the openings and their SHA-256, the concurrency, the core count, and whether
the process was pinned and promoted — is written to `matches/anchor/run_manifest.json`.

`--concurrency 1` is what makes the number an anchor rather than an anchor *at some speed*; a
larger `--games` narrows the interval, at a cost this table sets:

| Sample | Resolution |
|---|---|
| 34 games | ±130 Elo |
| 200 games | CI [+3, +86] on a ~+44 effect |
| ~2,000 games | roughly +30 Elo under SPRT |
| several thousand | +10 Elo |

Ten games cannot distinguish anything below roughly +200 Elo and are not a rating. These are
figures measured on this project, not borrowed rules of thumb; `docs/ENGINEERING.md` is where
they are maintained.

### Openings

Games no longer all start from the initial position. Against an external engine both sides are
near-deterministic there, so two ten-game runs produced only four distinct openings across ten
games: the runs had a third of the sample size their confidence intervals were computed from.

The default is a built-in set of sixteen mainline openings, four moves deep, each played once
with each colour and cycled round-robin. `--openings <path>` takes an EPD/FEN list (one position
per line, `id "..."` naming it) or a PGN file, from which `--opening-plies` (default 8) plies are
replayed — SAN or long algebraic both work. The schedule is a pure function of the game index, so
game *n* is always the same position with the same colour, and colours are balanced at every even
prefix, which is what makes a killed run's partial result usable. `--start-position-only` restores
the old behaviour deliberately.

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

Games are independent and run in parallel at a fixed degree of parallelism; the two
colour-reversed games of an opening always run together on one slot, so a pair sees the same
machine conditions even on a CPU whose cores differ.

### How much of the machine a run may take

The budget decides it, because the two budgets are not comparable.

A **node budget** is deterministic and immune to CPU contention: concurrency 32 gives
bit-identical results to concurrency 1, just sooner. It defaults to every physical core. A **time
budget** measures the scheduler as much as the engine, so it defaults to half the physical cores
and warns above that. Most A/B questions here — evaluation terms, the LMR schedule, piece-square
tables — are node-budget questions, which is what makes a large machine worth having; only the
cost questions (the threat term, mobility, SEE pruning) need a time budget and a quiet machine.

Physical cores, counted from the OS topology, not `Environment.ProcessorCount`, which counts
hyperthreads — two hyperthreads on one core do not run two searches at full speed. There is no
fixed ceiling: a 32-core machine is allowed to be a 32-core machine.

Timed runs also pin each game's worker to a core and raise the process above Normal. Without it
the scheduler migrates a search between cores mid-game and its transposition and history tables
land in a cold cache: one past run's NPS varied between 367k and 2,373k within itself for that
reason alone. `--no-pin` (`--ab-no-pin` for the A/B harness) turns both off. What the OS actually
granted is recorded in the manifest, not what was asked for.

```powershell
dotnet run -c Release --project ChessBot.MatchRunner -- --engine <path> --games 20 --concurrency 8
dotnet run -c Release --project ChessBot.EloEvaluator -- sweep --games-per-round 2 --concurrency 2
```

Every run records the concurrency, the budget kind and the core count it saw, because they
qualify every timing-derived number in it: concurrent games leave each engine less CPU per move,
so the contest stays fair but the strength measured is strength at that speed. Pass
`--concurrency 1` to measure at the machine's full speed, and never quote NPS from a run with
high concurrency.

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
