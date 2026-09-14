# Phase 4, Group A — Search techniques that were absent

In progress. Tasks are taken in the order the group prompt lists them, except that
A3 is written on top of A2 because the prompt says A3 multiplies it.

| # | Task | State |
|---|---|---|
| A1 | reverse futility pruning | **ADOPTED** — `9f31e04`, +70.0 ± 21.7 Elo |
| A2 | late move pruning | measuring, time budget |
| A3 | the improving heuristic | written, not measured |
| A4 | history-scaled reductions | not started |
| A5 | deeper futility, depth-proportional margin | not started |
| A6 | razoring | not started |

---

## The budget question, settled by measurement before A2 was measured

The group prompt specified a **node budget at full core count**, on the stated
premise that everything in this group is cost-neutral or nearly so. That premise
is false for late move pruning, so it was measured rather than assumed.

Six positions × 2,000,000 nodes, concurrency 1, quiet machine, Release, .NET 8,
arms alternated, spread under 2% across repeats:

| Build | Time for 12M nodes | Per node, vs A1 |
|---|---|---|
| base `40f7462` | 9.27 s | +19% |
| **A1 `9f31e04`** | **7.78 s** | — |
| A2, threshold only | 10.07 s | **+29%** |
| A2, with the check exemption | 10.47 s | **+35%** |

Only about 4 points of that is the check exemption. The rest is late move pruning
itself: it removes cheap leaf nodes from the tree while leaving the parent's move
generation and ordering in place, so every surviving node carries more overhead.

The mechanism does not matter to the decision. **A node budget hands A2 about a
third more thinking time than it would get on a clock**, which is the one thing
ENGINEERING.md says a node budget hides. So A2 onward are measured on a **time
budget, 50 ms/move, concurrency 7 of 14** — the same conditions as Phase 3, which
also makes those results comparable to the +18.0 and +57.6 already on record.

A1's bias runs the other way: it is 16% *cheaper* per node than the build it beat,
because a reverse-futility cutoff returns before move generation and a node in
check no longer evaluates. Its node-budget result is therefore conservative and
was not re-measured.

The probe script is `nodecost.sh` in the run scratchpad; it drives a published arm
through UCI with a fixed `go nodes` and times the set. It has to read each `go` to
its `bestmove` before sending the next command, because the search runs on a
background thread and the following command would otherwise cancel it.

---

## A1 — Reverse futility pruning: ADOPTED

Depth 7 and below, non-PV, not in check, 75 cp per remaining ply, and never
against a beta inside the mate band.

| Comparison | Result | Score | Elo | SPRT |
|---|---|---|---|---|
| **A1 vs base `40f7462`** | +367 =189 −214 | 59.94% ± 2.99% | **+70.0 ± 21.7** | **AcceptH1** at 770 |

SPRT elo0=0 elo1=5 α=β=0.05, bounds [−2.944, 2.944], LLR 2.968. **NODE budget**,
50,000 nodes/move, concurrency 14 of 14 physical cores, 500-opening paired suite,
colours balanced, .NET 8. Run directory `ab_runs/a1-rfp`.

### Why it sits under UseFutility rather than a switch of its own

Futility pruning and reverse futility pruning make the same unsound bet — that a
static score can stand in for a search — in opposite directions: one asks whether
a move can raise alpha, the other whether the node is so far above beta that the
opponent cannot claw it back in the depth that remains. The only caller that turns
either off is the minimax equivalence gate, and it has to turn off both. A second
flag would have been a second thing to forget.

### The in-check evaluation was being paid for and never read

`staticEval` was computed unconditionally at every node. All three consumers —
reverse futility, null-move and futility — already guarded on `!inCheck`, so at
every check node the call was made and the result discarded. It is now skipped.

Asserted by counting rather than by inspection: with quiescence, the check
extension and the transposition table off, a depth-1 search from a position in
check must ask for exactly one evaluation per legal evasion — the root, being in
check, must ask for none. It was that plus one before.

### Tests

Three in `StaticEvalPruningTests`: the rule fires in an ordinary middlegame (not a
lopsided position — a rule that needs a queen-up position to trigger would hide
one that never fires in a real game), it is off under `PlainAlphaBeta`, and the
evaluation-call count above. The 49-case minimax equivalence gate still passes,
which is the thing the shared switch exists to protect. Full suite: **543 passed**
(540 + 3).

---

## A2 — Late move pruning: measuring

Quiet moves past a `3 + depth²` threshold at non-PV nodes of depth 8 and below are
dropped outright rather than reduced. Committed undecided on `phase4/a2-lmp`
(`4fbf5eb`); not merged.

