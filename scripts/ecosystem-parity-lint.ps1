#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Ecosystem parity lint — detects structural gaps in plugin repos against the canonical spec.
.DESCRIPTION
    Reads parity-spec.json and scans a plugin repo for missing files, incorrect global.json,
    missing workflows, and other structural violations. Mirrors parity-lint.ps1 pattern.
.PARAMETER RepoPath
    Path to a single plugin repo to scan.
.PARAMETER AllRepos
    Scan all known plugin repos (qobuzarr, tidalarr, brainarr, applemusicarr).
.PARAMETER Mode
    Run mode: 'interactive' (warnings only, exit 0) or 'ci' (strict, exit 1 on violations).
#>

param(
    [string]$RepoPath,
    [switch]$AllRepos,
    [ValidateSet('interactive', 'ci')]
    [string]$Mode = 'interactive'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$specPath = Join-Path $PSScriptRoot 'parity-spec.json'
if (-not (Test-Path $specPath)) {
    Write-Host "ERROR: parity-spec.json not found at $specPath" -ForegroundColor Red
    exit 2
}
$spec = Get-Content $specPath -Raw | ConvertFrom-Json

$script:IsCIMode = ($Mode -eq 'ci')

function Normalize-Path {
    param([string]$Path)
    return $Path -replace '\\', '/'
}

function Get-PluginRepos {
    param([string]$CommonRoot)
    $parent = Split-Path $CommonRoot -Parent
    $repos = @()
    foreach ($name in @('qobuzarr', 'tidalarr', 'applemusicarr')) {
        $path = Join-Path $parent $name
        $capPath = Join-Path $parent ($name.Substring(0,1).ToUpper() + $name.Substring(1))
        if (Test-Path $path) { $repos += @{ Name = $name; Path = $path } }
        elseif (Test-Path $capPath) { $repos += @{ Name = $name; Path = $capPath } }
    }
    return $repos
}

function Test-RequiredFiles {
    param([string]$RepoPath, [string]$RepoName)
    $violations = @()
    foreach ($req in $spec.requiredFiles) {
        $filePath = Join-Path $RepoPath $req.path
        if (-not (Test-Path $filePath)) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName
                Category = 'MissingFile'
                Path = $req.path
                Message = "Missing required file: $($req.path) ($($req.description))"
                Severity = 'error'
            }
        }
    }
    return $violations
}

function Test-RequiredDirectories {
    param([string]$RepoPath, [string]$RepoName)
    $violations = @()
    foreach ($req in $spec.requiredDirectories) {
        $dirPath = Join-Path $RepoPath $req.path
        if (-not (Test-Path $dirPath)) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName
                Category = 'MissingDirectory'
                Path = $req.path
                Message = "Missing required directory: $($req.path) ($($req.description))"
                Severity = 'error'
            }
        } else {
            $fileCount = @(Get-ChildItem -Path $dirPath -File -ErrorAction SilentlyContinue).Count
            if ($fileCount -lt $req.minFiles) {
                $violations += [PSCustomObject]@{
                    Repo = $RepoName
                    Category = 'InsufficientFiles'
                    Path = $req.path
                    Message = "Directory $($req.path) has $fileCount file(s), need at least $($req.minFiles)"
                    Severity = 'error'
                }
            }
        }
    }
    return $violations
}

function Test-RequiredWorkflows {
    param([string]$RepoPath, [string]$RepoName)
    $violations = @()
    $workflowDir = Join-Path $RepoPath '.github/workflows'
    foreach ($req in $spec.requiredWorkflows) {
        $wfPath = Join-Path $workflowDir $req.file
        if (-not (Test-Path $wfPath)) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName
                Category = 'MissingWorkflow'
                Path = ".github/workflows/$($req.file)"
                Message = "Missing required workflow: $($req.file) ($($req.description))"
                Severity = 'error'
            }
        }
    }
    return $violations
}

