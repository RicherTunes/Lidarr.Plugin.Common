#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Tripwire test: ensures only e2e-sanitize.psm1 owns sanitization patterns.
.DESCRIPTION
    Greps e2e-json-output.psm1 and e2e-diagnostics.psm1 for forbidden pattern
    definitions to prevent reintroducing local sanitizers after centralization.

    This is a guardrail against "pattern drift" where modules grow their own
    sanitization logic instead of using the centralized e2e-sanitize.psm1.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$libDir = Join-Path $PSScriptRoot '../lib'
$failed = 0

Write-Host "`n========================================"
Write-Host "Sanitizer Ownership Tripwire Test"
Write-Host "========================================`n"

# Forbidden patterns that should ONLY exist in e2e-sanitize.psm1
$forbiddenPatterns = @(
    'ErrorSanitizationPatterns'
    'PrivateEndpointPatterns'
    'SecretQueryParams'
    'SensitivePatterns'
    'SensitiveFieldPatterns'
)

# Modules that must NOT own their own patterns (should delegate to e2e-sanitize.psm1)
$guardedModules = @(
    'e2e-json-output.psm1'
    'e2e-diagnostics.psm1'
)

foreach ($module in $guardedModules) {
    $modulePath = Join-Path $libDir $module
    if (-not (Test-Path $modulePath)) {
        Write-Host "  [SKIP] $module not found" -ForegroundColor Yellow
        continue
    }

    Write-Host "Checking $module for forbidden pattern ownership..." -ForegroundColor Cyan
    $content = Get-Content $modulePath -Raw

    foreach ($pattern in $forbiddenPatterns) {
        # Look for pattern definitions (assignment with =), not just references
        # Pattern: $script:PatternName = or $PatternName = @(
        if ($content -match "\`$script:$pattern\s*=|\`$$pattern\s*=\s*@") {
            # Allow DEPRECATED comments (these are marked for removal)
            $lines = $content -split "`n"
            $isDeprecated = $false
            for ($i = 0; $i -lt $lines.Count; $i++) {
                if ($lines[$i] -match "\`$script:$pattern\s*=") {
                    # Check if previous 5 lines contain DEPRECATED
                    $start = [Math]::Max(0, $i - 5)
                    $context = $lines[$start..$i] -join "`n"
                    if ($context -match 'DEPRECATED') {
                        $isDeprecated = $true
                        break
                    }
                }
            }

            if ($isDeprecated) {
                Write-Host "  [WARN] $module defines $pattern (DEPRECATED - OK for now)" -ForegroundColor Yellow
            } else {
                Write-Host "  [FAIL] $module defines $pattern without DEPRECATED marker" -ForegroundColor Red
                Write-Host "         Pattern ownership must be in e2e-sanitize.psm1 only" -ForegroundColor Red
                $failed++
            }
        } else {
            Write-Host "  [PASS] $module does not own $pattern" -ForegroundColor Green
        }
    }
    Write-Host ""
}

# Verify e2e-sanitize.psm1 actually owns the patterns
Write-Host "Verifying e2e-sanitize.psm1 is the source of truth..." -ForegroundColor Cyan
$sanitizePath = Join-Path $libDir 'e2e-sanitize.psm1'
if (-not (Test-Path $sanitizePath)) {
    Write-Host "  [FAIL] e2e-sanitize.psm1 not found!" -ForegroundColor Red
    $failed++
} else {
    $sanitizeContent = Get-Content $sanitizePath -Raw

    # Check for essential patterns
    $requiredPatterns = @('ErrorPatterns', 'PrivateEndpointPatterns', 'SensitiveFieldPatterns')
    foreach ($pattern in $requiredPatterns) {
        if ($sanitizeContent -match "\`$script:$pattern\s*=") {
            Write-Host "  [PASS] e2e-sanitize.psm1 owns $pattern" -ForegroundColor Green
        } else {
            Write-Host "  [FAIL] e2e-sanitize.psm1 missing $pattern" -ForegroundColor Red
            $failed++
        }
    }

    # Verify version function exists
    if ($sanitizeContent -match 'function Get-SanitizerVersion') {
        Write-Host "  [PASS] e2e-sanitize.psm1 exports Get-SanitizerVersion" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] e2e-sanitize.psm1 missing Get-SanitizerVersion" -ForegroundColor Red
        $failed++
    }
}

Write-Host "`n========================================"
if ($failed -gt 0) {
    Write-Host "FAIL: $failed tripwire violation(s) detected" -ForegroundColor Red
    Write-Host "Fix: Move pattern definitions to e2e-sanitize.psm1" -ForegroundColor Red
    exit 1
} else {
    Write-Host "PASS: Sanitizer ownership verified" -ForegroundColor Green
    Write-Host "========================================"
}
