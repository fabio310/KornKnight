# KornKnight

A UCI chess engine written from scratch in C# / .NET 8, with a WPF board UI for playing and
analysing, and a harness for measuring its strength against other engines.

Rating: **~2367 Elo** on Stockfish's `UCI_Elo` scale (v1.0.0, 200 games, 95% CI [2319, 2415]).
That is a repeatable yardstick, not a FIDE or CCRL rating — [`docs/RATING.md`](docs/RATING.md)
holds the full record.

## Getting the engine

A release is one self-contained executable per platform: no .NET runtime needed on the machine
that runs it.

```powershell
pwsh scripts/publish.ps1                    # -> publish/KornKnight-<version>-win-x64.exe
pwsh scripts/publish.ps1 -Runtime linux-x64 # -> publish/KornKnight-<version>-linux-x64
```

The script prints the file's SHA-256 and the `id name` the binary reports, which carries the
version and the commit it was built from. A build from a working tree with uncommitted changes is
refused, because its id could not be traced back to a commit.

Point any UCI host at the executable — Arena, Cute Chess, BanksiaGUI, lichess-bot. A lichess-bot
config fragment is in [`docs/lichess-bot/`](docs/lichess-bot/config.fragment.yml).

```powershell
# Engine against itself
cutechess-cli -engine cmd=publish/KornKnight-1.0.0-win-x64.exe `
              -engine cmd=publish/KornKnight-1.0.0-win-x64.exe `
              -each proto=uci tc=10+0.1 -games 10
```

## Building from source

```powershell
dotnet build ChessBot.sln
dotnet test ChessBot.Tests --filter "Category!=Slow"   # fast suite, the inner loop
dotnet test ChessBot.Tests                             # adds the deep perft runs
dotnet run --project ChessBot.Wpf                      # board UI
dotnet run --project ChessBot.Uci                      # UCI on stdin/stdout
```

`Category=Slow` marks the deep perft positions; the eight-billion-node Kiwipete depth 6 is
additionally `Category=VerySlow`, so `--filter "Category!=VerySlow"` runs everything else.

Everything targets `net8.0`. A newer SDK builds it, but running the tests needs the .NET 8
runtime installed; with only a newer runtime present, `DOTNET_ROLL_FORWARD=LatestMajor dotnet test
ChessBot.Tests` runs them on that instead. The release binaries are self-contained and need
neither.

## Projects

| Project | Type | Purpose |
|---|---|---|
| `ChessBot.Engine` | class library | The engine: board, move generation, search, evaluation. No external dependencies. |
| `ChessBot.Uci` | console app | UCI front end over the engine, and the binary a release ships. |
| `ChessBot.Wpf` | WPF app (net8.0-windows) | Play against the engine or watch it analyse, with a live eval/PV/depth/nodes panel. |
| `ChessBot.MatchRunner` | console app | Plays automated matches against an external UCI engine, and A/B matches between two of our own builds under SPRT. |
| `ChessBot.EloEvaluator` | console app | Turns the harness's PGNs and logs into Elo ratings and comparison reports. |
| `ChessBot.Tests` | xUnit | Perft, move generation, FEN, evaluation, search, UCI protocol, Zobrist hashing, tactical and regression tests. |

## How the engine works

- **Board** — 8x8 mailbox (`Piece[64]`) with per-colour piece lists, a preallocated undo stack for
  O(1) make/unmake, and incrementally maintained Zobrist hash and evaluation state.
- **Move generation** (`Board/MoveGenerator.cs`) — fully legal moves in one pass, no pseudo-legal
  filtering step, with bitboards for checkers, pins and attacked squares. Handles check evasion,
  pins, castling and en passant including the discovered-check case.
- **Search** (`Search/Searcher.cs`) — negamax with alpha-beta, iterative deepening, transposition
  table, quiescence search, null-move pruning, late move reductions, internal iterative reduction,
  aspiration windows and check extensions. Ordering: TT move → PV move → MVV-LVA/SEE captures →
  killers → history, continuation-history and counter-move heuristics.
- **Evaluation** (`Evaluation/Evaluator.cs`) — material, tapered piece-square tables, pawn
  structure, passed pawns, king safety, the bishop pair and rook placement, with a full recompute
  path and an incremental fast path that are tested to agree.
