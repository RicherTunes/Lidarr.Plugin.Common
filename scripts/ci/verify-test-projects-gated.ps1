#!/usr/bin/env pwsh
<#
.SYNOPSIS
    F4 Test-project dropout guard.

.DESCRIPTION
    Asserts every *.Tests.csproj discovered under a plugin repo root is either
    (a) in the set of projects the repo's CI actually runs, or
    (b) explicitly listed in the repo's .ci-test-skip.json skip manifest.

    Any test project that is neither run nor skip-listed causes a non-zero exit,
    naming the offending project — making parity guards that "never run" impossible
    to ship silently.

    The ext/ directory is excluded from discovery because it contains the
    Lidarr.Plugin.Common submodule, whose test projects are not the plugin's
    responsibility.

.PARAMETER RepoRoot
    Root directory of the plugin repo to inspect.

.PARAMETER RunProjects
    Array of test project paths (relative to RepoRoot OR absolute) that the
    repo's CI/verify-local.ps1 passes to the test runner. These are matched
    against discovered projects after path normalisation.

.PARAMETER CI
    Terse output mode (single PASS/FAIL line per outcome).

.PARAMETER DefineFunctionsOnly
    Define pure functions and return without running the main check.
    Used by Test-VerifyTestProjectsGated.ps1 to exercise functions hermetically.

