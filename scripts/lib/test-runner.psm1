#Requires -Version 5.1
<#
.SYNOPSIS
    Shared test runner utilities for Lidarr plugin ecosystem.

.DESCRIPTION
    This module provides common functions used by test.ps1 and test-packaging.ps1
    scripts across all plugins. Import this module to get standardized:
    - TRX result parsing
    - Artifact freshness validation
    - Plugin assembly discovery
    - Package extraction
    - Build/test argument generation

.NOTES
    Usage in plugin scripts:

    $CommonScripts = Join-Path $PSScriptRoot "../../ext/Lidarr.Plugin.Common/scripts/lib"
    Import-Module (Join-Path $CommonScripts "test-runner.psm1") -Force
#>

Set-StrictMode -Version Latest

# ============================================================================
# TRX RESULT PARSING
# ============================================================================

<#
.SYNOPSIS
    Parses a TRX test results file and returns a summary object.

.PARAMETER TrxPath
    Path to the .trx file.

.OUTPUTS
    PSCustomObject with Total, Passed, Failed, Skipped, PassRate properties.
    Returns $null if file doesn't exist or can't be parsed.
#>
function Get-TrxTestSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TrxPath
    )

    if (-not (Test-Path $TrxPath)) {
        Write-Warning "TRX file not found: $TrxPath"
        return $null
    }

    try {
        [xml]$trxXml = Get-Content $TrxPath
        $counters = $trxXml.TestRun.ResultSummary.Counters

        $total = [int]$counters.total
        $executed = [int]$counters.executed
        $passed = [int]$counters.passed
        $failed = [int]$counters.failed

        # xUnit adapter doesn't populate notExecuted reliably (reports 0 even with skips).
        # Fall back to max(0, total - executed) which is always correct.
        $notExecuted = 0
        try { $notExecuted = [int]$counters.notExecuted } catch { }
        $skipped = if ($notExecuted -gt 0) { $notExecuted } else { [Math]::Max(0, $total - $executed) }

        $passRate = if ($total -gt 0) {
            [math]::Round(($passed / $total) * 100, 2)
        } else {
            0
        }

        return [PSCustomObject]@{
            Total    = $total
            Executed = $executed
            Passed   = $passed
            Failed   = $failed
            Skipped  = $skipped
            PassRate = $passRate
        }
    }
    catch {
        Write-Warning "Could not parse TRX file: $_"
        return $null
    }
}

<#
.SYNOPSIS
    Displays a formatted test results summary to the console.

.PARAMETER Summary
    Test summary object from Get-TrxTestSummary.
#>
function Write-TestSummary {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [PSCustomObject]$Summary
    )

    Write-Host "Test Results Summary:" -ForegroundColor Cyan
    Write-Host "  Total:   $($Summary.Total)" -ForegroundColor White
    Write-Host "  Passed:  $($Summary.Passed)" -ForegroundColor Green
    Write-Host "  Failed:  $($Summary.Failed)" -ForegroundColor $(if ($Summary.Failed -gt 0) { "Red" } else { "Gray" })
    Write-Host "  Skipped: $($Summary.Skipped)" -ForegroundColor Yellow

    $rateColor = if ($Summary.PassRate -ge 80) { "Green" }
                 elseif ($Summary.PassRate -ge 60) { "Yellow" }
                 else { "Red" }
    Write-Host "  Pass Rate: $($Summary.PassRate)%" -ForegroundColor $rateColor
}

# ============================================================================
# BUILD SERVER HARDENING
# ============================================================================

<#
.SYNOPSIS
    Sets environment variables to prevent file-lock issues on Windows.

.DESCRIPTION
    Disables MSBuild node reuse and .NET CLI build servers which can hold
    locks on DLLs (especially in submodules like Lidarr.Plugin.Abstractions.dll).
    This trades some build speed for determinism.

.NOTES
    Call this at the start of test scripts that build shared projects.
    The settings persist for the current process and child processes.
#>
function Set-BuildServerHardening {
    [CmdletBinding()]
    param()

    $env:DOTNET_CLI_DISABLE_BUILD_SERVERS = "1"
    $env:MSBUILDDISABLENODEREUSE = "1"

    Write-Verbose "Build server hardening enabled: DOTNET_CLI_DISABLE_BUILD_SERVERS=1, MSBUILDDISABLENODEREUSE=1"
}

<#
.SYNOPSIS
    Gets standard MSBuild arguments to prevent parallel build issues.

.DESCRIPTION
    Returns arguments that disable parallelism and shared compilation
    to prevent file lock issues when building projects with shared dependencies.

.OUTPUTS
    Array of MSBuild arguments.
#>
function Get-BuildHardeningArgs {
    [CmdletBinding()]
    param()

    return @(
        "/m:1",
        "/p:BuildInParallel=false",
        "/p:UseSharedCompilation=false"
    )
}

