# Engineering Guide

How work is done on this engine. Every group prompt assumes this document has
been read. It changes rarely; the group prompts carry everything specific to the
work at hand.

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

## How work is scheduled

**A group is the unit of work, not a task.** Work every task in the group without
stopping for go-ahead, and report once at the end.

Per task as you go: build, run only that task's own targeted tests, run its
measurement if it has one, apply the accept/reject rule, commit the winner, move
on. The full test suite runs once, when the group is done.

Build after every task even though the suite does not — it costs seconds, and a
compile error discovered six tasks later cannot be attributed to one of them. Run
the task's own targeted tests for the same reason they exist: they prove the
change is not silently a no-op, which is the failure a measurement reports as
"inconclusive" while the code never ran at all.

### Accept and reject, applied without asking

A task whose SPRT accepts H1 is adopted and becomes the baseline for the next
task. A task that is rejected or inconclusive is not adopted, the baseline does
not move, and the next task is measured against the same build. Record which
build every measurement used — a delta is meaningless without its baseline.

### Stop and report immediately if

- the build breaks and one fix attempt does not explain it
- a test that passed before now fails
- a measurement contradicts something the group prompt states as measured fact
- a task needs a change large enough that carrying on would bury it in a group
  report

Those four are worth interrupting for. Nothing else is.

## Working rules

1. **Hot-path code stays allocation-free.** Searcher, MoveOrdering,
   MoveGenerator, Evaluator and Board make/unmake use fixed-size buffers with an
   out count. No `List<Move>`, no LINQ, no per-node closures, no boxing.
   List-returning APIs stay as thin wrappers for tests and external callers.
2. **Every NPS figure is reported with first-move cutoff rate and node counts.**
   NPS alone cannot tell a speedup from a regression in tree quality.
3. **Defect fixes get a regression test that fails first.** Show the failure,
   then the fix.
4. **Deleting a branch of behaviour means deleting all of it:** the flag, the
   dead path, the parameter threading, the tests that only covered it, and the
   README mention. No commented-out code, no "kept for reference".
5. **No drive-by refactors.** Do not reformat or rename code a task does not
   otherwise touch.
6. **English doc comments explaining why**, not what. Hot-path trade-offs
   especially.

## The lesson that generalises

The first passed-pawn weighting measured −4.5 ± 9.8 Elo because the pawn tables
already ramp with advancement, and the term repeated what the table said instead
of adding something new. Halving it turned −4.5 into +18.0.

Every evaluation term sits on top of tapered piece-square tables that already
encode part of the same idea. Before adding one, look up what the tables already
pay for that feature and subtract it. **A term that merely amplifies its table
measures as nothing.**

The same trap catches terms that were fine when written. The development term
stopped being independent the moment kings entered the taper: knights on their
home squares already lose 40 to the table, and the term adds 30 more.

## The measurement rig

    apt-get install -y dotnet-sdk-8.0        # in the Ubuntu 24.04 repositories

The engine has no package references. If restore fails, add a `nuget.config`
with `<packageSources><clear /></packageSources>`.

Reference opponent: Stockfish, from
`github.com/official-stockfish/Stockfish/releases`, asset
`stockfish-ubuntu-x86-64-avx2.tar` (or the Windows equivalent).

**Rating anchor** — what the engine is worth today, in one command:

    ChessBot.MatchRunner --engine <stockfish> --engine-elo <n> --time <ms> \
                         --games <n> --concurrency 1 --quiet \
                         --openings <suite.epd>

**A/B between two of our own builds:** both arms as external UCI processes,
paired openings, SPRT with elo0=0 elo1=5 alpha=beta=0.05.

### Which budget, and how much of the machine to use

Use a **node budget** by default. It is deterministic and immune to CPU
contention: full parallelism gives bit-identical results to concurrency 1, just
faster. Run it at the physical core count.

Use a **time budget** only when the change affects how expensive evaluation or
move ordering is to compute. A node budget hides exactly that cost. A time budget
also measures the scheduler, so run it at half the physical cores or fewer, on a
quiet machine, and never quote NPS from a run with high concurrency — one past
run varied between 367k and 2373k NPS within itself.

Physical cores, not `Environment.ProcessorCount`, which counts hyperthreads.

State the budget type in every report. A node-budget result and a time-budget
result are not comparable.

## Sample sizes

Measured on this project, not borrowed from elsewhere:

| Sample | Resolution |
|---|---|
| 34 games | ±130 Elo |
| 200 games | CI [+3, +86] on a ~+44 effect |
| 4,000 games | ±9.4 Elo |
| ~2,000 games under SPRT | roughly +30 Elo |
| several thousand | +10 Elo |

If a task's effect is plausibly under +10 Elo, say so before spending a day
measuring it, and measure it grouped with its neighbours instead: if the group
wins, split it and attribute; if the group loses, no member wins alone. Ten games
cannot distinguish anything below roughly +200 Elo and should never be quoted as
a result.

Long runs must be resumable. Two measurement attempts in this project have been
lost to a process dying mid-run.

### A caveat on every SPRT point estimate

SPRT stops at the moment the evidence is most extreme, so a point estimate from a
run that hit a bound is biased away from zero, and Elo does not compose
additively in any case. When several adopted changes are summarised, the
cumulative measurement against a frozen baseline is the number to trust — not the
sum of the individual deltas.

### A caveat on the anchor

`UCI_LimitStrength` randomises Stockfish's move choice, so the opponent is not a
fixed quantity. Three runs of the same build once scored 55.8 %, 49.25 % and
43.25 % — roughly 85 Elo of spread on identical code. Treat any anchor change
under about 50 Elo as unmeasured, and use the cumulative A/B against a frozen
baseline for anything finer.

## Reporting format

Per group, covering every task:

- What changed — one line per file.
- What was deleted.
- Build and test status.
- For defect fixes: the failing test, then passing.
- For measured tasks: score, Elo point estimate with confidence interval, SPRT
  verdict and where it stopped, sample size, budget type, and which build the
  measurement was taken against.

Keep it short. No summaries of code that can be read directly.