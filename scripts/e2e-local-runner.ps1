#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Shared local Docker E2E harness runner for Lidarr plugin repositories.

.DESCRIPTION
    Thin orchestrator invoked by each plugin's scripts/e2e.ps1 shim.  Handles the
    three steps that are identical across all four plugins:

      1. Print a banner with the plugin name.
      2. Docker engine preflight (warns but does not fail when Docker is absent —
         xUnit tests skip gracefully on their own).
      3. Optionally build the plugin via verify-local.ps1 -SkipExtract -SkipTests
         (with an optional fallback to dotnet build when the verify script is
         absent and -FallbackBuildOnMissingVerify is set).
      4. Run dotnet test with the standard flags used across the ecosystem.

    Per-plugin differences are injected via parameters:
      - -PluginName      : banner text and error messages
      - -TestProject     : path passed to dotnet test (relative to repo root)
      - -ExtraBuildArgs  : extra arguments forwarded to dotnet build (e.g., "-m:1")
      - -FallbackBuildOnMissingVerify : brainarr-style fallback when verify-local.ps1 absent

.PARAMETER PluginName
    Human-readable plugin name used in banner and log messages (e.g., "Brainarr").

.PARAMETER TestProject
    Path to the test .csproj, relative to the calling repo root
    (e.g., "Brainarr.Tests/Brainarr.Tests.csproj").

.PARAMETER VerifyLocalScript
    Path to verify-local.ps1, relative to the calling repo root.
    Defaults to "scripts/verify-local.ps1".

.PARAMETER SkipBuild
    Skip the build prep step entirely.  Use when the merged plugin DLL is already
    present at its expected location.

.PARAMETER Configuration
    Build/test configuration.  Defaults to Release.

.PARAMETER Filter
    xUnit test filter string.  Defaults to Category=DockerE2E.

.PARAMETER ExtraBuildArgs
    Extra arguments appended to the dotnet build fallback invocation.
    Brainarr passes "-m:1" here to work around Windows file-lock races.
    Not forwarded to verify-local.ps1 (which has its own arg surface).

.PARAMETER FallbackBuildOnMissingVerify
    When set and verify-local.ps1 is not found, fall back to a bare
    dotnet build of $TestProject instead of throwing.
    Brainarr uses this because its CI can run without the verify script.

.EXAMPLE
    # Called from a plugin's scripts/e2e.ps1 shim — do not invoke directly.
    & "$PSScriptRoot/../ext/Lidarr.Plugin.Common/scripts/e2e-local-runner.ps1" `
        -PluginName 'Brainarr' `
        -TestProject 'Brainarr.Tests/Brainarr.Tests.csproj' `
        -SkipBuild:$SkipBuild -Configuration $Configuration -Filter $Filter `
        -ExtraBuildArgs '-m:1' -FallbackBuildOnMissingVerify
#>
param(
    [Parameter(Mandatory)]
    [string]$PluginName,

    [Parameter(Mandatory)]
    [string]$TestProject,

    [string]$VerifyLocalScript = 'scripts/verify-local.ps1',

    [switch]$SkipBuild,

    [string]$Configuration = 'Release',

    [string]$Filter = 'Category=DockerE2E',

    [string]$ExtraBuildArgs = '',

    [switch]$FallbackBuildOnMissingVerify
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# The calling shim is at <repo>/scripts/e2e.ps1; PSScriptRoot is <repo>/scripts.
# This runner lives at <common>/scripts/e2e-local-runner.ps1; we receive a
# $repoRoot from the caller via the working directory — so use the CWD which
# the shim sets to the repo root before invoking us.
$repoRoot = (Get-Location).Path
$testProjectFull = Join-Path $repoRoot $TestProject

Push-Location $repoRoot
try {
    Write-Host '================================================================================' -ForegroundColor Cyan
    Write-Host "  $($PluginName.ToUpperInvariant()) DOCKER E2E HARNESS" -ForegroundColor Cyan
    Write-Host '================================================================================' -ForegroundColor Cyan

    # ------------------------------------------------------------------
    # Docker engine preflight.  We do NOT fail when Docker is missing —
    # the tests skip gracefully — but the user benefits from a clear
    # heads-up so they don't wonder why everything was skipped.
    # ------------------------------------------------------------------
    $dockerOk = $false
    try {
        & docker info *>$null
        $dockerOk = ($LASTEXITCODE -eq 0)
    } catch {
        $dockerOk = $false
    }

    if (-not $dockerOk) {
        Write-Host '  WARNING: Docker engine is not running. E2E tests will skip.' -ForegroundColor Yellow
        Write-Host '           Start Docker Desktop and re-run to actually exercise the harness.' -ForegroundColor Yellow
    } else {
        Write-Host '  Docker engine: OK' -ForegroundColor Green
    }

    # ------------------------------------------------------------------
    # Build step (optional)
    # ------------------------------------------------------------------
    if (-not $SkipBuild) {
        Write-Host ''
        Write-Host '  [1/2] Building plugin (host-bridge)...' -ForegroundColor Cyan

        $verifyScriptFull = Join-Path $repoRoot $VerifyLocalScript

        if (Test-Path $verifyScriptFull) {
            & pwsh $verifyScriptFull -SkipExtract -SkipTests
            if ($LASTEXITCODE -ne 0) { throw 'verify-local.ps1 build prep failed' }
        } elseif ($FallbackBuildOnMissingVerify) {
            Write-Host "  verify-local.ps1 not found — falling back to dotnet build" -ForegroundColor Yellow
            $buildArgs = @($testProjectFull, '-c', $Configuration, '--nologo')
            if (-not [string]::IsNullOrWhiteSpace($ExtraBuildArgs)) {
                $buildArgs += $ExtraBuildArgs.Split(' ', [System.StringSplitOptions]::RemoveEmptyEntries)
            }
            & dotnet build @buildArgs
            if ($LASTEXITCODE -ne 0) { throw 'dotnet build failed' }
        } else {
            throw "verify-local.ps1 not found at '$verifyScriptFull' and -FallbackBuildOnMissingVerify is not set."
        }
    } else {
        Write-Host '  [1/2] Skipping build (-SkipBuild)' -ForegroundColor DarkGray
    }

    # ------------------------------------------------------------------
    # Test step
    # ------------------------------------------------------------------
    Write-Host ''
    Write-Host "  [2/2] Running E2E tests (filter: $Filter)..." -ForegroundColor Cyan

    & dotnet test $testProjectFull `
        -c $Configuration `
        -v normal `
        -m:1 `
        -p:PluginPackagingDisable=true `
        --filter $Filter

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet test exited with code $LASTEXITCODE"
    }

    Write-Host ''
    Write-Host '  E2E harness complete.' -ForegroundColor Green
}
finally {
    Pop-Location
}