<#
.SYNOPSIS
    Removes stale TRX files from the output directory.

.PARAMETER OutputDir
    Directory containing TRX files.

.DESCRIPTION
    Ensures the final summary reflects only the current test run,
    not results from previous runs.
#>
function Clear-StaleTrxFiles {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$OutputDir
    )

    if (Test-Path $OutputDir) {
        Get-ChildItem -Path $OutputDir -Filter "*.trx" -ErrorAction SilentlyContinue |
            Remove-Item -Force -ErrorAction SilentlyContinue
        Write-Verbose "Cleared stale TRX files from $OutputDir"
    }
}

# ============================================================================
# ARTIFACT FRESHNESS VALIDATION
# ============================================================================

<#
.SYNOPSIS
    Validates that a plugin package matches the current checkout.

.PARAMETER PluginJsonPath
    Path to the plugin.json file in the package.

.PARAMETER CsprojPath
    Path to the plugin's .csproj file.

.PARAMETER ProjectRoot
    Root directory of the plugin repository.

.PARAMETER RequireMatch
    If true, returns $false on mismatch. If false, only warns.

.OUTPUTS
    $true if validation passes (or RequireMatch is false), $false otherwise.
#>
function Test-ArtifactFreshness {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PluginJsonPath,

        [Parameter(Mandatory)]
        [string]$CsprojPath,

        [Parameter(Mandatory)]
        [string]$ProjectRoot,

        [switch]$RequireMatch = $false
    )

    if (-not (Test-Path $PluginJsonPath)) {
        Write-Warning "No plugin.json found - cannot validate freshness"
        return $true  # Can't validate, allow to proceed
    }

    $isValid = $true

    try {
        $pluginJson = Get-Content $PluginJsonPath -Raw | ConvertFrom-Json
        $packageVersion = $pluginJson.version
        Write-Host "[INFO] Package plugin.json version: $packageVersion" -ForegroundColor Gray

        # Query MSBuild for authoritative version
        if (Test-Path $CsprojPath) {
            $msbuildVersion = (dotnet msbuild $CsprojPath -getProperty:Version -verbosity:quiet 2>$null)
            if (-not $msbuildVersion) {
                $msbuildVersion = (dotnet msbuild $CsprojPath -getProperty:AssemblyInformationalVersion -verbosity:quiet 2>$null)
            }

            if ($msbuildVersion) {
                $expectedVersion = $msbuildVersion.Trim()
                Write-Host "[INFO] MSBuild expected version: $expectedVersion" -ForegroundColor Gray

                # Compare versions (strip build metadata)
                $pkgVersionBase = ($packageVersion -split '\+')[0]
                $expVersionBase = ($expectedVersion -split '\+')[0]

                if ($pkgVersionBase -ne $expVersionBase) {
                    Write-Host "[WARN] Version mismatch detected!" -ForegroundColor Yellow
                    Write-Host "[WARN]   Package version: $packageVersion" -ForegroundColor Yellow
                    Write-Host "[WARN]   Expected version: $expectedVersion" -ForegroundColor Yellow

                    if ($RequireMatch) {
                        Write-Host "[ERROR] Stale package detected! Rebuild with current checkout." -ForegroundColor Red
                        $isValid = $false
                    } else {
                        Write-Host "[WARN] Continuing anyway (use -RequirePackage to fail on mismatch)" -ForegroundColor Yellow
                    }
                } else {
                    Write-Host "[OK] Package version matches checkout" -ForegroundColor Green
                }
            } else {
                Write-Warning "Could not query MSBuild for version"
            }
        }

        # Check git SHA - REQUIRED if present in package
        $currentSha = (git -C $ProjectRoot rev-parse HEAD 2>$null)
        if ($pluginJson.PSObject.Properties['gitSha'] -and $pluginJson.gitSha -and $currentSha) {
            $packageSha = $pluginJson.gitSha.Trim()
            $currentSha = $currentSha.Trim()

            # Compare first 8 chars minimum (short SHA)
            $compareLen = [Math]::Min(8, [Math]::Min($packageSha.Length, $currentSha.Length))
            $pkgShaShort = $packageSha.Substring(0, $compareLen)
            $curShaShort = $currentSha.Substring(0, $compareLen)

            if ($pkgShaShort -ne $curShaShort) {
                Write-Host "[WARN] Git SHA mismatch!" -ForegroundColor Yellow
                Write-Host "[WARN]   Package SHA: $packageSha" -ForegroundColor Yellow
                Write-Host "[WARN]   Current SHA: $($currentSha.Substring(0, 12))..." -ForegroundColor Yellow

                if ($RequireMatch) {
                    Write-Host "[ERROR] Package was built from different commit! Rebuild required." -ForegroundColor Red
                    $isValid = $false
                }
            } else {
                Write-Host "[OK] Package git SHA matches checkout" -ForegroundColor Green
            }
        } elseif ($RequireMatch -and -not $pluginJson.PSObject.Properties['gitSha']) {
            Write-Warning "Package missing gitSha field - cannot verify commit match"
            Write-Warning "Update PluginPackaging.targets to embed gitSha in plugin.json"
        }
    }
    catch {
        Write-Warning "Could not validate package freshness: $_"
    }

    return $isValid
}

