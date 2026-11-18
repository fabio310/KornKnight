# Working on KornKnight

Standing rules for changes to this repository.

- **No feature switches in the engine.** It always plays its best known configuration.
  Alternatives are separate builds, compared against each other, and the loser is deleted,
  together with its flag, dead path, parameter threading and tests. A flag that survives a
  decision is a decision that was never made.
- **Hot-path code stays allocation-free.** `Searcher`, `MoveOrdering`, `MoveGenerator`,
  `Evaluator` and `Board` make/unmake use fixed-size `Move[]` buffers with an out count: no
  `List<Move>`, no LINQ, no per-node closures, no boxing. The list-returning APIs stay as thin
  wrappers for tests and external callers.
- **Strength changes are measured, not argued.** Every search or evaluation change goes through
  `scripts/sprt.ps1` before it is adopted; see [`docs/MEASUREMENT.md`](../docs/MEASUREMENT.md).
  Report what was observed, including "inconclusive at n games". Game results outrank telemetry:
  NPS, depth and node counts are proxies.
- **Every NPS figure comes with node counts and first-move cutoff rate.** NPS alone cannot tell a
  speedup from a regression in tree quality.
- **A defect fix gets a regression test that fails first.** Show the failure, then the fix.
- **Green before and after:** `dotnet build ChessBot.sln` and
  `dotnet test ChessBot.Tests --filter "Category!=Slow"`.
- **No drive-by refactors**, and no commented-out code kept "for reference".
- **Doc comments explain why**, not what — hot-path trade-offs especially.
