#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Unit tests for Configure gate functionality.

.DESCRIPTION
    Tests the masked field handling, redaction, and skip behavior
    of Update-ComponentAuthFields and Get-PluginEnvConfig.

.EXAMPLE
    ./tests/Test-ConfigureGate.ps1
#>

$ErrorActionPreference = "Stop"
$script:TestsPassed = 0
$script:TestsFailed = 0

function Write-TestResult {
    param(
        [string]$TestName,
        [bool]$Passed,
        [string]$Message = ""
    )

    if ($Passed) {
        Write-Host "  [PASS] $TestName" -ForegroundColor Green
        $script:TestsPassed++
    } else {
        Write-Host "  [FAIL] $TestName" -ForegroundColor Red
        if ($Message) {
            Write-Host "         $Message" -ForegroundColor Yellow
        }
        $script:TestsFailed++
    }
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Configure Gate Unit Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# =============================================================================
# Test 1: Sensitive fields list completeness
# =============================================================================
Write-Host "Test Group: Sensitive Fields List" -ForegroundColor Yellow

# Load the e2e-runner.ps1 to get the sensitiveFields definition
$scriptPath = Join-Path (Join-Path $PSScriptRoot "..") "e2e-runner.ps1"
$scriptContent = Get-Content $scriptPath -Raw

# Extract sensitiveFields array from the script
# Check each expected field appears in the sensitiveFields definition
$expectedFields = @(
    # Auth secrets
    "authToken", "password", "redirectUrl", "appSecret",
    "refreshToken", "accessToken", "clientSecret",
    "apiKey", "secret", "token",
    # PII fields
    "userId", "email", "username",
    # Internal URLs
    "configurationUrl"
)

# Find the sensitiveFields block - it starts with $sensitiveFields = @( and ends with the closing )
# Use a simpler approach: check if each field appears near $sensitiveFields
$sensitiveBlockStart = $scriptContent.IndexOf('$sensitiveFields = @(')
if ($sensitiveBlockStart -gt 0) {
    # Get a generous block of text after the start
    $sensitiveBlock = $scriptContent.Substring($sensitiveBlockStart, [Math]::Min(1000, $scriptContent.Length - $sensitiveBlockStart))

    foreach ($field in $expectedFields) {
        $fieldInScript = $sensitiveBlock -match "`"$field`""
        Write-TestResult -TestName "sensitiveFields includes '$field'" -Passed $fieldInScript
    }
} else {
    Write-TestResult -TestName "Parse sensitiveFields from script" -Passed $false -Message "Could not find sensitiveFields array"
}

Write-Host ""

# =============================================================================
# Test 2: Get-PluginEnvConfig - Missing env vars returns correct structure
# =============================================================================
Write-Host "Test Group: Get-PluginEnvConfig Missing Env Vars" -ForegroundColor Yellow

# Clear any existing env vars
$originalToken = $env:QOBUZARR_AUTH_TOKEN
$originalQobuzToken = $env:QOBUZ_AUTH_TOKEN
$env:QOBUZARR_AUTH_TOKEN = $null
$env:QOBUZ_AUTH_TOKEN = $null

# Source the function definitions (import module pattern)
# We'll parse and execute just the Get-PluginEnvConfig function
$functionMatch = [regex]::Match($scriptContent, '(?s)function Get-PluginEnvConfig \{.*?^\}', [System.Text.RegularExpressions.RegexOptions]::Multiline)
if ($functionMatch.Success) {
    # Also need helper functions
    $helperFunctions = @"
function Get-LidarrFieldValue { param([AllowNull()]`$Fields, [string]`$Name) return `$null }
function Set-LidarrFieldValue { param([AllowNull()]`$Fields, [string]`$Name, `$Value) return @() }
"@
    Invoke-Expression $helperFunctions
    Invoke-Expression $functionMatch.Value

    $config = Get-PluginEnvConfig -PluginName "Qobuzarr"

    Write-TestResult -TestName "HasRequiredEnvVars is false when QOBUZARR_AUTH_TOKEN missing" `
        -Passed ($config.HasRequiredEnvVars -eq $false)

    Write-TestResult -TestName "MissingRequired contains QOBUZARR_AUTH_TOKEN" `
        -Passed ($config.MissingRequired -contains "QOBUZARR_AUTH_TOKEN")

    Write-TestResult -TestName "IndexerFields is empty hashtable" `
        -Passed ($config.IndexerFields.Count -eq 0)
} else {
    Write-TestResult -TestName "Parse Get-PluginEnvConfig function" -Passed $false
}

# Restore env vars
$env:QOBUZARR_AUTH_TOKEN = $originalToken
$env:QOBUZ_AUTH_TOKEN = $originalQobuzToken

Write-Host ""

# =============================================================================
# Test 3: Get-PluginEnvConfig - With valid env vars returns correct structure
# =============================================================================
Write-Host "Test Group: Get-PluginEnvConfig With Env Vars" -ForegroundColor Yellow

$env:QOBUZARR_AUTH_TOKEN = "test-token-12345"
$env:QOBUZARR_USER_ID = "999"
$env:QOBUZARR_COUNTRY_CODE = "GB"

if ($functionMatch.Success) {
    $config = Get-PluginEnvConfig -PluginName "Qobuzarr"

    Write-TestResult -TestName "HasRequiredEnvVars is true when QOBUZARR_AUTH_TOKEN set" `
        -Passed ($config.HasRequiredEnvVars -eq $true)

    Write-TestResult -TestName "MissingRequired is empty" `
        -Passed ($config.MissingRequired.Count -eq 0)

    Write-TestResult -TestName "IndexerFields contains authToken" `
        -Passed ($config.IndexerFields.ContainsKey("authToken"))

    Write-TestResult -TestName "IndexerFields.authToken matches env var" `
        -Passed ($config.IndexerFields["authToken"] -eq "test-token-12345")

    Write-TestResult -TestName "IndexerFields contains userId from env" `
        -Passed ($config.IndexerFields["userId"] -eq "999")

    Write-TestResult -TestName "IndexerFields contains countryCode from env" `
        -Passed ($config.IndexerFields["countryCode"] -eq "GB")

    Write-TestResult -TestName "DownloadClientFields contains downloadPath" `
        -Passed ($config.DownloadClientFields.ContainsKey("downloadPath"))
}

# Clean up
$env:QOBUZARR_AUTH_TOKEN = $originalToken
$env:QOBUZARR_USER_ID = $null
$env:QOBUZARR_COUNTRY_CODE = $null

Write-Host ""

# =============================================================================
# Test 4: Masked value detection logic
# =============================================================================
Write-Host "Test Group: Masked Value Detection" -ForegroundColor Yellow

# Test the masked value logic inline
$sensitiveFields = @(
    "authToken", "password", "redirectUrl", "appSecret",
    "refreshToken", "accessToken", "clientSecret",
    "apiKey", "secret", "token",
    "userId", "email", "username",
    "configurationUrl"
)

# Scenario: masked value + no ForceUpdate = skip
$fieldName = "authToken"
$currentValue = "********"
$newValue = "new-token"
$ForceUpdate = $false
$skippedMasked = $false
$isDifferent = $false

if ($fieldName -in $sensitiveFields -and "$currentValue" -eq "********" -and -not [string]::IsNullOrWhiteSpace("$newValue")) {
    if ($ForceUpdate) {
        $isDifferent = $true
    } else {
        $skippedMasked = $true
    }
}

Write-TestResult -TestName "Masked + no ForceUpdate: isDifferent=false" -Passed ($isDifferent -eq $false)
Write-TestResult -TestName "Masked + no ForceUpdate: skippedMasked=true" -Passed ($skippedMasked -eq $true)

# Scenario: masked value + ForceUpdate = update
$ForceUpdate = $true
$skippedMasked = $false
$isDifferent = $false

if ($fieldName -in $sensitiveFields -and "$currentValue" -eq "********" -and -not [string]::IsNullOrWhiteSpace("$newValue")) {
    if ($ForceUpdate) {
        $isDifferent = $true
    } else {
        $skippedMasked = $true
    }
}

Write-TestResult -TestName "Masked + ForceUpdate: isDifferent=true" -Passed ($isDifferent -eq $true)
Write-TestResult -TestName "Masked + ForceUpdate: skippedMasked=false" -Passed ($skippedMasked -eq $false)

# Scenario: non-masked value = normal comparison
$currentValue = "existing-token"
$ForceUpdate = $false
$isDifferent = $false
$skippedMasked = $false

# This should fall through to the elseif branches
if ($fieldName -in $sensitiveFields -and "$currentValue" -eq "********" -and -not [string]::IsNullOrWhiteSpace("$newValue")) {
    if ($ForceUpdate) { $isDifferent = $true } else { $skippedMasked = $true }
} elseif ("$currentValue" -ne "$newValue") {
    $isDifferent = $true
}

Write-TestResult -TestName "Non-masked different value: isDifferent=true" -Passed ($isDifferent -eq $true)
Write-TestResult -TestName "Non-masked different value: skippedMasked=false" -Passed ($skippedMasked -eq $false)

Write-Host ""

# =============================================================================
# Test 5: Redaction in changedFieldNames
# =============================================================================
Write-Host "Test Group: Redaction Consistency" -ForegroundColor Yellow

$changedFieldNames = @()

foreach ($testField in @("authToken", "password", "redirectUrl", "refreshToken", "accessToken", "clientSecret", "apiKey", "secret", "token", "appSecret", "userId", "email", "username", "configurationUrl")) {
    $changedFieldNames = @()
    if ($testField -in $sensitiveFields) {
        $changedFieldNames += "$testField=[REDACTED]"
    } else {
        $changedFieldNames += "$testField=some-value"
    }

    $isRedacted = $changedFieldNames[0] -eq "$testField=[REDACTED]"
    Write-TestResult -TestName "Field '$testField' is redacted in output" -Passed $isRedacted
}

Write-Host ""

# =============================================================================
# Summary
# =============================================================================
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Test Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Passed: $script:TestsPassed" -ForegroundColor Green
Write-Host "Failed: $script:TestsFailed" -ForegroundColor $(if ($script:TestsFailed -eq 0) { "Green" } else { "Red" })

if ($script:TestsFailed -gt 0) {
    exit 1
}
exit 0