# ============================================================================
# PLUGIN ASSEMBLY DISCOVERY
# ============================================================================

<#
.SYNOPSIS
    Finds a plugin assembly in common build output locations.

.PARAMETER ProjectRoot
    Root directory of the plugin repository.

.PARAMETER AssemblyName
    Name of the assembly file (e.g., "Lidarr.Plugin.Qobuzarr.dll").

.PARAMETER PackagePrefix
    Prefix for package zip files (e.g., "qobuzarr"). Case-insensitive.

.PARAMETER SearchPaths
    Additional paths to search (relative to ProjectRoot).

.PARAMETER Configuration
    Build configuration (Debug or Release).

.OUTPUTS
    Full path to the assembly, or $null if not found.
    Also sets $script:ExtractDir if a package was extracted.
#>
function Find-PluginAssembly {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ProjectRoot,

        [Parameter(Mandatory)]
        [string]$AssemblyName,

        [Parameter(Mandatory)]
        [string]$PackagePrefix,

        [string[]]$SearchPaths = @(),

        [string]$Configuration = "Release"
    )

    # Default search paths if not specified
    if ($SearchPaths.Count -eq 0) {
        $SearchPaths = @(
            "artifacts/packages",
            "artifacts",
            "bin/$Configuration"
        )
    }

    foreach ($relativePath in $SearchPaths) {
        $searchPath = Join-Path $ProjectRoot $relativePath
        if (-not (Test-Path $searchPath)) {
            continue
        }

        # Look for zip packages first (case-insensitive)
        # PowerShell -like is case-insensitive by default
        $package = Get-ChildItem -Path $searchPath -Filter "*.zip" -ErrorAction SilentlyContinue |
            Where-Object { $_.Name -like "$PackagePrefix*" } |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if ($package) {
            $result = Expand-PluginPackage -PackagePath $package.FullName -AssemblyName $AssemblyName -TempPrefix $PackagePrefix
            if ($result) {
                return $result
            }
        }

        # Look for merged DLL directly
        $dll = Get-ChildItem -Path $searchPath -Filter $AssemblyName -Recurse -ErrorAction SilentlyContinue |
            Sort-Object LastWriteTime -Descending |
            Select-Object -First 1

        if ($dll) {
            Write-Host "[INFO] Found assembly: $($dll.FullName)" -ForegroundColor Gray
            return $dll.FullName
        }
    }

    return $null
}

<#
.SYNOPSIS
    Extracts a plugin package to a temp directory.

.PARAMETER PackagePath
    Path to the .zip package file.

.PARAMETER AssemblyName
    Name of the assembly to find after extraction.

.PARAMETER TempPrefix
    Prefix for the temp directory name.

.OUTPUTS
    Full path to the assembly in the extracted directory, or $null.
    Sets $script:ExtractDir to the extraction directory for cleanup.
#>
function Expand-PluginPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$PackagePath,

        [Parameter(Mandatory)]
        [string]$AssemblyName,

        [string]$TempPrefix = "plugin"
    )

    $script:ExtractDir = Join-Path $env:TEMP "$TempPrefix-test-$(Get-Random)"
    Write-Host "[INFO] Found package: $PackagePath" -ForegroundColor Gray
    Write-Host "[INFO] Extracting to $($script:ExtractDir)" -ForegroundColor Gray

    Expand-Archive -Path $PackagePath -DestinationPath $script:ExtractDir -Force

    $dll = Get-ChildItem -Path $script:ExtractDir -Filter $AssemblyName -Recurse |
        Select-Object -First 1

    if ($dll) {
        return $dll.FullName
    }

    Write-Warning "Assembly $AssemblyName not found in package"
    return $null
}

<#
.SYNOPSIS
    Cleans up any extracted package directory.
#>
function Remove-ExtractedPackage {
    [CmdletBinding()]
    param()

    if ($script:ExtractDir -and (Test-Path $script:ExtractDir)) {
        Remove-Item -Path $script:ExtractDir -Recurse -Force -ErrorAction SilentlyContinue
        $script:ExtractDir = $null
    }
}

# ============================================================================
# BUILD/TEST ARGUMENT GENERATION
# ============================================================================

<#
.SYNOPSIS
    Generates standard build arguments for plugin test projects.