- **API** — `ChessEngine` is the public, thread-safe facade: `LoadFen`/`ExportFen`,
  `GetLegalMoves`, `MakeMove`/`UndoMove`, `FindBestMove`, `Evaluate`, `RunPerft`/`RunPerftDivide`,
  `NewGame`. The engine core does not speak UCI; `ChessBot.Uci` wraps it in the protocol.

Hot-path code (searcher, move ordering, move generation, evaluation, make/unmake) is
allocation-free: fixed-size buffers with an out count, no `List<Move>`, no LINQ, no boxing. The
list-returning APIs are thin wrappers for tests and external callers.

### UCI support

- **Commands** — `uci`, `isready`, `setoption`, `ucinewgame`, `position startpos|fen [moves ...]`,
  `go`, `stop`, `quit`. Unknown commands are ignored; end of input ends the process.
- **Options** — `Hash` (MB, 1–4096, default 64), `Threads` (exactly 1 — the search is
  single-threaded), `Move Overhead` (ms, 0–5000, default 10). No `Ponder`, no `UCI_Chess960`.
- **`go`** — `wtime`, `btime`, `winc`, `binc`, `movestogo`, `movetime`, `depth`, `nodes`,
  `mate`, `infinite`. A `go` with nothing bounding it waits for `stop`.
- **Time** — a move gets `remaining / movestogo` (30 assumed when the host sends none) plus the
  increment, capped at half the remaining clock, with the move overhead taken off. The budget runs
  from the instant `go` arrives and keeps a 5 ms return reserve, so `go movetime X` answers inside
  X as the host measures it. Measured: 0 of 1,200 samples over budget, p99 at least 12 ms inside
  every limit ([`docs/RATING.md`](docs/RATING.md)).
- Search runs on a background thread, so `stop` and `isready` are answered while it is thinking;
  `stop` returns the best move found so far.

### Move generation is proven by perft

`ChessBot.Tests/PerftTests.cs` runs the six standard positions from
[chessprogramming.org](https://www.chessprogramming.org/Perft_Results) to depth 5–6. All counts
are exact, including Kiwipete depth 6 at 8,031,647,685 nodes (103 s) and the start position at
depth 6 (119,060,324 nodes, 2.1 s).

`ChessEngine.RunPerftDivide(depth)` splits a perft by root move, which is how a mismatch is
localised: diff the divide against a reference engine, play the move whose subtree diverges, and
repeat until one position is left.

## Measuring strength

Strength claims here come from games, never from node counts or depth. The rating anchor is one
fixed command so the number can be looked up rather than designed:

```powershell
dotnet run -c Release --project ChessBot.MatchRunner -- `
  --engine <path-to-stockfish> --engine-elo 2300 --time 100 --games 200 `
  --concurrency 1 --quiet --openings openings/generated-500.epd --pgn-dir matches/anchor
```

Every search change is gated by an SPRT between two binaries:

```powershell
pwsh scripts/sprt.ps1 -Base <base.exe> -Change <change.exe>
# exit 0 = adopt the change, 4 = discard it, 5 = inconclusive
```

- [`docs/MEASUREMENT.md`](docs/MEASUREMENT.md) — the harness: openings, A/B runs, SPRT, how much
  of the machine a run may take, and what a given sample size can resolve.
- [`docs/RATING.md`](docs/RATING.md) — every anchor measured, the strength phases, and the
  release's timing acceptance.
- `scripts/` — `publish.ps1` (release build), `sprt.ps1` (A/B gate), `gauntlet.ps1` (fastchess
  gauntlet against fixed opponents), `uci-compliance.ps1` (protocol contract on the shipped
  binary), `timing-probe.ps1` (go-to-bestmove distribution). `tools/` holds the run library the
  scripts share, with its own tests in `tools/tests/`.

## License

Source-available, not open source ([`LICENSE.txt`](LICENSE.txt)). You may view, clone, run and
study the source, and modify it and run your own modified version. You may not sell it, and you
may not publish or redistribute it or a modified version of it. Anything beyond that needs
written permission — ask, and it will often be given.

Copyright (c) 2026 Fabio Kornfeld. All rights reserved.
