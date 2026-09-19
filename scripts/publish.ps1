#Requires -Version 7.0
<#
.SYNOPSIS
    Builds the release executable and says what it is.

.DESCRIPTION
    Runs the self-contained single-file publish (ChessBot.Uci/Properties/PublishProfiles), copies
    the result to a file named after the release — KornKnight-<version>-<runtime>[.exe] — and
    prints its path and the "id name" the binary itself reports when handed "uci" on stdin.

    The id is read from the binary, not from the project file, because the binary is what gets
    shipped: it names the version and the commit the file was built from, and it is the line a
    tester's GUI will show.

    A working tree with uncommitted changes is refused by the build itself (see
    StampBuildCommit in ChessBot.Uci.csproj): a published binary whose id says "dirty" cannot be
    rebuilt from any commit. -AllowDirty passes the override through, for a throwaway build.

.PARAMETER Runtime
    win-x64 (default) or linux-x64. Only a binary for this machine's OS is run to read its id.

.PARAMETER Project
    The UCI project. Defaults to this repository's.

.PARAMETER OutDir
    Where the named release file is written. Defaults to publish/.

.EXAMPLE
    pwsh scripts/publish.ps1

.EXAMPLE
    pwsh scripts/publish.ps1 -Runtime linux-x64
#>
[CmdletBinding()]
param(
    [ValidateSet('win-x64', 'linux-x64')]
    [string] $Runtime = 'win-x64',
    [string] $Project = (Join-Path $PSScriptRoot '..\ChessBot.Uci\ChessBot.Uci.csproj'),
    [string] $OutDir  = (Join-Path $PSScriptRoot '..\publish'),
    [switch] $AllowDirty
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..\tools\lib\RunSupport.psm1') -Force

$Project = (Resolve-Path -LiteralPath $Project).Path
$version = ([xml] (Get-Content -LiteralPath $Project -Raw)).Project.PropertyGroup.Version |
    Where-Object { $_ } | Select-Object -First 1
if (-not $version) { throw "No <Version> in $Project" }

$publishArgs = @('publish', $Project, "-p:PublishProfile=$Runtime")
if ($AllowDirty) { $publishArgs += '-p:AllowDirtyPublish=true' }

Write-Host "dotnet $($publishArgs -join ' ')" -ForegroundColor DarkGray
& dotnet @publishArgs
if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed (exit $LASTEXITCODE)" }

# The profile's PublishDir: publish/<runtime>/ next to the project folder.
$suffix   = if ($Runtime -like 'win-*') { '.exe' } else { '' }
$built    = Join-Path (Split-Path $Project -Parent) "..\publish\$Runtime\ChessBot.Uci$suffix"
$built    = (Resolve-Path -LiteralPath $built).Path

$null     = New-Item -ItemType Directory -Force -Path $OutDir
$release  = Join-Path (Resolve-Path -LiteralPath $OutDir).Path "KornKnight-$version-$Runtime$suffix"
Copy-Item -LiteralPath $built -Destination $release -Force

$sizeMb   = [Math]::Round((Get-Item -LiteralPath $release).Length / 1MB, 1)
Write-Host ''
Write-Host "release : $release ($sizeMb MB)" -ForegroundColor Cyan
Write-Host "sha256  : $(Get-FileSha256 -Path $release)"

$runsHere = ($Runtime -like 'win-*' -and $IsWindows) -or ($Runtime -like 'linux-*' -and $IsLinux)
if ($runsHere) {
    $idName = Get-EngineIdName -Exe $release
    if (-not $idName) { throw "The published binary did not answer 'uci' with an id name." }
    Write-Host "id name : $idName" -ForegroundColor Cyan

    if ($idName -notlike "KornKnight $version+*") {
        throw "The binary reports '$idName', not version $version."
    }
} else {
    Write-Host "id name : (not run — a $Runtime binary does not run on this machine)" -ForegroundColor Yellow
}
