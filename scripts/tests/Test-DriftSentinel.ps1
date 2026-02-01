#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Tests for e2e-drift-sentinel.psm1 - URL redaction, field validation, artifact generation.
.DESCRIPTION
    Validates drift sentinel helper functions and edge cases:
    - URL redaction strips query strings and sensitive params
    - Header redaction masks authorization tokens
    - Field validation handles missing/partial data
    - Artifact generation produces valid JSON structure
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$modulePath = Join-Path $PSScriptRoot "..\lib\e2e-drift-sentinel.psm1"
if (-not (Test-Path $modulePath)) {
    Write-Error "Drift sentinel module not found at: $modulePath"
    exit 1
}

Import-Module $modulePath -Force

$script:TestResults = @{
    Passed = 0
    Failed = 0
    Errors = @()
}

function Assert-Equal {
    param($Expected, $Actual, [string]$Message)
    if ($Expected -ne $Actual) {
        $script:TestResults.Failed++
        $script:TestResults.Errors += "FAIL: $Message`n  Expected: $Expected`n  Actual: $Actual"
        Write-Host "FAIL: $Message" -ForegroundColor Red
        Write-Host "  Expected: $Expected" -ForegroundColor DarkGray
        Write-Host "  Actual: $Actual" -ForegroundColor DarkGray
    }
    else {
        $script:TestResults.Passed++
        Write-Host "PASS: $Message" -ForegroundColor Green
    }
}

function Assert-True {
    param($Condition, [string]$Message)
    if (-not $Condition) {
        $script:TestResults.Failed++
        $script:TestResults.Errors += "FAIL: $Message (expected true)"
        Write-Host "FAIL: $Message" -ForegroundColor Red
    }
    else {
        $script:TestResults.Passed++
        Write-Host "PASS: $Message" -ForegroundColor Green
    }
}

function Assert-Contains {
    param([string]$Haystack, [string]$Needle, [string]$Message)
    if (-not $Haystack.Contains($Needle)) {
        $script:TestResults.Failed++
        $script:TestResults.Errors += "FAIL: $Message`n  Expected to contain: $Needle`n  Actual: $Haystack"
        Write-Host "FAIL: $Message" -ForegroundColor Red
    }
    else {
        $script:TestResults.Passed++
        Write-Host "PASS: $Message" -ForegroundColor Green
    }
}

function Assert-NotContains {
    param([string]$Haystack, [string]$Needle, [string]$Message)
    if ($Haystack.Contains($Needle)) {
        $script:TestResults.Failed++
        $script:TestResults.Errors += "FAIL: $Message`n  Should NOT contain: $Needle`n  Actual: $Haystack"
        Write-Host "FAIL: $Message" -ForegroundColor Red
    }
    else {
        $script:TestResults.Passed++
        Write-Host "PASS: $Message" -ForegroundColor Green
    }
}

Write-Host "`n=== Drift Sentinel Unit Tests ===" -ForegroundColor Cyan

#region URL Redaction Tests

Write-Host "`n--- URL Redaction Tests ---" -ForegroundColor Yellow

# Test 1: URL with query string is redacted
$url1 = "https://api.example.com/search?query=test&app_id=12345&token=secret"
$redacted1 = Get-RedactedUrl -Url $url1
Assert-Contains $redacted1 "https://api.example.com/search" "URL base path preserved"
Assert-Contains $redacted1 "[REDACTED]" "Query string marked as redacted"
Assert-NotContains $redacted1 "app_id=12345" "App ID removed from URL"
Assert-NotContains $redacted1 "token=secret" "Token removed from URL"

# Test 2: URL without query string unchanged
$url2 = "https://api.example.com/v1/albums"
$redacted2 = Get-RedactedUrl -Url $url2
Assert-Equal $url2 $redacted2 "URL without query string unchanged"

# Test 3: URL with only path params
$url3 = "https://api.example.com/album/12345/tracks"
$redacted3 = Get-RedactedUrl -Url $url3
Assert-Equal $url3 $redacted3 "URL with path params preserved"

# Test 4: URL with fragment
$url4 = "https://api.example.com/docs#section?fake=query"
$redacted4 = Get-RedactedUrl -Url $url4
Assert-NotContains $redacted4 "fake=query" "Fragment that looks like query handled"

#endregion

#region Header Redaction Tests

Write-Host "`n--- Header Redaction Tests ---" -ForegroundColor Yellow

# Test 5: Authorization header redacted
$headers1 = @{
    "Authorization" = "Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9"
    "Content-Type" = "application/json"
    "X-Request-Id" = "abc123"
}
$redactedHeaders1 = Get-RedactedHeaders -Headers $headers1
Assert-Equal "[REDACTED]" $redactedHeaders1["Authorization"] "Authorization header redacted"
Assert-Equal "application/json" $redactedHeaders1["Content-Type"] "Content-Type preserved"
Assert-Equal "abc123" $redactedHeaders1["X-Request-Id"] "X-Request-Id preserved"

