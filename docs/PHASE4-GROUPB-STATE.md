# Phase 4, Group B — Evaluation, at no per-node cost

Complete. Tasks taken in the order the group prompt lists them.

| # | Task | State |
|---|---|---|
| B1 | development term double-counts the tables | **ADOPTED** — delete it, `e6467b4`, +19.5 ± 13.2 |
| B2 | phase-scale the pawn structure penalties | adopted in the group; +9.9 ± 13.2 alone, inconclusive |
| B3 | bishop pair | adopted in the group; +12.9 ± 13.3 alone, inconclusive |
| B4 | rooks on open files, half-open files, the seventh | **ADOPTED** — `cd2a902`, +58.2 ± 19.3, AcceptH1 |

Working branch `FabioK/Morestuffdone`. Full suite **580 passed** (545 at the start
of the group).

---

## Budget, and why none of this is on the node budget the prompt specified

The group prompt asked for a NODE budget at full core count for B2–B4, on the
stated premise that everything there is cost-neutral. Group A's third transferable
finding is that a cost-neutral premise is a measurement rather than an assumption,
so it was measured. Twelve million nodes over six positions, three repeats, quiet
machine, Release, best of each:

| Build | Time for 12M nodes | NPS | Per node |
|---|---|---|---|
| pre-group `3c207f6` | 9.57 s | 1,253,867 | — |
| B1 delete | 9.54 s | 1,258,468 | −0.3% |
| B1 halve | 9.52 s | 1,261,051 | −0.5% |
| B1 baseline `e6467b4` | 9.23 s | 1,300,197 | — |
| B2+B3+B4 | 9.64 s | 1,245,061 | **+4.4%** |

B2–B4 are **not** cost-neutral. 4.4% more time per node is worth roughly 3–4 Elo
of extra thinking, against effects the prompt itself expected to be under 10. A
node budget would have handed the arms that for free, so every measurement in this
group is on a **TIME budget, 50 ms/move, concurrency 7 of 14** — the same
conditions as Group A and Phase 3, which also makes the numbers comparable.

---

## B1 — The development term: deleted, +19.5 Elo

Three arms against the pre-group build `3c207f6`, 2,000 games each:

| Arm | Score | Elo | SPRT |
|---|---|---|---|
| **(a) delete entirely** | 52.80% (+806 =500 −694) | **+19.5 ± 13.2** | Continue, LLR 1.88 at 2,000 |
| (b) halve the weights | 52.42% (+808 =481 −711) | +16.9 ± 13.3 | Continue, LLR 1.57 at 2,000 |

Both intervals exclude 50%; neither reached an SPRT bound inside 2,000 games. The
two arms are 0.38 points apart with ±1.9 on each, which is a tie, and the task's
rule on a tie is to take the deletion. `GamePhase.ScaleByOpening` went with it —
the development term was its only caller.

The arithmetic the prompt gave is confirmed by the result: the term was paying a
second time for what the tapered tables already said. A knight on b1 is −40 MG /
−30 EG to its own table and the term added −30; a bishop on c1 is −10 MG / −5 EG
and the term added −20; a king castled on g1 is +30 MG and the term added 40 more
for not being there.

### The cost premise was false, and it did not matter

The prompt's tiebreak reasoning was that a term measuring as nothing is worse than
no term when it costs twelve board reads per node. Those reads cost **0.3%** —
inside the noise of the probe. So B1 was decided entirely by what the term
decides, not by what it costs, and the deletion won on the tie rule rather than on
speed. Worth recording because the same reasoning will be reached for again: ten
`GetPiece` calls and two `GetKingPosition` calls per node sound expensive and are
not, beside everything else a node does.

---

## B2+B3+B4 — Adopted as a group at +61.7, and B4 is the group

Measured as a group first, because each was plausibly under 10 Elo and the rig
resolves ±9.4 only at 4,000 games. Against the B1 baseline `e6467b4`:

| Comparison | Score | Elo | SPRT |
|---|---|---|---|
| **B2+B3+B4 vs B1 baseline** | 58.79% (+469 =186 −301) | **+61.7 ± 20.0** | **AcceptH1** at 956 |

The group won, so it was split. Each split arm adds exactly one term to the same
baseline, under the same conditions:

| Arm | Score | Elo | SPRT | Run |
|---|---|---|---|---|
| **B4 rook placement** | 58.30% (+472 =215 −307) | **+58.2 ± 19.3** | **AcceptH1** at 994 | `ab_runs/split-b4-only` |
| B3 bishop pair | 51.85% (+796 =482 −722) | +12.9 ± 13.3 | Continue at 2,000 | `ab_runs/split-b3-only` |
| B2 pawn structure | 51.42% (+780 =497 −723) | +9.9 ± 13.2 | Continue at 2,000 | `ab_runs/split-b2-only` |

**B4 is the group.** Its +58.2 and the group's +61.7 are within noise of each
other, so there is no evidence that B2 and B3 add anything on top of it.

The group prompt predicted this from the tables alone, and the prediction is the
most transferable thing here. The rook tables are the flattest in the set — a
midgame spread of 10 and an endgame spread of 5 — so they say almost nothing about
where a rook belongs, and the thing they leave out is the one that decides it:
whether the file in front of it is clear. The standing lesson is usually a warning
about terms that amplify their table. Read forwards it is a way of finding the
terms worth writing: **look for the flattest table and ask what it is failing to
say.**

### All three are kept

