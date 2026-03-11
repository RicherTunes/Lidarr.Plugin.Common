#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Unified test runner for Lidarr plugin ecosystem.

.DESCRIPTION
    This script provides a standardized way to run tests across all plugins,
    supporting category-based filtering and integration with the shared
    test-runner.psm1 module.

    It replaces ad-hoc `dotnet test --filter` calls in CI workflows with
    a consistent interface that:
    - Automatically excludes expensive categories by default (fast lane)
    - Supports running specific categories on demand
    - Parses and displays TRX results
    - Integrates with CI systems (GitHub Actions annotations)

.PARAMETER Category
    Run tests for specific category(s). Multiple categories can be specified.
    When specified, ExcludeCategories is ignored.

    Valid values: Integration, Packaging, LibraryLinking, Benchmark, Slow

.PARAMETER ExcludeCategories
    Categories to exclude from the test run.
    Default: Integration, Packaging, LibraryLinking, Benchmark, Slow

    This is the standard "fast lane" exclusion for PR builds.

.PARAMETER TestProject
    Path to the test project or solution. Supports wildcards.
    Default: Searches for *.Tests.csproj or *.sln in current directory.

.PARAMETER Configuration
    Build configuration (Debug or Release).
    Default: Release

.PARAMETER Coverage
    Enable code coverage collection with XPlat Code Coverage.

.PARAMETER NoBuild
    Skip the build step (assumes project is already built).

.PARAMETER OutputDir
    Directory for test results.
    Default: TestResults

.PARAMETER CI
    Enable CI mode with GitHub Actions annotations and stricter failure handling.

.PARAMETER IncludeQuarantined
    Include tests marked with [Trait("State", "Quarantined")].
    By default, quarantined tests are excluded from all runs.
    Use this for weekly quarantine verification.

.PARAMETER Verbose
    Enable verbose output from dotnet test.

.EXAMPLE
    ./test.ps1
    # Runs fast unit tests (excludes Integration/Packaging/LibraryLinking/Benchmark/Slow)

.EXAMPLE
    ./test.ps1 -Category Integration
    # Runs only Integration tests

.EXAMPLE
    ./test.ps1 -Category Packaging,LibraryLinking -RequirePackage
    # Runs packaging tests

.EXAMPLE
    ./test.ps1 -Coverage -CI
    # Runs fast tests with coverage in CI mode

.EXAMPLE
    ./test.ps1 -ExcludeCategories @() -Coverage
    # Runs ALL tests with coverage (no exclusions)

.NOTES
    This script uses the shared test-runner.psm1 module for:
    - TRX result parsing
    - Standard argument generation
    - Artifact validation

    Category conventions are documented in docs/TESTING_WITH_TESTKIT.md
#>

