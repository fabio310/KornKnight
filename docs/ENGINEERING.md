# Engineering Guide

How work is done on this engine. Every phase prompt assumes this document has
been read. It changes rarely; the phase prompts carry everything that is
specific to the work at hand.

## Four standing principles

**The engine has no feature switches.** It plays its best known configuration,
always. Alternatives are separate builds, compared against each other, and the
loser is deleted. A flag that survives a decision is a decision that was never
made.

**Measure trade-offs, not bugs.** A defect gets fixed. A trade-off — where the
better answer is genuinely unknown before the run — gets an SPRT match. Building
an A/B run to prove that a bug is a bug wastes a day.

**Report what you observed.** Never restate a predicted figure as an outcome. If
a run is inconclusive, "inconclusive at n games" is the finding, not a failure.

**Game results outrank telemetry.** First-move cutoff rate, depth and node counts
are proxies. Won games are the objective. When they disagree, the games win.

## Working rules

1. **One task at a time.** Implement, build, run tests, write a short report,
   then STOP and wait for go-ahead. Do not start the next task unprompted, even
   when the phase prompt lists it.
2. **Hot-path code stays allocation-free.** Searcher, MoveOrdering,
   MoveGenerator, Evaluator and Board make/unmake use fixed-size buffers with an
   out count. No `List<Move>`, no LINQ, no per-node closures, no boxing.
   List-returning APIs stay as thin wrappers for tests and external callers.
3. **Every NPS figure is reported with first-move cutoff rate and node counts.**
   NPS alone cannot tell a speedup from a regression in tree quality.
4. **Defect fixes get a regression test that fails first.** Show the failure,
   then the fix.
5. **Deleting a branch of behaviour means deleting all of it:** the flag, the
   dead path, the parameter threading, the tests that only covered it, and the
   README mention. No commented-out code, no "kept for reference".
6. **No drive-by refactors.** Do not reformat or rename code a task does not
   otherwise touch.
7. **Green after every task:** `dotnet build ChessBot.sln` and
   `dotnet test ChessBot.Tests`.
8. **English doc comments explaining why**, not what. Hot-path trade-offs
   especially.

## The measurement rig

    apt-get install -y dotnet-sdk-8.0        # in the Ubuntu 24.04 repositories

The engine has no package references. If restore fails, add a `nuget.config`
with `<packageSources><clear /></packageSources>`.

Reference opponent: Stockfish, from
`github.com/official-stockfish/Stockfish/releases`, asset
`stockfish-ubuntu-x86-64-avx2.tar`. It supports `UCI_LimitStrength` and
`UCI_Elo` from 1320 to 3190.

**Rating anchor** — what the engine is worth today, in one command:

    ChessBot.MatchRunner --engine <stockfish> --engine-elo <n> --time <ms> \
                         --games <n> --concurrency 1 --quiet

**A/B between two of our own builds:** both arms as external UCI processes,
paired openings, SPRT with elo0=0 elo1=5 alpha=beta=0.05, concurrency 1 or
symmetric.

### Which budget, and how much of the machine to use

Use a **node budget** by default. It is deterministic and immune to CPU
contention: concurrency 32 gives bit-identical results to concurrency 1, just
faster. Run it at full parallelism.

Use a **time budget** only when the change affects how expensive evaluation or
move ordering is to compute — the threat term, mobility, SEE pruning. A node
budget hides exactly that cost. A time budget also measures the scheduler, so run
it at half the physical cores or fewer, on a quiet machine, and never quote NPS
from a run with high concurrency: one past run varied between 367k and 2373k NPS
within itself.

Physical cores, not `Environment.ProcessorCount` — that counts hyperthreads.

## Sample sizes

Measured on this project, not borrowed from elsewhere:

| Sample | Resolution |
|---|---|
| 34 games | ±130 Elo (the Stockfish rating anchor) |
| 200 games | CI [+3, +86] on a ~+44 effect |
| ~2,000 games | roughly +30 Elo under SPRT |
| several thousand | +10 Elo |

If a task's effect is plausibly under +10 Elo, say so before spending a day
measuring it. Ten games cannot distinguish anything below roughly +200 Elo and
should never be quoted as a result.

Two measurement attempts in this project have been lost to a process dying
mid-run. Long runs must be resumable.

## Reporting format

Per task:

- What changed — one line per file.
- What was deleted.
- Build and test status.
- For defect fixes: the failing test, then passing.
- For measured tasks: score, Elo point estimate with confidence interval, SPRT
  verdict and where it stopped, sample size.

Keep it short. No summaries of code that can be read directly.
