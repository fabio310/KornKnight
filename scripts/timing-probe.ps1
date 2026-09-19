#Requires -Version 7.0
<#
.SYNOPSIS
    Measures how long the engine really takes to answer "go", as a GUI sees it.

.DESCRIPTION
    The acceptance harness for the time-control work. For each movetime it issues -Samples
    searches and records the wall clock from the instant "go" is written to the engine's stdin
    to the instant the "bestmove" line is read back from its stdout. That interval is the only
    one a GUI can measure, and it is the one that forfeits games: it includes process scheduling,
    the pipe in both directions, and everything the engine does after it decides to stop.

    The timing loop is compiled C# (Add-Type), not PowerShell. A PowerShell loop reading lines
    adds interpreter jitter of the same order as the effect being measured, and Measure-Command
    per line measures the cmdlet rather than the engine.

    The position is set, and confirmed with isready/readyok, BEFORE the clock starts, so the
    replay of a long move list is not charged to the search. Positions are cycled from a small
    fixed set that includes capture-heavy middlegames, because a quiescence-heavy search is where
    an abort check that is not reached often enough shows up.

    A second, clock-based pass sends "go wtime W btime B winc I binc I" and compares each sample
    against the budget the engine says it allocated. The engine reports that as
    "info string budget <ms>" at the start of every timed search; without it there is nothing to
    compare against.

.PARAMETER Engine
    The engine executable. Defaults to the Release build in this repository.

.PARAMETER MoveTimes
    The movetime values to probe, in milliseconds.

.PARAMETER Samples
    Searches per movetime. 200 is the acceptance criterion's sample size.

.PARAMETER ClockSamples
    Searches for the clock-based pass. 0 skips it.

.PARAMETER ClockGo
    The operands of the clock-based "go".

.PARAMETER MoveOverhead
    When set, sent as "setoption name Move Overhead value <n>" before the first search. When not
    set the engine's own default is in effect, and that is what is measured.

.PARAMETER OutDir
    Where the raw samples (CSV) and the summary (JSON) are written. Defaults to a dated folder
    under runs/timing.

.EXAMPLE
    pwsh scripts/timing-probe.ps1

.EXAMPLE
    pwsh scripts/timing-probe.ps1 -MoveTimes 50,100 -Samples 50 -ClockSamples 0

.OUTPUTS
    Exit code 0 when no sample exceeded its budget, 1 when any did, 2 when the engine failed.