.PARAMETER TestProject
    Path to the test project file.

.PARAMETER Configuration
    Build configuration.

.PARAMETER Verbose
    Enable verbose output.

.PARAMETER AdditionalArgs
    Additional arguments to append.

.OUTPUTS
    Array of arguments for dotnet build.
#>
function Get-StandardBuildArgs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TestProject,

        [string]$Configuration = "Debug",

        [switch]$Verbose = $false,

        [string[]]$AdditionalArgs = @()
    )

    $args = @(
        "build", $TestProject,
        "--configuration", $Configuration,
        "-p:PluginPackagingDisable=true",
        "-p:RunAnalyzersDuringBuild=false",
        "-p:EnableNETAnalyzers=false",
        "-p:TreatWarningsAsErrors=false"
    )

    if ($Verbose) {
        $args += @("--verbosity", "detailed")
    } else {
        $args += @("--verbosity", "minimal")
    }

    $args += $AdditionalArgs

    return $args
}

<#
.SYNOPSIS
    Generates standard test arguments for unit test runs.

.PARAMETER TestProject
    Path to the test project file.

.PARAMETER Configuration
    Build configuration.

.PARAMETER OutputDir
    Directory for test results.

.PARAMETER TrxFileName
    Name of the TRX output file.

.PARAMETER Filter
    Test filter expression.

.PARAMETER ExcludeCategories
    Categories to exclude (default: Packaging, LibraryLinking, Integration, Benchmark, Slow).

.PARAMETER Coverage
    Enable code coverage.

.PARAMETER Verbose
    Enable verbose output.

.OUTPUTS
    Array of arguments for dotnet test.
#>
function Get-StandardTestArgs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TestProject,

        [string]$Configuration = "Debug",

        [Parameter(Mandatory)]
        [string]$OutputDir,

        [string]$TrxFileName = "Tests.trx",

        [string]$Filter = "",

        [string[]]$ExcludeCategories = @("Packaging", "LibraryLinking", "Integration", "Benchmark", "Slow"),

        [switch]$Coverage = $false,

        [switch]$Verbose = $false
    )

    $args = @(
        "test", $TestProject,
        "--configuration", $Configuration,
        "--no-build",
        "--logger", "trx;LogFileName=$TrxFileName",
        "--results-directory", $OutputDir
    )

    # Build filter expression
    $effectiveFilter = $Filter
    if ($ExcludeCategories.Count -gt 0) {
        $categoryFilter = ($ExcludeCategories | ForEach-Object { "Category!=$_" }) -join "&"
        if ($effectiveFilter) {
            $effectiveFilter = "($effectiveFilter) & ($categoryFilter)"
        } else {
            $effectiveFilter = $categoryFilter
        }
    }

    if ($effectiveFilter) {
        $args += @("--filter", $effectiveFilter)
    }

    if ($Coverage) {
        $args += @("--collect", "XPlat Code Coverage")
    }

    if ($Verbose) {
        $args += @("--verbosity", "detailed")
    } else {
        $args += @("--verbosity", "normal")
    }

    return $args
}

<#
.SYNOPSIS
    Generates test arguments for packaging/LibraryLinking test runs.

.PARAMETER TestProject
    Path to the test project file.

.PARAMETER Configuration
    Build configuration.

.PARAMETER OutputDir
    Directory for test results.

.PARAMETER Verbose
    Enable verbose output.

.OUTPUTS
    Array of arguments for dotnet test.
#>
function Get-PackagingTestArgs {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$TestProject,

        [string]$Configuration = "Release",

        [Parameter(Mandatory)]
        [string]$OutputDir,

        [switch]$Verbose = $false
    )

    $testFilter = "Category=LibraryLinking|Category=Packaging"

    $args = @(
        "test", $TestProject,
        "--configuration", $Configuration,
        "--no-build",
        "--filter", $testFilter,
        "--logger", "trx;LogFileName=Packaging.trx",
        "--results-directory", $OutputDir
    )

    if ($Verbose) {
        $args += @("--verbosity", "detailed")
    } else {
        $args += @("--verbosity", "normal")
    }

    return $args
}

# ============================================================================
# MODULE EXPORTS
# ============================================================================

# Track extraction directory for cleanup
$script:ExtractDir = $null

Export-ModuleMember -Function @(
    'Get-TrxTestSummary',
    'Write-TestSummary',
    'Set-BuildServerHardening',
    'Get-BuildHardeningArgs',
    'Clear-StaleTrxFiles',
    'Test-ArtifactFreshness',
    'Find-PluginAssembly',
    'Expand-PluginPackage',
    'Remove-ExtractedPackage',
    'Get-StandardBuildArgs',
    'Get-StandardTestArgs',
    'Get-PackagingTestArgs'
)
