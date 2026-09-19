#Requires -Version 7.0
<#
.SYNOPSIS
    Drives the engine over stdin/stdout as a GUI would, and asserts the UCI contract.

.DESCRIPTION
    The unit tests drive UciSession in-process. This drives the shipped binary as a child
    process, which is the only way to catch what an in-process test cannot see: a crash that
    takes the process down, a line that never gets flushed, a search that blocks the command
    loop so "isready" goes unanswered.

    Checked:
      - "uci" answers with id name, id author, the Hash, Threads and Move Overhead options, and
        uciok — and does NOT offer Ponder or UCI_Chess960, which the engine does not implement.
      - "isready" answers readyok, including while a search is running.
      - every form of "go" produces exactly one bestmove, and a legal-looking one.
      - "stop" produces its bestmove promptly.
      - a series of hostile inputs leaves the process alive and answering.

    Every check is independent: a failure is recorded and the run continues, so one run reports
    everything that is wrong rather than the first thing.

.PARAMETER Engine
    The engine executable. Defaults to the Release build in this repository.

.PARAMETER StopLatencyMs
    How long "stop" may take to produce its bestmove before it counts as a failure.

.EXAMPLE
    pwsh scripts/uci-compliance.ps1

.EXAMPLE
    pwsh scripts/uci-compliance.ps1 -Engine publish/win-x64/ChessBot.Uci.exe

.OUTPUTS
    Exit code 0 when every check passed, 1 otherwise.
#>
[CmdletBinding()]
param(
    [string] $Engine        = (Join-Path $PSScriptRoot '..\ChessBot.Uci\bin\Release\net8.0\ChessBot.Uci.exe'),
    [int]    $StopLatencyMs = 100
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not (Test-Path -LiteralPath $Engine -PathType Leaf)) {
    throw "Engine not found: $Engine (build it with: dotnet build ChessBot.Uci -c Release)"
}
$Engine = (Resolve-Path -LiteralPath $Engine).Path

# ── A minimal line-oriented driver ───────────────────────────────────────────
# Compiled rather than scripted. Output is read on a dedicated thread into a queue, so a check
# can wait for a line with a timeout without blocking forever on an engine that never answers —
# which is exactly the failure several of these checks exist to catch. A first version used
# Register-ObjectEvent, whose actions only run when PowerShell pumps its event queue: lines
# arrived late and out of step with the commands that caused them, and every check downstream
# of that measured the driver instead of the engine.

