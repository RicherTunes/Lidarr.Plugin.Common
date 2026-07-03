#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Self-tests for the F4 test-project dropout guard.

.DESCRIPTION
    Builds tiny fake repo layouts in a temp dir, dot-sources the guard's pure
    functions via -DefineFunctionsOnly, and exercises each case:

      - Project in RunProjects                     => PASS
      - Project missing from RunProjects + no skip => FAIL (named)
      - Project in .ci-test-skip.json              => PASS
      - *.Parity.Tests.csproj is discovered        => appears as dropout
      - *.Cli.Tests.csproj is discovered            => appears as dropout
      - ext/ submodule projects are excluded        => not reported
      - Absolute RunProject path is normalised      => accepted

    Also runs the full guard script end-to-end and checks exit codes.

    Temp fixtures are removed after the test regardless of outcome.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptDir = $PSScriptRoot
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$Guard     = Join-Path $RepoRoot 'scripts/ci/verify-test-projects-gated.ps1'

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host 'F4 Test-Project Dropout Guard Self-Tests' -ForegroundColor Cyan
Write-Host '========================================' -ForegroundColor Cyan
Write-Host ''

if (-not (Test-Path -LiteralPath $Guard)) {
    Write-Host "FATAL: guard not found: $Guard" -ForegroundColor Red
    Write-Host '  (Run this test AFTER implementing verify-test-projects-gated.ps1)' -ForegroundColor Yellow
    exit 1
}

. $Guard -DefineFunctionsOnly

$passed = 0
$failed = 0

function Test-Assertion {
    param([string]$Name, [scriptblock]$Test)
    Write-Host "  Testing: $Name..." -NoNewline
    try {
        $result = & $Test
        if ($result) {
            Write-Host ' PASS' -ForegroundColor Green
            $script:passed++
        }
        else {
            Write-Host ' FAIL' -ForegroundColor Red
            $script:failed++
        }
    }
    catch {
        Write-Host " ERROR: $_" -ForegroundColor Red
        $script:failed++
    }
}

$TempDir = Join-Path ([System.IO.Path]::GetTempPath()) "test-projects-gated-$([System.Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $TempDir -Force | Out-Null

