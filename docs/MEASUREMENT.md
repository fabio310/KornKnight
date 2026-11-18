# Measuring KornKnight

How a strength claim is produced here, and what it is worth. Game results outrank telemetry:
first-move cutoff rate, depth and node counts are proxies, and when they disagree with the games,
the games win. A verdict stays inconclusive unless games or a reference engine adjudicate it.

## Matches against an external engine

```powershell
dotnet run -c Release --project ChessBot.MatchRunner -- --engine <path-to-uci-engine> `
  [--time <ms>] [--nodes <n>] [--games <n>] [--openings <path>] [--pgn-dir <dir>] `
  [--concurrency <n>] [--engine-elo <n>] [--reference-engine <path>] [--quiet]

dotnet run -c Release --project ChessBot.EloEvaluator -- --pgn-dir pgns --out-dir elo-reports
```

With `--reference-engine`, every game is also analysed for move loss against that engine, so a
run can say how much each side threw away and not only who won.

If either engine plays a move the board will not accept, the game is aborted and a full report is
appended to `<pgn-dir>/rejected-moves.txt` and echoed to stderr: the position the mover was given,
the move it answered with, the legal moves it chose from, what it claimed about the move (depth,
score, nodes, time against its budget), and the last 40 protocol lines exchanged with each engine.
Without that, a rejected move looks like an ordinary loss and injects silent forfeits into the
measurement.

## The rating anchor

One command, fixed opponent strength, fixed openings, concurrency 1:

```powershell
dotnet run -c Release --project ChessBot.MatchRunner -- `
  --engine <path-to-stockfish> --engine-elo 2300 --time 100 --games 200 `
  --concurrency 1 --quiet --openings openings/generated-500.epd --pgn-dir matches/anchor
```

The opponent Elo, the time control and the opening suite are fixed parts: change any of them and
the result is not comparable to the previous anchor. `--engine-elo` caps Stockfish through
`UCI_LimitStrength`, a crude limiter that is not FIDE-calibrated and not linear, so a change of
anchor opponent carries uncertainty the confidence interval does not show. An anchor is only
informative while the opponent is close; above about 65% score, re-pitch it.

`--concurrency 1` is what makes the result an anchor rather than an anchor *at some speed*.
Everything needed to reproduce the run — the opponent's Elo, the openings and their SHA-256, the
concurrency, the core count, whether the process was pinned and promoted — is written to
`run_manifest.json` beside the PGNs. Results are recorded in [`RATING.md`](RATING.md).

Run-to-run spread is wider than one interval suggests: three measurements of one build scored
55.8%, 49.25% and 43.25%. Treat a change under about 50 Elo on a single anchor as unmeasured.

## Openings

Games do not all start from the initial position. Against an external engine both sides are
near-deterministic there, and two ten-game runs once produced only four distinct openings between
them — a third of the sample size their confidence intervals were computed from.

The default is a built-in set of sixteen mainline openings, four moves deep, each played once with
each colour and cycled round-robin. `--openings <path>` takes an EPD/FEN list (one position per
line, `id "..."` naming it) or a PGN, from which `--opening-plies` (default 8) plies are replayed;
SAN and long algebraic both work. The schedule is a pure function of the game index, so game *n*
is always the same position with the same colour and colours are balanced at every even prefix —
which is what makes a killed run's partial result usable. `--start-position-only` restores the old
behaviour deliberately.

A run longer than twice the opening count replays positions, and the runner warns when it will.
Sixteen openings cover 32 games; for a long run, generate a suite:

```powershell
dotnet run -c Release --project ChessBot.MatchRunner -- `
  --make-openings 500 --openings-out openings/generated-500.epd
```

Those are balanced positions from a seeded random walk, not book lines: a large A/B run needs many
independent, unbiased starts. The seed is printed and recorded in the file header, so the suite is
reproducible without keeping the file. For the rating anchor, where the question is how the engine
plays real chess, the built-in mainline set is the better instrument. Longer runs use the UHO
book, which is not in the repository.

## A/B between two builds

An arm is a **binary** plus its UCI options, never a feature switch inside the engine. That is
what lets a decision actually be made: the winner is kept, the loser is deleted, and nothing is
left behind to configure. The engine has no feature switches for this reason.

Two harnesses do this, and they are different instruments:

**`scripts/sprt.ps1`** drives fastchess, which computes the SPRT on the pentanomial (game-pair)
model. This is the gate for search changes.

```powershell
pwsh scripts/sprt.ps1 -Base <base.exe> -Change <change.exe>   # 8+0.08, elo0=0, elo1=5, 5%/5%
# exit 0 = adopt, 4 = discard, 5 = inconclusive
```

**`ChessBot.MatchRunner --ab`** is the in-repo harness, with its own SPRT over per-game scores:

```powershell
git switch -c experiment/king-pst
dotnet build -c Release ChessBot.Uci
Copy-Item ChessBot.Uci/bin/Release/net8.0 arms/base -Recurse
# change exactly one thing, then build the other arm the same way into arms/change