[CmdletBinding()]
param(
    [ValidateSet('Integration', 'Packaging', 'LibraryLinking', 'Benchmark', 'Slow')]
    [string[]]$Category = @(),

    [string[]]$ExcludeCategories = @('Integration', 'Packaging', 'LibraryLinking', 'Benchmark', 'Slow'),

    [string]$TestProject = "",

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$Coverage,

    [switch]$NoBuild,

    [string]$OutputDir = "TestResults",

    [switch]$CI,

    [switch]$IncludeQuarantined,

    [switch]$VerboseOutput,

    # Additional filter expressions to append (e.g., "scope!=cli")
    [string]$AdditionalFilter = "",

    # Additional MSBuild properties (e.g., "SkipHostBridge=true")
    [string[]]$Properties = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

#region Module Import

$ScriptDir = $PSScriptRoot
$LibDir = Join-Path $ScriptDir "lib"
$TestRunnerModule = Join-Path $LibDir "test-runner.psm1"

if (Test-Path $TestRunnerModule) {
    Import-Module $TestRunnerModule -Force
    $ModuleLoaded = $true
} else {
    Write-Warning "test-runner.psm1 not found at: $TestRunnerModule"
    Write-Warning "Some features (TRX parsing, summary display) will be unavailable."
    $ModuleLoaded = $false
}

#endregion

#region Test Project Discovery

function Find-TestProject {
    param([string]$Hint)

    if ($Hint -and (Test-Path $Hint)) {
        return $Hint
    }

    # Try to find test project in common locations
    $candidates = @(
        (Get-ChildItem -Filter "*.Tests.csproj" -Recurse -Depth 3 -ErrorAction SilentlyContinue | Select-Object -First 1),
        (Get-ChildItem -Filter "*Tests.csproj" -Recurse -Depth 3 -ErrorAction SilentlyContinue | Select-Object -First 1),
        (Get-ChildItem -Filter "*.sln" -Depth 1 -ErrorAction SilentlyContinue | Select-Object -First 1)
    )

    foreach ($candidate in $candidates) {
        if ($candidate) {
            return $candidate.FullName
        }
    }

    throw "Could not find a test project. Specify -TestProject explicitly."
}

#endregion

#region Filter Construction

function Build-TestFilter {
    param(
        [string[]]$IncludeCategories,
        [string[]]$ExcludeCategories,
        [bool]$ExcludeQuarantined = $true,
        [string]$AdditionalFilter = ""
    )

    $parts = @()

    # Category filtering
    if ($IncludeCategories.Count -gt 0) {
        # Include mode: (Category=X|Category=Y)
        $categoryPart = "(" + (($IncludeCategories | ForEach-Object { "Category=$_" }) -join "|") + ")"
        $parts += $categoryPart
    } elseif ($ExcludeCategories.Count -gt 0) {
        # Exclude mode: Category!=X&Category!=Y
        $parts += ($ExcludeCategories | ForEach-Object { "Category!=$_" })
    }

    # State=Quarantined exclusion (default: exclude)
    if ($ExcludeQuarantined) {
        $parts += "State!=Quarantined"
    }

    # Additional repo-specific filters (e.g., "scope!=cli")
    if ($AdditionalFilter) {
        $parts += $AdditionalFilter
    }

    if ($parts.Count -gt 0) {
        return $parts -join "&"
    }

    return ""
}

#endregion

#region Main Execution

# Apply build server hardening to prevent file-lock issues on Windows
if ($ModuleLoaded) {
    Set-BuildServerHardening
}
else {
    # Fallback if module not loaded
    $env:DOTNET_CLI_DISABLE_BUILD_SERVERS = "1"
    $env:MSBUILDDISABLENODEREUSE = "1"
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Lidarr Plugin Test Runner" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Find test project
$project = Find-TestProject -Hint $TestProject
Write-Host "Test Project: $project" -ForegroundColor White

# Build filter
$effectiveExclusions = if ($Category.Count -gt 0) { @() } else { $ExcludeCategories }
$filter = Build-TestFilter -IncludeCategories $Category -ExcludeCategories $effectiveExclusions -ExcludeQuarantined (-not $IncludeQuarantined) -AdditionalFilter $AdditionalFilter

if ($Category.Count -gt 0) {
    Write-Host "Mode: Category Include ($($Category -join ', '))" -ForegroundColor Yellow
} elseif ($effectiveExclusions.Count -gt 0) {
    Write-Host "Mode: Fast Lane (excluding: $($effectiveExclusions -join ', '))" -ForegroundColor Green
} else {
    Write-Host "Mode: All Tests (no category exclusions)" -ForegroundColor Magenta
}

if (-not $IncludeQuarantined) {
    Write-Host "Quarantine: Excluded by default (use -IncludeQuarantined to include)" -ForegroundColor DarkGray
} else {
    Write-Host "Quarantine: INCLUDED (running quarantined tests)" -ForegroundColor Yellow
}

if ($AdditionalFilter) {
    Write-Host "Additional Filter: $AdditionalFilter" -ForegroundColor DarkGray
}

Write-Host "Configuration: $Configuration" -ForegroundColor White
Write-Host "Output: $OutputDir" -ForegroundColor White
if ($Coverage) { Write-Host "Coverage: Enabled" -ForegroundColor Cyan }
if ($CI) { Write-Host "CI Mode: Enabled" -ForegroundColor Cyan }
if ($Properties.Count -gt 0) { Write-Host "Properties: $($Properties -join ', ')" -ForegroundColor DarkGray }
Write-Host ""

# Ensure output directory exists
if (-not (Test-Path $OutputDir)) {
    New-Item -ItemType Directory -Path $OutputDir -Force | Out-Null
}

# Clear stale TRX files so summary reflects only this run
if ($ModuleLoaded) {
    Clear-StaleTrxFiles -OutputDir $OutputDir
}
else {
    Get-ChildItem -Path $OutputDir -Filter "*.trx" -ErrorAction SilentlyContinue |
        Remove-Item -Force -ErrorAction SilentlyContinue
}

# Build if needed
if (-not $NoBuild) {
    Write-Host "Building project..." -ForegroundColor Cyan
    $buildArgs = @(
        "build", $project,
        "--configuration", $Configuration,
        "-p:RunAnalyzersDuringBuild=false",
        "-p:EnableNETAnalyzers=false",
        "-p:TreatWarningsAsErrors=false"
    )

    # Add hardening args to prevent file-lock issues on Windows
    if ($ModuleLoaded) {
        $buildArgs += Get-BuildHardeningArgs
    }
    else {
        $buildArgs += @("/m:1", "/p:BuildInParallel=false", "/p:UseSharedCompilation=false")
    }

    if (-not $VerboseOutput) {
        $buildArgs += @("--verbosity", "minimal")
    }

    & dotnet @buildArgs
    if ($LASTEXITCODE -ne 0) {
        Write-Host "Build failed with exit code $LASTEXITCODE" -ForegroundColor Red
        exit $LASTEXITCODE
    }
    Write-Host ""
}

# Construct test arguments
$trxFileName = "Tests-$(Get-Date -Format 'yyyyMMdd-HHmmss').trx"
$testArgs = @(
    "test", $project,
    "--configuration", $Configuration,
    "--no-build",
    "--logger", "trx;LogFileName=$trxFileName",
    "--results-directory", $OutputDir
)

if ($filter) {
    $testArgs += @("--filter", $filter)
    Write-Host "Filter: $filter" -ForegroundColor DarkGray
}

if ($Coverage) {
    $testArgs += @("--collect", "XPlat Code Coverage")
}

if ($VerboseOutput) {
    $testArgs += @("--verbosity", "detailed")
} else {
    $testArgs += @("--verbosity", "normal")
}

# Add additional MSBuild properties
foreach ($prop in $Properties) {
    $testArgs += @("-p:$prop")
}

Write-Host ""
Write-Host "Running tests..." -ForegroundColor Cyan
Write-Host "dotnet $($testArgs -join ' ')" -ForegroundColor DarkGray
Write-Host ""

& dotnet @testArgs
$testExitCode = $LASTEXITCODE

Write-Host ""

# Parse and display results
$trxFile = Get-ChildItem -Path $OutputDir -Filter "*.trx" -Recurse -ErrorAction SilentlyContinue |
    Sort-Object LastWriteTime -Descending |
    Select-Object -First 1

if ($trxFile -and $ModuleLoaded) {
    $summary = Get-TrxTestSummary -TrxPath $trxFile.FullName
    if ($summary) {
        Write-Host ""
        Write-TestSummary -Summary $summary

        # CI annotations
        if ($CI -and $summary.Failed -gt 0) {
            Write-Host "::error::$($summary.Failed) test(s) failed" -ForegroundColor Red
        }
    }
} elseif ($trxFile) {
    Write-Host "TRX file generated: $($trxFile.FullName)" -ForegroundColor Gray
}

# Summary line
Write-Host ""
if ($testExitCode -eq 0) {
    Write-Host "[PASS] All tests passed" -ForegroundColor Green
} else {
    Write-Host "[FAIL] Tests failed (exit code: $testExitCode)" -ForegroundColor Red
}

exit $testExitCode

#endregion