function Test-GlobalJson {
    param([string]$RepoPath, [string]$RepoName)
    $violations = @()
    $gjPath = Join-Path $RepoPath 'global.json'
    if (-not (Test-Path $gjPath)) { return $violations }  # Already caught by file check

    try {
        $gj = Get-Content $gjPath -Raw | ConvertFrom-Json
        $expectedSdk = $spec.globalJson.sdk

        if ($gj.PSObject.Properties.Match('sdk').Count -eq 0) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName; Category = 'GlobalJson'; Path = 'global.json'
                Message = "global.json missing 'sdk' section"
                Severity = 'error'
            }
            return $violations
        }

        if ($gj.sdk.version -ne $expectedSdk.version) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName; Category = 'GlobalJson'; Path = 'global.json'
                Message = "global.json sdk.version is '$($gj.sdk.version)', expected '$($expectedSdk.version)'"
                Severity = 'error'
            }
        }
        if ($gj.sdk.rollForward -ne $expectedSdk.rollForward) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName; Category = 'GlobalJson'; Path = 'global.json'
                Message = "global.json sdk.rollForward is '$($gj.sdk.rollForward)', expected '$($expectedSdk.rollForward)'"
                Severity = 'error'
            }
        }
        if ($gj.sdk.PSObject.Properties.Match('allowPrerelease').Count -gt 0 -and $gj.sdk.allowPrerelease -ne $expectedSdk.allowPrerelease) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName; Category = 'GlobalJson'; Path = 'global.json'
                Message = "global.json sdk.allowPrerelease is '$($gj.sdk.allowPrerelease)', expected '$($expectedSdk.allowPrerelease)'"
                Severity = 'warning'
            }
        }
    } catch {
        $violations += [PSCustomObject]@{
            Repo = $RepoName; Category = 'GlobalJson'; Path = 'global.json'
            Message = "Failed to parse global.json: $($_.Exception.Message)"
            Severity = 'error'
        }
    }
    return $violations
}

function Test-DirectoryBuildProps {
    param([string]$RepoPath, [string]$RepoName)
    $violations = @()
    $dbpPath = Join-Path $RepoPath 'Directory.Build.props'
    if (-not (Test-Path $dbpPath)) { return $violations }

    $content = Get-Content $dbpPath -Raw
    $reqProps = $spec.directoryBuildProps

    # Check required properties
    foreach ($prop in $reqProps.requiredProperties) {
        $pattern = "<$($prop.name)>$([regex]::Escape($prop.value))</$($prop.name)>"
        if ($content -notmatch $pattern) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName; Category = 'DirectoryBuildProps'; Path = 'Directory.Build.props'
                Message = "Missing or wrong property: <$($prop.name)>$($prop.value)</$($prop.name)> ($($prop.description))"
                Severity = 'error'
            }
        }
    }

    # Check required package references
    foreach ($pkgRef in $reqProps.requiredPackageReferences) {
        $pattern = [regex]::Escape($pkgRef.include)
        if ($content -notmatch $pattern) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName; Category = 'DirectoryBuildProps'; Path = 'Directory.Build.props'
                Message = "Missing PackageReference: $($pkgRef.include) ($($pkgRef.description))"
                Severity = 'error'
            }
        }
    }

    # Check required sections
    foreach ($section in $reqProps.requiredSections) {
        if ($content -notmatch $section.marker) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName; Category = 'DirectoryBuildProps'; Path = 'Directory.Build.props'
                Message = "Missing section: $($section.name) (look for '$($section.marker)') — $($section.description)"
                Severity = 'error'
            }
        }
    }

    # Check CPM exclusion
    foreach ($cond in $reqProps.requiredConditions) {
        if ($content -notmatch $cond.pattern) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName; Category = 'DirectoryBuildProps'; Path = 'Directory.Build.props'
                Message = "Missing condition: $($cond.description)"
                Severity = 'error'
            }
        }
    }

    return $violations
}

function Test-DirectoryPackagesProps {
    param([string]$RepoPath, [string]$RepoName)
    $violations = @()
    $dppPath = Join-Path $RepoPath 'Directory.Packages.props'
    if (-not (Test-Path $dppPath)) { return $violations }

    $content = Get-Content $dppPath -Raw
    foreach ($prop in $spec.directoryPackagesProps.requiredProperties) {
        $pattern = "<$($prop.name)>$([regex]::Escape($prop.value))</$($prop.name)>"
        if ($content -notmatch $pattern) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName; Category = 'DirectoryPackagesProps'; Path = 'Directory.Packages.props'
                Message = "Missing or wrong property: <$($prop.name)>$($prop.value)</$($prop.name)> ($($prop.description))"
                Severity = 'error'
            }
        }
    }
    return $violations
}

