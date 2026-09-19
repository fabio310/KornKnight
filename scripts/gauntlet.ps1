#Requires -Version 7.0
<#
.SYNOPSIS
    Plays KornKnight against fixed opponents with fastchess and prints the Elo table.

.DESCRIPTION
    An absolute strength check: where the engine stands against known engines under rating-list
    conditions, as opposed to scripts/sprt.ps1, which asks whether one build beats another.

    Conditions are fixed rather than parameters where a rating list fixes them, because a
    gauntlet run under different conditions is a different measurement:
      - one thread and the same Hash for every engine (option.Threads=1, option.Hash=<Hash>);
        fastchess warns that Blunder has no Threads option, which is harmless — it is
        single-threaded anyway,
      - no pondering (fastchess only ponders when asked to, and this never asks),
      - openings from a book (-openings), each played with colours reversed (-repeat),
      - a clock with increment, 2 minutes + 1 second by default — CCRL's blitz condition.
    PGN, the fastchess log and a run manifest (binaries by hash and by id name, book by hash,
    the exact command line) go to a dated folder under runs/gauntlet.

    Nothing here computes Elo. fastchess prints its own table, and this script shows it.

    COST. A 2+1 game lasts about five minutes. At the default 500 games per opponent, two
    opponents and concurrency = physical cores / 2 (7 on a 14-core machine), that is roughly
    1,000 x 5 / 7 = 12 hours. -GamesPerOpponent 100 is a ~2.5 h sanity check (+/-70 Elo).

.PARAMETER Engine
    The KornKnight executable. Defaults to the published win-x64 release, falling back to the
    Release build in bin/.

.PARAMETER Fastchess
    Path to fastchess.exe.

.PARAMETER Opponents
    Opponent executables.

.PARAMETER Tc
    Time control in fastchess format, moves/minutes:seconds+increment. Default '2:00+1'.

.PARAMETER GamesPerOpponent
    Games against each opponent. Played in colour-reversed pairs, so it must be even.

.PARAMETER Hash
    Transposition table size in MB, the same for every engine.

.PARAMETER Concurrency
    Games in flight. Defaults to physical cores / 2: these are timed games, and two sharing a
    core's hyperthreads would each get half the machine they think they have.

.PARAMETER Book
    Opening book (EPD or PGN). Defaults to this repository's balanced 500-position suite; a
    gauntlet wants balanced openings, unlike an A/B, which wants unbalanced ones.

.PARAMETER OutDir
    Output folder. Defaults to runs/gauntlet/<yyyy-MM-dd_HHmmss>.

.EXAMPLE
    pwsh scripts/gauntlet.ps1

.EXAMPLE
    pwsh scripts/gauntlet.ps1 -GamesPerOpponent 20 -Tc '0:10+0.1'
#>
[CmdletBinding()]
param(
    [string]   $Engine,
    [string]   $Fastchess        = 'C:\Tools\fastchess-windows-x86-64\fastchess.exe',
    [string[]] $Opponents        = @(
        'C:\Tools\blunder-8.5.5\windows\blunder-8.5.5-avx2.exe'
        'C:\Tools\Leorik 3.2 Windows\Leorik-3.2.1.exe'
    ),
    [string]   $Tc               = '2:00+1',
    [int]      $GamesPerOpponent = 500,
    [int]      $Hash             = 64,
    [int]      $Concurrency      = 0,
    [string]   $Book             = (Join-Path $PSScriptRoot '..\openings\generated-500.epd'),
    [string]   $OutDir
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..\tools\lib\RunSupport.psm1') -Force

# ── Resolve everything before starting anything ──────────────────────────────

if (-not $Engine) {
    $published = Get-ChildItem -Path (Join-Path $PSScriptRoot '..\publish') -Filter 'KornKnight-*-win-x64.exe' -ErrorAction SilentlyContinue |
        Sort-Object LastWriteTime -Descending | Select-Object -First 1
    $Engine = if ($published) { $published.FullName }
              else { Join-Path $PSScriptRoot '..\ChessBot.Uci\bin\Release\net8.0\ChessBot.Uci.exe' }
}

foreach ($path in @($Fastchess, $Engine) + $Opponents) {
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) { throw "Not found: $path" }
}
if ($GamesPerOpponent -lt 2 -or $GamesPerOpponent % 2) {
    throw "-GamesPerOpponent must be even (games are played in colour-reversed pairs); got $GamesPerOpponent."
}

if (-not $OutDir) {
    $OutDir = Join-Path $PSScriptRoot "..\runs\gauntlet\$(Get-Date -Format 'yyyy-MM-dd_HHmmss')"
}
$null   = New-Item -ItemType Directory -Force -Path $OutDir
$OutDir = (Resolve-Path -LiteralPath $OutDir).Path

if ($Concurrency -le 0) { $Concurrency = [Math]::Max(1, [int] ((Get-PhysicalCoreCount) / 2)) }