if (-not ('KornKnightUciDriver' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text.RegularExpressions;
using System.Threading;

public sealed class KornKnightUciDriver
{
    public Process Process { get; }
    public List<string> Transcript { get; } = new List<string>();
    private readonly BlockingCollection<string> _lines = new BlockingCollection<string>();

    public KornKnightUciDriver(string exe)
    {
        var psi = new ProcessStartInfo(exe)
        {
            RedirectStandardInput = true, RedirectStandardOutput = true,
            UseShellExecute = false, CreateNoWindow = true,
        };
        Process = Process.Start(psi);
        Process.StandardInput.AutoFlush = true;

        var reader = new Thread(() =>
        {
            string line;
            while ((line = Process.StandardOutput.ReadLine()) != null) _lines.Add(line);
            _lines.CompleteAdding();
        }) { IsBackground = true };
        reader.Start();
    }

    public void Send(string line)
    {
        lock (Transcript) Transcript.Add("> " + line);
        try { Process.StandardInput.WriteLine(line); } catch (System.IO.IOException) { }
    }

    /// <summary>First line matching the pattern within the timeout, or null. Lines read on
    /// the way are appended to <paramref name="seen"/> when it is not null.</summary>
    public string WaitFor(string pattern, int timeoutMs, List<string> seen)
    {
        var sw = Stopwatch.StartNew();
        while (true)
        {
            int left = timeoutMs - (int)sw.ElapsedMilliseconds;
            if (left <= 0) return null;
            string line;
            try { if (!_lines.TryTake(out line, left)) return null; }
            catch (InvalidOperationException) { return null; }   // output closed
            lock (Transcript) Transcript.Add("< " + line);
            if (seen != null) seen.Add(line);
            if (Regex.IsMatch(line, pattern)) return line;
        }
    }

    /// <summary>Whatever arrives within <paramref name="settleMs"/>, without waiting for any
    /// line in particular.</summary>
    public string[] Drain(int settleMs)
    {
        Thread.Sleep(settleMs);
        var output = new List<string>();
        string line;
        while (_lines.TryTake(out line)) { lock (Transcript) Transcript.Add("< " + line); output.Add(line); }
        return output.ToArray();
    }

    public bool IsAlive() { return !Process.HasExited; }

    public bool Sync(int timeoutMs)
    {
        if (!IsAlive()) return false;
        Send("isready");
        return WaitFor("^readyok$", timeoutMs, null) != null;
    }

    public void Close()
    {
        try { if (IsAlive()) Send("quit"); } catch { }
        if (!Process.WaitForExit(3000)) Process.Kill(true);
    }
}
'@
}

$results = [Collections.Generic.List[object]]::new()

function Assert-Check([string] $name, [bool] $ok, [string] $detail = '') {
    $results.Add([pscustomobject] @{ Check = $name; Passed = $ok; Detail = $detail })
    $mark = if ($ok) { 'PASS' } else { 'FAIL' }
    $colour = if ($ok) { 'Green' } else { 'Red' }
    Write-Host ("[{0}] {1}{2}" -f $mark, $name, $(if ($detail) { " — $detail" } else { '' })) -ForegroundColor $colour
}

# Runs one "go" and asserts it yields exactly one bestmove. Extra bestmoves are looked for by
# draining briefly after the first: a second one would be answered by the GUI as if it
# belonged to the next "go".
function Test-Go($e, [string] $position, [string] $go, [int] $timeoutMs, [bool] $needsStop) {
    $e.Send($position)
    $e.Send($go)

    if ($needsStop) {
        $null = $e.WaitFor('^info depth', 5000, $null)
        $e.Send('stop')
    }

    $seen = [Collections.Generic.List[string]]::new()
    $best = $e.WaitFor('^bestmove', $timeoutMs, $seen)
    $extra = @($e.Drain(150) | Where-Object { $_ -like 'bestmove*' })

    $ok = $null -ne $best -and $extra.Count -eq 0 -and $best -match '^bestmove ([a-h][1-8][a-h][1-8][qrbn]?|0000)( |$)'
    $detail = if ($null -eq $best) { "no bestmove within $timeoutMs ms" }
              elseif ($extra.Count) { "$($extra.Count + 1) bestmove lines" }
              else { $best }
    Assert-Check "'$go' yields exactly one bestmove" $ok $detail
}

$e = [KornKnightUciDriver]::new($Engine)
try {
    # ── Handshake ────────────────────────────────────────────────────────────
    $e.Send('uci')
    $handshake = [Collections.Generic.List[string]]::new()
    $uciok = $e.WaitFor('^uciok$', 5000, $handshake)

    Assert-Check 'uci -> uciok' ($null -ne $uciok)
    Assert-Check 'id name is reported' ([bool] ($handshake | Where-Object { $_ -like 'id name *' })) `
        (@($handshake | Where-Object { $_ -like 'id name *' }) -join '')
    Assert-Check 'id author is reported' ([bool] ($handshake | Where-Object { $_ -like 'id author *' }))

    foreach ($option in 'Hash', 'Threads', 'Move Overhead') {
        $line = $handshake | Where-Object { $_ -like "option name $option type *" } | Select-Object -First 1
        Assert-Check "option '$option' is advertised" ($null -ne $line) "$line"
    }

    # Advertising an option is a promise. Ponder needs ponderhit handling and Chess960 needs
    # Chess960 castling, and the engine has neither.
    foreach ($option in 'Ponder', 'UCI_Chess960') {
        $line = $handshake | Where-Object { $_ -like "option name $option *" }
        Assert-Check "option '$option' is NOT advertised" ($null -eq $line)
    }

    Assert-Check 'isready -> readyok' ($e.Sync(5000))

    # ── Every form of go gives exactly one bestmove ──────────────────────────
    $start = 'position startpos moves e2e4 e7e5'
    Test-Go $e $start 'go movetime 100'                                   5000  $false
    Test-Go $e $start 'go depth 5'                                        30000 $false
    Test-Go $e $start 'go nodes 20000'                                    10000 $false
    Test-Go $e $start 'go wtime 10000 btime 10000 winc 100 binc 100'      5000  $false
    Test-Go $e $start 'go wtime 10000 btime 10000 movestogo 20'           5000  $false
    Test-Go $e $start 'go mate 2'                                         30000 $false
    Test-Go $e $start 'go infinite'                                       5000  $true
    Test-Go $e $start 'go'                                                5000  $true

    # ── isready while a search is running ────────────────────────────────────
    $e.Send($start)
    $e.Send('go infinite')
    $null = $e.WaitFor('^info depth', 5000, $null)
    $sw = [Diagnostics.Stopwatch]::StartNew()
    $e.Send('isready')
    $ready = $e.WaitFor('^readyok$', 2000, $null)
    Assert-Check 'isready is answered during a search' ($null -ne $ready) "$($sw.ElapsedMilliseconds) ms"

    # ── stop is prompt ───────────────────────────────────────────────────────
    $sw.Restart()
    $e.Send('stop')
    $best = $e.WaitFor('^bestmove', 5000, $null)
    $stopMs = $sw.ElapsedMilliseconds
    Assert-Check "stop yields bestmove within $StopLatencyMs ms" ($null -ne $best -and $stopMs -le $StopLatencyMs) "$stopMs ms"
    $null = $e.Drain(50)

    # ── Hostile input: every one must leave the process alive and answering ──
    $hostile = [ordered] @{
        'stop with no search running'        = @('stop')
        'go with no preceding position'      = @('ucinewgame', 'go movetime 50')
        'setoption with a missing value'     = @('setoption name Hash value', 'setoption name Hash', 'setoption name', 'setoption')
        'setoption with a non-numeric value' = @('setoption name Hash value lots', 'setoption name Threads value -3')
        'position with garbage moves'        = @('position startpos moves e2e4 zz99 e7e5', 'go depth 2')
        'position with a garbage FEN'        = @('position fen this is not a fen', 'position', 'position banana', 'go depth 2')
        'go depth 0'                         = @('position startpos', 'go depth 0')
        'go nodes 0'                         = @('position startpos', 'go nodes 0')
        'go with an almost empty clock'      = @('position startpos', 'go wtime 1 btime 1')
        'go with negative or junk operands'  = @('position startpos', 'go movetime -5', 'go wtime -100 btime -100', 'go depth banana')
        'a line of pure whitespace'          = @("   `t  ")
        'an unknown command'                 = @('frobnicate the bishop')
        'a Hash change mid-search'           = @('position startpos', 'go infinite', 'setoption name Hash value 16', 'stop')
    }

    foreach ($name in $hostile.Keys) {
        foreach ($line in $hostile[$name]) { $e.Send($line) }

        # Ends anything left searching, so one group cannot leak into the next. Some of these
        # legitimately leave a search running: "go depth banana" drops the malformed operand,
        # which leaves a bare "go", which is "go infinite" and waits for exactly this.
        $e.Send('stop')
        $alive = $e.Sync(10000)
        $null = $e.Drain(100)
        Assert-Check "survives: $name" $alive
    }

    # quit mid-search must end the process, promptly, rather than hang it.
    $e.Send('position startpos')
    $e.Send('go infinite')
    $null = $e.WaitFor('^info depth', 5000, $null)
    $sw.Restart()
    $e.Send('quit')
    $exited = $e.Process.WaitForExit(3000)
    Assert-Check 'quit mid-search exits the process' $exited "$($sw.ElapsedMilliseconds) ms"
} finally {
    $e.Close()
}

