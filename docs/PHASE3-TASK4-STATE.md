# Phase 3, Task 4 — The bitboard question, decided with a number

Measured 2026-09-13. **Decision: do not add bitboards. Attack queries do not
dominate.** The instrumentation is preserved on `phase3/task4-probe` (`0969f96`)
and deliberately not merged — the counters sit in the innermost ray-walk loops.

## What an attack query actually costs

12 positions, 500 ms/move, concurrency 1, quiet machine, Release, .NET 10.
5,516,311 nodes over 5,871 ms.

| Source | Calls | Per node | Ray steps | Steps/call | Share of steps |
|---|---|---|---|---|---|
| move generation | 15,133,400 | 2.74 | 106,347,433 | 7.0 | **49.4%** |
| check detection | 5,327,653 | 0.97 | 45,636,181 | 8.6 | 21.2% |
| exchange (SEE) | 4,791,754 | 0.87 | 63,466,826 | 13.2 | 29.5% |
| **evaluation** | **0** | **0.00** | **0** | — | **0%** |
| TOTAL | 25,252,807 | 4.58 | 215,450,440 | 8.5 | 100% |

A "call" is one attack question — one slider's attack fan, one "is this square
attacked", one exchange. A "ray step" is one square walked.

Turning steps into time needs a cost per step, so that was measured too rather
than assumed: the same ray-walking primitive driven over every square of a dense
middlegame, 20,000 rounds.

    ns per ray step      5.73
    attack-query time    1,235 ms of 5,871 ms = 21.0% of search time

That 21.0% is an **upper bound**. The microbenchmark times
`IsSquareAttackedBy`, which does pawn, knight and king pattern tests before it
walks a single ray, and all of that overhead is attributed to ray steps.

Evaluation asks **zero** attack queries today. The threat term was the last one
that did, and deleting it in Phase 2 took the per-node board scan with it.

## The decision, and why the number settles it

Task 4 said: if attack queries dominate, add bitboards as a parallel
representation; if they do not, say so and skip it. **21% is not dominance**, and
two further facts make the case against stronger than the headline number:

1. **Half the cost is out of scope by the task's own instruction.** Move
   generation is 49.4% of all ray steps, and the task says to leave
   `MoveGenerator` alone — it works and is verified to perft 6. Bitboards could
   therefore address at most the other 50.6%, which is **10.7% of search time**.
2. **A bitboard `AttackersTo` is not free.** Replacing ray walks with magic or
   PEXT lookups turns ~10.7% into something smaller but non-zero, so the
   realistic ceiling is perhaps 6–8% of search time — worth single-digit Elo —
   against `ulong[2][6]` maintained in make/unmake/null-move, sliding-attack
   tables, a new invariant test suite, and a large new surface for bugs in the
   one part of the engine that is currently provably correct.

Skipping it is the measurement's answer, not a preference.

## The risk this leaves open, and how to read it later

Task 4 named the risk precisely: *measured on a representation that makes attack
queries expensive, mobility can lose its A/B and be deleted as worthless when it
is not.* That risk is real and is not removed by this decision, so it is written
down here rather than discovered later.

Mobility needs the attack fan of **both** sides' pieces at every node. Move
generation currently computes the fan for the enemy's sliders only, at 2.74
calls and 19.3 ray steps per node. Mobility roughly doubles that, so the estimate
is **+20 to +25 ray steps per node**, taking the total from 39 to roughly 60 —
attack queries would go from 21% of search time to around **32–35%**, and
evaluation would become the largest single consumer of a cost it currently
contributes nothing to.

So when mobility is measured:

- If it **wins**, nothing further is needed.
- If it **loses or is inconclusive**, that result must NOT be recorded as
  "mobility is worthless". It must be recorded as "mobility does not pay **on a
  mailbox representation**, at a measured cost of roughly 13% of search time",
  and the bitboard question reopened with mobility's own attack-query numbers in
  hand. Re-running this instrumentation on the mobility arm is the cheap way to
  get them: the branch is `phase3/task4-probe`.

That is the difference between a measurement and a verdict, and it is the whole
reason the task asked for this instrumentation before the term rather than after.

## How to re-run it

    git checkout phase3/task4-probe      # rebase onto the current tip first
    dotnet test ChessBot.Tests -c Release --filter "FullyQualifiedName~AttackProbe" \
        -l "console;verbosity=detailed"

`PROBE_POSITIONS`, `PROBE_MS` and `PROBE_EPD` override the defaults (12, 500 ms,
the 500-opening suite). Run at concurrency 1 on a quiet machine; it is a timing
measurement.
