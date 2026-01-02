$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot '..\lib\e2e-gates.psm1') -Force

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Strict Prereqs Detection Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

$failed = 0
$passed = 0

function Assert-Equal {
    param(
        [string]$Name,
        $Actual,
        $Expected
    )
    if ($Actual -ne $Expected) {
        Write-Host "  [FAIL] $Name (expected=$Expected actual=$Actual)" -ForegroundColor Red
        $script:failed++
    }
    else {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
    }
}

Assert-Equal -Name "Null skip reason -> false" -Actual (Test-IsCredentialPrereqSkipReason $null) -Expected $false
Assert-Equal -Name "Empty skip reason -> false" -Actual (Test-IsCredentialPrereqSkipReason '') -Expected $false
Assert-Equal -Name "Credentials not configured -> true" -Actual (Test-IsCredentialPrereqSkipReason 'Credentials not configured (missing: authToken)') -Expected $true
Assert-Equal -Name "Indexer invalid credentials -> true" -Actual (Test-IsCredentialPrereqSkipReason 'Indexer test indicates missing/invalid credentials') -Expected $true
Assert-Equal -Name "Auth error -> true" -Actual (Test-IsCredentialPrereqSkipReason 'Grab failed with auth error: 401 Unauthorized') -Expected $true
Assert-Equal -Name "Credentials file missing -> true" -Actual (Test-IsCredentialPrereqSkipReason 'Credentials file missing post-restart') -Expected $true
Assert-Equal -Name "Missing env vars -> false" -Actual (Test-IsCredentialPrereqSkipReason 'Missing env vars: QOBUZARR_AUTH_TOKEN') -Expected $false
Assert-Equal -Name "Non-credential skip reason -> false" -Actual (Test-IsCredentialPrereqSkipReason 'No configured indexer found') -Expected $false

Write-Host ""
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor Red

if ($failed -gt 0) {
    throw "Strict prereqs detection tests failed: $failed"
}
