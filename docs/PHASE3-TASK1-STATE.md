# Phase 3, Task 1 — Reduction and pruning exemptions: RESULT

Measured 2026-09-13. **All three arms inconclusive at 4,000 games each.**
Nothing is merged; `FabioK/SomeMoreStuff` is untouched at `cada4f2`.

## The finding

Any difference between the baseline and either exemption — separately or
together — is smaller than roughly ±9.4 Elo. Three SPRT runs of 4,000 games
each, 12,000 games and about 3¼ hours in total, and not one of them reached a
bound.

| Arm | Change | Result | Score | Elo | LLR | Verdict |
|---|---|---|---|---|---|---|
| A | checks exempt from LMR + futility | +1505 =940 −1555 | 49.38% ± 1.36% | **−4.3 ± 9.4** | −1.482 | Continue |
| B | killers/counter-moves exempt from LMR | +1548 =928 −1524 | 50.30% ± 1.36% | **+2.1 ± 9.4** | −0.090 | Continue |
| A+B | both | +1539 =932 −1529 | 50.12% ± 1.36% | **+0.9 ± 9.4** | −0.352 | Continue |

All three: SPRT elo0=0 elo1=5 α=β=0.05, bounds [−2.944, 2.944], stopped because
they played all 4,000 games. Colour balanced 2000/2000 in every run. TIME budget
50 ms/move, concurrency 7 of 14 physical cores, 500-opening paired suite
(`openings/generated-500.epd`, sha256 81bb81373f40…).

Run directories, resumable and kept: `ab_runs/task1-{a,b,ab}`.
Run IDs `20260913_104421`, `20260913_114658`, `20260913_125528`.

A time budget was used, not a node budget, because arm A adds real per-node work
and a node budget hides exactly that.

## Why this was predictable from the cost probe

12 positions, 500 ms/move, concurrency 1, quiet machine, Release:

| | NPS | Δ | avg depth | first-move cutoff | nodes |
|---|---|---|---|---|---|
| baseline | 1,308,953 | — | 10.42 | 81.8% | 7,811,831 |
| A | 1,294,248 | −1.1% | 10.25 | 82.9% | 7,712,425 |
| B | 1,258,876 | −3.8% | 10.25 | 81.9% | 7,494,089 |
| A+B | 1,271,659 | −2.8% | 10.33 | 82.8% | 7,601,979 |

How often the exemptions fire:

| Arm | Exemption | Fired | Of candidates | Rate |
|---|---|---|---|---|
| A | LMR | 5,054 | 432,901 | 1.17% |
| A | futility | 21,918 | 772,086 | 2.84% |
| B | LMR | 2,911 | 406,040 | 0.72% |

Each change costs ~0.17 ply of depth and alters the treatment of ~1% of
reductions. A heuristic that fires that rarely has to be enormously right on
those cases to show up above ±9.4 Elo, and it is not.

The NPS column must not be read as a cost ranking. Arm B is three comparisons
and one array read per candidate move, yet shows a *larger* NPS drop than arm A,
which does an eight-ray scan. Exempting reductions changes the node mix and NPS
is sensitive to mix — the exact confound the engineering guide warns about.

The laziness in arm A did work as intended: computing the check context only at
a move about to be pruned or reduced held the cost to 1.1% of node rate, against
the 15–20% an eager per-node scan would have charged.

## Recommendation: keep the baseline, adopt neither

Precedent is the Phase 2 LMR schedule task, which was inconclusive at 2,000
games and resolved as "the logarithmic schedule stays because it is the default
and nothing beat it." Same logic. Arm A in particular buys nothing measurable
for ~250 lines of machinery, a 32 KB static table and 1.1% of node rate, with a
point estimate on the wrong side of zero.

**No action is needed to discard**: nothing was merged, so the default state is
already correct. The branches stay in git as the record of what was measured.

If Task 4 adopts bitboards and wants `GivesCheck` pointed at them, it is
recoverable from `19d4448` — verified, tested, and not lost by leaving it
unmerged.

## What was built (recoverable from git, not merged)

| Arm | Branch | Commit |
|---|---|---|
| A | `phase3/task1-a` | `19d4448` |
| B | `phase3/task1-b` | `bf3e0b6` |
| A+B | `phase3/task1-ab` | `c9c8d95` |

Tests green on all four builds: baseline 530, A 536, B 530, A+B 536.
The six extra are `GivesCheckTests`.

### `GivesCheck` (`ChessBot.Engine/Board/MoveGenerator.cs`, arm A)

The task asked for it to be built from the attack data the generator already
computes per node. **It cannot be.** `_enemyAttacks`, `_checkMask` and `_pinned`
are computed around OUR king and decide which of our moves are legal; "does this
move check THEIR king" is the mirror question and needs a scan out of the enemy
king. So it is a second pass — a `CheckContext` snapshot (per-piece-type check
squares plus discovery candidates), a 32 KB `LineThrough[64*64]` table for the
discovered-check test, and a dedicated eight-ray scan for castling, where two
pieces move and the rook's destination is screened by our own king when the rays
are taken.

Contract: defined for every move except promotions and en passant, both of which
change occupancy in ways the snapshot cannot express. The search only ever asks
about quiet moves.

`GivesCheckTests` walks the standard perft trees (startpos d4, Kiwipete d3,
Position3 d5, Position4 d4, Position5 d3, ~1.4M positions) asserting agreement
with a make/unmake `CheckDetector.IsInCheck` probe. The castling case was
confirmed load-bearing: stubbing `CastlingGivesCheck` to `return false` fails the
targeted FEN `8/8/8/8/8/8/8/R3K2k w Q - 0 1` while the tree walks still pass,
which is why that FEN exists.

## Corrections to carry forward

1. **The Phase 2 fingerprint defect did not reproduce.** Phase 2 recorded that
   `EngineArm.Fingerprint` "cannot distinguish two of our own arms — every arm
   reported sha256 b99af8798979". All four arms here hashed distinctly, in both
   the apphost and `ChessBot.Engine.dll`:

       base 1759990cb6a7…   a c7d2c4285dce…   b 408e1f3d07ec…   ab 0dfbd9fb1291…

   The reason is that each arm was built from **its own commit**, and the commit
   is stamped into the apphost's version resource. The defect is real but
   conditional: it bites when two arms share a commit stamp, which is what
   happens if arms are built from one commit with uncommitted edits or flags.
   Building each arm as a real commit sidesteps it. That is a cheaper fix than
   changing the hashing.

2. **The check extension does not offset the reduction.** A reduced checking
   move enters a child that is in check and gets `depth++` — but so does an
   unreduced one, so the reduced line is still R plies shallower. Arm A's premise
   survived that objection; it simply did not pay. The futility exemption is the
   sharper of the two, since a pruned checking move never reaches the extension
   at all.

3. **Exempting every checking quiet move includes spite checks.** That is the
   most likely reason arm A's point estimate is negative rather than positive: it
   buys full depth for checks that go nowhere, at a fixed clock. A SEE-gated or
   otherwise filtered version is a different change and was not measured.

## Cleanup still outstanding

- `C:\Users\Fabio\source\repos\Chess-arms\` — four worktrees (`src-*`), four
  published binaries (`bin/*`), and an untracked throwaway
  `ChessBot.Tests/CostProbe.cs` in each worktree. Remove with `git worktree
  remove` per worktree, then delete the directory.
- `ab_runs/task1-{a,b,ab}` — 12,000 PGNs plus result JSON. Worth keeping as the
  evidence behind the table above; delete if not.
- The three branches, if the recommendation is accepted and the record is not
  wanted.