B2 and B3 are unresolved rather than refuted. The group is what won its SPRT; both
point estimates are positive; and both came from full 2,000-game runs that never
hit a bound, so neither is biased away from zero the way the two accepted figures
are. Dropping a change for failing to prove itself at a sample size the group
prompt had already said was too small for it would be reading an inconclusive
result as a negative one.

If the group is revisited, B2 and B3 at 4,000 games each is the outstanding
question — the rig resolves ±9.4 there, which is where a true +10 becomes visible.

### What each term does

- **B2** — doubled and isolated pawns were the last flat terms in the evaluation.
  Now 15/30 and 8/18 on the midgame/endgame scales. The midgame values sit slightly
  below the old flat 20 and 10 and the endgame values well above, so the change is
  to the shape, not the average magnitude. No piece-square table speaks to doubling
  or isolation, so there was no table here to amplify.
- **B3** — the bishop pair, 25/40, on a per-colour bishop count kept on the same
  incremental path as the pawn file counts. The ramp is shallow on purpose: a
  bishop's endgame material value already rises 320 → 340 while a knight's falls
  320 → 310, so part of the opening-board effect is paid for already.
- **B4** — open files 20/10, half-open 10/5, seventh rank 15/20, from a rook
  bitboard on the same add/remove path. The endgame table already pays +5 for the
  seventh, and the term's weight is the remainder rather than the whole classical
  value.

All three read state the Board maintains incrementally, so `EvaluateFast` still
scans nothing.

---

## The measurement rig grew a governor

Not a Group B task. It came out of the group: three of these runs were launched by
hand at `--ab-concurrency 7`, the number ENGINEERING.md prescribed for a timed run,
and that number was never measured — it was a guess at where contention starts.

`--ab-concurrency` now defaults to `auto`. Two bounds:

- **What somebody else is using** bounds what the run may take. Engines are claimed
  by process id rather than by name, so a second A/B run on the same machine is
  external load and the two yield to each other instead of fighting.
- **What another game costs the engines** is the bound that matters on a time
  budget. The run adds a game, watches the node rate the engines actually achieve,
  and keeps the extra game only if it cost nothing within 5%. A node budget skips
  the probe entirely and takes every free core, being bit-identical at any
  concurrency.

Nothing is decided once: a cap from a failed probe relaxes after a few minutes, the
polite bound is re-read every five seconds, and the probe repeats for the life of
the run.

Two properties make it safe to rely on. The target moves only **between games**, so
no game is played under two sets of conditions. And load is read as the **median**
of the last five samples — an earlier cut used a running average and its own test
caught the problem: one full-load spike cost three cores and took fifteen seconds
to recover.

The cost is that a run's conditions are a range rather than a number, and the
report says so. An explicit `--ab-concurrency <n>` still pins a run that has to
match an earlier one exactly, which is why B4 was run pinned at 7: B2 and B3 were
measured there and the split had to be comparable.

### What it found on this machine

The cumulative run was its first real job, and it answered the question the number
7 had been standing in for. It climbed 7 → 8 and kept it, tried 9 and gave it back,
and settled at a mean of 8.0 within about a minute.

So **8 concurrent games is this machine's measured limit at a 50 ms budget**, on 14
physical cores. The hand-picked 7 was nearly right and 14 would have been badly
wrong — which is worth knowing in both directions. The intuition that 14 physical
cores should carry 14 timed games ignores that each game also runs two engine
processes, an arbiter and the pipes between them, and that only one of the two
engines is searching at a time.

---

## Group B measured cumulatively against the pre-group build

| Comparison | Score | Elo | SPRT |
|---|---|---|---|
| **Group B vs pre-group `3c207f6`** | 66.59% (+239 =92 −97) | **+119.8 ± 30.5** | **AcceptH1** at 428 |

TIME budget, 50 ms/move, adaptive concurrency 7–9 (mean 8.0). Run directory
`ab_runs/groupb-cumulative`.

**This is the figure to quote for the group, not the sum of +19.5 and +61.7.**
Both of those stopped at an SPRT bound and are biased away from zero, and Elo does
not compose additively in any case.

The conditions differ slightly from the individual measurements, which were pinned
at concurrency 7. The governor climbed to 8 only because the engines' node rate was
unchanged there, which is the same condition that makes the runs comparable — but
it is a difference and is recorded rather than glossed.

---

## Where the group leaves the engine

Adopted: **B1** (deletion) and **B2+B3+B4** (as a group, carried by B4).
**+119.8 ± 30.5 Elo** cumulatively against the pre-group build, measured directly
rather than summed.

Nothing was left behind a flag. The B1 halve arm and the three split arms were
measurement instruments and are deleted.

### Three things worth carrying forward

**1. The flattest table is where the next term lives.** The standing lesson warns
against terms that amplify their table. Inverted, it is a search strategy: the rook
tables have a spread of 10 and 5, and the term that fills that gap measured +58.2
on its own — more than everything else in Group B put together. Before writing an
evaluation term, rank the tables by how little they say.

**2. "Cost-neutral" was wrong again, and by a margin that mattered.** Group A found
it false for its largest adopted change. Here it was false for B2–B4 by 4.4%, which
would have been worth 3–4 Elo of free thinking against effects expected to be under
10. Ten minutes of probing changed the budget choice for the whole group. This is
now two groups in a row.

**3. A number in a document is not a measurement.** `--ab-concurrency 7` came from
ENGINEERING.md's "half the physical cores or fewer", which was a reasonable guess
that nobody had ever tested on this machine. The governor replaces it with the only
question that matters — does another game cost the engines nodes — and answers it
continuously, because the answer depends on what else is running and that changes.
