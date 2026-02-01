#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Local Docker-based verification when CI is billing-blocked.

.DESCRIPTION
    This script runs build and test verification in a Docker container,
    providing CI-equivalent results when GitHub Actions is unavailable
    due to billing limits.

    Use this script to verify changes before merging when CI is blocked.

.PARAMETER Project
    Project to verify. Defaults to current directory.

.PARAMETER Configuration
    Build configuration. Default: Release

.PARAMETER SkipTests
    Skip running tests (build only).

.PARAMETER TestFilter
    Filter expression for tests.

.PARAMETER Image
    Docker image to use. Default: mcr.microsoft.com/dotnet/sdk:8.0

.PARAMETER Interactive
    Run container interactively (for debugging).

.EXAMPLE
    ./docker-verify.ps1
    # Full build + test verification

.EXAMPLE
    ./docker-verify.ps1 -SkipTests
    # Build verification only

.EXAMPLE
    ./docker-verify.ps1 -TestFilter "FullyQualifiedName~Streaming"
    # Run only streaming tests

.NOTES
    Requires Docker to be installed and running.

    This script is designed for use when GitHub Actions billing is blocked.
    It provides local CI-equivalent verification to ensure changes are safe
    to merge.
#>

[CmdletBinding()]
param(
    [string]$Project = ".",

    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',

    [switch]$SkipTests,

    [string]$TestFilter = "",

    [string]$Image = "mcr.microsoft.com/dotnet/sdk:8.0",

    [switch]$Interactive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# ============================================================================
# DOCKER AVAILABILITY CHECK
# ============================================================================

function Test-DockerAvailable {
    try {
        $null = docker version 2>&1
        return $LASTEXITCODE -eq 0
    }
    catch {
        return $false
    }
}

if (-not (Test-DockerAvailable)) {
    Write-Host "[ERROR] Docker is not available or not running." -ForegroundColor Red
    Write-Host ""
    Write-Host "Please ensure Docker Desktop is installed and running." -ForegroundColor Yellow
    Write-Host "Download: https://www.docker.com/products/docker-desktop" -ForegroundColor Gray
    exit 1
}

# ============================================================================
# PROJECT RESOLUTION
# ============================================================================

$ProjectRoot = Resolve-Path $Project
$ProjectName = Split-Path $ProjectRoot -Leaf

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Docker Local Verification" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""
Write-Host "Project:       $ProjectName" -ForegroundColor White
Write-Host "Path:          $ProjectRoot" -ForegroundColor Gray
Write-Host "Configuration: $Configuration" -ForegroundColor White
Write-Host "Image:         $Image" -ForegroundColor Gray
Write-Host ""

# ============================================================================
# FIND SOLUTION OR PROJECT FILE
# ============================================================================

$SolutionFile = Get-ChildItem -Path $ProjectRoot -Filter "*.sln" -Depth 0 | Select-Object -First 1
$ProjectFile = Get-ChildItem -Path $ProjectRoot -Filter "*.csproj" -Depth 0 | Select-Object -First 1

$BuildTarget = $null
if ($SolutionFile) {
    $BuildTarget = $SolutionFile.Name
    Write-Host "Build target:  $BuildTarget (solution)" -ForegroundColor White
}
elseif ($ProjectFile) {
    $BuildTarget = $ProjectFile.Name
    Write-Host "Build target:  $BuildTarget (project)" -ForegroundColor White
}
else {
    # Search for solution in subdirectories
    $SolutionFile = Get-ChildItem -Path $ProjectRoot -Filter "*.sln" -Recurse -Depth 2 | Select-Object -First 1
    if ($SolutionFile) {
        $BuildTarget = $SolutionFile.FullName.Replace($ProjectRoot, "").TrimStart("/\")
        Write-Host "Build target:  $BuildTarget (nested solution)" -ForegroundColor White
    }
    else {
        Write-Host "[ERROR] No solution or project file found." -ForegroundColor Red
        exit 1
    }
}

Write-Host ""

# ============================================================================
# BUILD DOCKER COMMAND
# ============================================================================

# Convert Windows path to Docker-compatible format
$DockerVolumePath = $ProjectRoot -replace '\\', '/' -replace '^([A-Z]):', '/mnt/$1'.ToLower()

# For Windows Docker Desktop, use standard volume mount
if ($IsWindows -or ($PSVersionTable.PSEdition -eq 'Desktop')) {
    $DockerVolumePath = $ProjectRoot
}

$ContainerWorkDir = "/src"

# Build command to execute in container
$BuildCommand = "dotnet restore && dotnet build `"$BuildTarget`" --configuration $Configuration"

if (-not $SkipTests) {
    # Find test projects
    $TestProjectGlob = '$(find . -name "*.Tests.csproj" -o -name "*Tests.csproj" | head -5)'

    $TestCommand = @"
for testproj in $TestProjectGlob; do
    echo "Testing: \$testproj"
    dotnet test "\$testproj" --configuration $Configuration --no-build --verbosity normal
done
"@

    if ($TestFilter) {
        $TestCommand = @"
for testproj in $TestProjectGlob; do
    echo "Testing: \$testproj"
    dotnet test "\$testproj" --configuration $Configuration --no-build --filter "$TestFilter" --verbosity normal
done
"@
    }

    $FullCommand = "$BuildCommand && $TestCommand"
}
else {
    $FullCommand = $BuildCommand
}

# ============================================================================
# EXECUTE IN CONTAINER
# ============================================================================

Write-Host "Starting Docker container..." -ForegroundColor Cyan
Write-Host ""

$dockerArgs = @(
    "run"
    "--rm"
    "-v", "${ProjectRoot}:${ContainerWorkDir}"
    "-w", $ContainerWorkDir
    "-e", "DOTNET_CLI_TELEMETRY_OPTOUT=1"
    "-e", "DOTNET_NOLOGO=1"
)

if ($Interactive) {
    $dockerArgs += "-it"
    $dockerArgs += $Image
    $dockerArgs += "/bin/bash"
}
else {
    $dockerArgs += $Image
    $dockerArgs += "/bin/bash"
    $dockerArgs += "-c"
    $dockerArgs += $FullCommand
}

Write-Host "Command: docker $($dockerArgs -join ' ')" -ForegroundColor DarkGray
Write-Host ""

& docker @dockerArgs
$exitCode = $LASTEXITCODE

Write-Host ""

if ($exitCode -eq 0) {
    Write-Host "========================================" -ForegroundColor Green
    Write-Host "[PASS] Docker verification successful" -ForegroundColor Green
    Write-Host "========================================" -ForegroundColor Green
}
else {
    Write-Host "========================================" -ForegroundColor Red
    Write-Host "[FAIL] Docker verification failed (exit code: $exitCode)" -ForegroundColor Red
    Write-Host "========================================" -ForegroundColor Red
}

exit $exitCode
