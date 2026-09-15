# Phase 4, Group C — Ordering tables, a check predicate, and what follows

**In progress.** Tasks 1–6 are done; 7–20 are not started. Work stopped after
task 6 at the operator's request, with the two attribution runs allowed to
finish.

| # | Task | State |
|---|---|---|
| 1 | history table keyed piece-to | **done**, `d3506e5` |
| 2 | counter-move table given a colour dimension | **done**, `b4edf27` |
| 3 | re-run history-scaled reductions | **OFF** — task 1's own acceptance gate rejected it |
| 4 | continuation history, one ply | **done**, `4162b73` |
| 5 | a real `GivesCheck` predicate | **done**, `85e66bd` |
| 6 | point late move pruning at it | **done**, `8c29e16`, **+11.7% node rate** |
| 7–20 | — | not started |

Measured: T1+T2 **−4.9 ± 13.2**, T4+T5+T6 **+12.2 ± 13.2**, both inconclusive at
2,000 games. Nothing in this group has reached an SPRT bound, so nothing here is
adopted on a measurement — tasks 1, 2, 5 and 6 are kept as defect fixes and a
cost reduction, and task 4 is kept as unresolved rather than refuted, the way B2
and B3 were.

Working branch `FabioK/Morestuffdone`. Full suite **588 passed** (580 at the
start of the group).

---

## Budget, and why it is a time budget again

ENGINEERING.md takes a node budget by default and a time budget when a change
alters what the search costs to compute. That premise was measured rather than
assumed, for the third group running. Six positions × 2,000,000 nodes, best of
three, quiet machine, Release:

| Build | NPS | vs pre-group |
|---|---|---|
| pre-group `89ebe74` | 1,298,528 | — |
| T1+T2 `b4edf27` | 1,252,272 | **−3.6%** |
| T4 `4162b73` | 1,231,934 | **−5.1%** |
| T5 `85e66bd` | 1,246,931 | −4.0% (inert; within repeat spread of T4) |
| T6 `8c29e16` | **1,375,474** | **+5.9%** |

So the group is not cost-neutral in either direction: tasks 1, 2 and 4 make a
node 5% dearer, and task 6 makes it 12% cheaper than that, leaving the whole
group 5.9% *faster* per node than the build it started from. A node budget would
therefore have charged tasks 1–4 nothing for their cost and given task 6 nothing
for its saving. Everything here is measured on a **time budget, 50 ms/move**.

The probe is now a checked-in test, `ChessBot.Tests/NodeCostProbe.cs`, rather
than a scratch script. It also prints the best move, score and node count per
position, which is how the task 6 finding below was reached.

---

## 1 — The history table: re-keyed, and the shape was not the cause

`int[64,64]` keyed `[from, to]` held two collisions at once: both colours wrote
into the same 4,096 slots, and a rook and a queen arriving on the same square
shared one. Now `int[12,64]` keyed `[colour × 6 + type, to]`.

Two tests fail on the old table and pass on the new: a queen that later stands
on d1 does not inherit a rook's record on d1–d4, and neither does Black inherit
White's.

### The acceptance gate, and what it decided

The task's rule was: re-take the histogram in the same two positions at the same
depth; if it still sits inside ±250, the shape was not the cause and task 3 is
off. It does, so **task 3 is off**. The instrumentation is on branch
`phase4/c-histprobe` and deliberately not merged, the way `phase3/task4-probe`
is; it also adds a warm-table reading, because `NewSearch` halves rather than
clears and a probe that searches one position from a fresh engine measures a
table no real game ever has.

Share of LMR-eligible moves whose history score lies outside ±250:

| | old table, cold | new table, cold | new table, warm |
|---|---|---|---|
| closed position | 0.6% | 1.6% | 2.0% |
| middlegame | 0.4% | 0.3% | 1.0% |

The distribution did not move. 78–90% of LMR-eligible moves sit in the single
band −100…0 whichever table is used, and the median |score| is 6–45 against a
ceiling of 16,384.

**Why, and this is the transferable part.** The probe samples the history score
*at LMR-eligible moves*, which are the late quiet moves — precisely the
population that collects the malus and rarely cuts. Their scores are small and
negative by construction. The table does have a long positive tail (maxima of
908–1,609 were seen), but it belongs to moves that get searched early and are
therefore never LMR-eligible. **The history score at an LMR-eligible move is a
selected sample, not the table's distribution**, and no re-keying changes that.
Scaling a reduction by it was always going to be scaling by a number confined to
a 100-point band.

