#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Self-tests for importing the E2E auth failure module.

.DESCRIPTION
    Catches module parse failures before smoke startup and checks the exported
    gate and available mode names. Requires only PowerShell; no network or Docker
    calls are made and credential presets are neither invoked nor logged.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$RepoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$module = Import-Module (Join-Path $RepoRoot 'scripts/lib/e2e-authfail.psm1') -Force -PassThru -ErrorAction Stop

$passed = 0
$failed = 0

function Test-Assertion {
    param([string]$Name, [scriptblock]$Test)
    Write-Host "  Testing: $Name..." -NoNewline
    try {
        if (& $Test) { Write-Host ' PASS' -ForegroundColor Green; $script:passed++ }
        else { Write-Host ' FAIL' -ForegroundColor Red; $script:failed++ }
    }
    catch {
        Write-Host " FAIL ($($_.Exception.Message))" -ForegroundColor Red
        $script:failed++
    }
}

Test-Assertion 'module exports Invoke-AuthFailRedactionGate' {
    $module.ExportedFunctions.ContainsKey('Invoke-AuthFailRedactionGate')
}

Test-Assertion 'module exports Get-AuthFailModes' {
    $module.ExportedFunctions.ContainsKey('Get-AuthFailModes')
}

Test-Assertion 'Get-AuthFailModes exposes 401, 403, and 429' {
    $modes = @(& $module.ExportedFunctions['Get-AuthFailModes'])
    $modes -contains '401' -and $modes -contains '403' -and $modes -contains '429'
}

Write-Host ''
Write-Host "Results: $passed passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
if ($failed -gt 0) { exit 1 }
exit 0
