#Requires -Version 7.0
<#
.SYNOPSIS
    Two-binary A/B with fastchess's SPRT: elo0=0 elo1=5 alpha=0.05 beta=0.05. Run it on every
    search change.

.DESCRIPTION
    A front door to tools/sprt.ps1, which does the work — sets up fastchess, writes the run
    manifest, parses the verdict and maps it to an exit code — with this machine's tool path and
    the release process's hypotheses as defaults. It deliberately adds nothing else: one
    implementation of the run is one thing to keep right, and tools/sprt.ps1 is the one
    tools/tests covers.

    H0: the change is no better than the base (elo0 = 0). H1: it is at least 5 Elo better
    (elo1 = 5). Both errors at 5%. fastchess reports from the point of view of its first engine,
    and tools/sprt.ps1 always puts the change first, so a positive Elo means the change is better.

.PARAMETER Base
    The baseline engine executable — the arm to beat.

.PARAMETER Change
    The candidate engine executable.

.PARAMETER Out
    Output folder. Defaults to runs/sprt/<yyyy-MM-dd_HHmmss>.

.PARAMETER Fastchess
    Path to fastchess.exe.

.PARAMETER Book
    Opening book. Default: the UHO book tools/sprt.ps1 expects, which is deliberately unbalanced
    (more decisive games, fewer games to a bound). If it is not in openings/, tools/sprt.ps1 stops
    and says where to get it.

.PARAMETER Tc
    Time control, fastchess format. Default 8+0.08 (seconds), the short control an SPRT needs to
    finish in hours rather than days.

.PARAMETER Nodes
    Fixed node budget per move instead of a clock; reproducible and immune to machine load.

.PARAMETER Concurrency
    Games in flight. 0 lets tools/sprt.ps1 choose (physical cores - 2).

.EXAMPLE
    pwsh scripts/sprt.ps1 -Base arms/base/ChessBot.Uci.exe -Change arms/change/ChessBot.Uci.exe

.OUTPUTS
    Exit code 0 when H1 is accepted (adopt), 4 when H0 is accepted (discard), 5 when the run
    ended without a verdict — the codes tools/sprt.ps1 and ChessBot.MatchRunner --ab share.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Base,
    [Parameter(Mandatory)] [string] $Change,
    [string] $Out,
    [string] $Fastchess   = 'C:\Tools\fastchess-windows-x86-64\fastchess.exe',
    [string] $Book        = (Join-Path $PSScriptRoot '..\openings\UHO_Lichess_4852_v1.epd'),
    [string] $Tc          = '8+0.08',
    [long]   $Nodes       = 0,
    [int]    $Concurrency = 0,
    [double] $Elo0        = 0,
    [double] $Elo1        = 5,
    [double] $Alpha       = 0.05,
    [double] $Beta        = 0.05,
    [int]    $Rounds      = 50000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if (-not $Out) { $Out = Join-Path $PSScriptRoot "..\runs\sprt\$(Get-Date -Format 'yyyy-MM-dd_HHmmss')" }

$forward = @{
    Base          = $Base
    Change        = $Change
    Out           = $Out
    FastchessPath = $Fastchess
    Book          = $Book
    Tc            = $Tc
    Nodes         = $Nodes
    Concurrency   = $Concurrency
    Elo0          = $Elo0
    Elo1          = $Elo1
    Alpha         = $Alpha
    Beta          = $Beta
    Rounds        = $Rounds
}

& (Join-Path $PSScriptRoot '..\tools\sprt.ps1') @forward
exit $LASTEXITCODE