# Test 6: X-User-Auth-Token redacted
$headers2 = @{
    "X-User-Auth-Token" = "qobuz-token-12345"
    "Accept" = "application/json"
}
$redactedHeaders2 = Get-RedactedHeaders -Headers $headers2
Assert-Equal "[REDACTED]" $redactedHeaders2["X-User-Auth-Token"] "X-User-Auth-Token redacted"
Assert-Equal "application/json" $redactedHeaders2["Accept"] "Accept preserved"

# Test 7: Empty headers handled
$headers3 = @{}
$redactedHeaders3 = Get-RedactedHeaders -Headers $headers3
Assert-Equal 0 $redactedHeaders3.Count "Empty headers returns empty hashtable"

# Test 8: Null headers handled
$redactedHeaders4 = Get-RedactedHeaders -Headers $null
Assert-Equal 0 $redactedHeaders4.Count "Null headers returns empty hashtable"

#endregion

#region Field Validation Tests

Write-Host "`n--- Field Validation Tests ---" -ForegroundColor Yellow

# Test 9: All required fields present
$obj1 = [PSCustomObject]@{ id = 1; title = "Test"; artist = "Artist" }
$missing1 = @(Get-MissingRequiredFields -Object $obj1 -RequiredFields @("id", "title", "artist") -ObjectName "Album")
Assert-Equal 0 $missing1.Count "No missing fields when all present"

# Test 10: Some required fields missing
$obj2 = [PSCustomObject]@{ id = 1; title = "Test" }
$missing2 = @(Get-MissingRequiredFields -Object $obj2 -RequiredFields @("id", "title", "artist", "duration") -ObjectName "Album")
Assert-Equal 2 $missing2.Count "Two missing fields detected"
Assert-Contains ($missing2 -join ",") "Album.artist" "Missing artist field reported"
Assert-Contains ($missing2 -join ",") "Album.duration" "Missing duration field reported"

# Test 11: Null object returns all fields as missing
$missing3 = @(Get-MissingRequiredFields -Object $null -RequiredFields @("id", "title") -ObjectName "Album")
Assert-Equal 2 $missing3.Count "Null object returns all required fields"

# Test 12: At-least-one validation - field present in some items
$items1 = @(
    [PSCustomObject]@{ id = 1; title = "A" },
    [PSCustomObject]@{ id = 2; title = "B"; hires = $true },
    [PSCustomObject]@{ id = 3; title = "C" }
)
$atLeastMissing1 = @(Get-MissingAtLeastOneFields -Items $items1 -AtLeastOneFields @("hires") -ObjectName "Album")
Assert-Equal 0 $atLeastMissing1.Count "At-least-one satisfied when one item has field"

# Test 13: At-least-one validation - field missing from all items
$items2 = @(
    [PSCustomObject]@{ id = 1; title = "A" },
    [PSCustomObject]@{ id = 2; title = "B" }
)
$atLeastMissing2 = @(Get-MissingAtLeastOneFields -Items $items2 -AtLeastOneFields @("hires", "streamable") -ObjectName "Album")
Assert-Equal 2 $atLeastMissing2.Count "At-least-one reports missing when no item has field"

# Test 14: Empty items array
$atLeastMissing3 = @(Get-MissingAtLeastOneFields -Items @() -AtLeastOneFields @("hires") -ObjectName "Album")
Assert-Equal 1 $atLeastMissing3.Count "Empty array returns all at-least-one fields"

#endregion

#region Artifact Generation Tests

Write-Host "`n--- Artifact Generation Tests ---" -ForegroundColor Yellow

# Test 15: Artifact structure is valid
$mockResult = [PSCustomObject]@{
    Success = $true
    DriftCount = 1
    ErrorCount = 0
    InconclusiveCount = 0
    SkippedCount = 0
    Warnings = @("[qobuz/auth-error] Missing: error.code")
    Results = @(
        [PSCustomObject]@{
            Provider = "qobuz"
            Endpoint = "auth-error"
            Mode = "error"
            Success = $true
            DriftDetected = $true
            IsInconclusive = $false
            Details = "Missing: error.code"
            MissingFields = @("error.code")
            AtLeastOneMissing = @()
            ExpectedFields = @("status", "message", "code")
            ActualFields = @("status", "message")
            Error = $null
            SkippedReason = $null
        }
    )
    ExpectationsVersion = "1.0.0"
}

$artifact = ConvertTo-DriftArtifact -Result $mockResult -Providers @("qobuz")
Assert-True ($null -ne $artifact.timestamp) "Artifact has timestamp"
Assert-Equal "1.0.0" $artifact.expectationsVersion "Artifact has expectations version"
Assert-Equal 1 $artifact.summary.driftCount "Artifact summary has drift count"
Assert-Equal 1 $artifact.probes.Count "Artifact has probe results"
Assert-Equal "qobuz" $artifact.probes[0].provider "Probe has provider"
Assert-Equal "error" $artifact.probes[0].mode "Probe has mode"
Assert-True $artifact.probes[0].driftDetected "Probe drift detected flag"

