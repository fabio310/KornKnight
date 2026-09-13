# Phase 3, Task 3 — Evaluation terms, one per commit

In progress. Terms are taken in the order the phase prompt lists them.

| # | Term | State |
|---|---|---|
| 1 | passed pawns | **ADOPTED** — `2df22ad`, +18.0 ± 10.1 Elo |
| 2 | piece mobility | blocked on Task 4 (see below) |
| 3 | bishop pair | not started |
| 4 | rooks on open/half-open files and the seventh | not started |
| 5 | real king safety | not started |

---

## Term 1 — Passed pawns: ADOPTED

Rank-scaled, phase-scaled, with separate bonuses for protected and connected
passers. Merged to `FabioK/SomeMoreStuff` (`1134421` + `2df22ad`).

### Result

| Comparison | Result | Score | Elo | SPRT |
|---|---|---|---|---|
| first weighting vs current build | +1619 =710 −1671 | 49.35% ± 1.41% | −4.5 ± 9.8 | Continue at 4,000 |
| **retuned vs current build** | +1516 =767 −1329 | 52.59% ± 1.44% | **+18.0 ± 10.1** | **AcceptH1** at 3,612 |
| **retuned vs Phase 2 baseline** | +513 =188 −343 | 58.14% ± 2.70% | **+57.1 ± 19.3** | **AcceptH1** at 1,044 |

All runs: SPRT elo0=0 elo1=5 α=β=0.05, TIME budget 50 ms/move, concurrency 7 of
14 physical cores, 500-opening paired suite, colours balanced, .NET 10.
Directories `ab_runs/task3-passers-{delta,tuned,cumulative}`.

### The first weighting failed, and why

It measured −4.5 ± 9.8 over 4,000 games — no effect, point estimate on the wrong
side. The cause was double counting, not the idea.

The pawn piece-square tables already ramp steeply with advancement: **+30** on
the seventh rank in the midgame and **+90** in the endgame, for *every* pawn,
whatever stands in front of it. The first curve added another 100/190 on top, so
a seventh-rank passer scored 100 (material) + 90 (PST) + 190 = **380 cp** in an
endgame, and the term had the same shape as the table it was supposed to
discriminate against.

A passed pawn bonus should pay for being **unopposed**. The tables already pay
for being **advanced**. Halving the curve (endgame 190 → 100 on the seventh)
turned −4.5 into +18.0.

This is worth carrying into the remaining terms: every one of them sits on top
of piece-square tables that already encode part of the same idea, and a term
that merely amplifies its table will measure as nothing.

### Implementation

`ChessBot.Engine/Evaluation/PassedPawns.cs`, plus two pawn bitboards on `Board`.

The per-file pawn counts the board already kept answer "how many pawns on this
file", which is all doubled and isolated need. Passed is a question about ranks
too, so it needs to know where the pawns stand. `_whitePawnBitboard` /
`_blackPawnBitboard` are maintained on the same `AddPieceEval` / `RemovePieceEval`
path as the counts — one bit operation per pawn moved, and they survive
make/undo identically because that path is symmetric.

The same routine serves both evaluation paths: `Evaluate` builds the bitboards
in the scan it already does, `EvaluateFast` reads the Board's. That is what keeps
them numerically identical, and the existing
`EvaluateFast_MatchesEvaluate_AcrossASelfPlayGame` (400 positions) plus
`…_ThroughMakeAndUnmake` cover the invariant rule 4 asks for.

`PassedPawnTests` (10 cases) assert the term is not silently zero: rank scaling,
phase scaling, blockade on the file and on adjacent files, protected, connected,
and colour symmetry. Tests green: 540 (530 + 10).

One test premise of mine was wrong and the code was right — a lone enemy pawn
"behind" the runner is itself a passer on the mirrored rank, so the two bonuses
cancel to zero. The test now blocks that pawn so the assertion means something.

### A caveat on all three numbers

SPRT stops at the moment the evidence is most extreme, so a point estimate from
a run that hit a bound is biased **away from zero**. That is why +57.6 (SEE) and
+18.0 (passers) do not add up to the +57.1 measured cumulatively: none of the
three is an unbiased estimate, and Elo does not compose additively anyway. The
cumulative figure is the one to trust, because it is a direct measurement of the
build against the baseline rather than a sum.

---

## Term 2 — Mobility: blocked on Task 4, deliberately

Task 4 says, in its own words, to instrument attack-query cost **before**
measuring mobility, because mobility is attack computation at every node on a
mailbox board whose attack queries are ray walks. The risk it names is specific:
measured on a representation that makes attack queries expensive, mobility can
lose its A/B and be deleted as worthless when it is not.

So Task 4's instrumentation comes next, then the bitboard decision, then
mobility — not the listed order, because the phase prompt itself overrides it.
