#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Shared local CI verification runner for Lidarr plugin repos.

.DESCRIPTION
    Orchestrates the merge-critical verification pipeline locally, delegating
    to existing Common tools (PluginPack.psm1, generate-expected-contents.ps1).

    Designed to be called by per-repo verify-local.ps1 scripts that supply
    repo-specific configuration via the $Config hashtable.

.PARAMETER Config
    Hashtable with repo-specific configuration. Required keys:
      RepoName, PluginCsproj, ManifestPath, MainDll, HostAssembliesPath,
      CommonPath, LidarrDockerVersion, ExpectedContentsFile
    Optional keys:
      SolutionFile, BuildFlags, TestProjects, PackageParams

.PARAMETER SkipExtract
    Reuse previously extracted host assemblies (fast rerun).

.PARAMETER SkipTests
    Skip hermetic E2E tests (build + package + closure only).

.PARAMETER NoRestore
    Skip dotnet restore (fast iteration after first run).

.PARAMETER IncludeSmoke
    Run optional Docker smoke test after main pipeline.

.PARAMETER Configuration
    Build configuration. Default: Release.
#>
param(
    [Parameter(Mandatory)]
    [hashtable]$Config,

    [switch]$SkipExtract,
    [switch]$SkipTests,
    [switch]$NoRestore,
    [switch]$IncludeSmoke,
    [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ── Helpers ──────────────────────────────────────────────────────────────

$script:StageResults = [ordered]@{}
$script:TotalStopwatch = [System.Diagnostics.Stopwatch]::StartNew()

function Write-Stage {
    param([string]$Name, [string]$Status, [string]$Detail = '', [int]$Seconds = 0)
    $icon = switch ($Status) {
        'PASS' { 'PASS' }
        'FAIL' { 'FAIL' }
        'SKIP' { 'SKIP' }
        default { '....' }
    }
    $color = switch ($Status) {
        'PASS' { 'Green' }
        'FAIL' { 'Red' }
        'SKIP' { 'DarkGray' }
        default { 'White' }
    }
    $pad = 40 - $Name.Length
    if ($pad -lt 2) { $pad = 2 }
    $dots = '.' * $pad
    $timeStr = if ($Seconds -gt 0) { "  (${Seconds}s)" } else { '' }
    Write-Host "  $Name $dots $icon$timeStr" -ForegroundColor $color
    if ($Detail) {
        Write-Host "      $Detail" -ForegroundColor DarkGray
    }
}

function Invoke-Stage {
    param(
        [string]$Name,
        [string]$Number,
        [scriptblock]$Action,
        [switch]$Required
    )

    $stageLabel = "[$Number] $Name"
    Write-Host "`n--- $stageLabel ---" -ForegroundColor Cyan
    $sw = [System.Diagnostics.Stopwatch]::StartNew()

    try {
        $detail = & $Action
        $sw.Stop()
        $script:StageResults[$stageLabel] = @{
            Status  = 'PASS'
            Seconds = [int]$sw.Elapsed.TotalSeconds
            Detail  = if ($detail) { "$detail" } else { '' }
        }
        return $true
    }
    catch {
        $sw.Stop()
        Write-Host "  ERROR: $_" -ForegroundColor Red
        $script:StageResults[$stageLabel] = @{
            Status  = 'FAIL'
            Seconds = [int]$sw.Elapsed.TotalSeconds
            Detail  = "$_"
        }
        if ($Required) {
            Write-Host "  Required stage failed. Short-circuiting remaining stages." -ForegroundColor Red
        }
        return $false
    }
}

# ── Stage 0: PREFLIGHT ──────────────────────────────────────────────────

$requiredKeys = @(
    'RepoName', 'PluginCsproj', 'ManifestPath', 'MainDll',
    'HostAssembliesPath', 'CommonPath', 'LidarrDockerVersion', 'ExpectedContentsFile'
)
$missing = $requiredKeys | Where-Object { -not $Config.ContainsKey($_) -or [string]::IsNullOrWhiteSpace($Config[$_]) }
if ($missing) {
    Write-Host "PREFLIGHT FAIL: Missing required config keys: $($missing -join ', ')" -ForegroundColor Red
    exit 1
}

$repoName       = $Config.RepoName
$pluginCsproj   = $Config.PluginCsproj
$manifestPath   = $Config.ManifestPath
$mainDll        = $Config.MainDll
$hostPath       = $Config.HostAssembliesPath
$commonPath     = $Config.CommonPath
$dockerVersion  = $Config.LidarrDockerVersion
$expectedFile   = $Config.ExpectedContentsFile
$solutionFile   = $Config['SolutionFile']
$buildFlags     = $Config['BuildFlags']
$testProjects   = $Config['TestProjects']
$packageParams  = $Config['PackageParams']

# Validate Common submodule
if (-not (Test-Path -LiteralPath $commonPath)) {
    Write-Host "PREFLIGHT FAIL: Common submodule not found at: $commonPath" -ForegroundColor Red
    Write-Host "  Run: git submodule update --init ext/Lidarr.Plugin.Common" -ForegroundColor Yellow
    exit 1
}

# Validate tools exist and resolve to absolute paths (Import-Module requires absolute)
$pluginPackModule = Join-Path $commonPath 'tools/PluginPack.psm1'
$genExpectedScript = Join-Path $commonPath 'scripts/generate-expected-contents.ps1'
if (-not (Test-Path -LiteralPath $pluginPackModule)) {
    Write-Host "PREFLIGHT FAIL: PluginPack.psm1 not found at: $pluginPackModule" -ForegroundColor Red
    exit 1
}
if (-not (Test-Path -LiteralPath $genExpectedScript)) {
    Write-Host "PREFLIGHT FAIL: generate-expected-contents.ps1 not found at: $genExpectedScript" -ForegroundColor Red
    exit 1
}
$pluginPackModule = (Resolve-Path -LiteralPath $pluginPackModule).Path
$genExpectedScript = (Resolve-Path -LiteralPath $genExpectedScript).Path

# Check .NET SDK
Write-Host "Checking .NET SDK..."
$dotnetVersion = & dotnet --version 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "PREFLIGHT FAIL: .NET SDK not found. Install from https://dot.net" -ForegroundColor Red
    exit 1
}
Write-Host "  .NET SDK: $dotnetVersion" -ForegroundColor DarkGray

# Check Docker (only if we need it)
if (-not $SkipExtract -or $IncludeSmoke) {
    Write-Host "Checking Docker..."
    & docker version --format '{{.Server.Version}}' 2>$null | Out-Null
    if ($LASTEXITCODE -ne 0) {
        Write-Host "PREFLIGHT FAIL: Docker not available. Install Docker Desktop or use -SkipExtract." -ForegroundColor Red
        exit 1
    }
    Write-Host "  Docker: available" -ForegroundColor DarkGray
}

# Auto-detect host assemblies path if configured path doesn't exist and we're skipping extract
if ($SkipExtract -and -not (Test-Path -LiteralPath $hostPath)) {
    $alternates = @(
        'ext/Lidarr-docker/_output/net8.0',
        'ext/Lidarr/_output/net8.0'
    )
    $found = $false
    foreach ($alt in $alternates) {
        if (Test-Path -LiteralPath $alt) {
            Write-Host "  Auto-detected host assemblies at: $alt" -ForegroundColor Yellow
            $hostPath = $alt
            $found = $true
            break
        }
    }
    if (-not $found) {
        Write-Host "PREFLIGHT FAIL: Host assemblies not found. Tried:" -ForegroundColor Red
        Write-Host "  - $($Config.HostAssembliesPath)" -ForegroundColor Red
        foreach ($alt in $alternates) {
            Write-Host "  - $alt" -ForegroundColor Red
        }
        Write-Host "  Run without -SkipExtract to pull assemblies from Docker." -ForegroundColor Yellow
        exit 1
    }
}

$script:hostPathAbsolute = if (Test-Path -LiteralPath $hostPath) {
    (Resolve-Path -LiteralPath $hostPath).Path
} else {
    # Will be created during EXTRACT
    [System.IO.Path]::GetFullPath($hostPath)
}

Write-Host ""
Write-Host "=======================================" -ForegroundColor Cyan
Write-Host "  LOCAL CI VERIFICATION: $repoName" -ForegroundColor Cyan
Write-Host "=======================================" -ForegroundColor Cyan

# ── Stage 1: EXTRACT ────────────────────────────────────────────────────

$currentStage = 1

if ($SkipExtract) {
    $script:StageResults["[$currentStage/5] EXTRACT"] = @{
        Status = 'SKIP'; Seconds = 0; Detail = 'Using cached assemblies'
    }
    Write-Host "`n--- [$currentStage/5] EXTRACT (skipped) ---" -ForegroundColor DarkGray
} else {
    $extractOk = Invoke-Stage -Name 'EXTRACT' -Number "$currentStage/5" -Required -Action {
        $image = "ghcr.io/hotio/lidarr:$dockerVersion"

        Write-Host "  Pulling $image ..."
        $pullOutput = & docker pull $image 2>&1
        $pullExit = $LASTEXITCODE
        $pullOutput | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        if ($pullExit -ne 0) { throw "docker pull failed for $image" }

        Write-Host "  Extracting assemblies..."
        $containerId = (& docker create $image 2>&1) | Select-Object -Last 1
        if ($LASTEXITCODE -ne 0 -or -not $containerId) { throw "docker create failed" }

        try {
            # Ensure destination exists
            if (Test-Path -LiteralPath $hostPath) {
                Remove-Item -LiteralPath $hostPath -Recurse -Force
            }
            New-Item -ItemType Directory -Path $hostPath -Force | Out-Null

            $cpOutput = & docker cp "${containerId}:/app/bin/." $hostPath 2>&1
            $cpExit = $LASTEXITCODE
            $cpOutput | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            if ($cpExit -ne 0) { throw "docker cp failed" }
        }
        finally {
            & docker rm $containerId 2>$null | Out-Null
        }

        # Update absolute path after extraction
        $script:hostPathAbsolute = (Resolve-Path -LiteralPath $hostPath).Path

        # .NET 8 guardrail
        $rcPath = Join-Path $hostPath 'Lidarr.runtimeconfig.json'
        if (-not (Test-Path -LiteralPath $rcPath)) {
            throw "Lidarr.runtimeconfig.json not found in extracted assemblies"
        }
        $rc = Get-Content -LiteralPath $rcPath -Raw | ConvertFrom-Json
        $runtimeVersion = $rc.runtimeOptions.framework.version
        if (-not $runtimeVersion) {
            # Try tfm array format
            $runtimeVersion = ($rc.runtimeOptions.frameworks | Where-Object { $_.name -eq 'Microsoft.NETCore.App' }).version
        }
        if (-not $runtimeVersion -or -not $runtimeVersion.StartsWith('8.')) {
            throw ".NET 8 guardrail FAILED: runtime version is '$runtimeVersion' (expected 8.x)"
        }
        $net8Status = "OK"

        # FluentValidation version guardrail
        $fvDll = Join-Path $hostPath 'FluentValidation.dll'
        $fvVersion = 'N/A'
        if (Test-Path -LiteralPath $fvDll) {
            $fvInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo((Resolve-Path $fvDll))
            $fvVersion = $fvInfo.ProductVersion
            if (-not $fvVersion.StartsWith('9.5.4')) {
                throw "FluentValidation guardrail FAILED: version is '$fvVersion' (expected 9.5.4.x)"
            }
        }

        ".NET 8: $net8Status ($runtimeVersion) | FV: $fvVersion"
    }
    if (-not $extractOk) { $SkipTests = $true }
}

# ── Stage 2: BUILD ──────────────────────────────────────────────────────

$currentStage++

$buildOk = Invoke-Stage -Name 'BUILD' -Number "$currentStage/5" -Required -Action {
    # Restore
    if (-not $NoRestore) {
        $restoreTarget = if ($solutionFile) { $solutionFile } else { $pluginCsproj }
        Write-Host "  Restoring $restoreTarget ..."
        $restoreOutput = & dotnet restore $restoreTarget 2>&1
        $restoreExit = $LASTEXITCODE
        $restoreOutput | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        if ($restoreExit -ne 0) { throw "dotnet restore failed" }
    }

    # Build with repo-specific flags
    $buildArgs = @($pluginCsproj, '-c', $Configuration, '--no-restore')

    if ($buildFlags) {
        $resolvedFlags = $buildFlags | ForEach-Object {
            $_ -replace '\{HOST_PATH\}', $hostPathAbsolute
        }
        $buildArgs += $resolvedFlags
    }

    Write-Host "  Building $pluginCsproj ..."
    $buildOutput = & dotnet build @buildArgs 2>&1
    $buildExit = $LASTEXITCODE
    $buildOutput | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
    if ($buildExit -ne 0) { throw "dotnet build failed" }

    $null  # no detail string
}
if (-not $buildOk) { $SkipTests = $true }

# ── Stage 3: PACKAGE ────────────────────────────────────────────────────

$currentStage++
$zipPath = $null

if (-not $buildOk) {
    $script:StageResults["[$currentStage/5] PACKAGE"] = @{
        Status = 'SKIP'; Seconds = 0; Detail = 'Skipped due to BUILD failure'
    }
} else {
    $packageOk = Invoke-Stage -Name 'PACKAGE' -Number "$currentStage/5" -Required -Action {
        # Set LIDARR_PATH so New-PluginPackage's internal dotnet build can find
        # host assemblies via the .csproj env-var fallback, even if the extraction
        # path differs from the csproj's hardcoded fallback paths.
        $env:LIDARR_PATH = $hostPathAbsolute

        Import-Module $pluginPackModule -Force

        $pkgArgs = @{
            Csproj        = $pluginCsproj
            Manifest      = $manifestPath
            Framework     = 'net8.0'
            Configuration = $Configuration
        }

        # Merge extra packaging params from config
        if ($packageParams -and $packageParams -is [hashtable]) {
            foreach ($key in $packageParams.Keys) {
                $pkgArgs[$key] = $packageParams[$key]
            }
        }

        $script:zipPath = New-PluginPackage @pkgArgs
        $zipName = Split-Path -Leaf $script:zipPath
        "ZIP: $zipName"
    }

    if (-not $packageOk) { $SkipTests = $true }
}

# ── Stage 4: PACKAGING CLOSURE ──────────────────────────────────────────

$currentStage++

if (-not $zipPath) {
    $script:StageResults["[$currentStage/5] PACKAGING CLOSURE"] = @{
        Status = 'SKIP'; Seconds = 0; Detail = 'Skipped due to PACKAGE failure'
    }
} else {
    Invoke-Stage -Name 'PACKAGING CLOSURE' -Number "$currentStage/5" -Action {
        Write-Host "  Checking package contents against $expectedFile ..."
        # Run in a child process so the script's exit 1 sets $LASTEXITCODE
        # rather than potentially terminating the current scope.
        $closureOutput = & pwsh -NoProfile -File $genExpectedScript -ZipPath $script:zipPath -ManifestPath $expectedFile -Check 2>&1
        $closureExit = $LASTEXITCODE
        $closureOutput | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
        if ($closureExit -ne 0) {
            throw "Packaging closure check failed: ZIP contents do not match expected manifest"
        }

        # Count required/forbidden entries from manifest for summary
        if (Test-Path -LiteralPath $expectedFile) {
            $content = Get-Content -LiteralPath $expectedFile -Raw
            $requiredCount = 0
            $forbiddenCount = 0
            $section = ''
            foreach ($line in ($content -split "`n")) {
                $trimmed = $line.Trim()
                if ($trimmed -eq '[REQUIRED]') { $section = 'req'; continue }
                if ($trimmed -eq '[FORBIDDEN]') { $section = 'forb'; continue }
                if ($trimmed -and -not $trimmed.StartsWith('#')) {
                    if ($section -eq 'req') { $requiredCount++ }
                    if ($section -eq 'forb') { $forbiddenCount++ }
                }
            }
            "Required: $requiredCount present | $forbiddenCount forbidden rules checked"
        } else {
            "Check passed"
        }
    } | Out-Null
}

# ── Stage 5: HERMETIC E2E ──────────────────────────────────────────────

$currentStage++

if ($SkipTests) {
    $script:StageResults["[$currentStage/5] HERMETIC E2E"] = @{
        Status = 'SKIP'; Seconds = 0; Detail = 'Skipped (--SkipTests or upstream failure)'
    }
    Write-Host "`n--- [$currentStage/5] HERMETIC E2E (skipped) ---" -ForegroundColor DarkGray
} else {
    $e2eOk = Invoke-Stage -Name 'HERMETIC E2E' -Number "$currentStage/5" -Action {
        if (-not $testProjects -or $testProjects.Count -eq 0) {
            return "No test projects configured"
        }

        $totalPassed = 0
        $totalFailed = 0
        $totalSkipped = 0

        foreach ($testProj in $testProjects) {
            Write-Host "  Running tests in $testProj ..."

            # Build test project first (disable plugin packaging for test builds)
            $testBuildArgs = @($testProj, '-c', $Configuration, '--no-restore')
            if ($buildFlags) {
                $resolvedFlags = $buildFlags | ForEach-Object {
                    $_ -replace '\{HOST_PATH\}', $hostPathAbsolute
                }
                $testBuildArgs += $resolvedFlags
            }
            $testBuildArgs += '-p:PluginPackagingDisable=true'

            $tbOutput = & dotnet build @testBuildArgs 2>&1
            $tbExit = $LASTEXITCODE
            $tbOutput | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }
            if ($tbExit -ne 0) { throw "Test project build failed: $testProj" }

            # Run tests with hermetic E2E filter
            $resultsDir = Join-Path ([System.IO.Path]::GetTempPath()) "local-ci-trx-$([guid]::NewGuid().ToString('N').Substring(0,8))"
            New-Item -ItemType Directory -Path $resultsDir -Force | Out-Null

            $testArgs = @(
                $testProj, '--no-build', '-c', $Configuration,
                '--filter', 'Area=E2E/Hermetic',
                '--logger', 'trx',
                '--results-directory', $resultsDir
            )

            $testOutput = & dotnet test @testArgs 2>&1
            $testExitCode = $LASTEXITCODE
            $testOutput | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }

            # Parse TRX for counts
            $trxFiles = Get-ChildItem -Path $resultsDir -Filter '*.trx' -ErrorAction SilentlyContinue
            if ($trxFiles) {
                try {
                    [xml]$trx = Get-Content -LiteralPath $trxFiles[0].FullName -Raw
                    $counters = $trx.TestRun.ResultSummary.Counters
                    $totalPassed += [int]$counters.passed
                    $totalFailed += [int]$counters.failed
                    $totalSkipped += [int]$counters.notExecuted
                }
                catch {
                    Write-Host "    Warning: Could not parse TRX results" -ForegroundColor Yellow
                }
            }
            Remove-Item -LiteralPath $resultsDir -Recurse -Force -ErrorAction SilentlyContinue

            if ($testExitCode -ne 0 -and $totalFailed -eq 0) {
                throw "dotnet test exited with code $testExitCode"
            }
        }

        if ($totalFailed -gt 0) {
            throw "$totalFailed test(s) failed"
        }

        "$totalPassed passed, $totalFailed failed" + $(if ($totalSkipped) { ", $totalSkipped skipped" } else { '' })
    }
}