# Test 16: Artifact JSON serialization
$json = $artifact | ConvertTo-Json -Depth 10
Assert-True ($json.Length -gt 100) "Artifact serializes to JSON"
Assert-Contains $json "expectationsVersion" "JSON contains version field"
Assert-Contains $json "driftDetected" "JSON contains drift flag"

# Test 17: Artifact excludes raw error messages (security)
$mockResultWithError = [PSCustomObject]@{
    Success = $false
    DriftCount = 0
    ErrorCount = 1
    InconclusiveCount = 0
    SkippedCount = 0
    Warnings = @()
    Results = @(
        [PSCustomObject]@{
            Provider = "qobuz"
            Endpoint = "auth-error"
            Mode = "error"
            Success = $false
            DriftDetected = $false
            IsInconclusive = $false
            Details = $null
            MissingFields = @()
            AtLeastOneMissing = @()
            ExpectedFields = @()
            ActualFields = @()
            Error = "HTTP request failed: https://api.qobuz.com?app_id=SECRET&token=MYSECRET"
            SkippedReason = $null
        }
    )
    ExpectationsVersion = "1.0.0"
}

$artifactWithError = ConvertTo-DriftArtifact -Result $mockResultWithError -Providers @("qobuz")
$jsonWithError = $artifactWithError | ConvertTo-Json -Depth 10
Assert-NotContains $jsonWithError "SECRET" "Artifact JSON does not contain raw error with secrets"
Assert-NotContains $jsonWithError "MYSECRET" "Artifact JSON does not contain token from error"
Assert-True $artifactWithError.probes[0].hasError "Artifact indicates error occurred"

#endregion

#region Version Info Tests

Write-Host "`n--- Version Info Tests ---" -ForegroundColor Yellow

# Test 18: Version info available
$version = Get-DriftSentinelVersion
Assert-True ($null -ne $version.Version) "Version string available"
Assert-True ($null -ne $version.LastUpdated) "LastUpdated available"
Assert-True ($version.Version -match '^\d+\.\d+\.\d+$') "Version follows semver format"

# Test 19: Provider list available
$providers = Get-DriftSentinelProviders
Assert-True ($providers.Count -ge 2) "At least 2 providers configured"
Assert-True ($providers -contains "qobuz") "Qobuz provider available"
Assert-True ($providers -contains "tidal") "Tidal provider available"

# Test 20: Provider expectations available
$qobuzExpectations = Get-ProviderFieldExpectations -Provider "qobuz"
Assert-True ($null -ne $qobuzExpectations) "Qobuz expectations available"
Assert-True ($null -ne $qobuzExpectations.AuthEndpoint) "Qobuz auth endpoint defined"
Assert-True ($null -ne $qobuzExpectations.ExpectedErrorFields) "Qobuz error fields defined"
Assert-True ($null -ne $qobuzExpectations._expectationsVersion) "Expectations version included"

#endregion

#endregion

#region Tidal OAuth Tests

Write-Host "`n--- Tidal OAuth Tests ---" -ForegroundColor Yellow

# Test 21: Get-TidalAccessToken returns expected structure
# Note: We can't test actual OAuth without real credentials, but we can test the function exists and returns proper structure
$tokenResult = Get-TidalAccessToken -ClientId "invalid" -ClientSecret "invalid" -TimeoutSeconds 5
Assert-True ($null -ne $tokenResult) "Get-TidalAccessToken returns result object"
Assert-True ($null -ne $tokenResult.Success) "Token result has Success property"
Assert-True ($null -ne $tokenResult.Error -or $tokenResult.Success) "Token result has Error or Success"
Assert-Equal $false $tokenResult.Success "Invalid credentials return Success=false"
Assert-True ($tokenResult.Error -match "Invalid|credentials|401|400") "Error message indicates auth failure"

#endregion

#region Summary

Write-Host "`n=== Test Summary ===" -ForegroundColor Cyan
Write-Host "Passed: $($script:TestResults.Passed)" -ForegroundColor Green
Write-Host "Failed: $($script:TestResults.Failed)" -ForegroundColor $(if ($script:TestResults.Failed -gt 0) { "Red" } else { "Green" })

if ($script:TestResults.Failed -gt 0) {
    Write-Host "`nFailures:" -ForegroundColor Red
    foreach ($error in $script:TestResults.Errors) {
        Write-Host "  $error" -ForegroundColor Red
    }
    exit 1
}

Write-Host "`nAll drift sentinel tests passed!" -ForegroundColor Green
exit 0

#endregion
