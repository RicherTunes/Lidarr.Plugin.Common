#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Regression tests for e2e-runner component resolution.
.DESCRIPTION
    Ensures the runner does NOT use wildcard name matching for component discovery,
    and that it relies on Find-ConfiguredComponent (which supports preferred IDs).
#>

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$passed = 0
$failed = 0

function Assert-True {
    param(
        [bool]$Condition,
        [string]$Message
    )
    if ($Condition) {
        Write-Host "  [PASS] $Message" -ForegroundColor Green
        $script:passed++
    }
    else {
        Write-Host "  [FAIL] $Message" -ForegroundColor Red
        $script:failed++
    }
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Runner Component Resolution Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$runnerPath = Join-Path (Join-Path $PSScriptRoot "..") "e2e-runner.ps1"
$content = Get-Content -LiteralPath $runnerPath -Raw

# ---------------------------------------------------------------------------
# Guardrail 1: No wildcard selection based on user-controlled component name
# ---------------------------------------------------------------------------
Assert-True -Condition (-not ($content -match '(?m)\\$_.name\\s*-like\\s*\"\\*\\$plugin\\*\"')) -Message "No wildcard name matching for configured indexer selection"
Assert-True -Condition (-not ($content -match '(?m)\\$_.implementation\\s*-like\\s*\"\\*\\$plugin\\*\"')) -Message "No wildcard implementation matching for configured indexer selection"

# ---------------------------------------------------------------------------   
# Guardrail 2: Gates resolve indexer via Find-ConfiguredComponent
# ---------------------------------------------------------------------------   
Assert-True -Condition ($content -like '*Find-ConfiguredComponent -Type "indexer" -PluginName $plugin*') -Message "Runner uses Find-ConfiguredComponent for indexer resolution in gates"
Assert-True -Condition ($content -like '*-ComponentIdsInstanceSalt*' -or $content -like '*ComponentIdsInstanceSalt*') -Message "Runner supports ComponentIdsInstanceSalt for instance namespacing"

# ---------------------------------------------------------------------------   
# Guardrail 2b: Ambiguity must be detected and fail loudly (not silently skip)
# ---------------------------------------------------------------------------
$ambNeedle = 'Get-ComponentAmbiguityDetails -Type "indexer" -PluginName $plugin'
$ambIndexerCalls = ([regex]::Matches($content, [regex]::Escape($ambNeedle))).Count
Assert-True -Condition ($ambIndexerCalls -ge 4) -Message "Runner checks for ambiguous configured indexer selection across multiple gates (Search/AlbumSearch/Revalidation/PostRestartGrab)"

# ---------------------------------------------------------------------------
# Guardrail 3: ImportList gate must not select by user-controlled name
# ---------------------------------------------------------------------------
$gatesPath = Join-Path (Join-Path $PSScriptRoot "..") "lib/e2e-gates.psm1"
$gatesContent = Get-Content -LiteralPath $gatesPath -Raw
Assert-True -Condition (-not ($gatesContent -match '(?m)\\$_.name\\s*-like\\s*\"\\*\\$PluginName\\*\"')) -Message "ImportList gate does not use wildcard name matching for configured list selection"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) { exit 1 }
