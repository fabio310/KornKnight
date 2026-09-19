#Requires -Version 7.0
<#
.SYNOPSIS
    Tests for the fastchess output parser.

.DESCRIPTION
    Run with:  pwsh tools/tests/Test-FastchessResult.ps1
    Exits 0 when every assertion passes, 1 otherwise, so CI can gate on it.

    Every fixture under fixtures/ is REAL fastchess output, captured from an actual run of
    fastchess 1.8.2-alpha against ChessBot.Uci — not hand-written to match the parser. That
    distinction is the entire value of these tests: a parser tested against a summary its own
    author invented tests nothing except that two pieces of the author's imagination agree.

    NOT Pester. Pester 5 is not installed on this machine (only the legacy 3.4 that ships with
    Windows PowerShell, whose syntax Pester 5 cannot run), and installing a module is not
    something a test script should require of whoever clones the repository. The assertions
    below are plain functions, which costs nothing here: what is being tested is a pure
    text-to-object transform with no mocking, no setup and no teardown.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '../lib/FastchessResult.psm1') -Force

$script:Failures = 0
$script:Checks   = 0

function Assert-Equal {
    param($Expected, $Actual, [string] $Because)

    $script:Checks++
    $same = if ($null -eq $Expected) { $null -eq $Actual } else { $Expected.Equals($Actual) }

    if (-not $same) {
        $script:Failures++
        Write-Host "  FAIL  $Because" -ForegroundColor Red
        Write-Host "        expected: $Expected" -ForegroundColor Red
        Write-Host "        actual  : $Actual"   -ForegroundColor Red
    }
}

function Assert-True {
    param([bool] $Condition, [string] $Because)

    $script:Checks++
    if (-not $Condition) {
        $script:Failures++
        Write-Host "  FAIL  $Because" -ForegroundColor Red
    }
}

function Start-TestGroup {
    param([string] $Name)
    Write-Host "  $Name" -ForegroundColor Cyan
}

function Get-Fixture {
    param([string] $Name)
    Get-Content -LiteralPath (Join-Path $PSScriptRoot "fixtures/$Name") -Raw
}

Write-Host 'FastchessResult' -ForegroundColor White

# ── An inconclusive run ──────────────────────────────────────────────────────

Start-TestGroup 'parses a run that ended without a verdict'
$r = ConvertFrom-FastchessSprt -Text (Get-Fixture 'sprt-inconclusive.txt')

Assert-Equal 'base'   $r.Engine1      'engine1 is the first engine named'
Assert-Equal 'change' $r.Engine2      'engine2 is the second'
Assert-Equal '1+0.01' $r.TimeControl  'time control comes out of the parenthetical'
Assert-Equal 'generated-500.epd' $r.Book 'book is the last field of the parenthetical'

Assert-Equal 16  $r.Games  'games'
Assert-Equal 4   $r.Wins   'wins'
Assert-Equal 6   $r.Losses 'losses'
Assert-Equal 6   $r.Draws  'draws'
Assert-Equal 7.0 $r.Points 'points'
Assert-Equal 43.75 $r.ScorePercent 'score percent'

Assert-Equal (-43.66) $r.Elo                'elo'
Assert-Equal 82.47    $r.EloError           'elo error'
Assert-Equal (-92.86) $r.NormalizedElo      'nElo'
Assert-Equal 170.24   $r.NormalizedEloError 'nElo error'
Assert-Equal 14.25    $r.Los                'LOS'
Assert-Equal 50.0     $r.DrawRatio          'draw ratio'
Assert-Equal 0.33     $r.PairsRatio         'pairs ratio'

Assert-Equal 0 $r.Pentanomial.LL 'pentanomial LL'
Assert-Equal 3 $r.Pentanomial.LD 'pentanomial LD'
Assert-Equal 4 $r.Pentanomial.DD 'pentanomial DD'
Assert-Equal 1 $r.Pentanomial.DW 'pentanomial DW'
Assert-Equal 0 $r.Pentanomial.WW 'pentanomial WW'

# The pentanomial counts pairs, so they must sum to half the games. This is the assertion that
# would catch a regex that picked up the wrong bracketed list.
$pairs = $r.Pentanomial.LL + $r.Pentanomial.LD + $r.Pentanomial.DD + $r.Pentanomial.DW + $r.Pentanomial.WW
Assert-Equal ($r.Games / 2) $pairs 'pentanomial counts sum to the number of game PAIRS'

Assert-Equal (-0.11) $r.Llr      'LLR'
Assert-Equal (-2.94) $r.LlrLower 'LLR lower bound'
Assert-Equal 2.94    $r.LlrUpper 'LLR upper bound'
Assert-Equal 0.0     $r.Elo0     'elo0'
Assert-Equal 5.0     $r.Elo1     'elo1'

Assert-Equal 'inconclusive' $r.Verdict 'no SPRT completion line means no verdict'

