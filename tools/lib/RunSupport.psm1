#Requires -Version 7.0
<#
.SYNOPSIS
    Shared plumbing for the fastchess measurement scripts: locating tools, identifying binaries,
    and writing the run manifest.

.DESCRIPTION
    The manifest is the point of this module. A match result is only evidence if you can say
    later what produced it, and "the current working tree" is not an answer — by the time anyone
    reads the result the tree has moved on, and in an A/B run the two arms were built from two
    different commits neither of which is the tree. So every run records the binaries by hash
    AND by the commit they report in their own UCI id, the book by hash, the exact fastchess
    command line, and the machine's core count.
#>

Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Physical cores, not logical processors.

.DESCRIPTION
    Concurrency for a match is chosen against physical cores because two games sharing a core's
    two hyperthreads do not each get a core's worth of search, and a time-controlled game that
    silently gets half the machine it thinks it has is a measurement of the scheduler. Falls
    back to logical processors only if WMI refuses, which is the conservative direction: it
    overestimates, and the callers subtract from it.
#>
function Get-PhysicalCoreCount {
    try {
        $cores = (Get-CimInstance -ClassName Win32_Processor -ErrorAction Stop |
            Measure-Object -Property NumberOfCores -Sum).Sum
        if ($cores -gt 0) { return [int] $cores }
    } catch {
        Write-Verbose "Falling back to logical processor count: $($_.Exception.Message)"
    }

    return [int] [Environment]::ProcessorCount
}