dotnet run -c Release --project ChessBot.MatchRunner -- --ab `
  --arm-a arms/base/ChessBot.Uci.exe --arm-b arms/change/ChessBot.Uci.exe `
  --ab-games 4000 --ab-nodes 50000 --sprt --ab-out ab_runs/king-pst
```

Change **one** thing per run. Two changes measured together give one number and no way to tell
which of them earned it.

- Each arm reports the commit and build configuration in its UCI `id name`, and the run records
  both plus each binary's SHA-256, so a result is traceable to two commits. An arm built from a
  dirty working tree is flagged as such.
- `--sprt` tests H0 "B is `--sprt-elo0` stronger" against H1 "B is `--sprt-elo1` stronger"
  (default 0 and 5) at `--sprt-alpha`/`--sprt-beta` (default 0.05), and stops the moment the
  log-likelihood ratio crosses a bound. It only ever stops at a colour-balanced point: ending
  mid-pair would leave one arm with White more often, and that bias would land in the score. The
  LLR uses the normal approximation over per-game scores and treats games as independent, which
  colour-reversed pairs are not quite — that makes it conservative, never over-eager.
- Every finished game is appended to `games.jsonl` and flushed before the next starts, so a killed
  run keeps what it finished and re-running the same command against the same `--ab-out` resumes.
  A truncated final line costs one game, not the file. Pointing it at a directory holding a
  different run — rebuilt arm, other openings, another budget — is refused rather than spliced.
- A run directory holds `run_state.json` (written once, and what resume checks), `games.jsonl`,
  one PGN per game, and `ab_report.log` / `ab_result.json`, rewritten after every game so a killed
  run still leaves a readable result.
- `--ab-reference-engine` adds per-arm move loss. Median and 95th percentile stay per game: an
  order statistic cannot be combined across games, so the run-level summary reports exact means
  and counts.

`--ab --help` lists the opening, budget and concurrency flags, which work as they do for matches.

## Which budget, and how much of the machine

The budget decides the concurrency, because the two budgets are not comparable.

A **node budget** is deterministic and immune to CPU contention: concurrency 32 gives
bit-identical results to concurrency 1, just sooner. It defaults to every physical core. Most
questions here — evaluation terms, the LMR schedule, piece-square tables — are node-budget
questions, which is what makes a large machine worth having.

A **time budget** measures the scheduler as much as the engine, so it defaults to half the
physical cores and warns above that. Only the cost questions (a new evaluation term, mobility, SEE
pruning) need one, because a node budget gives an expensive evaluation its cost back for free.
Those need a quiet machine, and NPS from a run with high concurrency must never be quoted.

Physical cores are counted from the OS topology, not `Environment.ProcessorCount`, which counts
hyperthreads — two hyperthreads on one core do not run two searches at full speed. There is no
fixed ceiling: a 32-core machine is allowed to be a 32-core machine.

Timed runs also pin each game's worker to a core and raise the process above Normal. Without it
the scheduler migrates a search between cores mid-game and its transposition and history tables
land in a cold cache: one run's NPS varied between 367k and 2,373k within itself for that reason
alone. `--no-pin` (`--ab-no-pin`) turns both off. What the OS actually granted is recorded in the
manifest, not what was asked for.

Every run records the concurrency, the budget kind and the core count it saw, because they qualify
every timing-derived number in it: concurrent games leave each engine less CPU per move, so the
contest stays fair but the strength measured is strength at that speed.

## What a sample size can resolve

Measured on this project, not borrowed rules of thumb:

| Sample | Resolution |
|---|---|
| 34 games | ±130 Elo |
| 200 games | CI [+3, +86] on a ~+44 effect |
| ~2,000 games | roughly +30 Elo under SPRT |
| several thousand | +10 Elo |

Ten games cannot distinguish anything below roughly +200 Elo and are not a rating. If a change's
effect is plausibly under +10 Elo, say so before spending a day measuring it.

Long runs must be resumable: two measurement attempts in this project have been lost to a process
dying mid-run.

## Generated output is not source

`pgns/`, `matches/`, `runs/`, `ab_reports/`, `elo-reports/`, `elo-sweep/`, `arms/` and `publish/`
are gitignored. A run inside the working tree would otherwise make `git status` dirty, and the run
manifest would then record `HasUncommittedChanges=true` for a source tree that was clean when the
run started. [`RATING.md`](RATING.md) is the durable record of what those runs found.