# Input that ends without "quit" — a host that crashes, or a pipe closed early — must end the
# process too, not leave an orphan searching forever.
$psi = [Diagnostics.ProcessStartInfo]::new($Engine)
$psi.RedirectStandardInput = $true; $psi.RedirectStandardOutput = $true
$psi.UseShellExecute = $false; $psi.CreateNoWindow = $true
$p = [Diagnostics.Process]::Start($psi)
$p.StandardInput.WriteLine('position startpos')
$p.StandardInput.WriteLine('go infinite')
Start-Sleep -Milliseconds 200
$p.StandardInput.Close()
$eofExit = $p.WaitForExit(5000)
$eofOut = $p.StandardOutput.ReadToEnd()
if (-not $eofExit) { $p.Kill($true) }
Assert-Check 'end of input without quit exits the process' $eofExit
Assert-Check 'end of input still writes the pending bestmove' ($eofOut -match '(?m)^bestmove ')

$failed = @($results | Where-Object { -not $_.Passed })
Write-Host ''
Write-Host ("{0}/{1} checks passed" -f ($results.Count - $failed.Count), $results.Count) -ForegroundColor $(if ($failed) { 'Red' } else { 'Green' })

if ($failed) {
    Write-Host 'Transcript of the main session:' -ForegroundColor DarkGray
    $e.Transcript | Select-Object -Last 60 | ForEach-Object { Write-Host "  $_" -ForegroundColor DarkGray }
    exit 1
}
exit 0
