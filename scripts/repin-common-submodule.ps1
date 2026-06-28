<#
.SYNOPSIS
    Re-pins ext/Lidarr.Plugin.Common (or ext/lidarr.plugin.common) submodule to a specific SHA.

.DESCRIPTION
    This script updates the Common submodule in a plugin repo to a specific commit SHA,
    updates ext-common-sha.txt, and optionally stages the changes for commit.

    CI bump workflows should use: -ShaFromSubmoduleHead -Stage
    Maintainers can also use -UpdatePins to rewrite workflow SHA pins (requires PAT).

    Note on flag style: this PowerShell script uses -PascalCase parameters
    (-ShaFromSubmoduleHead, -Stage, -VerifyOnly, -UpdatePins).
    The companion .sh script uses GNU-style flags (--sha-from-submodule, --stage,
    --verify-only, --update-pins). The semantics are identical; only the syntax differs.

.PARAMETER SHA
    The Common commit SHA to pin to. Required unless -VerifyOnly or -ShaFromSubmoduleHead is specified.

.PARAMETER SubmodulePath
    Path to the submodule (default: auto-detect ext/Lidarr.Plugin.Common or ext/lidarr.plugin.common).
    Accepts both absolute and relative paths; relative paths are resolved from the git repo root.

.PARAMETER Stage
    If specified, stages the submodule and ext-common-sha.txt changes, then self-verifies that the
    staged gitlink, sentinel file, and submodule HEAD all match the target SHA.

.PARAMETER Verify
    If specified, verifies the submodule is clean before and after the operation.

.PARAMETER VerifyOnly
    CI mode: Verify that ext-common-sha.txt matches the submodule gitlink. Exits non-zero on
    mismatch or stale reusable Common workflow SHA pins.

.PARAMETER ShaFromSubmoduleHead
    Read SHA from submodule HEAD instead of requiring an explicit -SHA parameter.

