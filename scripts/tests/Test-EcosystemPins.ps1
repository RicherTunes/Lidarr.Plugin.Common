#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Self-tests for check-ecosystem-pins.ps1 — the cross-repo Common-pin drift guard.

.DESCRIPTION
    Exercises the pure decision function Test-EcosystemPinAgreement hermetically (no network).
    The guard dot-sources cleanly via -DefineFunctionsOnly, so these tests pin the agreement
    semantics that gate the consolidation program:

      - all readable streaming plugins on one SHA  => CONVERGED (Drifted=$false, Agreed=<sha>)
      - two or more DISTINCT readable SHAs          => DRIFT     (Drifted=$true)
      - unreadable repos ($null) are EXCLUDED, never counted as a distinct pin
      - a single readable pin amid unreadables      => CONVERGED on that pin
      - no readable pins                            => not drift, Agreed=$null
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$ScriptDir = $PSScriptRoot
$RepoRoot  = Split-Path -Parent (Split-Path -Parent $ScriptDir)
$Guard     = Join-Path $RepoRoot "scripts/check-ecosystem-pins.ps1"

# Dot-source: define functions only (no network).
. $Guard -DefineFunctionsOnly

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Ecosystem Pin Guard Self-Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$passed = 0
$failed = 0

function Test-Assertion {
    param(
        [string]$Name,
        [scriptblock]$Test
    )
    Write-Host "  Testing: $Name..." -NoNewline
    try {
        $result = & $Test
        if ($result) {
            Write-Host " PASS" -ForegroundColor Green
            $script:passed++
        } else {
            Write-Host " FAIL" -ForegroundColor Red
            $script:failed++
        }
    }
    catch {
        Write-Host " ERROR: $_" -ForegroundColor Red
        $script:failed++
    }
}

$shaA = 'aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'
$shaB = 'bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'

Write-Host "Function availability:" -ForegroundColor White

Test-Assertion "Test-EcosystemPinAgreement is defined after dot-source" {
    (Get-Command Test-EcosystemPinAgreement -ErrorAction SilentlyContinue) -ne $null
}

Write-Host ""
Write-Host "Agreement semantics:" -ForegroundColor White

Test-Assertion "all three on same SHA => not drifted, Agreed=that sha" {
    $r = Test-EcosystemPinAgreement -StreamingPins @{ q = $shaA; t = $shaA; a = $shaA }
    (-not $r.Drifted) -and ($r.Agreed -eq $shaA) -and ($r.Pins.Count -eq 1)
}

Test-Assertion "two distinct readable SHAs => drifted, Agreed is null" {
    $r = Test-EcosystemPinAgreement -StreamingPins @{ q = $shaA; t = $shaA; a = $shaB }
    $r.Drifted -and ($r.Pins.Count -eq 2) -and ($null -eq $r.Agreed)
}

Test-Assertion "unreadable pin is excluded, not a distinct value => readable pair agrees" {
    $r = Test-EcosystemPinAgreement -StreamingPins @{ q = $shaA; t = $shaA; a = $null }
    (-not $r.Drifted) -and ($r.Agreed -eq $shaA)
}

Test-Assertion "single readable pin amid unreadables => converged on it" {
    $r = Test-EcosystemPinAgreement -StreamingPins @{ q = $shaA; t = $null; a = $null }
    (-not $r.Drifted) -and ($r.Agreed -eq $shaA) -and ($r.Pins.Count -eq 1)
}

Test-Assertion "no readable pins => not drift, Agreed is null, zero pins" {
    $r = Test-EcosystemPinAgreement -StreamingPins @{ q = $null; t = $null; a = $null }
    (-not $r.Drifted) -and ($null -eq $r.Agreed) -and ($r.Pins.Count -eq 0)
}

Test-Assertion "one readable plus one unreadable => never drift (null excluded)" {
    # A single readable repo can never 'drift' against an unreadable one (null is excluded).
    $r = Test-EcosystemPinAgreement -StreamingPins @{ q = $shaB; t = $null }
    (-not $r.Drifted) -and ($r.Agreed -eq $shaB)
}

Write-Host ""
Write-Host "End-to-end (live guard, real repos) — converged exit code:" -ForegroundColor White

Test-Assertion "guard exits 0 against live repos when ecosystem is converged" {
    # Best-effort live check: only assert exit 0 (converged). If the network/gh is unavailable the
    # guard still exits 0 (all-unreadable is not drift), so this never produces a false failure.
    & pwsh -NoProfile -File $Guard *> $null
    $LASTEXITCODE -eq 0
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
Write-Host "========================================" -ForegroundColor Cyan

if ($failed -gt 0) {
    exit 1
}

Write-Host ""
Write-Host "[OK] All ecosystem pin guard tests passed." -ForegroundColor Green
exit 0
