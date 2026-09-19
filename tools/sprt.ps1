#Requires -Version 7.0
<#
.SYNOPSIS
    Runs an SPRT A/B between two ChessBot.Uci binaries using fastchess.

.DESCRIPTION
    This is the gate for every single change: fastchess plays the games and decides when the
    evidence is conclusive, and this script only sets it up, records what was run, and turns the
    verdict into an exit code.

    Nothing here computes SPRT, LLR or Elo. fastchess does all of it, on the pentanomial
    (game-pair) model, which is why games are played in colour-reversed pairs from a shared
    opening. A second implementation of the stopping rule would be a second thing to be wrong,
    and the existing MatchRunner --ab already has one; both are kept, and they are not expected
    to agree game for game because they are different instruments.

    WHICH ARM IS "FIRST" MATTERS. fastchess reports Elo and runs the SPRT from the point of view
    of the FIRST -engine on the command line: a summary reading "Results of A vs B ... Elo: -40"
    says A is 40 Elo worse than B. The hypotheses here are about the CHANGE (H1: the change is
    at least elo1 better), so the change is passed to fastchess first, whatever order the
    parameters were given in.

.PARAMETER Base
    The baseline engine executable — the arm to beat.

.PARAMETER Change
    The candidate engine executable.

.PARAMETER Out
    Directory for the manifest, the PGN, the fastchess log and the parsed result. Created if
    absent.

.PARAMETER Elo0
    The null hypothesis, in Elo. Default 0: "the change is no better than the base".

.PARAMETER Elo1
    The alternative hypothesis, in Elo. Default 5.

.PARAMETER Tc
    Time control in fastchess/cutechess format, "seconds+increment". Default "8+0.08".

.PARAMETER Nodes
    A fixed node budget per move instead of a clock. Makes the run reproducible and immune to
    machine load, at the cost of not measuring anything that only pays off under a real clock
    (time management, for one). When set, -Tc is not passed at all.

.PARAMETER Concurrency
    Games in flight. Defaults to physical cores minus two, leaving one core for the operating
    system and one for the harness itself; a run that takes the whole machine measures
    contention as much as chess.

.PARAMETER Book
    Opening book. Default is the UHO book, which is the right instrument for an A/B: its
    positions are deliberately unbalanced, which raises the decisive-game rate and so cuts the
    number of games an SPRT needs to reach a bound.

.PARAMETER FastchessPath
    Path to fastchess.exe, or a folder containing it. Falls back to $env:FASTCHESS, then PATH.

.EXAMPLE
    pwsh tools/sprt.ps1 -Base arms/base/ChessBot.Uci.exe -Change arms/change/ChessBot.Uci.exe -Out runs/lmr-tweak

.OUTPUTS
    Exit code 0 when the change should be adopted (H1 accepted), 4 when it should be discarded
    (H0 accepted), 5 when the run ended without a verdict. These match ChessBot.MatchRunner --ab
    so that either instrument can drive the same automation.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [string] $Base,
    [Parameter(Mandatory)] [string] $Change,
    [Parameter(Mandatory)] [string] $Out,

    [double] $Elo0 = 0,
    [double] $Elo1 = 5,

    [string] $Tc = '8+0.08',
    [long]   $Nodes = 0,

    [int]    $Concurrency = 0,
    [string] $Book = 'openings/UHO_Lichess_4852_v1.epd',
    [string] $FastchessPath,

    [double] $Alpha = 0.05,
    [double] $Beta  = 0.05,

    # Effectively no cap: the SPRT bounds are what end the run. Rounds rather than games because
    # fastchess counts a colour-reversed pair as one round.
    [int]    $Rounds = 50000
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot 'lib/RunSupport.psm1')      -Force
Import-Module (Join-Path $PSScriptRoot 'lib/FastchessResult.psm1') -Force

# Exit codes, shared with ChessBot.MatchRunner --ab.
$EXIT_ADOPT   = 0
$EXIT_DISCARD = 4
$EXIT_UNKNOWN = 5

# ── Resolve everything before starting anything ──────────────────────────────