.PARAMETER UpdatePins
    Rewrite workflow SHA pins in .github/workflows/*.yml to match the new SHA.
    MANUAL ONLY: requires a PAT with 'workflows' scope to push the changes.
    CI bump workflows should NOT use this flag (GITHUB_TOKEN cannot push
    .github/workflows/ changes).

.EXAMPLE
    # CI bump workflow (recommended):
    .\repin-common-submodule.ps1 -ShaFromSubmoduleHead -Stage -SubmodulePath ext/Lidarr.Plugin.Common
    .\repin-common-submodule.ps1 -VerifyOnly -SubmodulePath ext/Lidarr.Plugin.Common

.EXAMPLE
    # Manual re-pin with verification:
    .\repin-common-submodule.ps1 -SHA "08f04e0c938669cb1d8890e179bc3b91f9c71725" -Stage -Verify

.EXAMPLE
    # Maintainer: also update workflow SHA pins (requires PAT to push):
    .\repin-common-submodule.ps1 -SHA $sha -Stage -Verify -UpdatePins

.EXAMPLE
    # CI verification (fail fast if mismatch):
    .\repin-common-submodule.ps1 -VerifyOnly
#>

param(
    [string]$SHA,

    [string]$SubmodulePath,

    [switch]$Stage,

    [switch]$Verify,

    [switch]$VerifyOnly,

    [switch]$ShaFromSubmoduleHead,

    [switch]$UpdatePins
)

$ErrorActionPreference = "Stop"

# Anchor all path operations to the git repo root so the script is cwd-independent.
$RepoRoot = (git rev-parse --show-toplevel 2>$null)
if (-not $RepoRoot -or $LASTEXITCODE -ne 0) {
    throw "Not inside a git repository. Run this script from within the plugin repo."
}
$RepoRoot = $RepoRoot.Trim()

# Auto-detect submodule path if not specified (uses $RepoRoot, not $PWD)
if (-not $SubmodulePath) {
    if (Test-Path (Join-Path $RepoRoot "ext/Lidarr.Plugin.Common")) {
        $SubmodulePath = Join-Path $RepoRoot "ext/Lidarr.Plugin.Common"
    } elseif (Test-Path (Join-Path $RepoRoot "ext/lidarr.plugin.common")) {
        $SubmodulePath = Join-Path $RepoRoot "ext/lidarr.plugin.common"
    } else {
        throw "Could not find Common submodule. Specify -SubmodulePath explicitly."
    }
}

# Normalize SubmodulePath: relative paths are resolved from the repo root
if (-not [System.IO.Path]::IsPathRooted($SubmodulePath)) {
    $SubmodulePath = Join-Path $RepoRoot $SubmodulePath
}
$SubmoduleRelPath = [System.IO.Path]::GetRelativePath($RepoRoot, $SubmodulePath)

$shaFile = "ext-common-sha.txt"
$absPath = Join-Path $RepoRoot $shaFile

# --sha-from-submodule mode: read SHA from submodule gitlink (eliminates "passed wrong SHA" bugs)
if ($ShaFromSubmoduleHead) {
    $SHA = (git -C $SubmodulePath rev-parse HEAD).Trim()
    Write-Host "Read SHA from submodule HEAD: $SHA" -ForegroundColor Cyan
}

# VerifyOnly mode: CI check that gitlink matches ext-common-sha.txt
if ($VerifyOnly) {
    Write-Host "Verifying submodule gitlink matches $shaFile..." -ForegroundColor Cyan

    if (-not (Test-Path $absPath)) {
        Write-Host "ERROR: $shaFile not found" -ForegroundColor Red
        exit 1
    }

    # Validate file format: exactly 40 lowercase hex + LF (41 bytes, no BOM, no CRLF)
    $rawBytes = [System.IO.File]::ReadAllBytes($absPath)
    $byteLen = $rawBytes.Length
    if ($byteLen -ne 41) {
        Write-Host "ERROR: $shaFile must be exactly 41 bytes (40 hex + LF), got $byteLen" -ForegroundColor Red
        exit 1
    }
    if ($rawBytes[40] -ne 0x0A) {
        Write-Host "ERROR: $shaFile must end with LF (0x0A), got 0x$($rawBytes[40].ToString('X2'))" -ForegroundColor Red
        exit 1
    }
    $rawContent = [System.Text.Encoding]::ASCII.GetString($rawBytes, 0, 40)
    if ($rawContent -cnotmatch '^[0-9a-f]{40}$') {
        Write-Host "ERROR: $shaFile must contain exactly 40 lowercase hex chars, got: $rawContent" -ForegroundColor Red
        exit 1
    }

    $expectedSha = $rawContent
    $actualSha = (git -C $SubmodulePath rev-parse HEAD).Trim()

    Write-Host "Expected (from $shaFile): $expectedSha" -ForegroundColor Cyan
    Write-Host "Actual (submodule HEAD):  $actualSha" -ForegroundColor Cyan

    if ($expectedSha -ne $actualSha) {
        Write-Host "ERROR: Submodule SHA mismatch!" -ForegroundColor Red
        Write-Host "The submodule gitlink does not match $shaFile." -ForegroundColor Red
        Write-Host "Fix: Run .\scripts\repin-common-submodule.ps1 -SHA $expectedSha -Stage" -ForegroundColor Yellow
        exit 1
    }

    # Also verify submodule is clean
    $status = git -C $SubmodulePath status --porcelain
    if ($status) {
        Write-Host "ERROR: Submodule has uncommitted changes:" -ForegroundColor Red
        Write-Host $status
        exit 1
    }

    Write-Host "Submodule verification passed." -ForegroundColor Green

    # Guard: fail on stale reusable Common workflow SHA pins.
    # Suppressed when the repo has no Common reusable-workflow references at all.
    $workflowDir = Join-Path $RepoRoot ".github/workflows"
    if (Test-Path $workflowDir) {
        $workflowFiles = @(
            Get-ChildItem -Path $workflowDir -File -Filter "*.yml" -ErrorAction SilentlyContinue
            Get-ChildItem -Path $workflowDir -File -Filter "*.yaml" -ErrorAction SilentlyContinue
        )
        $pinPattern = '^\s*uses:\s+RicherTunes/Lidarr\.Plugin\.Common/.+@([0-9a-f]{40})'
        $stale = 0
        $totalPins = 0
        $workflowFiles | ForEach-Object {
            $lines = [IO.File]::ReadAllLines($_.FullName)
            for ($i = 0; $i -lt $lines.Length; $i++) {
                $m = [regex]::Match($lines[$i], $pinPattern)
                if ($m.Success) {
                    $totalPins++
                    $pinSha = $m.Groups[1].Value
                    if ($pinSha -ne $expectedSha) {
                        $stale++
                        $lineNum = $i + 1
                        Write-Host "ERROR: Stale pin in $($_.Name):$lineNum" -ForegroundColor Red
                        Write-Host "  $($lines[$i].Trim())" -ForegroundColor Red
                        Write-Host "  pinned: $($pinSha.Substring(0,12))...  expected: $($expectedSha.Substring(0,12))..." -ForegroundColor Red
                    }
                }
            }
        }
        if ($stale -gt 0) {
            Write-Host "ERROR: $stale/$totalPins workflow pin(s) are stale." -ForegroundColor Red
            Write-Host "Fix: .\scripts\repin-common-submodule.ps1 -SHA $expectedSha -UpdatePins -Stage" -ForegroundColor Cyan
            exit 1
        }
    }

    exit 0
}

# Normal re-pin mode requires SHA
if (-not $SHA) {
    Write-Host "Error: -SHA is required" -ForegroundColor Red
    Write-Host "Usage: .\repin-common-submodule.ps1 -SHA <sha> [-Stage] [-Verify]"
    Write-Host "       .\repin-common-submodule.ps1 -VerifyOnly  # CI mode"
    exit 1
}

# Validate 40-hex (case-insensitive), then normalize to lowercase
if ($SHA -notmatch '^[0-9a-fA-F]{40}$') {
    Write-Host "Error: SHA must be a 40-character hex string, got: $SHA" -ForegroundColor Red
    exit 1
}
$SHA = $SHA.ToLowerInvariant()

Write-Host "Re-pinning Common submodule at: $SubmodulePath" -ForegroundColor Cyan
Write-Host "Target SHA: $SHA" -ForegroundColor Cyan

# Verify submodule is clean before operation
if ($Verify) {
    Write-Host "`nVerifying submodule is clean..." -ForegroundColor Yellow
    $status = git -C $SubmodulePath status --porcelain
    if ($status) {
        Write-Host "ERROR: Submodule has uncommitted changes:" -ForegroundColor Red
        Write-Host $status
        throw "Submodule must be clean before re-pinning. Discard changes first."
    }
    Write-Host "Submodule is clean." -ForegroundColor Green
}

# Fetch and checkout the target SHA
Write-Host "`nFetching origin..." -ForegroundColor Yellow
git -C $SubmodulePath fetch origin

Write-Host "Checking out $SHA..." -ForegroundColor Yellow
git -C $SubmodulePath checkout $SHA

if ($LASTEXITCODE -ne 0) {
    throw "Failed to checkout SHA: $SHA"
}

# Update ext-common-sha.txt (anchored to repo root, not $PWD)
Write-Host "`nUpdating $shaFile..." -ForegroundColor Yellow
# Write SHA + LF (Unix line ending, no BOM, satisfies end-of-file-fixer)
$bytes = [System.Text.Encoding]::ASCII.GetBytes($SHA + "`n")
[System.IO.File]::WriteAllBytes($absPath, $bytes)

# Verify the checkout
Write-Host "`nVerifying checkout..." -ForegroundColor Yellow
$currentSha = git -C $SubmodulePath rev-parse HEAD
if ($currentSha -ne $SHA) {
    throw "SHA mismatch after checkout. Expected: $SHA, Got: $currentSha"
}
Write-Host "Checkout verified: $currentSha" -ForegroundColor Green

if ($UpdatePins) {
    $workflowDir = Join-Path $RepoRoot ".github/workflows"
    if (Test-Path $workflowDir) {
        $workflowFiles = @(
            Get-ChildItem -Path $workflowDir -File -Filter "*.yml" -ErrorAction SilentlyContinue
            Get-ChildItem -Path $workflowDir -File -Filter "*.yaml" -ErrorAction SilentlyContinue
        )
        Write-Host "`nUpdating workflow SHA pins..." -ForegroundColor Yellow
        # Anchored to non-comment uses: lines only
        $pattern = '(?m)^(\s*uses:\s+RicherTunes/Lidarr\.Plugin\.Common/.+@)[0-9a-f]{40}'
        $updated = 0
        $workflowFiles | ForEach-Object {
            $content = [IO.File]::ReadAllText($_.FullName)
            $newContent = [regex]::Replace($content, $pattern, "`${1}$SHA")
            if ($newContent -ne $content) {
                [IO.File]::WriteAllText($_.FullName, $newContent)
                $updated++
                Write-Host "  Updated: $($_.Name)" -ForegroundColor Green
            }
        }
        Write-Host "Updated $updated workflow file(s)." -ForegroundColor Cyan
    }
}

# Verify submodule is clean after operation
if ($Verify) {
    Write-Host "`nVerifying submodule is still clean..." -ForegroundColor Yellow
    $status = git -C $SubmodulePath status --porcelain
    if ($status) {
        Write-Host "WARNING: Submodule has changes after checkout:" -ForegroundColor Yellow
        Write-Host $status
    } else {
        Write-Host "Submodule is clean." -ForegroundColor Green
    }
}

# Stage changes if requested
if ($Stage) {
    Write-Host "`nStaging changes..." -ForegroundColor Yellow
    $filesToStage = @($SubmodulePath, $absPath)
    if ($UpdatePins -and (Test-Path (Join-Path $RepoRoot ".github/workflows"))) {
        $filesToStage += Join-Path $RepoRoot ".github/workflows"
    }
    git -C $RepoRoot add @filesToStage
    Write-Host "`nStaged changes:" -ForegroundColor Green
    git -C $RepoRoot status --short @filesToStage

    # Self-verify: sentinel file, submodule HEAD, and staged gitlink must all equal $SHA.
    # This catches silent drift (e.g. checkout failed silently, git add picked up wrong state).
    $sentinelBytes = [System.IO.File]::ReadAllBytes($absPath)
    $sentinelSha   = [System.Text.Encoding]::ASCII.GetString($sentinelBytes, 0, [Math]::Min(40, $sentinelBytes.Length))
    $submoduleHead = (git -C $SubmodulePath rev-parse HEAD 2>$null).Trim()
    $lsOutput      = (git -C $RepoRoot ls-files -s $SubmoduleRelPath 2>$null).Trim()
    $stagedSha     = if ($lsOutput -match '^\d+\s+([0-9a-f]{40})\s+\d+') { $Matches[1] } else { "" }
    $failures = @()
    if ($sentinelSha -ne $SHA) { $failures += "ext-common-sha.txt contains '$sentinelSha', expected '$SHA'" }
    if ($submoduleHead -ne $SHA) { $failures += "submodule HEAD is '$submoduleHead', expected '$SHA'" }
    if ($stagedSha -ne $SHA) { $failures += "staged gitlink is '$stagedSha', expected '$SHA' (was git add run?)" }
    if ($failures.Count -gt 0) {
        Write-Host "ERROR: Self-verify FAILED — silent pin drift detected:" -ForegroundColor Red
        $failures | ForEach-Object { Write-Host "  - $_" -ForegroundColor Red }
        exit 1
    }
    Write-Host "Self-verify passed: sentinel, submodule HEAD, and staged gitlink all = $SHA" -ForegroundColor Green
}

Write-Host "`nDone! Submodule pinned to: $SHA" -ForegroundColor Green
Write-Host "ext-common-sha.txt updated." -ForegroundColor Green

if (-not $Stage) {
    Write-Host "`nTo stage: git -C '$RepoRoot' add '$SubmodulePath' '$absPath'" -ForegroundColor Cyan
}