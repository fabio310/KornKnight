#Requires -Version 7.0
<#
.SYNOPSIS
    Parses fastchess console output into objects.

.DESCRIPTION
    fastchess does all of the statistics — SPRT, LLR, Elo, pentanomial pairs. Nothing in this
    module computes any of it; it reads what fastchess printed and turns it into a PowerShell
    object so a script can write JSON and choose an exit code. If a number below looks wrong,
    the bug is here or in fastchess, never in a second implementation of the maths.

    Two properties of fastchess output drive the design.

    First, a summary block is printed repeatedly — once every -ratinginterval games and again at
    the end — so the output holds many blocks and only the LAST one describes the finished run.
    Everything here parses the last match.

    Second, every number is formatted with a '.' decimal separator regardless of the machine's
    locale. Parsing them with the current culture would silently misread on a German or French
    Windows: [double]"‑62.71" under de-DE does not fail, it returns -6271. Every conversion in
    this module names InvariantCulture for that reason.
#>

Set-StrictMode -Version Latest

# fastchess prints ANSI colour codes on some terminals and not others — the match summary was
# clean when captured through a pipe, the --help output was not. Stripping is cheap and makes
# the parser independent of how the caller captured the text.
$script:AnsiPattern = "`e\[[0-9;]*[A-Za-z]"

<#
.SYNOPSIS
    Converts a numeric field from fastchess output, tolerating inf/nan.

.DESCRIPTION
    Elo is reported as 'inf' or '-inf' when one side scored 0% or 100%, and ratios come back as
    'nan' when their denominator is zero. Both are real outputs of a short or lopsided run, so
    they are mapped to [double]::PositiveInfinity and friends rather than being treated as a
    parse failure — a caller writing JSON needs to know the difference between "no value" and
    "a value that is off the scale".
#>
function ConvertTo-FastchessNumber {
    param([string] $Text)

    if ([string]::IsNullOrWhiteSpace($Text)) { return $null }

    switch -Regex ($Text.Trim()) {
        '^-inf(inity)?$' { return [double]::NegativeInfinity }
        '^\+?inf(inity)?$' { return [double]::PositiveInfinity }
        '^-?nan$' { return [double]::NaN }
    }

    [double] $value = 0
    if ([double]::TryParse($Text.Trim(), [Globalization.NumberStyles]::Float,
            [Globalization.CultureInfo]::InvariantCulture, [ref] $value)) {
        return $value
    }

    return $null
}

<#
.SYNOPSIS
    Parses the final match summary out of fastchess output.

.PARAMETER Text
    The whole stdout+stderr of a fastchess run.

.OUTPUTS
    A PSCustomObject, or $null when the text contains no summary block at all (fastchess died
    before finishing a single game, which the caller must report rather than paper over).
