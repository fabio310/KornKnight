# KornKnight — rating and timing record

What the engine is worth, and what the release binary does with a clock, measured rather than
estimated. Every row below says which build it measured and how. `matches/` and `runs/` are
gitignored, so this file is the durable record. Add a row whenever the anchor is rerun, and keep
the old rows.

## Rating anchor

The protocol is in the README ("Rating anchor"). KornKnight plays in-process through
`ChessBot.MatchRunner` against Stockfish capped with `UCI_LimitStrength`/`UCI_Elo`: 100 ms per
move, openings from `openings/generated-500.epd` played with both colours, concurrency 1, pinned
to one core at High priority. The Elo figure is `ChessBot.EloEvaluator`'s estimate of the
difference, added to the opponent's `UCI_Elo`. It sits on Stockfish's limiter scale, which is a
repeatable yardstick and not a FIDE or CCRL rating.

| Date | Build | Opponent | Result (W/D/L) | Score | Elo vs opponent | Rating (95% CI) |
|---|---|---|---|---|---|---|
| — | before cada4f2 | UCI_Elo 2100 | — | — | — | ~2058 [2009, 2105] |
| — | cada4f2 | UCI_Elo 2300 | +83 =7 −110 | 43.25% | −47 | ~2253 [2204, 2300] |
| 2026-09-19 | **v1.0.0** (e3acd63) | UCI_Elo 2300 | **+116 =6 −78** | **59.5%** | **+66.8 ± 48** | **~2367 [2319, 2415]** |

v1.0.0 details: 200 games, 32 min (15:51–16:23), LOS 99.7%. As White +64 =2 −34 (65.0%,
+108 ± 71); as Black +52 =4 −44 (54.0%, +28 ± 67). Average depth 12.0 at about 1.65 Mnps on
the pinned core. No illegal or rejected moves.

### Reading the change from 2253 to 2367

- **Where it comes from.** The 51 commits between cada4f2 and the start of the release work.
  Among them: the lockless transposition table, one transposition probe per node instead of two,
  the index mask, the packed 32-bit `Move` and the cheaper `Square`. The release work itself
  (the time-control, protocol and packaging tasks) leaves the search tree bit-identical: bench is
  2,354,841 nodes at depth 11 before and after, and a fixed-node fingerprint over 12 positions
  matches line for line. It cannot be the source of a strength gain.
- **The release work costs this anchor a little time.** The searcher now stops at the hard
  deadline minus a 5 ms return reserve. At 100 ms per move that is 95 ms where it used to be
  100 ms; the soft limit was already 90 ms. If anything that biases this row downward.
- **How far to trust the gap.** The two 95% intervals only just separate (2300 against 2319).
  The README records a run-to-run spread of several score points between anchors of the same
  build, and `UCI_Elo` is randomised, so no two runs are reproducible. Read the result as "at
  least as strong, very probably stronger", not as a measured +114. An SPRT between the two
  commits would settle it.

## Time control — acceptance of v1.0.0

`scripts/timing-probe.ps1` measures from writing "go" to reading "bestmove", as a GUI sees it.
The binary under test was `publish/KornKnight-1.0.0-win-x64.exe` (id `KornKnight
1.0.0+e3acd6351c83`) with default options (Move Overhead 10 ms). There were 200 searches per row,
cycled over 8 positions including capture-heavy ones. The machine was idle (0–1% load). Times are
in ms.

| Case | Limit | Min | Mean | p50 | p99 | Max | Over | Limit − p99 |
|---|---:|---:|---:|---:|---:|---:|---:|---:|
| movetime 50 | 50 | 35.0 | 35.5 | 35.4 | 37.9 | 38.7 | 0 | 12.1 |
| movetime 100 | 100 | 81.9 | 85.2 | 85.3 | 86.0 | 86.0 | 0 | 14.1 |
| movetime 500 | 500 | 442.1 | 481.9 | 485.3 | 486.0 | 486.1 | 0 | 14.0 |
| movetime 1000 | 1000 | 891.3 | 979.4 | 985.3 | 985.9 | 986.0 | 0 | 14.1 |
| movetime 5000 | 5000 | 1056.0 | 4915.2 | 4985.3 | 4986.0 | 4986.0 | 0 | 14.0 |
| wtime/btime 60000, inc 600 | 2599* | 1238.4 | 2561.4 | 2594.3 | 2594.9 | 2595.0 | 0 | 4.1 |

\* The budget the engine announced (`info string budget`). The clock itself was 60 s.

The criterion was zero samples over X and p99 within 15 ms below X. It is met at every movetime:
0 of 1,200 samples were over. The low minimums are searches that legitimately stopped early. Most
declined to start an iteration past the soft deadline, 90% of the budget: 4,492 ms of a 4,990 ms
budget at movetime 5000. The single 1,056 ms sample is the bare king-and-pawn ending, where
iterative deepening simply runs out of depth.

An earlier run of the same binary was cut off in its clock case. It was taken with a game holding
the machine at 55–87% load. Its movetime maxima were 41.0 / 88.2 / 489.4 / 988.0 / 4987.6, with
0 over; its raw samples were not kept.

Before the release work (d4666f3, Move Overhead 30, 100 samples each), 85–88% of samples were
over at every movetime. `go movetime 1000` had p50 1002.1 and max 1018.0.

## What a sample size can resolve

These are figures measured on this project, not rules of thumb.

| Sample | Resolution |
|---|---|
| 34 games | ±130 Elo |
| 200 games | CI [+3, +86] on a ~+44 effect; ±48 Elo at 59.5% (the v1.0.0 anchor) |
| ~2,000 games | roughly +30 Elo under SPRT |
| several thousand | +10 Elo |

Ten games cannot distinguish anything below roughly +200 Elo, and are not a rating.