$fastchess = Resolve-ExternalTool `
    -DisplayName 'fastchess' `
    -Explicit $FastchessPath `
    -EnvironmentVariable 'FASTCHESS' `
    -CommandName 'fastchess.exe' `
    -DownloadUrl 'https://github.com/Disservin/fastchess/releases' `
    -ExtraHelp 'Windows builds are attached to each release as fastchess-windows-x86-64.zip.'

if (-not (Test-Path -LiteralPath $Book -PathType Leaf)) {
    throw @"
Opening book not found: $Book

The default book is UHO_Lichess_4852_v1.epd, from
https://github.com/official-stockfish/books (download UHO_Lichess_4852_v1.epd.zip and unpack it
into openings/). Pass -Book to use another one.

UHO positions are deliberately unbalanced, which is what makes them the right book for an A/B:
more decisive games means fewer games to a bound. Do not use it for the absolute gauntlet — see
tools/gauntlet.ps1, which defaults to a balanced book.
"@
}

$null = New-Item -ItemType Directory -Force -Path $Out
$outFull = (Resolve-Path -LiteralPath $Out).Path

if ($Concurrency -le 0) {
    $Concurrency = [Math]::Max(1, (Get-PhysicalCoreCount) - 2)
}

$bookInfo = Resolve-OpeningBook -Book $Book -OutDir $outFull

$baseEngine   = Get-EngineDescriptor -Exe $Base   -Label 'base'
$changeEngine = Get-EngineDescriptor -Exe $Change -Label 'change'

Write-Host "base   : $($baseEngine.idName)"   -ForegroundColor Cyan
Write-Host "change : $($changeEngine.idName)" -ForegroundColor Cyan

if ($baseEngine.sha256 -eq $changeEngine.sha256) {
    Write-Warning "Both arms are the SAME binary (identical SHA-256). This measures the harness's own noise, which is a legitimate smoke test and a meaningless A/B."
}

# ── Build the fastchess command line ─────────────────────────────────────────
# The change goes FIRST so that the reported Elo and the SPRT are about the change. See the
# comment at the top of this file.

$pgnPath = Join-Path $outFull 'games.pgn'
$logPath = Join-Path $outFull 'fastchess.log'

$perEngine = @('proto=uci')
if ($Nodes -gt 0) { $perEngine += "nodes=$Nodes" } else { $perEngine += "tc=$Tc" }

$fcArgs = @(
    '-engine', "cmd=$($changeEngine.path)", 'name=change'
    '-engine', "cmd=$($baseEngine.path)",   'name=base'
    '-each'
) + $perEngine + @(
    '-openings', "file=$($bookInfo.Path)", "format=$($bookInfo.Format)", 'order=random'
    '-repeat'
    '-games',   '2'
    '-rounds',  "$Rounds"
    '-sprt',    "elo0=$($Elo0.ToString([Globalization.CultureInfo]::InvariantCulture))",
                "elo1=$($Elo1.ToString([Globalization.CultureInfo]::InvariantCulture))",
                "alpha=$($Alpha.ToString([Globalization.CultureInfo]::InvariantCulture))",
                "beta=$($Beta.ToString([Globalization.CultureInfo]::InvariantCulture))",
                'model=logistic'
    '-report',  'penta=true'
    '-concurrency', "$Concurrency"
    '-recover'
    '-pgnout',  "file=$pgnPath"
    '-log',     "file=$logPath", 'level=warn'
)

$fastchessVersion = Get-ToolVersion -Exe $fastchess -VersionArgs @('--version')