#>
function ConvertFrom-FastchessSprt {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Text)

    $clean = [regex]::Replace($Text, $script:AnsiPattern, '')
    $lines = $clean -split "`r?`n"

    # The last "Results of ..." is the finished run; everything before it is an interim report
    # printed at a rating interval.
    $startIndex = -1
    for ($i = $lines.Count - 1; $i -ge 0; $i--) {
        if ($lines[$i] -match '^Results of ') { $startIndex = $i; break }
    }

    if ($startIndex -lt 0) { return $null }

    $block = ($lines[$startIndex..($lines.Count - 1)]) -join "`n"

    $result = [ordered] @{
        Engine1        = $null
        Engine2        = $null
        TimeControl    = $null
        Book           = $null

        Games          = $null
        Wins           = $null
        Losses         = $null
        Draws          = $null
        Points         = $null
        ScorePercent   = $null

        Elo            = $null
        EloError       = $null
        NormalizedElo  = $null
        NormalizedEloError = $null
        Los            = $null
        DrawRatio      = $null
        PairsRatio     = $null

        # LL, LD, DD (and LW), DW, WW — the five outcomes of a colour-reversed game PAIR, which
        # is the unit fastchess does its statistics on. Pairs, not games, are what make the
        # variance estimate honest when both games of a pair start from the same opening.
        Pentanomial    = $null

        Llr            = $null
        LlrLower       = $null
        LlrUpper       = $null
        Elo0           = $null
        Elo1           = $null

        Verdict        = 'inconclusive'
    }

    if ($block -match '^Results of\s+(?<e1>.+?)\s+vs\s+(?<e2>.+?)\s+\((?<detail>[^)]*)\):') {
        $result.Engine1 = $Matches['e1']
        $result.Engine2 = $Matches['e2']

        # The parenthetical is "tc, threads, hash, book" with NULL standing in for anything not
        # set. Split rather than pattern-matched field by field, because the field count has
        # changed between fastchess versions and the first and last are the ones worth keeping.
        $detail = $Matches['detail'] -split '\s*,\s*'
        if ($detail.Count -ge 1) { $result.TimeControl = $detail[0] }
        if ($detail.Count -ge 2) { $result.Book = $detail[-1] }
    }

    if ($block -match 'Elo:\s*(?<elo>\S+)\s*\+/-\s*(?<err>\S+?),\s*nElo:\s*(?<nelo>\S+)\s*\+/-\s*(?<nerr>\S+)') {
        $result.Elo                = ConvertTo-FastchessNumber $Matches['elo']
        $result.EloError           = ConvertTo-FastchessNumber $Matches['err']
        $result.NormalizedElo      = ConvertTo-FastchessNumber $Matches['nelo']
        $result.NormalizedEloError = ConvertTo-FastchessNumber $Matches['nerr']
    }

    if ($block -match 'LOS:\s*(?<los>\S+)\s*%,\s*DrawRatio:\s*(?<dr>\S+)\s*%,\s*PairsRatio:\s*(?<pr>\S+)') {
        $result.Los        = ConvertTo-FastchessNumber $Matches['los']
        $result.DrawRatio  = ConvertTo-FastchessNumber $Matches['dr']
        $result.PairsRatio = ConvertTo-FastchessNumber $Matches['pr']
    }

    if ($block -match 'Games:\s*(?<g>\d+),\s*Wins:\s*(?<w>\d+),\s*Losses:\s*(?<l>\d+),\s*Draws:\s*(?<d>\d+),\s*Points:\s*(?<p>\S+)\s*\((?<pct>\S+)\s*%\)') {
        $result.Games        = [int] $Matches['g']
        $result.Wins         = [int] $Matches['w']
        $result.Losses       = [int] $Matches['l']
        $result.Draws        = [int] $Matches['d']
        $result.Points       = ConvertTo-FastchessNumber $Matches['p']
        $result.ScorePercent = ConvertTo-FastchessNumber $Matches['pct']
    }

    if ($block -match 'Ptnml\(0-2\):\s*\[\s*(?<ll>\d+),\s*(?<ld>\d+),\s*(?<dd>\d+),\s*(?<dw>\d+),\s*(?<ww>\d+)\s*\]') {
        $result.Pentanomial = [ordered] @{
            LL = [int] $Matches['ll']
            LD = [int] $Matches['ld']
            DD = [int] $Matches['dd']
            DW = [int] $Matches['dw']
            WW = [int] $Matches['ww']
        }
    }

    if ($block -match 'LLR:\s*(?<llr>\S+)\s*\([^)]*\)\s*\((?<lo>\S+?),\s*(?<hi>\S+?)\)\s*\[(?<e0>\S+?),\s*(?<e1>\S+?)\]') {
        $result.Llr      = ConvertTo-FastchessNumber $Matches['llr']
        $result.LlrLower = ConvertTo-FastchessNumber $Matches['lo']
        $result.LlrUpper = ConvertTo-FastchessNumber $Matches['hi']
        $result.Elo0     = ConvertTo-FastchessNumber $Matches['e0']
        $result.Elo1     = ConvertTo-FastchessNumber $Matches['e1']
    }

    # The verdict comes from fastchess's own statement, not from comparing LLR against the
    # bounds here. Re-deriving it would be a second implementation of the stopping rule, which
    # is the one thing this module must not contain — and fastchess applies the rule to the
    # pentanomial model, which an LLR-versus-bound comparison would silently get wrong at the
    # boundary.
    if ($clean -match 'SPRT\s*\([^)]*\)\s*completed\s*-\s*(?<h>H[01])\s+was\s+accepted') {
        $result.Verdict = $Matches['h']
    }

    return [pscustomobject] $result
}

<#
.SYNOPSIS
    Parses fastchess's per-pairing summaries, which is what a gauntlet prints one of per opponent.

.OUTPUTS
    One object per pairing, in the order fastchess printed them, each with the same shape as
    ConvertFrom-FastchessSprt returns.
#>
function ConvertFrom-FastchessPairings {
    [CmdletBinding()]
    param([Parameter(Mandatory)] [AllowEmptyString()] [string] $Text)

    $clean = [regex]::Replace($Text, $script:AnsiPattern, '')
    $lines = $clean -split "`r?`n"

    # Only the final report matters, and in a gauntlet the final report is the last run of
    # consecutive "Results of ..." blocks. Walk backwards collecting blocks until the gap.
    $starts = @()
    for ($i = 0; $i -lt $lines.Count; $i++) {
        if ($lines[$i] -match '^Results of ') { $starts += $i }
    }

    if ($starts.Count -eq 0) { return @() }

    $pairings = @{}
    foreach ($start in $starts) {
        $end = [Math]::Min($start + 8, $lines.Count - 1)
        $block = ($lines[$start..$end]) -join "`n"

        $parsed = ConvertFrom-FastchessSprt -Text $block
        if ($null -eq $parsed) { continue }

        # Later blocks for the same pairing supersede earlier ones: the last report of a pairing
        # is its finished result, the ones before are rating-interval snapshots.
        $pairings["$($parsed.Engine1) vs $($parsed.Engine2)"] = $parsed
    }

    return @($pairings.Values)
}

Export-ModuleMember -Function ConvertFrom-FastchessSprt, ConvertFrom-FastchessPairings, ConvertTo-FastchessNumber