.EXAMPLE
    # From a plugin's verify-local.ps1 — pass the existing TestProjects array:
    $Common = 'ext/Lidarr.Plugin.Common'
    & "$Common/scripts/ci/verify-test-projects-gated.ps1" `
        -RepoRoot $repoRoot -RunProjects $config.TestProjects

.EXAMPLE
    # Standalone check against tidalarr
    pwsh scripts/ci/verify-test-projects-gated.ps1 `
        -RepoRoot C:\R\Alex\github\tidalarr `
        -RunProjects @('tests/Tidalarr.Tests/Tidalarr.Tests.csproj')

.NOTES
    Skip manifest (.ci-test-skip.json at repo root) format:
    {
      "skip": [
        {
          "project": "tests/Foo.Parity.Tests/Foo.Parity.Tests.csproj",
          "reason":  "requires Apple SDK; covered by Foo.Tests integration suite"
        }
      ]
    }
    Project paths in the manifest are relative to the repo root, forward-slash.
#>

[CmdletBinding()]
param(
    [string]$RepoRoot,
    [string[]]$RunProjects    = @(),
    [switch]$CI,
    [switch]$DefineFunctionsOnly
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ============================================================
# Pure helper functions
# ============================================================

function Resolve-ProjectKey {
    <#
    .SYNOPSIS
        Normalise a project path to a comparable key: relative to the repo root,
        lower-case, forward-slash separated.
    #>
    param([string]$Path, [string]$Root)

    $rootNorm = $Root.TrimEnd('\', '/')
    if ([System.IO.Path]::IsPathRooted($Path)) {
        $rel = $Path.Substring($rootNorm.Length).TrimStart('\', '/')
    }
    else {
        $rel = $Path.TrimStart('\', '/')
    }
    return $rel.Replace('\', '/').ToLowerInvariant()
}

function Get-SkipManifest {
    <#
    .SYNOPSIS
        Read .ci-test-skip.json at the repo root. Returns an empty array if missing.
    #>
    param([string]$Root)

    $skipFile = Join-Path $Root '.ci-test-skip.json'
    if (-not (Test-Path -LiteralPath $skipFile)) { return @() }

    $raw = Get-Content -LiteralPath $skipFile -Raw | ConvertFrom-Json
    if ($null -eq $raw -or $null -eq $raw.skip) { return @() }

    return @($raw.skip | ForEach-Object {
        [PSCustomObject]@{
            Project = [string]$_.project
            Reason  = [string]$_.reason
        }
    })
}

function Test-AllTestProjectsGated {
    <#
    .SYNOPSIS
        Pure assertion: every discovered test project is run or skip-listed.
    .OUTPUTS
        PSCustomObject { Ok, Dropouts }
        Dropouts is an array of PSCustomObject { Key, Path }
    #>
    param(
        [string]  $RepoRoot,
        [string[]]$RunProjects,
        [object[]]$SkipEntries
    )

    $root = $RepoRoot.TrimEnd('\', '/')

    # Normalise the run set and skip set to comparable keys.
    # Filter nulls explicitly: PS7 collapses empty function return arrays to $null,
    # and $null | ForEach-Object iterates once with $_ = $null — causing property errors.
    $runKeys  = @($RunProjects | Where-Object { $_ } | ForEach-Object { Resolve-ProjectKey $_ $root })
    $skipKeys = @($SkipEntries | Where-Object { $_ } | ForEach-Object { Resolve-ProjectKey $_.Project $root })

    # Paths to exclude from discovery (submodule + git internals)
    $sep      = [System.IO.Path]::DirectorySeparatorChar
    $excluded = @(
        (Join-Path $root 'ext')  + $sep
        (Join-Path $root '.git') + $sep
    )

    # Discover all *.Tests.csproj recursively (covers *.Parity.Tests.csproj,
    # *.Cli.Tests.csproj, etc. since all end with .Tests.csproj)
    $discovered = @(
        Get-ChildItem -LiteralPath $root -Recurse -Filter '*.Tests.csproj' -File -ErrorAction SilentlyContinue |
            Where-Object {
                $fp = $_.FullName
                -not ($excluded | Where-Object { $fp.StartsWith($_, [System.StringComparison]::OrdinalIgnoreCase) })
            }
    )

    $dropouts = [System.Collections.Generic.List[PSCustomObject]]::new()

    foreach ($proj in $discovered) {
        $key = Resolve-ProjectKey $proj.FullName $root
        if (-not ($runKeys -contains $key) -and -not ($skipKeys -contains $key)) {
            $dropouts.Add([PSCustomObject]@{ Key = $key; Path = $proj.FullName })
        }
    }

    return [PSCustomObject]@{
        Ok       = $dropouts.Count -eq 0
        Dropouts = @($dropouts)
    }
}

# ============================================================
# Early return when invoked for unit-test dot-sourcing
# ============================================================

if ($DefineFunctionsOnly) { return }

# ============================================================
# Main: validate and report
# ============================================================

if (-not $RepoRoot) {
    Write-Host 'ERROR: -RepoRoot is required.' -ForegroundColor Red
    exit 1
}
if (-not (Test-Path -LiteralPath $RepoRoot)) {
    Write-Host "ERROR: RepoRoot not found: $RepoRoot" -ForegroundColor Red
    exit 1
}

$skipEntries = Get-SkipManifest -Root $RepoRoot
$result      = Test-AllTestProjectsGated -RepoRoot $RepoRoot -RunProjects $RunProjects -SkipEntries $skipEntries

if ($result.Ok) {
    if (-not $CI) {
        Write-Host '[PASS] All test projects are gated (run or skip-listed).' -ForegroundColor Green
    }
    else {
        Write-Host 'PASS all-test-projects-gated' -ForegroundColor Green
    }
    exit 0
}

foreach ($d in $result.Dropouts) {
    if ($CI) {
        Write-Host "FAIL test-project-dropout: $($d.Key)" -ForegroundColor Red
    }
    else {
        Write-Host '[FAIL] Test project is neither run nor skip-listed:' -ForegroundColor Red
        Write-Host "       $($d.Path)" -ForegroundColor Red
        Write-Host "  Relative: $($d.Key)" -ForegroundColor Yellow
        Write-Host '  Fix: add to RunProjects in verify-local.ps1, or add to .ci-test-skip.json with a reason.' -ForegroundColor Yellow
        Write-Host ''
    }
}

exit 1