#>
[CmdletBinding()]
param(
    [string]   $Engine       = (Join-Path $PSScriptRoot '..\ChessBot.Uci\bin\Release\net8.0\ChessBot.Uci.exe'),
    # Strings, split below, rather than [int[]]: under "pwsh -File" on a German-locale machine
    # "-MoveTimes 50,100,1000" binds to the single integer 501001000 — the commas are read as
    # thousands separators — and the probe then waits 5.8 days for its first bestmove.
    [string[]] $MoveTimes    = @('50', '100', '500', '1000', '5000'),
    [int]      $Samples      = 200,
    [int]      $ClockSamples = 200,
    [string]   $ClockGo      = 'wtime 60000 btime 60000 winc 600 binc 600',
    [Nullable[int]] $MoveOverhead,
    [string]   $OutDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The CSV and JSON are read by other tools and by people on other machines; on a German Windows
# Export-Csv would otherwise write 100.1497 as "100,1497".
[Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::InvariantCulture

[int[]] $MoveTimes = @($MoveTimes -split '[,\s]+' | Where-Object { $_ } | ForEach-Object { [int] $_ })

if (-not (Test-Path -LiteralPath $Engine -PathType Leaf)) {
    throw "Engine not found: $Engine (build it with: dotnet build ChessBot.Uci -c Release)"
}
$Engine = (Resolve-Path -LiteralPath $Engine).Path

if (-not $OutDir) {
    $OutDir = Join-Path $PSScriptRoot "..\runs\timing\$(Get-Date -Format 'yyyy-MM-dd_HHmmss')"
}
$null = New-Item -ItemType Directory -Force -Path $OutDir
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

if (-not ('KornKnightTimingProbe' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Diagnostics;
using System.IO;

public sealed class KornKnightTimingProbe : IDisposable
{
    private readonly Process _process;
    private readonly StreamWriter _in;
    private readonly StreamReader _out;

    public string IdName { get; private set; }

    public KornKnightTimingProbe(string exe)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput  = true,
            RedirectStandardOutput = true,
            UseShellExecute        = false,
            CreateNoWindow         = true,
        };

        _process = Process.Start(psi);
        _in  = _process.StandardInput;
        _in.AutoFlush = true;
        _out = _process.StandardOutput;

        Send("uci");
        string line;
        while ((line = ReadLine()) != "uciok")
            if (line.StartsWith("id name ", StringComparison.Ordinal)) IdName = line.Substring(8);
    }

    public bool HasExited => _process.HasExited;

    public void Send(string line) => _in.WriteLine(line);

    private string ReadLine()
    {
        string line = _out.ReadLine();
        if (line == null) throw new IOException("the engine closed its output");
        return line;
    }

    public void Sync()
    {
        Send("isready");
        while (ReadLine() != "readyok") { }
    }

    /// <summary>
    /// Writes "go ..." and reads until "bestmove". Returns the elapsed wall clock in
    /// milliseconds, and the budget the engine announced (-1 if it announced none).
    /// </summary>
    public double Go(string operands, int watchdogMs, out int budgetMs, out string bestMove)
    {
        budgetMs = -1;

        // A search that never answers must fail the run, not hang it: killing the engine makes
        // the blocked ReadLine below return null, which surfaces as an IOException.
        using var watchdog = new System.Threading.Timer(
            _ => { try { _process.Kill(true); } catch { } },
            null, watchdogMs, System.Threading.Timeout.Infinite);

        long start = Stopwatch.GetTimestamp();
        _in.WriteLine("go " + operands);

        string line;
        while (true)
        {
            line = ReadLine();
            if (line.StartsWith("bestmove", StringComparison.Ordinal)) break;
            if (line.StartsWith("info string budget ", StringComparison.Ordinal))
                int.TryParse(line.Substring(19), out budgetMs);
        }

        long end = Stopwatch.GetTimestamp();
        bestMove = line;
        return (end - start) * 1000.0 / Stopwatch.Frequency;
    }

    public void Dispose()
    {
        try { Send("quit"); } catch { }
        if (!_process.WaitForExit(3000)) _process.Kill(true);
        _process.Dispose();
    }
}
'@
}

# A mix of opening, quiet middlegame, capture-heavy middlegame and endgame. Kiwipete and the
# perft positions are here because they are the ones where quiescence dominates.
$positions = @(
    'position startpos'
    'position startpos moves e2e4 e7e5 g1f3 b8c6 f1b5 a7a6 b5a4 g8f6 e1g1 f8e7'
    'position fen r3k2r/p1ppqpb1/bn2pnp1/3PN3/1p2P3/2N2Q1p/PPPBBPPP/R3K2R w KQkq - 0 1'
    'position fen r4rk1/1pp1qppp/p1np1n2/2b1p1B1/2B1P1b1/P1NP1N2/1PP1QPPP/R4RK1 w - - 0 10'
    'position fen 8/2p5/3p4/KP5r/1R3p1k/8/4P1P1/8 w - - 0 1'
    'position fen r2q1rk1/pP1p2pp/Q4n2/bbp1p3/Np6/1B3NBn/pPPP1PPP/R3K2R b KQ - 0 1'
    'position fen 2kr3r/pp1q1ppp/2n1b3/3pP3/3P4/2PB1N2/P4PPP/R2Q1RK1 w - - 0 15'
    'position fen 8/8/4k3/3p4/3P4/4K3/8/8 w - - 0 1'
)

function Get-Percentile([double[]] $sorted, [double] $p) {
    # Nearest-rank, so p99 of 200 samples is the 198th value: a real sample, not an interpolation.
    $rank = [Math]::Max(1, [Math]::Ceiling($p / 100.0 * $sorted.Count))
    return $sorted[$rank - 1]
}

function Get-Summary([string] $label, [object[]] $rows) {
    $elapsed = [double[]] ($rows | ForEach-Object { $_.ElapsedMs } | Sort-Object)
    $over    = @($rows | Where-Object { $_.ElapsedMs -gt $_.LimitMs }).Count

    # How far under its limit each sample landed. The acceptance criterion is stated this way
    # round: p99 within 15 ms below X means the 99th-percentile sample is no more than 15 ms
    # early, so the engine is using the time it was given rather than hiding under it.
    $slack   = [double[]] ($rows | ForEach-Object { $_.LimitMs - $_.ElapsedMs } | Sort-Object)

    [pscustomobject] [ordered] @{
        Case       = $label
        N          = $rows.Count
        Limit      = ($rows | Select-Object -First 1).LimitMs
        Min        = [Math]::Round($elapsed[0], 2)
        Mean       = [Math]::Round(($elapsed | Measure-Object -Average).Average, 2)
        P50        = [Math]::Round((Get-Percentile $elapsed 50), 2)
        P99        = [Math]::Round((Get-Percentile $elapsed 99), 2)
        Max        = [Math]::Round($elapsed[-1], 2)
        Over       = $over
        MinSlack   = [Math]::Round($slack[0], 2)
        P99Under   = [Math]::Round(($rows[0].LimitMs - (Get-Percentile $elapsed 99)), 2)
    }
}

$probe = [KornKnightTimingProbe]::new($Engine)
$allRows   = [Collections.Generic.List[object]]::new()
$summaries = [Collections.Generic.List[object]]::new()
$failed    = $false

try {
    Write-Host "engine : $($probe.IdName)" -ForegroundColor Cyan
    if ($null -ne $MoveOverhead) {
        $probe.Send("setoption name Move Overhead value $MoveOverhead")
        Write-Host "overhead: $MoveOverhead ms (set)" -ForegroundColor Cyan
    } else {
        Write-Host 'overhead: engine default' -ForegroundColor Cyan
    }
    $probe.Send('ucinewgame')
    $probe.Sync()

    $cases = @($MoveTimes | ForEach-Object { @{ Label = "movetime $_"; Go = "movetime $_"; Limit = $_; N = $Samples } })
    if ($ClockSamples -gt 0) {
        $cases += @{ Label = "clock ($ClockGo)"; Go = $ClockGo; Limit = $null; N = $ClockSamples }
    }

    foreach ($case in $cases) {
        $rows = [Collections.Generic.List[object]]::new()
        $sw   = [Diagnostics.Stopwatch]::StartNew()

        for ($i = 0; $i -lt $case.N; $i++) {
            $probe.Send($positions[$i % $positions.Count])
            $probe.Sync()

            $budget = 0; $best = ''
            $watchdogMs = if ($null -ne $case.Limit) { 4 * $case.Limit + 10000 } else { 120000 }
            $ms = $probe.Go($case.Go, $watchdogMs, [ref] $budget, [ref] $best)

            # A movetime case is judged against the instruction; a clock case against what the
            # engine said it would spend, which is the only budget a clock implies.
            $limit = if ($null -ne $case.Limit) { $case.Limit } else { $budget }
            if ($null -eq $case.Limit -and $budget -lt 0) {
                throw "The engine did not announce 'info string budget <ms>' for '$($case.Go)'; the clock pass has nothing to compare against."
            }

            $row = [pscustomobject] @{
                Case = $case.Label; Sample = $i; ElapsedMs = $ms; LimitMs = $limit
                EngineBudgetMs = $budget; BestMove = $best
            }
            $rows.Add($row); $allRows.Add($row)

            if ($i % 20 -eq 19) {
                Write-Progress -Activity $case.Label -Status "$($i + 1)/$($case.N)" `
                    -PercentComplete (100 * ($i + 1) / $case.N)
            }
        }

        Write-Progress -Activity $case.Label -Completed
        $summary = Get-Summary $case.Label $rows.ToArray()
        $summaries.Add($summary)
        Write-Host ([string]::Format([Globalization.CultureInfo]::InvariantCulture,
            '{0,-40} done in {1:N0} s, max {2:F1} ms, over {3}',
            $case.Label, $sw.Elapsed.TotalSeconds, $summary.Max, $summary.Over))
    }
} catch {
    Write-Error $_ -ErrorAction Continue
    $failed = $true
} finally {
    $probe.Dispose()
}

$csv = Join-Path $OutDir 'samples.csv'
$allRows | Export-Csv -LiteralPath $csv -NoTypeInformation -Encoding utf8
$summaries | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $OutDir 'summary.json') -Encoding utf8

Write-Host ''
# Rendered invariantly: this table is pasted into commit messages, and on a German Windows the
# default would print 62.73 as "62,73".
$inv = [Globalization.CultureInfo]::InvariantCulture
$summaries | Format-Table Case, N, Limit,
    @{ n = 'Min';      e = { $_.Min.ToString('F1', $inv) } },
    @{ n = 'Mean';     e = { $_.Mean.ToString('F1', $inv) } },
    @{ n = 'P50';      e = { $_.P50.ToString('F1', $inv) } },
    @{ n = 'P99';      e = { $_.P99.ToString('F1', $inv) } },
    @{ n = 'Max';      e = { $_.Max.ToString('F1', $inv) } },
    Over,
    @{ n = 'P99Under'; e = { $_.P99Under.ToString('F1', $inv) } } -AutoSize |
    Out-String -Width 200 | Write-Host
Write-Host "Elapsed = wall clock from writing 'go' to reading 'bestmove', in ms. Over = samples above Limit."
Write-Host "P99Under = Limit - p99 (the acceptance band is 0..15). Raw samples: $csv"

if ($failed) { exit 2 }
if (($summaries | Measure-Object -Property Over -Sum).Sum -gt 0) { exit 1 }
exit 0