### It measured as nothing

| Comparison | Score | Elo | SPRT |
|---|---|---|---|
| T1+T2 vs pre-group `89ebe74` | 49.30% (+737 =498 −765) | **−4.9 ± 13.2** | Continue, LLR −0.81 at 2,000 |

TIME budget, 50 ms/move, adaptive concurrency 7–8 (mean 7.6), run directory
`ab_runs/c-tables`. Inconclusive at 2,000 games, with the point estimate on the
wrong side.

That is the finding, not a failure. Both changes are defect fixes — the tables
were answering questions about the wrong pieces — and both are kept on that
basis. But the honest reading is that the defect cost less than the 3.6% node
rate the fix costs, and the group prompt's premise that the ±250 histogram was
"the table shape, not the update" is not supported by either measurement.

---

## 2 — The counter-move table

`Move[64,64]` keyed by the opponent's last move alone is one slot shared by both
sides, so the counter White learned to 1...e5 was offered to Black at 150,000 —
a band above every quiet move — as its own reply. Now `Move[2,64,64]`, filed
under the side that plays the counter. Measured with task 1 above.

---

## 4 — Continuation history

One ply: a quiet move's cutoff record conditioned on the move played
immediately before it, `768 × 768` entries flattened to one dimension, read on
exactly the moves the plain history table is already read on and aged with it.

It exposed a defect the counter-move table has always had and survived. The
context move is read from its **destination** square, since it has been made by
the time it is context — and a null-move child reads its parent's slot in
`_lastMoveAtPly` before the parent's move loop has written anything into it, so
what it finds is a sibling's move from an unrelated subtree. For counter-moves
that was a wrong probe; for a piece-keyed table it is an index of −1. The
null-move search now clears the slot and restores it, which is also the honest
answer: after a null move there is no previous move.

Five tests, one of which drives a real depth-8 search and asserts the table is
non-empty afterwards.

**The killer-removal arm the task asks for has not been run.**

---

## 5 — A real `GivesCheck` predicate

Two questions, each walking at most one ray: does the arriving piece see the
enemy king from where it lands, and does a square the move empties open a line
something was already standing on. The second is the ray scan
`DetectSlidingCheckersAndPins` already runs for our own pins, run from the other
king.

It is not cached per node. `GivesCheck` is called from inside a move loop whose
recursive children generate through the same `MoveGenerator` instance, so
anything cached at the node would be a sibling's data by the second call.
Walking outward from the king fixes the direction from the two squares, which is
what makes the uncached form one ray rather than eight.

Castling is judged by where the rook lands, not the king; en passant empties two
squares, so both are tested for a discovery; a promotion is judged as the piece
it became, with its origin treated as empty, which for a promotion can matter.

### Acceptance

| Corpus | Moves judged | Checks | Disagreements |
|---|---|---|---|
| 400 positions (opening suite walked forward, fixed seed) | 12,607 | 526 | **0** |
| every move at every node of five trees to depth 4 | 6,676,837 | 66,432 | **0** |
| a searcher instrumented to throw on disagreement | 3,000,000 nodes | — | **0** |

Castling, en passant and promotion are too rare in such corpora to rely on, so
each is also named explicitly.

---

## 6 — Late move pruning, pointed at the predicate

Node rate **1,231,934 → 1,375,474, +11.7%**. The group prompt expected about 4%,
which is what Group A measured for the exemption alone against the threshold's
own 29% cost; the rest is the make/unmake pair, which maintains the hash, the
piece lists, the pawn and rook bitboards and the incremental eval on the way in
and undoes all of it on the way out.

### The decisions moved, and the predicate is not the reason

The task's rule was that identical decisions mean the score must not move, and
that if it does, task 5 is wrong. It did move — and task 5 is not wrong. Three
measurements say so, the strongest being the instrumented searcher above.

What moved is tie-breaking. `RemoveFromPieceList` is a swap-removal, so a
make/unmake pair leaves the piece list **permuted**; move generation walks that
list, and the ordering sort is a stable insertion sort, so equal-scoring moves
come out in a different order afterwards. The old form made and unmade every
pruned move and the new one does not, so the two shuffle the list differently.
Verified directly: generate, make and unmake one move, generate again — same
move set, different order.