# ── H1 accepted ──────────────────────────────────────────────────────────────

Start-TestGroup 'reads the verdict when H1 is accepted'
$h1 = ConvertFrom-FastchessSprt -Text (Get-Fixture 'sprt-h1-accepted.txt')

Assert-Equal 'H1'      $h1.Verdict 'H1 accepted'
Assert-Equal 28        $h1.Games   'games'
Assert-Equal 3.11      $h1.Llr     'LLR at the moment of acceptance'
Assert-Equal (-200.0)  $h1.Elo0    'negative elo0 parses with its sign'
Assert-Equal (-100.0)  $h1.Elo1    'negative elo1 parses with its sign'

# ── H0 accepted, and the last block wins ─────────────────────────────────────

Start-TestGroup 'reads the verdict when H0 is accepted, from the LAST of several blocks'
$h0 = ConvertFrom-FastchessSprt -Text (Get-Fixture 'sprt-h0-accepted.txt')

Assert-Equal 'H0' $h0.Verdict 'H0 accepted'

# This fixture holds three summary blocks, printed at a rating interval of 4: 10 games, then 16,
# then the final 20. Parsing anything but the last would report a run that never finished.
Assert-Equal 20    $h0.Games 'the FINAL block is parsed, not the first'
Assert-Equal 10.5  $h0.Points 'points from the final block'
Assert-Equal (-3.0) $h0.Llr  'LLR from the final block'
Assert-Equal 100.0 $h0.Elo0  'elo0'
Assert-Equal 200.0 $h0.Elo1  'elo1'

# ── Degenerate input ─────────────────────────────────────────────────────────

Start-TestGroup 'refuses to invent a result'
Assert-Equal $null (ConvertFrom-FastchessSprt -Text '')                    'empty output parses to nothing'
Assert-Equal $null (ConvertFrom-FastchessSprt -Text 'Indexing opening suite...') 'output with no summary parses to nothing'

Start-TestGroup 'handles the non-finite values fastchess really prints'
Assert-Equal ([double]::PositiveInfinity) (ConvertTo-FastchessNumber 'inf')  'inf'
Assert-Equal ([double]::NegativeInfinity) (ConvertTo-FastchessNumber '-inf') '-inf'
Assert-True  ([double]::IsNaN((ConvertTo-FastchessNumber 'nan')))            'nan'
Assert-Equal $null (ConvertTo-FastchessNumber '')                            'empty is no value'
Assert-Equal (-62.71) (ConvertTo-FastchessNumber '-62.71')                   'an ordinary negative decimal'

Start-TestGroup 'parses with the invariant culture, whatever the machine is set to'
$previous = [Threading.Thread]::CurrentThread.CurrentCulture
try {
    # On de-DE, '.' is a thousands separator: [double]'-62.71' returns -6271 rather than
    # failing. fastchess always prints '.', so a parser that follows the machine's culture
    # silently reports an Elo a hundred times too large on a German Windows — which is the
    # machine this repository is developed on.
    [Threading.Thread]::CurrentThread.CurrentCulture = [Globalization.CultureInfo]::GetCultureInfo('de-DE')

    $german = ConvertFrom-FastchessSprt -Text (Get-Fixture 'sprt-inconclusive.txt')
    Assert-Equal (-43.66) $german.Elo      'elo under de-DE'
    Assert-Equal 43.75    $german.ScorePercent 'score percent under de-DE'
    Assert-Equal (-0.11)  $german.Llr      'LLR under de-DE'
} finally {
    [Threading.Thread]::CurrentThread.CurrentCulture = $previous
}

Start-TestGroup 'strips ANSI colour codes'
$coloured = "`e[1;32mResults of a vs b (1+0.01, NULL, NULL, book.epd):`e[0m`n" +
            "Elo: 12.34 +/- 5.00, nElo: 20.00 +/- 8.00`n" +
            "LOS: 99.00 %, DrawRatio: 10.00 %, PairsRatio: 2.00`n" +
            "Games: 100, Wins: 40, Losses: 30, Draws: 30, Points: 55.0 (55.00 %)`n" +
            "Ptnml(0-2): [1, 2, 3, 4, 40], WL/DD Ratio: 1.00`n" +
            "LLR: 1.23 (41.0%) (-2.94, 2.94) [0.00, 5.00]"
$ansi = ConvertFrom-FastchessSprt -Text $coloured
Assert-Equal 'a'   $ansi.Engine1 'engine name is free of escape codes'
Assert-Equal 12.34 $ansi.Elo     'elo parses through colour codes'

# ── Summary ──────────────────────────────────────────────────────────────────

Write-Host ''
if ($script:Failures -eq 0) {
    Write-Host "PASSED  $($script:Checks) checks" -ForegroundColor Green
    exit 0
}

Write-Host "FAILED  $($script:Failures) of $($script:Checks) checks" -ForegroundColor Red
exit 1
