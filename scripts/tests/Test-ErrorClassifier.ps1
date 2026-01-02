$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

Import-Module (Join-Path $PSScriptRoot '..\\lib\\e2e-error-classifier.psm1') -Force

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "E2E Error Classifier Tests" -ForegroundColor Cyan
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

function Classify {
    param([string[]]$Messages)
    return Get-E2EErrorClassification -Messages $Messages
}

$c = Classify @()
Assert-Equal -Name "Empty messages -> isCredentialPrereq false" -Actual $c.isCredentialPrereq -Expected $false
Assert-Equal -Name "Empty messages -> errorCode null" -Actual $c.errorCode -Expected $null

$c = Classify @('Missing env vars: QOBUZARR_AUTH_TOKEN')
Assert-Equal -Name "Missing env vars -> prereq" -Actual $c.isCredentialPrereq -Expected $true
Assert-Equal -Name "Missing env vars -> E2E_AUTH_MISSING" -Actual $c.errorCode -Expected 'E2E_AUTH_MISSING'

$c = Classify @('Strict prereqs: Missing env vars: QOBUZARR_AUTH_TOKEN')
Assert-Equal -Name "Strict prereqs wrapper -> prereq" -Actual $c.isCredentialPrereq -Expected $true
Assert-Equal -Name "Strict prereqs wrapper -> E2E_AUTH_MISSING" -Actual $c.errorCode -Expected 'E2E_AUTH_MISSING'

$c = Classify @('Grab failed with auth error: 401 Unauthorized')
Assert-Equal -Name "401 Unauthorized -> prereq" -Actual $c.isCredentialPrereq -Expected $true
Assert-Equal -Name "401 Unauthorized -> E2E_AUTH_MISSING" -Actual $c.errorCode -Expected 'E2E_AUTH_MISSING'

$c = Classify @('Timed out waiting for command completion')
Assert-Equal -Name "Timeout -> not prereq" -Actual $c.isCredentialPrereq -Expected $false
Assert-Equal -Name "Timeout -> E2E_API_TIMEOUT" -Actual $c.errorCode -Expected 'E2E_API_TIMEOUT'

$c = Classify @('Docker daemon not running')
Assert-Equal -Name "Docker unavailable -> E2E_DOCKER_UNAVAILABLE" -Actual $c.errorCode -Expected 'E2E_DOCKER_UNAVAILABLE'

$c = Classify @('Search returned 0 results (expected >= 1) for \"Miles Davis\"')
Assert-Equal -Name "0 results -> not prereq" -Actual $c.isCredentialPrereq -Expected $false
Assert-Equal -Name "0 results -> errorCode null" -Actual $c.errorCode -Expected $null

$c = Classify @('401 timeout during request')
Assert-Equal -Name "Ordering: auth beats timeout" -Actual $c.errorCode -Expected 'E2E_AUTH_MISSING'

Write-Host ""
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor Red

if ($failed -gt 0) {
    throw "E2E error classifier tests failed: $failed"
}