**This generalises, and it is worth carrying.** "Remove a make/unmake pair and
the tree cannot change" is false on this engine. Any future change that removes
or adds a make/unmake on a path the search takes will move every score slightly,
and that movement is not evidence of a defect. The way to test such a change is
to assert the two predicates agree inside a real search, not to compare scores.

### Measured with task 4

| Comparison | Score | Elo | SPRT |
|---|---|---|---|
| T4+T5+T6 vs T1+T2 `b4edf27` | 51.75% (+780 =510 −710) | **+12.2 ± 13.2** | Continue, LLR 1.08 at 2,000 |

TIME budget, 50 ms/move, run directory `ab_runs/c-conthist`, report kept at
`docs/measurements/groupc-t4t6-conthist.log`. Inconclusive at 2,000 games, with
the point estimate on the right side and the LLR still positive — the arm most
worth re-running at 4,000 when the group resumes.

This bundles three changes and cannot attribute between them: continuation
history (which should cost), the inert predicate, and the node-rate recovery
(which should pay). Splitting it needs two more runs, against a build carrying
task 4 alone and a build carrying tasks 5–6 alone.

---

## Conditions, and one thing to fix in the rig

Both runs above were launched together, on the operator's instruction to keep
two runs in parallel. The governor did **not** split the machine between them:
each reported the same 7–8 concurrent games it reports when running alone, so
the two together oversubscribed 14 physical cores. Each run's own comparison is
unaffected — both arms play every game under that game's conditions — but these
numbers are measured under heavier contention than Group A's and Group B's
pinned runs, and are not directly comparable with them.

The `Conditions` line also prints `adaptive concurrency 0` on a run that ends
before a full sample window, which is how both the calibration run and an
interim report of `c-conthist` were labelled. Cosmetic, but it is the line a
reader trusts to say what the run was.

---

## What is left

Tasks 7–20 are untouched. Task 3 is closed off by task 1's gate. Outstanding
measurements the group prompt asks for and that were not reached: the
killer-removal arm (task 4), both deeper-futility arms (task 7), the
reproducible anchor and a re-anchor (tasks 8 and 9), bishop pair and pawn
structure at 4,000 games each (task 10), mobility (11), king safety (12), the
two time-management arms (13 and 14), and everything from task 15 onward.

### One loose end, unresolved

The full suite was run three times at the end. Twice on a quiet machine it was
**597 passed**. The third run overlapped another full suite run — two `dotnet
test` processes at once, which was an operator mistake rather than anything the
engine did — and reported **1 failed, 596 passed**. That run's output was piped
through `tail -3`, so the failing test's name was not captured and cannot be
recovered.

It is recorded here rather than dropped. The likely reading is a timing-sensitive
test failing under 2x contention — a good part of the suite drives real searches
under `MaxTimeMs` — but that is an inference, not a measurement, and the test has
not been identified. Anyone resuming this group should run the suite twice under
deliberate CPU load and name it.

### How to resume

The rig costs about **56 games per minute** at 50 ms/move on this machine, so a
2,000-game run is roughly 35 minutes and a 4,000-game one roughly 70. That is
measured, from a 60-game calibration and confirmed by both runs above.

Node cost first, every time — that premise has now been wrong in three groups
running, and it was wrong in both directions inside this one:

    dotnet test ChessBot.Tests -c Release --filter "FullyQualifiedName~NodeCostProbe" \
        -l "console;verbosity=detailed"

Arms are published binaries compared against each other:

    dotnet publish ChessBot.Uci -c Release -o <dir>
    dotnet run --project ChessBot.MatchRunner -c Release -- --ab \
        --arm-a <baseline>/ChessBot.Uci.exe --arm-b <change>/ChessBot.Uci.exe \
        --ab-games 2000 --ab-time 50 --sprt --sprt-elo0 0 --sprt-elo1 5 \
        --openings openings/generated-500.epd --ab-out ab_runs/<name>

Task 7 was designed but not written. The two arms are: **(a)** the depth cap
alone, 2 → 6, with the check exemption A5 proved it needs — now
`!_moveGen.GivesCheck(move)` in the futility condition rather than a make/unmake
pair; and **(b)** the margin curve alone, 200/450 flat → 150 + 175 per ply, cap
left at 2 and no exemption, since at depth 2 there is no known defect to fix and
adding one would be a second change.

Task 20 (`Threads` as a UCI option) is not separable from task 19: an option
whose only legal value is 1 is not an option. It should be taken with Lazy SMP
or not at all.
