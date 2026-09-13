# Phase 3, Task 2 — SEE pruning in quiescence: RESULT

Measured 2026-09-13. **Adopted: skip captures the exchange scores below zero.**
Merged to `FabioK/SomeMoreStuff` as `13ca091` (from `b547eb9`).

## The finding

The first conclusive result of this phase. Quiescence searched every capture on
the board, including ones move ordering had just judged to lose material.

| Arm | Opponent | Result | Score | Elo | SPRT |
|---|---|---|---|---|---|
| SEE ≥ 0 | baseline | +480 =209 −315 | 58.22% ± 2.71% | **+57.6 ± 19.3** | **AcceptH1** at 1,004 games |
| SEE ≥ −100 | baseline | +764 =413 −595 | 54.77% ± 2.03% | **+33.2 ± 14.2** | **AcceptH1** at 1,772 games |
| SEE ≥ −100 | SEE ≥ 0 | +1491 =947 −1562 | 49.11% ± 1.35% | −6.2 ± 9.4 | Continue at 4,000 (inconclusive) |

All runs: SPRT elo0=0 elo1=5 α=β=0.05, bounds [−2.944, 2.944], TIME budget
50 ms/move, concurrency 7 of 14 physical cores, 500-opening paired suite,
colours balanced. Run directories `ab_runs/task2-{see0,see100,headtohead}`.

Both variants beat the baseline conclusively. The head-to-head then failed to
separate them, with the margin's point estimate on the wrong side — so the
margin buys nothing and costs an extra exchange call. The plain rule wins on
parsimony and on node rate.

## Quiescence node share and depth, per arm

12 positions, 500 ms/move, concurrency 1, quiet machine, Release:

| | qnode share | avg depth | max seldepth | NPS | SEE skips |
|---|---|---|---|---|---|
| baseline | **60.8%** | 10.42 | 32 | 1,252,166 | — |
| SEE ≥ 0 | **47.4%** | **10.75** | 35 | 1,266,950 (+1.2%) | 2,208,519 |
| SEE ≥ −100 | **48.6%** | **10.75** | 25 | 1,190,844 (−4.9%) | 1,925,886 |

First-move cutoff rate is unchanged across all three (81.9 / 81.8 / 81.7%), so
tree quality did not degrade — the reclaimed nodes went into depth.

Quiescence fell 13 points of the whole tree and both arms bought +0.33 ply at
the same clock. Contrast Task 1, whose arms *lost* 0.17 ply; that difference in
the probe predicted the difference in the match result before either was played.

The margin variant's −4.9% node rate is the second exchange call it needs. The
plain rule's +1.2% is real: it does no extra SEE work at all and searches a
smaller tree.

## What was implemented

`b547eb9` — two files.

**`MoveOrdering`**: `OrderMoves` now carries each tactical move's SEE verdict
through its insertion sort, exposed as `SeeWinningAt(index)`. The verdict is
**not** recomputed. Move ordering already runs `SeeGe` on every tactical move to
choose its band, and that is the most expensive thing it does — 17.6% of the
engine's node rate. Asking again at a quiescence node would double it exactly
where 54–61% of nodes are, which is more than the pruning could win back.

**`Searcher.QuiescenceSearch`**: a compaction pass over the ordered list drops
losing captures before the move loop starts.

The pass is not a test inside the loop, and that is a correctness point rather
than a style one: the verdicts live in the ordering's shared buffer, and the
first child to order its own moves overwrites them. Reading them inside the loop
returns a grandchild's data from the second move onward. (I wrote it that way
first and caught it before the build.)

Excluded from pruning:
- **in check** — every legal move is an escape, and the alternative to a losing
  capture may be being mated
- **promotions** — the exchange scores the promoted piece, and a promotion that
  looks like it hangs a queen is the move a lost position most often turns on

Tests green: 530 on both arms, unchanged from baseline. The minimax equivalence
gate is unaffected because it runs with `UseQuiescence = false`.

## Environment anomaly during the run

The **.NET 8 runtime disappeared from this machine** between the two
baseline-comparison matches. `dotnet --list-runtimes` showed only
`Microsoft.NETCore.App 10.0.12` plus an orphaned `Microsoft.AspNetCore.App
8.0.31`; every project here targets `net8.0`, and `ChessBot.MatchRunner` failed
to launch with "You must install .NET to run this application".

Worked around with `DOTNET_ROLL_FORWARD=Major` rather than installing a runtime.
The consequence, stated plainly:

- **SEE ≥ 0 vs baseline** ran on **.NET 8**
- **SEE ≥ −100 vs baseline** and the **head-to-head** ran on **.NET 10**

Each match is internally valid — both arms in a match shared a runtime and, for
the two baseline runs, the identical baseline binary — so both AcceptH1 verdicts
stand. The two baseline-comparison Elo figures are not strictly comparable
**to each other** across that boundary, which is exactly why the choice between
the variants was settled by a head-to-head run under a single runtime rather
than by comparing +57.6 against +33.2.

Anything that re-measures on this machine from here runs on .NET 10 unless the
.NET 8 runtime is reinstalled. The next rating anchor should say which runtime
it was taken on.

## Consequence for the rest of the phase

The Phase 2 anchor of ~2253 Elo was taken on the pre-Task-2 build. A +57.6 Elo
change invalidates it as a current figure. The end-of-phase anchor will measure
the whole phase, but Task 3's rule 7 (re-anchor after every second evaluation
term) now has a moved baseline to start from.