### Pruning on move count alone loses mates, and a test caught it

`MateIn2_WhiteQueenAndRook_FindsForcedMate` went from a mate score to `cp 1493`,
playing Qf7 instead of Qh7+. In `8/k7/5R2/3K3Q/8/8/8/8 w` the mate is
1.Qh7+ Kb8 2.Rf8#, and the rook move that finishes it is quiet, so it sat in the
pruned tail: nothing in the ordering promotes a quiet move before it has ever cut.

A mate is delivered by a quiet checking move about as often as by a capture, so
the exemption is not a tuning nicety. The engine has no gives-check predicate —
the group prompt lists `GivesCheck` among the techniques verified absent — and the
cheapest correct one is to make the move and ask the check detector, which is a
pattern test plus at most eight short ray walks. That is paid once per pruned move
against a subtree an order of magnitude larger, and it measures at ~4% of node
rate against the 29% the threshold itself costs.

The move loop therefore makes the move before judging it. `_quietsTried` and the
LMR reduction moved below the prune for the reason the futility comment already
gives: a move that was never searched must not be blamed for failing to cut.

### Tests

Two in `StaticEvalPruningTests` — the rule fires in a closed wide position, and it
is off under `PlainAlphaBeta`. The mate above is the regression test, and it is an
existing one that failed first.

---

## A1 + A2 measured cumulatively against the pre-A1 build

| Comparison | Result | Score | Elo | SPRT |
|---|---|---|---|---|
| **A1+A2 vs base `40f7462`** | +261 =85 −112 | 66.27% ± 3.86% | **+117.3 ± 30.1** | **AcceptH1** at 458 |

**TIME budget**, 50 ms/move, concurrency 7 of 14. Run directory
`ab_runs/a1a2-cumulative`.

This is the figure to quote for the group, not the sum of +70.0 and +38.3. Both of
those stopped at an SPRT bound, so both are biased away from zero, and Elo does
not compose additively in any case. The group prompt asked for A1+A2+A3 as a
triple; A3 was rejected, so this is the pair.

---

## A4 — History-scaled reductions: INCONCLUSIVE

| Comparison | Result | Score | Elo | SPRT |
|---|---|---|---|---|
| A4 vs A2 `4fbf5eb` | +388 =265 −347 | 52.05% ± 2.66% | +14.3 ± 18.5 | **Continue** at 1,000 |

**TIME budget**, 50 ms/move, concurrency 7 of 14, LLR 0.663 inside
[−2.944, 2.944]. Run directory `ab_runs/a4-histlmr`. Not adopted; the baseline
stayed at A2.

Inconclusive at 1,000 games, which is the finding — not "worthless". At 52.05%
the LLR was rising at a rate that would have reached the accept bound somewhere
near 4,000–4,500 games, so the sample cap is the reason there is no verdict here,
and this is the arm most worth re-running at 4,000 if the group is revisited.

### The divisor was scaled to the wrong number, and the test caught it

The first attempt divided the history score by 5,000 — a fraction of
`MoveOrdering.HistoryMax`, which is 16,384. That made the rule a near-no-op, and
the targeted test failed on exactly the assertion written to catch it: history
never once *deepened* a reduction.

A histogram of the history score of every LMR-eligible move, two positions at
depth 10, says why:

| Band | closed position | middlegame |
|---|---|---|
| < −250 | 0% | 0% |
| −250 … −100 | 10.3% | 0% |
| −100 … 0 | 65.3% | 44.2% |
| exactly 0 | 14.4% | 43.9% |
| 0 … 250 | 8.4% | 11.2% |
| > 250 | 1.6% | 0.7% |

98% of entries sit inside ±250 and none reach −250. Everything divided by 5,000
truncated to zero.

The entries stay small because of the update, not the ceiling. The bonus is
`min(depth², 1200)` — 9 at depth 3, 100 at depth 10 — and the gravity term only
pulls an entry toward ±16,384 when the updates are consistently signed. A from/to
table shared across piece types is churned by both bonus and malus, so it
random-walks near zero in steps of tens.

The divisor is now **100**, set from that measured range: the ±250 tails reach the
±2 clamp and the mass between −100 and +100 is left alone.

**The lesson generalises, and it is the same one the passed pawns taught in a
different costume.** There the term amplified what the piece-square table already
said; here the scaling was calibrated against a table's nominal ceiling rather
than the range it actually occupies. Both produce a change that measures as
nothing. Before scaling anything by a table, measure what the table actually
holds.