try {
    # Helper: create a fake repo with named test projects on disk
    function New-FakeRepo {
        param([string]$Dir, [string[]]$TestProjects)
        New-Item -ItemType Directory -Path $Dir -Force | Out-Null
        foreach ($proj in $TestProjects) {
            $full = Join-Path $Dir $proj
            New-Item -ItemType Directory -Path (Split-Path $full) -Force | Out-Null
            Set-Content $full '<Project Sdk="Microsoft.NET.Sdk" />'
        }
    }

    # ============================================================
    # Unit tests: pure assertion functions
    # ============================================================

    Write-Host 'Unit tests: Test-AllTestProjectsGated' -ForegroundColor White

    # --- Repo A: one project, in RunProjects ---------------------
    $DirA = Join-Path $TempDir 'repo-run'
    New-FakeRepo $DirA @('tests/Foo.Tests/Foo.Tests.csproj')

    Test-Assertion 'Project in RunProjects => Ok=$true' {
        $r = Test-AllTestProjectsGated -RepoRoot $DirA `
                -RunProjects @('tests/Foo.Tests/Foo.Tests.csproj') `
                -SkipEntries @()
        $r.Ok -eq $true -and $r.Dropouts.Count -eq 0
    }

    # --- Repo B: parity project NOT in RunProjects, no skip ------
    $DirB = Join-Path $TempDir 'repo-parity-dropout'
    New-FakeRepo $DirB @(
        'tests/Foo.Tests/Foo.Tests.csproj',
        'tests/Foo.Parity.Tests/Foo.Parity.Tests.csproj'
    )

    Test-Assertion 'Parity project absent from RunProjects => Ok=$false, named dropout' {
        $r = Test-AllTestProjectsGated -RepoRoot $DirB `
                -RunProjects @('tests/Foo.Tests/Foo.Tests.csproj') `
                -SkipEntries @()
        $r.Ok -eq $false -and
        $r.Dropouts.Count -eq 1 -and
        $r.Dropouts[0].Key -match 'parity'
    }

    # --- Repo C: parity project in skip manifest -----------------
    $DirC = Join-Path $TempDir 'repo-parity-skip'
    New-FakeRepo $DirC @(
        'tests/Foo.Tests/Foo.Tests.csproj',
        'tests/Foo.Parity.Tests/Foo.Parity.Tests.csproj'
    )

    Test-Assertion 'Parity project in skip manifest => Ok=$true' {
        $skipEntries = @([PSCustomObject]@{
            Project = 'tests/Foo.Parity.Tests/Foo.Parity.Tests.csproj'
            Reason  = 'requires Apple SDK; covered by integration suite'
        })
        $r = Test-AllTestProjectsGated -RepoRoot $DirC `
                -RunProjects @('tests/Foo.Tests/Foo.Tests.csproj') `
                -SkipEntries $skipEntries
        $r.Ok -eq $true
    }

    # --- Repo D: Cli.Tests dropout --------------------------------
    $DirD = Join-Path $TempDir 'repo-cli-dropout'
    New-FakeRepo $DirD @(
        'tests/Foo.Tests/Foo.Tests.csproj',
        'tests/Foo.Cli.Tests/Foo.Cli.Tests.csproj'
    )

    Test-Assertion 'Cli.Tests absent from RunProjects => Ok=$false, named dropout' {
        $r = Test-AllTestProjectsGated -RepoRoot $DirD `
                -RunProjects @('tests/Foo.Tests/Foo.Tests.csproj') `
                -SkipEntries @()
        $r.Ok -eq $false -and
        $r.Dropouts.Count -eq 1 -and
        $r.Dropouts[0].Key -match 'cli'
    }

    # --- Repo E: ext/ submodule projects excluded -----------------
    $DirE = Join-Path $TempDir 'repo-ext-excluded'
    New-FakeRepo $DirE @(
        'tests/Foo.Tests/Foo.Tests.csproj',
        'ext/Lidarr.Plugin.Common/tests/Common.Tests/Common.Tests.csproj'
    )

    Test-Assertion 'ext/ submodule test projects excluded from discovery' {
        $r = Test-AllTestProjectsGated -RepoRoot $DirE `
                -RunProjects @('tests/Foo.Tests/Foo.Tests.csproj') `
                -SkipEntries @()
        $r.Ok -eq $true
    }

    # --- Repo F: absolute path in RunProjects ---------------------
    $DirF = Join-Path $TempDir 'repo-abs-path'
    New-FakeRepo $DirF @('tests/Foo.Tests/Foo.Tests.csproj')
    $absPath = Join-Path $DirF 'tests/Foo.Tests/Foo.Tests.csproj'

    Test-Assertion 'Absolute RunProject path normalises and is accepted' {
        $r = Test-AllTestProjectsGated -RepoRoot $DirF `
                -RunProjects @($absPath) `
                -SkipEntries @()
        $r.Ok -eq $true
    }

    # --- Repo G: empty repo (no test projects) -------------------
    $DirG = Join-Path $TempDir 'repo-empty'
    New-Item -ItemType Directory -Path $DirG -Force | Out-Null

    Test-Assertion 'Repo with no test projects passes (vacuously)' {
        $r = Test-AllTestProjectsGated -RepoRoot $DirG -RunProjects @() -SkipEntries @()
        $r.Ok -eq $true
    }

    # --- Resolve-ProjectKey normalisation -------------------------
    Write-Host ''
    Write-Host 'Unit tests: Resolve-ProjectKey' -ForegroundColor White

    Test-Assertion 'Absolute path becomes relative, lower-case, forward-slash' {
        # Use real OS paths (Join-Path) so IsPathRooted works on both Windows and Linux
        $k = Resolve-ProjectKey $absPath $DirF
        $k -eq 'tests/foo.tests/foo.tests.csproj'
    }

    Test-Assertion 'Relative path is normalised without root stripping' {
        $k = Resolve-ProjectKey 'tests/Foo.Tests/Foo.Tests.csproj' 'C:\irrelevant'
        $k -eq 'tests/foo.tests/foo.tests.csproj'
    }

    Test-Assertion 'Backslash relative path normalised to forward slash' {
        $k = Resolve-ProjectKey 'tests\Foo.Tests\Foo.Tests.csproj' 'C:\irrelevant'
        $k -eq 'tests/foo.tests/foo.tests.csproj'
    }

    # ============================================================
    # End-to-end: run full guard script
    # ============================================================

    Write-Host ''
    Write-Host 'End-to-end: full guard script' -ForegroundColor White

    Test-Assertion 'Guard exits 0 when all projects run' {
        $null = & pwsh -NoProfile -File $Guard `
            -RepoRoot $DirA `
            -RunProjects @('tests/Foo.Tests/Foo.Tests.csproj') `
            -CI *>&1
        $LASTEXITCODE -eq 0
    }

    Test-Assertion 'Guard exits 1 when parity project not run or skip-listed' {
        $null = & pwsh -NoProfile -File $Guard `
            -RepoRoot $DirB `
            -RunProjects @('tests/Foo.Tests/Foo.Tests.csproj') `
            -CI *>&1
        $LASTEXITCODE -ne 0
    }

    Test-Assertion 'Guard exits 0 when missing project is in .ci-test-skip.json' {
        $skipJson = @{
            skip = @(
                @{
                    project = 'tests/Foo.Parity.Tests/Foo.Parity.Tests.csproj'
                    reason  = 'requires Apple SDK'
                }
            )
        } | ConvertTo-Json -Depth 5
        Set-Content (Join-Path $DirC '.ci-test-skip.json') $skipJson

        $null = & pwsh -NoProfile -File $Guard `
            -RepoRoot $DirC `
            -RunProjects @('tests/Foo.Tests/Foo.Tests.csproj') `
            -CI *>&1
        $LASTEXITCODE -eq 0
    }

    Test-Assertion 'Guard output names the dropped-out project on failure' {
        $output = (& pwsh -NoProfile -File $Guard `
            -RepoRoot $DirB `
            -RunProjects @('tests/Foo.Tests/Foo.Tests.csproj') `
            -CI *>&1) -join "`n"
        $output -match 'parity'
    }
}
finally {
    Remove-Item -Recurse -Force $TempDir -ErrorAction SilentlyContinue
}

Write-Host ''
Write-Host '========================================' -ForegroundColor Cyan
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
Write-Host '========================================' -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }

Write-Host ''
Write-Host '[OK] All test-project dropout guard tests passed.' -ForegroundColor Green
exit 0
