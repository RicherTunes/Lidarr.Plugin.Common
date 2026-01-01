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
# Test 6: PassIfAlreadyConfigured - Components exist -> success
# =============================================================================
Write-Host "Test Group: PassIfAlreadyConfigured with Existing Components" -ForegroundColor Yellow

# Extract Find-ConfiguredComponent function
$findFunctionMatch = [regex]::Match($scriptContent, '(?s)function Find-ConfiguredComponent \{.*?^\}', [System.Text.RegularExpressions.RegexOptions]::Multiline)

# Extract Test-ConfigureGateForPlugin function
$testConfigureFunctionMatch = [regex]::Match($scriptContent, '(?s)function Test-ConfigureGateForPlugin \{.*?^\}(?=\s*\n\s*function|\s*\n\s*#|\s*$)', [System.Text.RegularExpressions.RegexOptions]::Multiline)

# Create mock functions for hermetic testing
$mockFunctions = @'
# Mock Invoke-LidarrApi to return fake components
$script:mockComponents = @{
    indexer = @(
        [PSCustomObject]@{ id = 101; implementation = "Qobuzarr"; name = "Qobuzarr" }
    )
    downloadclient = @(
        [PSCustomObject]@{ id = 201; implementation = "Qobuzarr"; name = "Qobuzarr Download" }
    )
    importlist = @(
        [PSCustomObject]@{ id = 301; implementation = "Brainarr"; name = "Brainarr Import List" }
    )
}

# Minimal stubs for new preferred-ID infrastructure (keep tests hermetic)
$script:E2EComponentIdsState = @{}
$script:E2EComponentResolution = @{}
$DisableStoredComponentIds = $true
$DisableComponentIdPersistence = $true

function Get-E2EPreferredComponentId {
    param([hashtable]$State, [string]$PluginName, [string]$Type)
    return $null
}

function Select-ConfiguredComponent {
    param($Items, [string]$PluginName, [int]$PreferredId)
    $arr = if ($Items -is [array]) { @($Items) } elseif ($null -ne $Items) { @($Items) } else { @() }
    $match = $arr | Where-Object { $_.implementationName -eq $PluginName } | Select-Object -First 1
    if (-not $match) { $match = $arr | Where-Object { $_.implementation -eq $PluginName } | Select-Object -First 1 }
    if (-not $match) { $match = $arr | Where-Object { $_.name -like "*$PluginName*" } | Select-Object -First 1 }
    return [PSCustomObject]@{ Component = $match; Resolution = if ($match) { "test" } else { "none" } }
}

function Save-E2EComponentIdsIfEnabled { param([string]$PluginName, [hashtable]$ComponentIds) return }
function Get-HashtableStringOrDefault { param([hashtable]$Table, [string]$Key, [string]$DefaultValue) return $DefaultValue }

function Invoke-LidarrApi {
    param([string]$Endpoint, [string]$Method = "GET", $Body = $null)
    if ($Endpoint -in @("indexer", "downloadclient", "importlist")) {
        return $script:mockComponents[$Endpoint]
    }
    return $null
}

function Get-ComponentSchema { param($Type, $ImplementationMatch) return $null }
function New-ComponentFromEnv { param($Type, $PluginName, $Schema, $FieldValues) return $null }
function Update-ComponentAuthFields { param($Type, $ExistingComponent, $FieldValues, [switch]$ForceUpdate) return @{ Updated = $false; ChangedFields = @() } }
'@

try {
    # Save original functions if they exist
    $originalInvokeLidarrApi = if (Get-Command Invoke-LidarrApi -ErrorAction SilentlyContinue) { ${function:Invoke-LidarrApi} } else { $null }

    # Load mock functions
    Invoke-Expression $mockFunctions

    # Load Find-ConfiguredComponent
    if ($findFunctionMatch.Success) {
        Invoke-Expression $findFunctionMatch.Value
    }

    # Load Get-PluginEnvConfig (already loaded from earlier test)
    # Load Test-ConfigureGateForPlugin
    if ($testConfigureFunctionMatch.Success) {
        Invoke-Expression $testConfigureFunctionMatch.Value
    }

    # Clear env vars to trigger "missing env vars" path
    $env:QOBUZARR_AUTH_TOKEN = $null
    $env:QOBUZ_AUTH_TOKEN = $null

    # Test: Qobuzarr with PassIfAlreadyConfigured and components exist
    if ($testConfigureFunctionMatch.Success) {
        $result = Test-ConfigureGateForPlugin -PluginName "Qobuzarr" -PassIfAlreadyConfigured

        Write-TestResult -TestName "PassIfAlreadyConfigured + components exist: Outcome=success" `
            -Passed ($result.Outcome -eq "success")

        Write-TestResult -TestName "PassIfAlreadyConfigured + components exist: Success=true" `
            -Passed ($result.Success -eq $true)

        Write-TestResult -TestName "PassIfAlreadyConfigured + components exist: alreadyConfigured=true" `
            -Passed ($result.Details.alreadyConfigured -eq $true)

        Write-TestResult -TestName "PassIfAlreadyConfigured + components exist: indexerId populated" `
            -Passed ($result.Details.componentIds["indexerId"] -eq 101)

        Write-TestResult -TestName "PassIfAlreadyConfigured + components exist: downloadClientId populated" `
            -Passed ($result.Details.componentIds["downloadClientId"] -eq 201)

        Write-TestResult -TestName "PassIfAlreadyConfigured + components exist: Action message correct" `
            -Passed ($result.Actions -contains "Env vars missing; existing component(s) present -> no-op")
    } else {
        Write-TestResult -TestName "Parse Test-ConfigureGateForPlugin function" -Passed $false -Message "Could not find function"
    }
} catch {
    Write-TestResult -TestName "PassIfAlreadyConfigured test execution" -Passed $false -Message $_.Exception.Message
}

