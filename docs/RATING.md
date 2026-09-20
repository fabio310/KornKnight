# KornKnight — rating and timing record

What the engine is worth, and what the release binary does with a clock, measured rather than
estimated. Every row below says which build it measured and how. `matches/` and `runs/` are
gitignored, so this file is the durable record. Add a row whenever the anchor is rerun, and keep
the old rows.

## Strength phase 1 — cheap search wins (2026-09-20)

Each item was measured by SPRT (elo0=0, elo1=5, alpha=beta=0.05) at 8+0.08 on the UHO book,
change against base as its own binary, several runs in parallel. Elo is from the point of view of
the change; the first figure is at the moment the run was called, the second is fastchess's tally
over all games played.

| Item | Measured against | Verdict | Elo | Games |
|---|---|---|---:|---:|
| P1.1a improving flag, RFP charged one ply less when improving | v1.0.0 + P4.1 | **adopted** | +31.1 → +23.6 | 842 |
| P1.2 internal iterative reduction | P1.1a | **adopted** | +24.4 → +18.2 | 880 |
| P1.4 late move reductions scaled by move history | P1.1a | **adopted** | +33.1 → +26.7 | 522 |
| **The three together** | **v1.0.0 + P4.1** | **adopted** | **+45.7 → +35.6** | **500** |
| P1.1c one more LMR ply when not improving | P1.1b | rejected | −29.5 | 640 |
| P1.3a null-move R scaled by depth and eval | P1.1a | rejected | −23.6 | 700 |
| P1.1b late move pruning count halved when not improving | P1.1a | running | — | — |
| P1.5 aspiration window widening | P1.1a | running | — | — |
| P1.3b null-move cutoff returns the score it found | P1.1a | running | — | — |

The combined row is the one to quote: the three adopted items were each measured alone, and
+35.6 over the pre-phase base is what they are worth together.

Two of the plan's predicted wins measured clearly negative in this engine. Reducing MORE at a
non-improving node (P1.1c) and scaling the null-move reduction by how far the static score sits
above beta (P1.3a) each cost 20-30 Elo, on ~700 games, with their own toggles and tests in place.
P1.3b and P1.3c were first measured on top of P1.3a, which is not a fair test of them; P1.3b is
being re-measured on its own.

How the runs were called, which is a deliberate loosening of the SPRT's own stopping rule:
- **Early call.** After 500 games, a 90% interval entirely above elo1 adopts and one entirely
  below 0 discards, without waiting for the formal bound. Every such call is recorded in
  `early_verdict.json` beside the run, with the games and interval behind it.
- **Cut.** An item still measuring below zero at 2,000 games is dropped rather than proven.
- Both were chosen for throughput over rigour, so a marginal item (+2 or +3 Elo) is discarded
  rather than proven, and an adopted item rests on weaker evidence than alpha=0.05 implies.

Speed-only work in the same phase, node counts identical and 10 interleaved bench rounds each on
an idle machine:

| Item | Node rate | Kept |
|---|---|---|
| P4.1 flatten the four multidimensional tables | +0.8% (1.0006-1.0268) | yes |
| P2.1 every eval weight in one flat tunable vector | +3.3% (1.0237-1.0479) | yes |
| P2.2 pawn hash table | +1.2% (1.0025-1.0252) | yes |
| P4.4 transposition prefetch after MakeMove | +0.26% (0.9835-1.0167) | yes, on mechanism |
| P4.3 AggressiveInlining on 13 small methods | −0.3% (0.9873-1.0089) | **no** |
| P4.2 runtime build flags on the engine project | no effect / −2.6% | **no** |

P4.3 is the rule working: it promised to go if the measurement landed inside the noise, it did,
and it is not on the branch. P4.2 was rejected twice — the properties cannot reach the runtime
from a library, and the one flag that would (`QuickJitForLoops=false`) costs 2.6%.

Timing acceptance re-checked on the merged branch (100 samples each, machine under SPRT load):
0 of 300 samples over budget; p99 11.9 / 13.5 / 13.7 ms under the 50 / 100 / 1000 ms limits. The
contract in the next section still holds.

The rating anchor for this phase (the four Blunders) has not been run yet.

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