$bookInfo = Resolve-OpeningBook -Book $Book -OutDir $OutDir

$engines = @(Get-EngineDescriptor -Exe $Engine -Label 'KornKnight')
$i = 0
foreach ($opponent in $Opponents) {
    $engines += Get-EngineDescriptor -Exe $opponent -Label "opponent$((++$i))"
}
foreach ($e in $engines) { Write-Host ("{0,-10} {1}" -f $e.label, $e.idName) -ForegroundColor Cyan }

# ── Build the fastchess command line ─────────────────────────────────────────
# KornKnight goes first: with -tournament gauntlet and -seeds 1, the first engine plays every
# other, and the others do not play each other.

$pgnPath = Join-Path $OutDir 'games.pgn'
$logPath = Join-Path $OutDir 'fastchess.log'

$fcArgs = @()
foreach ($e in $engines) {
    $name = if ($e.label -eq 'KornKnight') { 'KornKnight' } else { [IO.Path]::GetFileNameWithoutExtension($e.path) }
    $fcArgs += @('-engine', "cmd=$($e.path)", "name=$name")
}
$fcArgs += @(
    '-each', 'proto=uci', "tc=$Tc", "option.Hash=$Hash", 'option.Threads=1'
    '-tournament', 'gauntlet', '-seeds', '1'
    '-openings', "file=$($bookInfo.Path)", "format=$($bookInfo.Format)", 'order=random'
    '-rounds', "$($GamesPerOpponent / 2)", '-games', '2', '-repeat'
    '-concurrency', "$Concurrency"
    '-recover'
    '-ratinginterval', '20'
    '-pgnout', "file=$pgnPath"
    '-log', "file=$logPath", 'level=warn'
)

$manifest = Write-RunManifest -OutDir $OutDir -Kind 'gauntlet' -Engines $engines `
    -ToolPath $Fastchess -ToolVersion (Get-ToolVersion -Exe $Fastchess) `
    -CommandLine (@($Fastchess) + $fcArgs) -BookPath $Book -Concurrency $Concurrency `
    -Extra @{ timeControl = $Tc; gamesPerOpponent = $GamesPerOpponent; hashMb = $Hash; threads = 1; ponder = $false }

Write-Host "manifest: $manifest" -ForegroundColor DarkGray
Write-Host "running : $Fastchess $($fcArgs -join ' ')" -ForegroundColor DarkGray
Write-Host ''

# ── Run ──────────────────────────────────────────────────────────────────────
# Teed: shown live, and kept whole so the final table can be printed again at the end, where it
# is not buried under thousands of per-game lines.

$transcript = Join-Path $OutDir 'fastchess_output.txt'
$captured   = [Text.StringBuilder]::new()
try {
    & $Fastchess @fcArgs 2>&1 | ForEach-Object {
        $line = $_ | Out-String -NoNewline
        [void] $captured.AppendLine($line)
        Write-Host $line
    }
} finally {
    $text = $captured.ToString()
    Set-Content -LiteralPath $transcript -Value $text -Encoding utf8

    # A gauntlet prints its rating table as a "Rank Name Elo ..." header and one row per engine,
    # between dashed rules, every -ratinginterval games and once at the end. The last is final.
    $lines = $text -split "`r?`n"
    $table = $null
    for ($n = 0; $n -lt $lines.Count; $n++) {
        if ($lines[$n] -match '^\s*Rank\s+Name\s+Elo') {
            $block = [Collections.Generic.List[string]]::new()
            for ($m = $n; $m -lt $lines.Count -and $lines[$m] -notmatch '^-{10,}'; $m++) { $block.Add($lines[$m]) }
            $table = $block
        }
    }

    # Losses on time are the one result this engine's own code decides outright, so they are
    # counted separately rather than left to be spotted in a PGN.
    # fastchess: "Finished game 7 (White vs Black): 0-1 {White loses on time}".
    $timeLosses = @($lines | Where-Object {
        $_ -match '^Finished game \d+ \((?<white>.+?) vs (?<black>.+?)\): .*\{(?<side>White|Black) loses on time\}' -and
        $(if ($Matches.side -eq 'White') { $Matches.white } else { $Matches.black }) -eq 'KornKnight'
    })

    Write-Host ''
    Write-Host '════ Final results ════' -ForegroundColor Green
    if ($null -eq $table) {
        Write-Warning "fastchess printed no rating table; see $transcript"
    } else {
        $table | ForEach-Object { Write-Host $_ }
    }
    Write-Host ''
    Write-Host ("KornKnight time losses: {0}" -f $timeLosses.Count) -ForegroundColor $(if ($timeLosses) { 'Red' } else { 'Green' })
    $timeLosses | ForEach-Object { Write-Host "  $_" -ForegroundColor Red }
    Write-Host "pgn     : $pgnPath" -ForegroundColor DarkGray
    Write-Host "output  : $transcript" -ForegroundColor DarkGray
}
