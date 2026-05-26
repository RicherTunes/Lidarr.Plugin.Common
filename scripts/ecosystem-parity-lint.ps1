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
    Scan all known plugin repos (qobuzarr, tidalarr, applemusicarr).
    Note: brainarr is excluded from bridge parity checks via .bridge-exempt marker.
.PARAMETER Mode
    Run mode: 'interactive' (warnings only, exit 0) or 'ci' (strict, exit 1 on violations).
#>

param(
    [string]$RepoPath,
    [switch]$AllRepos,
    [ValidateSet('interactive', 'ci')]
    [string]$Mode = 'interactive',
    [ValidateSet('all', 'Structural', 'VersionContract')]
    [string]$Check = 'all',
    [string]$CommonRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$specPath = Join-Path $PSScriptRoot 'parity-spec.json'
if (-not (Test-Path $specPath)) {
    Write-Host "ERROR: parity-spec.json not found at $specPath" -ForegroundColor Red
    exit 2
}
$script:Spec = Get-Content $specPath -Raw | ConvertFrom-Json
$spec = $script:Spec  # legacy alias

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

function Get-CanonicalCommonVersion {
    param([string]$CommonRootPath)
    $csprojRel = $script:Spec.versionContract.commonVersionSource
    $csprojAbs = Join-Path $CommonRootPath $csprojRel
    if (-not (Test-Path $csprojAbs)) {
        throw "Common csproj not found at $csprojAbs (versionContract.commonVersionSource)"
    }
    $content = Get-Content $csprojAbs -Raw
    if ($content -match '<Version>([^<]+)</Version>') {
        return $matches[1]
    }
    # Wave 6D: <Version> moved to Directory.Build.props as the single source of truth
    # for both Lidarr.Plugin.Common and Lidarr.Plugin.Abstractions. Fall back to reading
    # the props file from the canonical Common root. Probing this way keeps the script
    # forward-compatible for both layouts.
    $propsPath = Join-Path $CommonRootPath 'Directory.Build.props'
    if (Test-Path $propsPath) {
        $propsContent = Get-Content $propsPath -Raw
        if ($propsContent -match '<Version>([^<]+)</Version>') {
            return $matches[1]
        }
    }
    throw "Could not parse <Version> from $csprojAbs or fallback $propsPath"
}

function Test-VersionContract {
    param([string]$RepoPath, [string]$RepoName, [string]$CanonicalCommonVersion)
    $violations = @()
    $vc = $script:Spec.versionContract

    $pluginJsonPaths = @()
    foreach ($candidate in @('plugin.json', 'src/plugin.json')) {
        $p = Join-Path $RepoPath $candidate
        if (Test-Path $p) { $pluginJsonPaths += $p }
    }
    if ($pluginJsonPaths.Count -eq 0) {
        $pluginJsonPaths += @(Get-ChildItem -Path $RepoPath -Recurse -Filter 'plugin.json' -ErrorAction SilentlyContinue |
            Where-Object { $_.FullName -notmatch '[\\/](bin|obj|node_modules|ext|artifacts|_plugins|\.worktrees|\.git)[\\/]' } |
            Select-Object -ExpandProperty FullName)
    }
    if ($pluginJsonPaths.Count -eq 0) {
        $violations += [PSCustomObject]@{
            Repo = $RepoName; Category = 'VersionContract'; Path = 'plugin.json'
            Message = "No plugin.json found in repo"
            Severity = 'error'
        }
        return $violations
    }

    foreach ($pjPath in $pluginJsonPaths) {
        $pjRel = $pjPath.Substring($RepoPath.Length).TrimStart('\','/')
        try {
            $pj = Get-Content $pjPath -Raw | ConvertFrom-Json
        } catch {
            $violations += [PSCustomObject]@{
                Repo = $RepoName; Category = 'VersionContract'; Path = $pjRel
                Message = "Failed to parse plugin.json: $($_.Exception.Message)"
                Severity = 'error'
            }
            continue
        }

        # 1. commonVersion must match Common's canonical <Version>
        $pluginCommonVer = $pj.commonVersion
        if (-not $pluginCommonVer -or $pluginCommonVer -ne $CanonicalCommonVersion) {
            $violations += [PSCustomObject]@{
                Repo = $RepoName; Category = 'VersionContract'; Path = $pjRel
                Message = "plugin.json commonVersion is '$pluginCommonVer', expected canonical '$CanonicalCommonVersion' from Common csproj"
                Severity = 'error'
            }
        }

        # 2. targetFramework must equal canonical
        if ($pj.PSObject.Properties.Match('targetFramework').Count -gt 0) {
            if ($pj.targetFramework -ne $vc.targetFramework) {
                $violations += [PSCustomObject]@{
                    Repo = $RepoName; Category = 'VersionContract'; Path = $pjRel
                    Message = "plugin.json targetFramework is '$($pj.targetFramework)', expected '$($vc.targetFramework)'"
                    Severity = 'error'
                }
            }
        }

        # 2b. Enforce pluginJson.forbiddenFields (was dead config — now wired into lint)
        if ($script:Spec.pluginJson -and $script:Spec.pluginJson.forbiddenFields) {
            foreach ($ff in $script:Spec.pluginJson.forbiddenFields) {
                if ($pj.PSObject.Properties.Match($ff.field).Count -gt 0) {
                    $violations += [PSCustomObject]@{
                        Repo = $RepoName; Category = 'VersionContract'; Path = $pjRel
                        Message = "plugin.json contains forbidden field '$($ff.field)': $($ff.reason)"
                        Severity = 'error'
                    }
                }
            }
        }

        # 3. Look for sibling manifest.json — if present, version + commonVersion must match plugin.json
        $manifestPath = Join-Path (Split-Path $pjPath -Parent) 'manifest.json'
        if (Test-Path $manifestPath) {
            $manRel = $manifestPath.Substring($RepoPath.Length).TrimStart('\','/')
            try {
                $man = Get-Content $manifestPath -Raw | ConvertFrom-Json
            } catch {
                $violations += [PSCustomObject]@{
                    Repo = $RepoName; Category = 'VersionContract'; Path = $manRel
                    Message = "Failed to parse manifest.json: $($_.Exception.Message)"
                    Severity = 'error'
                }
                continue
            }

            if ($pj.version -and $man.version -and $pj.version -ne $man.version) {
                $violations += [PSCustomObject]@{
                    Repo = $RepoName; Category = 'VersionContract'; Path = $manRel
                    Message = "plugin.json version '$($pj.version)' does not match manifest.json version '$($man.version)'"
                    Severity = 'error'
                }
            }
            if ($man.PSObject.Properties.Match('commonVersion').Count -gt 0 -and $man.commonVersion -ne $CanonicalCommonVersion) {
                $violations += [PSCustomObject]@{
                    Repo = $RepoName; Category = 'VersionContract'; Path = $manRel
                    Message = "manifest.json commonVersion is '$($man.commonVersion)', expected canonical '$CanonicalCommonVersion'"
                    Severity = 'error'
                }
            }
            if ($man.PSObject.Properties.Match('targetFrameworks').Count -gt 0) {
                foreach ($fw in $man.targetFrameworks) {
                    if ($vc.forbiddenTargetFrameworks -contains $fw) {
                        $violations += [PSCustomObject]@{
                            Repo = $RepoName; Category = 'VersionContract'; Path = $manRel
                            Message = "manifest.json targetFrameworks contains forbidden framework '$fw'"
                            Severity = 'error'
                        }
                    }
                }
            }

            # Enforce manifestJson.forbiddenFields (catches minimumVersion, etc.)
            if ($script:Spec.manifestJson -and $script:Spec.manifestJson.forbiddenFields) {
                foreach ($ff in $script:Spec.manifestJson.forbiddenFields) {
                    if ($man.PSObject.Properties.Match($ff.field).Count -gt 0) {
                        $violations += [PSCustomObject]@{
                            Repo = $RepoName; Category = 'VersionContract'; Path = $manRel
                            Message = "manifest.json contains forbidden field '$($ff.field)': $($ff.reason)"
                            Severity = 'error'
                        }
                    }
                }
            }
        }
    }

    return $violations
}

function Test-BridgeExempt {
    param([string]$RepoPath)
    # Repos that place a .bridge-exempt marker in their root are excluded from
    # bridge parity checks (e.g., AddBridgeDefaults() wiring validation).
    # Current exemptions: brainarr (LLM import list, not a streaming service).
    # Policy: docs/TECH_DEBT.md "Bridge Parity Exemptions"
    return (Test-Path (Join-Path $RepoPath '.bridge-exempt'))
}

function Find-AllViolations {
    param(
        [string]$RepoPath,
        [string]$RepoName,
        [string]$CheckScope = 'all',
        [string]$CanonicalCommonVersion
    )
    $all = @()
    if ($CheckScope -eq 'all' -or $CheckScope -eq 'Structural') {
        $all += @(Test-RequiredFiles -RepoPath $RepoPath -RepoName $RepoName)
        $all += @(Test-RequiredDirectories -RepoPath $RepoPath -RepoName $RepoName)
        $all += @(Test-RequiredWorkflows -RepoPath $RepoPath -RepoName $RepoName)
        $all += @(Test-GlobalJson -RepoPath $RepoPath -RepoName $RepoName)
        $all += @(Test-DirectoryBuildProps -RepoPath $RepoPath -RepoName $RepoName)
        $all += @(Test-DirectoryPackagesProps -RepoPath $RepoPath -RepoName $RepoName)
    }
    if ($CheckScope -eq 'all' -or $CheckScope -eq 'VersionContract') {
        $all += @(Test-VersionContract -RepoPath $RepoPath -RepoName $RepoName -CanonicalCommonVersion $CanonicalCommonVersion)
    }
    return $all | Sort-Object Repo, Category, Path
}

# ═══════════════════════════════════════════════════════════
# Main
# ═══════════════════════════════════════════════════════════

if ($CommonRoot) {
    $commonRoot = (Resolve-Path $CommonRoot).Path
} else {
    $commonRoot = Split-Path $PSScriptRoot -Parent
    # When this script is consumed as a submodule (ext/Lidarr.Plugin.Common/scripts/...) the
    # default falls back to the SUBMODULE csproj, whose <Version> may be stale relative to
    # the canonical Common HEAD. Warn so callers can override with -CommonRoot when needed.
    if ($commonRoot -match '[\\/]ext[\\/]Lidarr\.Plugin\.Common[\\/]?$') {
        Write-Warning "ecosystem-parity-lint: -CommonRoot not passed; resolved Common from submodule path '$commonRoot'. Pass -CommonRoot explicitly to validate against an external canonical Common."
    }
}
$script:CanonicalCommonVersion = Get-CanonicalCommonVersion -CommonRootPath $commonRoot

Write-Host "=================================================" -ForegroundColor Cyan
Write-Host "Ecosystem Parity Lint — Structural Gap Detection" -ForegroundColor Cyan
if ($script:IsCIMode) { Write-Host "Mode: CI (strict)" -ForegroundColor Yellow }
else { Write-Host "Mode: Interactive (non-blocking)" -ForegroundColor DarkGray }
Write-Host "Check scope: $Check  |  Canonical commonVersion: $script:CanonicalCommonVersion" -ForegroundColor DarkGray
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
    $violations = @(Find-AllViolations -RepoPath $repo.Path -RepoName $repo.Name -CheckScope $Check -CanonicalCommonVersion $script:CanonicalCommonVersion)

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