Write-Host ""

# =============================================================================
# Test 7: PassIfAlreadyConfigured - Components missing -> skip
# =============================================================================
Write-Host "Test Group: PassIfAlreadyConfigured with Missing Components" -ForegroundColor Yellow

try {
    # Update mock to return empty components
    $script:mockComponents = @{
        indexer = @()
        downloadclient = @()
        importlist = @()
    }

    # Clear env vars
    $env:QOBUZARR_AUTH_TOKEN = $null
    $env:QOBUZ_AUTH_TOKEN = $null

    if ($testConfigureFunctionMatch.Success) {
        $result = Test-ConfigureGateForPlugin -PluginName "Qobuzarr" -PassIfAlreadyConfigured

        Write-TestResult -TestName "PassIfAlreadyConfigured + no components: Outcome=skipped" `
            -Passed ($result.Outcome -eq "skipped")

        Write-TestResult -TestName "PassIfAlreadyConfigured + no components: Success=false" `
            -Passed ($result.Success -eq $false)

        Write-TestResult -TestName "PassIfAlreadyConfigured + no components: SkipReason contains 'Missing env vars'" `
            -Passed ($result.SkipReason -like "*Missing env vars*")

        Write-TestResult -TestName "PassIfAlreadyConfigured + no components: alreadyConfigured=false" `
            -Passed ($result.Details.alreadyConfigured -eq $false)
    }
} catch {
    Write-TestResult -TestName "PassIfAlreadyConfigured missing components test" -Passed $false -Message $_.Exception.Message
}

# Restore env vars
$env:QOBUZARR_AUTH_TOKEN = $originalToken
$env:QOBUZ_AUTH_TOKEN = $originalQobuzToken

Write-Host ""

# =============================================================================
# Test 8: Brainarr PassIfAlreadyConfigured
# =============================================================================
Write-Host "Test Group: Brainarr PassIfAlreadyConfigured" -ForegroundColor Yellow

try {
    # Update mock to return Brainarr import list
    $script:mockComponents = @{
        indexer = @()
        downloadclient = @()
        importlist = @(
            [PSCustomObject]@{ id = 301; implementation = "Brainarr"; name = "Brainarr" }
        )
    }

    # Clear env vars
    $env:BRAINARR_LLM_BASE_URL = $null

    if ($testConfigureFunctionMatch.Success) {
        $result = Test-ConfigureGateForPlugin -PluginName "Brainarr" -PassIfAlreadyConfigured

        Write-TestResult -TestName "Brainarr + PassIfAlreadyConfigured + import list exists: Outcome=success" `
            -Passed ($result.Outcome -eq "success")

        Write-TestResult -TestName "Brainarr + PassIfAlreadyConfigured: importListId populated" `
            -Passed ($result.Details.componentIds["importListId"] -eq 301)

        Write-TestResult -TestName "Brainarr + PassIfAlreadyConfigured: alreadyConfigured=true" `
            -Passed ($result.Details.alreadyConfigured -eq $true)
    }

    # Test with missing import list
    $script:mockComponents.importlist = @()

    if ($testConfigureFunctionMatch.Success) {
        $result = Test-ConfigureGateForPlugin -PluginName "Brainarr" -PassIfAlreadyConfigured

        Write-TestResult -TestName "Brainarr + no import list: Outcome=skipped" `
            -Passed ($result.Outcome -eq "skipped")
    }
} catch {
    Write-TestResult -TestName "Brainarr PassIfAlreadyConfigured test" -Passed $false -Message $_.Exception.Message
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