function Find-AllViolations {
    param([string]$RepoPath, [string]$RepoName)
    $all = @()
    $all += @(Test-RequiredFiles -RepoPath $RepoPath -RepoName $RepoName)
    $all += @(Test-RequiredDirectories -RepoPath $RepoPath -RepoName $RepoName)
    $all += @(Test-RequiredWorkflows -RepoPath $RepoPath -RepoName $RepoName)
    $all += @(Test-GlobalJson -RepoPath $RepoPath -RepoName $RepoName)
    $all += @(Test-DirectoryBuildProps -RepoPath $RepoPath -RepoName $RepoName)
    $all += @(Test-DirectoryPackagesProps -RepoPath $RepoPath -RepoName $RepoName)
    return $all | Sort-Object Repo, Category, Path
}

# ═══════════════════════════════════════════════════════════
# Main
# ═══════════════════════════════════════════════════════════

$commonRoot = Split-Path $PSScriptRoot -Parent

Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "Ecosystem Parity Lint — Structural Gap Detection" -ForegroundColor Cyan
if ($script:IsCIMode) { Write-Host "Mode: CI (strict)" -ForegroundColor Yellow }
else { Write-Host "Mode: Interactive (non-blocking)" -ForegroundColor DarkGray }
Write-Host "=================================================" -ForegroundColor Cyan
Write-Host ""

$reposToScan = @()
if ($RepoPath) {
    if (-not (Test-Path $RepoPath)) {
        Write-Host "ERROR: Path not found: $RepoPath" -ForegroundColor Red
        exit 2
    }
    $reposToScan += @{ Name = (Split-Path $RepoPath -Leaf); Path = $RepoPath }
} elseif ($AllRepos) {
    $reposToScan = Get-PluginRepos -CommonRoot $commonRoot
    if ($reposToScan.Count -eq 0) {
        Write-Host "No repos found" -ForegroundColor Yellow
        exit 0
    }
} else {
    Write-Host "Usage: ecosystem-parity-lint.ps1 [-RepoPath <path>] [-AllRepos] [-Mode ci|interactive]"
    exit 0
}

$totalViolations = @()

foreach ($repo in $reposToScan) {
    Write-Host "Scanning: $($repo.Name)" -ForegroundColor Cyan
    $violations = @(Find-AllViolations -RepoPath $repo.Path -RepoName $repo.Name)

    if ($violations.Count -eq 0) {
        Write-Host "  [OK] No violations" -ForegroundColor Green
    } else {
        $errors = @($violations | Where-Object { $_.Severity -eq 'error' })
        $warnings = @($violations | Where-Object { $_.Severity -ne 'error' })

        if ($errors.Count -gt 0) {
            Write-Host "  Found $($errors.Count) violation(s):" -ForegroundColor Red
            foreach ($v in $errors) {
                Write-Host "    [X] $($v.Message)" -ForegroundColor Red
            }
        }
        if ($warnings.Count -gt 0) {
            Write-Host "  Found $($warnings.Count) warning(s):" -ForegroundColor Yellow
            foreach ($v in $warnings) {
                Write-Host "    [!] $($v.Message)" -ForegroundColor Yellow
            }
        }
        $totalViolations += $violations
    }
    Write-Host ""
}

$totalErrors = @($totalViolations | Where-Object { $_.Severity -eq 'error' })

Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "Summary: Repos=$($reposToScan.Count), Errors=$($totalErrors.Count), Total=$($totalViolations.Count)" -ForegroundColor Cyan
Write-Host "=================================================" -ForegroundColor Cyan

$exitCode = 0
if ($totalErrors.Count -gt 0 -and $script:IsCIMode) {
    Write-Host "FAILED: $($totalErrors.Count) error(s) in CI mode" -ForegroundColor Red
    $exitCode = 1
} elseif ($totalErrors.Count -gt 0) {
    Write-Host "WARNINGS: $($totalErrors.Count) violation(s) (interactive mode, non-blocking)" -ForegroundColor Yellow
} else {
    Write-Host "PASSED" -ForegroundColor Green
}
exit $exitCode
