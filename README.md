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

A run longer than twice the opening count replays positions, and the runner warns when it will.
Sixteen openings cover 32 games; for a thousand-game run, generate a suite:

```powershell
dotnet run -c Release --project ChessBot.MatchRunner -- `
  --make-openings 500 --openings-out openings/generated-500.epd
```

Those are balanced positions from a seeded random walk, not book lines: what a calibration or a
large A/B run needs is many independent, unbiased starts, and sixteen mainlines replayed sixty
times each are neither. The seed is printed and recorded in the file header, so the suite is
reproducible without keeping the file. For the rating anchor, where the question is how the engine
plays real chess, the built-in mainline set is the better instrument.

### A/B: comparing two builds

`ChessBot.MatchRunner --ab` plays two **engine binaries** against each other. An arm is a binary
plus its UCI options — not a settings flag inside the engine — which is what lets a decision
actually be made: the winner is kept and the loser is deleted, and nothing is left behind to
configure.

The workflow is the point:

```powershell
# 1. Branch, and build the baseline arm from the commit you are comparing against
git switch -c experiment/king-pst
dotnet build -c Release ChessBot.Uci
Copy-Item ChessBot.Uci/bin/Release/net8.0 arms/base -Recurse

# 2. Change exactly one thing, and build that as the other arm
#    (edit the engine, then:)
dotnet build -c Release ChessBot.Uci
Copy-Item ChessBot.Uci/bin/Release/net8.0 arms/change -Recurse

# 3. Run, with SPRT so it stops as soon as the answer is known
dotnet run -c Release --project ChessBot.MatchRunner -- --ab `
  --arm-a arms/base/ChessBot.Uci.exe `
  --arm-b arms/change/ChessBot.Uci.exe `
  --ab-games 4000 --ab-nodes 50000 --sprt --ab-out ab_runs/king-pst

# 4. Keep or discard. Exit code 0 = adopt B, 4 = discard B, 5 = still unknown.
```

Change **one** thing per run. Two changes measured together give one number and no way to tell
which of them earned it.

Each arm reports the commit and build configuration it was built from in its UCI `id name` line,
and the run records both — plus each binary's SHA-256 — so a result is traceable to two commits.
An arm built from a working tree with uncommitted changes is flagged as such, because it is not
traceable to the commit it names.

**SPRT.** `--sprt` tests H0 "B is `--sprt-elo0` stronger" against H1 "B is `--sprt-elo1`
stronger" (default 0 and 5) at `--sprt-alpha`/`--sprt-beta` (default 0.05 each), and stops the
run the moment the log-likelihood ratio crosses a bound. The running LLR is printed after every
game. The test only ever stops at a colour-balanced point: ending mid-pair would leave one arm
having had White more often, and that bias would land in the score. The LLR is the standard
normal approximation over per-game scores and treats games as independent, which
colour-reversed pairs are not quite — that makes it slightly conservative, never over-eager.

**Resumability.** Every finished game is appended to `games.jsonl` and flushed before the next
one starts, so a killed run keeps everything it finished. Re-running the same command against
the same `--ab-out` resumes from there; a truncated final line costs one game, not the file.
Pointing it at a directory holding a *different* run — a rebuilt arm, other openings, another
budget — is refused rather than silently spliced. Two measurement attempts in this project have
already been lost to a process dying mid-run.

A run directory holds `run_state.json` (written once, and what resume checks), `games.jsonl`
(the durable record), one PGN per game, and `ab_report.log` / `ab_result.json`, rewritten after
every game so a killed run still leaves a readable result.

With `--ab-reference-engine`, each finished game is also analysed for per-arm move loss, so a run
can say not only which arm won but how much each of them threw away. Median and 95th percentile
stay per game: an order statistic cannot be combined across games, so the run-level summary
reports the exact mean and counts and leaves the quantiles to the per-game reports.

Openings, budget and concurrency flags work as they do for matches — `--ab --help` lists them.

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