function Get-FileSha256 {
    param([Parameter(Mandatory)] [string] $Path)

    if (-not (Test-Path -LiteralPath $Path)) { return $null }
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

<#
.SYNOPSIS
    Asks a UCI engine what it calls itself.

.DESCRIPTION
    Returns the engine's "id name" line, which for ChessBot.Uci carries the commit the binary
    was built from and a "dirty" marker when it was built from a modified tree. That is the only
    trustworthy statement of what an arm actually is: a path says nothing, and a hash says
    nothing a human can act on.

    Talks the protocol directly rather than shelling out, so the engine is started, asked, and
    stopped without leaving a process behind if it misbehaves.
#>
function Get-EngineIdName {
    param(
        [Parameter(Mandatory)] [string] $Exe,
        [int] $TimeoutMs = 10000
    )

    if (-not (Test-Path -LiteralPath $Exe)) { return $null }

    $psi = [Diagnostics.ProcessStartInfo]::new()
    $psi.FileName               = $Exe
    $psi.RedirectStandardInput  = $true
    $psi.RedirectStandardOutput = $true
    $psi.RedirectStandardError  = $true
    $psi.UseShellExecute        = $false
    $psi.CreateNoWindow         = $true

    $process = $null
    try {
        $process = [Diagnostics.Process]::Start($psi)
        $process.StandardInput.WriteLine('uci')
        $process.StandardInput.Flush()

        $idName   = $null
        $deadline = [DateTime]::UtcNow.AddMilliseconds($TimeoutMs)

        while ([DateTime]::UtcNow -lt $deadline) {
            $readTask = $process.StandardOutput.ReadLineAsync()
            if (-not $readTask.Wait([Math]::Max(1, ($deadline - [DateTime]::UtcNow).TotalMilliseconds))) { break }

            $line = $readTask.Result
            if ($null -eq $line) { break }
            if ($line -match '^\s*id\s+name\s+(?<name>.+?)\s*$') { $idName = $Matches['name'] }
            if ($line -match '^\s*uciok\s*$') { break }
        }

        try {
            $process.StandardInput.WriteLine('quit')
            $process.StandardInput.Flush()
        } catch { }

        if (-not $process.WaitForExit(2000)) { $process.Kill($true) }

        return $idName
    } catch {
        Write-Warning "Could not read 'id name' from ${Exe}: $($_.Exception.Message)"
        return $null
    } finally {
        if ($null -ne $process) { $process.Dispose() }
    }
}

<#
.SYNOPSIS
    Finds an external tool, or stops with an message that says where to get it.

.DESCRIPTION
    Order: the explicit parameter, then the environment variable, then PATH. Stopping with a
    download link is the specified behaviour — a measurement script that quietly proceeds
    without the thing that does the measuring is worse than one that refuses.
#>
function Resolve-ExternalTool {
    param(
        [Parameter(Mandatory)] [string] $DisplayName,
        [string] $Explicit,
        [Parameter(Mandatory)] [string] $EnvironmentVariable,
        [Parameter(Mandatory)] [string] $CommandName,
        [Parameter(Mandatory)] [string] $DownloadUrl,
        [string] $ExtraHelp
    )

    $candidates = @()
    if (-not [string]::IsNullOrWhiteSpace($Explicit)) { $candidates += $Explicit }

    $fromEnv = [Environment]::GetEnvironmentVariable($EnvironmentVariable)
    if (-not [string]::IsNullOrWhiteSpace($fromEnv)) { $candidates += $fromEnv }

    foreach ($candidate in $candidates) {
        if (Test-Path -LiteralPath $candidate -PathType Leaf) {
            return (Resolve-Path -LiteralPath $candidate).Path
        }

        # A directory is accepted too, since both tools ship as a folder with the exe inside.
        if (Test-Path -LiteralPath $candidate -PathType Container) {
            $inside = Join-Path $candidate $CommandName
            if (Test-Path -LiteralPath $inside -PathType Leaf) {
                return (Resolve-Path -LiteralPath $inside).Path
            }
        }
    }

    $onPath = Get-Command $CommandName -ErrorAction SilentlyContinue
    if ($null -ne $onPath) { return $onPath.Source }

    $message = @"
$DisplayName was not found.

Looked at, in order:
  -$($DisplayName)Path parameter   : $(if ($Explicit) { $Explicit } else { '(not given)' })
  `$env:$EnvironmentVariable       : $(if ($fromEnv) { $fromEnv } else { '(not set)' })
  $CommandName on PATH             : (not found)

Download it from $DownloadUrl, then either put it on PATH or set `$env:$EnvironmentVariable to
the executable (or to the folder containing it).
$ExtraHelp
"@

    throw $message
}

<#
.SYNOPSIS
    Describes one engine binary: path, hash, and what it says its name is.
#>
function Get-EngineDescriptor {
    param(
        [Parameter(Mandatory)] [string] $Exe,
        [string] $Label
    )

    if (-not (Test-Path -LiteralPath $Exe -PathType Leaf)) {
        throw "Engine not found: $Exe"
    }

    $full = (Resolve-Path -LiteralPath $Exe).Path

    return [ordered] @{
        label  = $Label
        path   = $full
        sha256 = Get-FileSha256 -Path $full
        idName = Get-EngineIdName -Exe $full
    }
}

<#
.SYNOPSIS
    Writes <Out>/run_manifest.json.
#>
function Write-RunManifest {
    param(
        [Parameter(Mandatory)] [string] $OutDir,
        [Parameter(Mandatory)] [string] $Kind,
        [Parameter(Mandatory)] [array]  $Engines,
        [Parameter(Mandatory)] [string] $ToolPath,
        [Parameter(Mandatory)] [string] $ToolVersion,
        [Parameter(Mandatory)] [array]  $CommandLine,
        [string] $BookPath,
        [int]    $Concurrency,
        [hashtable] $Extra
    )

    $manifest = [ordered] @{
        kind              = $Kind
        startedUtc        = [DateTime]::UtcNow.ToString('o')
        host              = [ordered] @{
            machine            = [Environment]::MachineName
            physicalCores      = Get-PhysicalCoreCount
            logicalProcessors  = [Environment]::ProcessorCount
            os                 = [Environment]::OSVersion.VersionString
            powershell         = $PSVersionTable.PSVersion.ToString()
        }
        tool              = [ordered] @{
            path    = $ToolPath
            version = $ToolVersion
            sha256  = Get-FileSha256 -Path $ToolPath
        }
        engines           = $Engines
        book              = [ordered] @{
            path   = $BookPath
            sha256 = if ($BookPath) { Get-FileSha256 -Path $BookPath } else { $null }
        }
        concurrency       = $Concurrency

        # The exact argument vector, not a re-rendered approximation of it, so the run can be
        # repeated by copying it.
        commandLine       = $CommandLine
    }

    if ($null -ne $Extra) {
        foreach ($key in $Extra.Keys) { $manifest[$key] = $Extra[$key] }
    }

    $path = Join-Path $OutDir 'run_manifest.json'
    $manifest | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $path -Encoding utf8
    return $path
}

<#
.SYNOPSIS
    Prepares an opening book fastchess can actually read, sanitising it if necessary.

.DESCRIPTION
    fastchess parses an EPD book strictly: every non-empty line must be a position, and it
    aborts the tournament mid-run when it meets one that is not. This repository's own generated
    books carry a two-line header — the generator's seed and a content hash — written as '#'
    comments, which ChessBot.MatchRunner skips and fastchess does not. The failure is
    particularly unpleasant because it does not happen at startup: the book is read lazily, so a
    run dies at whatever game first reaches the comment line, after burning however much time it
    had already spent.

    So a book containing comment or blank lines is copied, stripped, into the run directory, and
    the run uses the copy. The copy lives with the run rather than replacing the original,
    because the original is what the manifest records and what the operator asked for; both
    hashes go into the manifest so the substitution is visible rather than silent.

.OUTPUTS
    A hashtable: Path (what to hand fastchess), Format ('epd' or 'pgn'), Original, Sanitised.
#>
function Resolve-OpeningBook {
    param(
        [Parameter(Mandatory)] [string] $Book,
        [Parameter(Mandatory)] [string] $OutDir
    )

    if (-not (Test-Path -LiteralPath $Book -PathType Leaf)) {
        throw "Opening book not found: $Book"
    }

    $original = (Resolve-Path -LiteralPath $Book).Path
    $format   = if ([IO.Path]::GetExtension($original) -ieq '.pgn') { 'pgn' } else { 'epd' }

    $result = @{
        Path      = $original
        Format    = $format
        Original  = $original
        Sanitised = $false
    }

    # Only EPD. A PGN book's comments are part of the format and fastchess reads them.
    if ($format -ne 'epd') { return $result }

    $lines = Get-Content -LiteralPath $original
    $clean = @($lines | Where-Object { $_.Trim() -and -not $_.TrimStart().StartsWith('#') })

    if ($clean.Count -eq $lines.Count) { return $result }

    $sanitisedPath = Join-Path $OutDir ([IO.Path]::GetFileNameWithoutExtension($original) + '.sanitised.epd')
    Set-Content -LiteralPath $sanitisedPath -Value $clean -Encoding ascii

    Write-Warning ("Opening book $([IO.Path]::GetFileName($original)) has " +
        "$($lines.Count - $clean.Count) comment/blank line(s), which fastchess rejects mid-run. " +
        "Using a stripped copy: $sanitisedPath")

    $result.Path      = $sanitisedPath
    $result.Sanitised = $true
    return $result
}

<#
.SYNOPSIS
    Formats a number for display with a '.' separator whatever the machine's locale is.

.DESCRIPTION
    The console summary is read next to fastchess's own output and pasted into commit messages,
    and on a German Windows the default formatting turns -5.79 into "-5,79". The JSON is already
    safe — ConvertTo-Json is invariant — but the thing a human actually copies was not.
#>
function Format-Invariant {
    param($Value, [string] $Format = 'G')

    if ($null -eq $Value) { return 'n/a' }
    if ($Value -is [double] -and ([double]::IsNaN($Value) -or [double]::IsInfinity($Value))) {
        return "$Value"
    }

    return [string]::Format([Globalization.CultureInfo]::InvariantCulture, "{0:$Format}", $Value)
}

<#
.SYNOPSIS
    Reads the version banner of fastchess or Ordo.
#>
function Get-ToolVersion {
    param([Parameter(Mandatory)] [string] $Exe, [string[]] $VersionArgs = @('--version'))

    try {
        $output = & $Exe @VersionArgs 2>&1 | Out-String
        return ($output -split "`r?`n" | Where-Object { $_.Trim() } | Select-Object -First 1).Trim()
    } catch {
        return 'unknown'
    }
}

Export-ModuleMember -Function Get-PhysicalCoreCount, Get-FileSha256, Get-EngineIdName,
    Resolve-ExternalTool, Get-EngineDescriptor, Write-RunManifest, Get-ToolVersion,
    Resolve-OpeningBook, Format-Invariant