# ── Stage 6: SMOKE (optional) ──────────────────────────────────────────

if ($IncludeSmoke) {
    $smokeOk = Invoke-Stage -Name 'SMOKE' -Number 'S' -Action {
        $image = "ghcr.io/hotio/lidarr:$dockerVersion"
        $containerName = "local-ci-smoke-$($repoName.ToLower())"

        # Find the ZIP to mount
        if (-not $zipPath -or -not (Test-Path -LiteralPath $zipPath)) {
            throw "No plugin ZIP available for smoke test"
        }

        Write-Host "  Starting Lidarr container for smoke test..."

        # Extract ZIP to temp directory for mounting
        $smokeDir = Join-Path ([System.IO.Path]::GetTempPath()) "local-ci-smoke-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $smokeDir -Force | Out-Null
        Expand-Archive -LiteralPath $zipPath -DestinationPath $smokeDir -Force

        try {
            & docker rm -f $containerName 2>$null | Out-Null
            & docker run -d --name $containerName `
                -v "${smokeDir}:/plugins/$repoName" `
                -e "PUID=1000" -e "PGID=1000" `
                -p 8686:8686 `
                $image 2>&1 | ForEach-Object { Write-Host "    $_" -ForegroundColor DarkGray }

            if ($LASTEXITCODE -ne 0) { throw "Failed to start Lidarr container" }

            # Wait for API readiness (up to 60s)
            $maxWait = 60
            $waited = 0
            $ready = $false
            while ($waited -lt $maxWait) {
                Start-Sleep -Seconds 2
                $waited += 2
                try {
                    $response = Invoke-RestMethod -Uri 'http://localhost:8686/api/v1/system/status' -Method Get -TimeoutSec 5 -ErrorAction SilentlyContinue
                    if ($response) { $ready = $true; break }
                }
                catch { }
            }

            if (-not $ready) { throw "Lidarr did not become ready within ${maxWait}s" }

            Write-Host "  Lidarr API ready. Checking plugin registration..."

            # Check schema endpoint for plugin
            $schema = Invoke-RestMethod -Uri 'http://localhost:8686/api/v1/importlist/schema' -Method Get -TimeoutSec 10
            $pluginEntry = $schema | Where-Object { $_.implementation -match $repoName }
            if ($pluginEntry) {
                "Plugin registered in schema"
            } else {
                throw "Plugin not found in Lidarr schema endpoint"
            }
        }
        finally {
            & docker rm -f $containerName 2>$null | Out-Null
            Remove-Item -LiteralPath $smokeDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

# ── Stage 7: SUMMARY ───────────────────────────────────────────────────

$script:TotalStopwatch.Stop()
$totalSeconds = [int]$script:TotalStopwatch.Elapsed.TotalSeconds

Write-Host ""
Write-Host "=======================================" -ForegroundColor Cyan
Write-Host "  LOCAL CI VERIFICATION: $repoName" -ForegroundColor Cyan
Write-Host "=======================================" -ForegroundColor Cyan
Write-Host ""

$allPassed = $true
foreach ($stage in $script:StageResults.Keys) {
    $r = $script:StageResults[$stage]
    Write-Stage -Name $stage -Status $r.Status -Detail $r.Detail -Seconds $r.Seconds
    if ($r.Status -eq 'FAIL') { $allPassed = $false }
}

Write-Host ""
Write-Host "=======================================" -ForegroundColor Cyan
$resultText = if ($allPassed) { 'PASS' } else { 'FAIL' }
$resultColor = if ($allPassed) { 'Green' } else { 'Red' }
Write-Host "  RESULT: $resultText  (${totalSeconds}s)" -ForegroundColor $resultColor
Write-Host "=======================================" -ForegroundColor Cyan

if (-not $allPassed) { exit 1 }
exit 0
