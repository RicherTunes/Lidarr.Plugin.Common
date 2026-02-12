<#
.SYNOPSIS
    Re-pins ext/Lidarr.Plugin.Common (or ext/lidarr.plugin.common) submodule to a specific SHA.

.DESCRIPTION
    This script updates the Common submodule in a plugin repo to a specific commit SHA,
    updates ext-common-sha.txt, and optionally stages the changes for commit.

.PARAMETER SHA
    The Common commit SHA to pin to. Required unless -VerifyOnly is specified.

.PARAMETER SubmodulePath
    Path to the submodule (default: auto-detect ext/Lidarr.Plugin.Common or ext/lidarr.plugin.common).

.PARAMETER Stage
    If specified, stages the submodule and ext-common-sha.txt changes.

.PARAMETER Verify
    If specified, verifies the submodule is clean before and after the operation.

.PARAMETER VerifyOnly
    CI mode: Only verify that ext-common-sha.txt matches the submodule gitlink. Exits non-zero on mismatch.

.EXAMPLE
    .\repin-common-submodule.ps1 -SHA "08f04e0c938669cb1d8890e179bc3b91f9c71725" -Stage -Verify

.EXAMPLE
    # After Common PR merges, get merge commit SHA and re-pin:
    $sha = gh pr view 316 --repo RicherTunes/Lidarr.Plugin.Common --json mergeCommit --jq .mergeCommit.oid
    .\repin-common-submodule.ps1 -SHA $sha -Stage -Verify

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

# Auto-detect submodule path if not specified
if (-not $SubmodulePath) {
    if (Test-Path "ext/Lidarr.Plugin.Common") {
        $SubmodulePath = "ext/Lidarr.Plugin.Common"
    } elseif (Test-Path "ext/lidarr.plugin.common") {
        $SubmodulePath = "ext/lidarr.plugin.common"
    } else {
        throw "Could not find Common submodule. Specify -SubmodulePath explicitly."
    }
}

$shaFile = "ext-common-sha.txt"

# --sha-from-submodule mode: read SHA from submodule gitlink (eliminates "passed wrong SHA" bugs)
if ($ShaFromSubmoduleHead) {
    $SHA = (git -C $SubmodulePath rev-parse HEAD).Trim()
    Write-Host "Read SHA from submodule HEAD: $SHA" -ForegroundColor Cyan
}

# VerifyOnly mode: CI check that gitlink matches ext-common-sha.txt
if ($VerifyOnly) {
    Write-Host "Verifying submodule gitlink matches $shaFile..." -ForegroundColor Cyan

    if (-not (Test-Path $shaFile)) {
        Write-Host "ERROR: $shaFile not found" -ForegroundColor Red
        exit 1
    }

    $expectedSha = (Get-Content $shaFile -Raw).Trim()
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

# Update ext-common-sha.txt
Write-Host "`nUpdating $shaFile..." -ForegroundColor Yellow
# Write SHA + LF (Unix line ending, no BOM, satisfies end-of-file-fixer)
$absPath = Join-Path $PWD.Path $shaFile
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
    $workflowDir = ".github/workflows"
    if (Test-Path $workflowDir) {
        Write-Host "`nUpdating workflow SHA pins..." -ForegroundColor Yellow
        # Anchored to non-comment uses: lines only
        $pattern = '(?m)^(\s*uses:\s+RicherTunes/Lidarr\.Plugin\.Common/.+@)[0-9a-f]{40}'
        $updated = 0
        Get-ChildItem "$workflowDir" -Include "*.yml","*.yaml" | ForEach-Object {
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
    $filesToStage = @($SubmodulePath, $shaFile)
    if ($UpdatePins -and (Test-Path ".github/workflows")) {
        $filesToStage += ".github/workflows"
    }
    git add @filesToStage
    Write-Host "`nStaged changes:" -ForegroundColor Green
    git status --short @filesToStage
}

Write-Host "`nDone! Submodule pinned to: $SHA" -ForegroundColor Green
Write-Host "ext-common-sha.txt updated." -ForegroundColor Green

if (-not $Stage) {
    Write-Host "`nTo stage: git add $SubmodulePath $shaFile" -ForegroundColor Cyan
}