$manifestPath = Write-RunManifest `
    -OutDir $outFull `
    -Kind 'sprt' `
    -Engines @($changeEngine, $baseEngine) `
    -ToolPath $fastchess `
    -ToolVersion $fastchessVersion `
    -CommandLine (@($fastchess) + $fcArgs) `
    -BookPath $Book `
    -Concurrency $Concurrency `
    -Extra @{
        sprt = [ordered] @{
            elo0  = $Elo0
            elo1  = $Elo1
            alpha = $Alpha
            beta  = $Beta
            model = 'logistic'
        }
        timeControl = if ($Nodes -gt 0) { $null } else { $Tc }
        nodes       = if ($Nodes -gt 0) { $Nodes } else { $null }

        # Recorded when the book handed to fastchess is not the book that was asked for, so the
        # substitution is visible in the record rather than only in a console warning.
        bookSanitised = if ($bookInfo.Sanitised) {
            [ordered] @{ path = $bookInfo.Path; sha256 = Get-FileSha256 -Path $bookInfo.Path }
        } else { $null }
    }

Write-Host "manifest: $manifestPath" -ForegroundColor DarkGray
Write-Host "running : $fastchess $($fcArgs -join ' ')" -ForegroundColor DarkGray
Write-Host ''

# ── Run ──────────────────────────────────────────────────────────────────────
# The output is teed: shown live so a long run can be watched, and kept whole so the summary can
# be parsed afterwards. The result is written in a finally block so that a Ctrl+C still produces
# a result file for the games that were played — an interrupted SPRT is not a wasted one, it is
# just an inconclusive one.

$transcriptPath = Join-Path $outFull 'fastchess_output.txt'
$captured = [Text.StringBuilder]::new()
$exitCode = $EXIT_UNKNOWN

try {
    & $fastchess @fcArgs 2>&1 | ForEach-Object {
        $line = $_ | Out-String -NoNewline
        [void] $captured.AppendLine($line)
        Write-Host $line
    }
} finally {
    $text = $captured.ToString()
    Set-Content -LiteralPath $transcriptPath -Value $text -Encoding utf8

    $parsed = ConvertFrom-FastchessSprt -Text $text

    if ($null -eq $parsed) {
        Write-Warning 'fastchess produced no result summary; nothing was parsed.'

        $result = [ordered] @{
            verdict  = 'unknown'
            reason   = 'fastchess produced no summary block'
            manifest = $manifestPath
        }
    } else {
        $verdict = switch ($parsed.Verdict) {
            'H1'    { 'adopt' }
            'H0'    { 'discard' }
            default { 'inconclusive' }
        }

        $exitCode = switch ($parsed.Verdict) {
            'H1'    { $EXIT_ADOPT }
            'H0'    { $EXIT_DISCARD }
            default { $EXIT_UNKNOWN }
        }

        $result = [ordered] @{
            verdict      = $verdict
            hypothesis   = $parsed.Verdict
            elo0         = $parsed.Elo0
            elo1         = $parsed.Elo1
            llr          = $parsed.Llr
            llrLower     = $parsed.LlrLower
            llrUpper     = $parsed.LlrUpper

            games        = $parsed.Games
            wins         = $parsed.Wins
            draws        = $parsed.Draws
            losses       = $parsed.Losses
            points       = $parsed.Points
            scorePercent = $parsed.ScorePercent

            elo          = $parsed.Elo
            eloError     = $parsed.EloError
            normalizedElo      = $parsed.NormalizedElo
            normalizedEloError = $parsed.NormalizedEloError
            los          = $parsed.Los
            drawRatio    = $parsed.DrawRatio
            pairsRatio   = $parsed.PairsRatio
            pentanomial  = $parsed.Pentanomial

            engines      = [ordered] @{
                change = $changeEngine.idName
                base   = $baseEngine.idName
            }
            timeControl  = $parsed.TimeControl
            book         = $parsed.Book
            manifest     = $manifestPath
            pgn          = $pgnPath
            finishedUtc  = [DateTime]::UtcNow.ToString('o')
        }

        Write-Host ''
        Write-Host "verdict : $verdict ($($parsed.Verdict))" -ForegroundColor $(
            switch ($parsed.Verdict) { 'H1' { 'Green' } 'H0' { 'Red' } default { 'Yellow' } })
        # Formatted invariantly: this line gets pasted into commit messages, and on a German
        # Windows the default would render -5.79 as "-5,79".
        Write-Host ("elo     : {0} +/- {1} over {2} games" -f
            (Format-Invariant $parsed.Elo 'F2'), (Format-Invariant $parsed.EloError 'F2'), $parsed.Games)
        Write-Host ("llr     : {0} in ({1}, {2})" -f
            (Format-Invariant $parsed.Llr 'F2'), (Format-Invariant $parsed.LlrLower 'F2'),
            (Format-Invariant $parsed.LlrUpper 'F2'))
    }

    $resultPath = Join-Path $outFull 'sprt_result.json'
    $result | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $resultPath -Encoding utf8
    Write-Host "result  : $resultPath" -ForegroundColor DarkGray
}

exit $exitCode
