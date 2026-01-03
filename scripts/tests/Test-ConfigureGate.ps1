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
    "configurationUrl",
    "privateKey", "musicUserToken"
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
 $script:E2EComponentResolutionCandidates = @{}
 $DisableStoredComponentIds = $true
 $DisableComponentIdPersistence = $true

# Mock $pluginConfigs for testing
$global:pluginConfigs = @{
    'Qobuzarr' = @{
        ExpectIndexer = $true
        ExpectDownloadClient = $true
        ExpectImportList = $false
    }
    'Tidalarr' = @{
        ExpectIndexer = $true
        ExpectDownloadClient = $true
        ExpectImportList = $false
    }
    'Brainarr' = @{
        ExpectIndexer = $false
        ExpectDownloadClient = $false
        ExpectImportList = $true
        ImportListCredentialAllOfFieldNames = @("configurationUrl")
    }
    'AppleMusicarr' = @{
        ExpectIndexer = $false
        ExpectDownloadClient = $false
        ExpectImportList = $true
        ImportListCredentialAllOfFieldNames = @("teamId", "keyId", "privateKey", "musicUserToken")
    }
}

function Get-E2EPreferredComponentId {
    param([hashtable]$State, [string]$PluginName, [string]$Type)
    return $null
}

 function Select-ConfiguredComponent {
     param($Items, [string]$PluginName, [int]$PreferredId, [switch]$DisableFuzzyMatch)
     $arr = if ($Items -is [array]) { @($Items) } elseif ($null -ne $Items) { @($Items) } else { @() }
     # Simulate ambiguity detection (matches runner/module behavior): do not guess.
     $nameMatches = @($arr | Where-Object { $_.implementationName -eq $PluginName })
     if ($nameMatches.Count -gt 1) {
         $candidateIds = @($nameMatches | ForEach-Object { $_.id } | Where-Object { $null -ne $_ })
         return [PSCustomObject]@{ Component = $null; Resolution = "ambiguousImplementationName"; CandidateIds = $candidateIds }
     }
     $match = $arr | Where-Object { $_.implementationName -eq $PluginName } | Select-Object -First 1
     if (-not $match) { $match = $arr | Where-Object { $_.implementation -eq $PluginName } | Select-Object -First 1 }
     if (-not $match) { $match = $arr | Where-Object { $_.name -like "*$PluginName*" } | Select-Object -First 1 }
     return [PSCustomObject]@{ Component = $match; Resolution = if ($match) { "test" } else { "none" }; CandidateIds = @() }
 }

 function Get-ComponentAmbiguityDetails {
     param([string]$Type, [string]$PluginName)
     $key = "$Type|$PluginName"
     $resolution = if ($script:E2EComponentResolution.ContainsKey($key)) { "$($script:E2EComponentResolution[$key])" } else { "none" }
     $candidateIds = @()
     if ($script:E2EComponentResolutionCandidates.ContainsKey($key)) { $candidateIds = @($script:E2EComponentResolutionCandidates[$key]) }

     return @{
         IsAmbiguous = ($resolution -like "ambiguous*")
         Resolution = $resolution
         CandidateIds = $candidateIds
     }
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
# Test 8: PassIfAlreadyConfigured with Ambiguous Components
# =============================================================================
Write-Host "Test Group: PassIfAlreadyConfigured with Ambiguous Components" -ForegroundColor Yellow

try {
    # Duplicate indexers simulate ambiguous selection
    $script:mockComponents = @{
        indexer = @(
            [PSCustomObject]@{ id = 101; implementationName = "Qobuzarr"; implementation = "Qobuzarr"; name = "Qobuzarr A" },
            [PSCustomObject]@{ id = 102; implementationName = "Qobuzarr"; implementation = "Qobuzarr"; name = "Qobuzarr B" }
        )
        downloadclient = @(
            [PSCustomObject]@{ id = 201; implementationName = "Qobuzarr"; implementation = "Qobuzarr"; name = "Qobuzarr Download" }
        )
        importlist = @()
    }

    # Clear env vars to trigger missing-env path
    $env:QOBUZARR_AUTH_TOKEN = $null
    $env:QOBUZ_AUTH_TOKEN = $null

    if ($testConfigureFunctionMatch.Success) {
        $result = Test-ConfigureGateForPlugin -PluginName "Qobuzarr" -PassIfAlreadyConfigured

        Write-TestResult -TestName "Ambiguous configured components -> Outcome=failed" `
            -Passed ($result.Outcome -eq "failed")

        Write-TestResult -TestName "Ambiguous configured components -> Success=false" `
            -Passed ($result.Success -eq $false)

        $hasAmbiguityError = ($result.Errors | Where-Object { $_ -like "*Ambiguous configured indexer selection*" } | Select-Object -First 1)
        Write-TestResult -TestName "Ambiguous configured components -> Error mentions ambiguity" `
            -Passed ([bool]$hasAmbiguityError)

        Write-TestResult -TestName "Ambiguous configured components -> ErrorCode=E2E_COMPONENT_AMBIGUOUS" `
            -Passed ($result.Details.ErrorCode -eq "E2E_COMPONENT_AMBIGUOUS")

        Write-TestResult -TestName "Ambiguous configured components -> ComponentType=indexer" `
            -Passed ($result.Details.ComponentType -eq "indexer")
    }
}
catch {
    Write-TestResult -TestName "Ambiguous configured components test" -Passed $false -Message $_.Exception.Message
}

Write-Host ""

# =============================================================================
# Test 9: Brainarr PassIfAlreadyConfigured
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
# Test 10: Configure gate must FAIL (not create) on ambiguous component selection
# =============================================================================
Write-Host "Test Group: Configure Gate Fail-Fast on Ambiguity (Env Vars Present)" -ForegroundColor Yellow

try {
    # -----------------------------
    # Case 10a: Qobuzarr indexer ambiguous
    # -----------------------------
    $originalQobuzToken = $env:QOBUZARR_AUTH_TOKEN
    $originalQobuzTokenAlt = $env:QOBUZ_AUTH_TOKEN
    $env:QOBUZARR_AUTH_TOKEN = "token-placeholder"
    $env:QOBUZ_AUTH_TOKEN = $null

    $script:mockComponents = @{
        indexer = @(
            [PSCustomObject]@{ id = 101; implementationName = "Qobuzarr"; implementation = "QobuzIndexer"; name = "Qobuzarr A" },
            [PSCustomObject]@{ id = 102; implementationName = "Qobuzarr"; implementation = "QobuzIndexer"; name = "Qobuzarr B" }
        )
        downloadclient = @(
            [PSCustomObject]@{ id = 201; implementationName = "Qobuzarr"; implementation = "QobuzDownloadClient"; name = "Qobuzarr Download" }
        )
        importlist = @()
    }

    if ($testConfigureFunctionMatch.Success) {
        $result = Test-ConfigureGateForPlugin -PluginName "Qobuzarr"

        Write-TestResult -TestName "Configure gate ambiguity (env vars present) -> Outcome=failed" `
            -Passed ($result.Outcome -eq "failed")
        Write-TestResult -TestName "Configure gate ambiguity (env vars present) -> ErrorCode=E2E_COMPONENT_AMBIGUOUS" `
            -Passed ($result.Details.ErrorCode -eq "E2E_COMPONENT_AMBIGUOUS")
        Write-TestResult -TestName "Configure gate ambiguity (env vars present) -> ComponentType=indexer" `
            -Passed ($result.Details.ComponentType -eq "indexer")
    }
}
catch {
    Write-TestResult -TestName "Configure gate ambiguity (env vars present) - Qobuzarr indexer" -Passed $false -Message $_.Exception.Message
}
finally {
    $env:QOBUZARR_AUTH_TOKEN = $originalQobuzToken
    $env:QOBUZ_AUTH_TOKEN = $originalQobuzTokenAlt
}

try {
    # -----------------------------
    # Case 10b: Qobuzarr download client ambiguous (indexer unique)
    # -----------------------------
    $originalQobuzToken = $env:QOBUZARR_AUTH_TOKEN
    $originalQobuzTokenAlt = $env:QOBUZ_AUTH_TOKEN
    $env:QOBUZARR_AUTH_TOKEN = "token-placeholder"
    $env:QOBUZ_AUTH_TOKEN = $null

    $script:mockComponents = @{
        indexer = @(
            [PSCustomObject]@{ id = 101; implementationName = "Qobuzarr"; implementation = "QobuzIndexer"; name = "Qobuzarr" }
        )
        downloadclient = @(
            [PSCustomObject]@{ id = 201; implementationName = "Qobuzarr"; implementation = "QobuzDownloadClient"; name = "Qobuzarr Download A" },
            [PSCustomObject]@{ id = 202; implementationName = "Qobuzarr"; implementation = "QobuzDownloadClient"; name = "Qobuzarr Download B" }
        )
        importlist = @()
    }

    if ($testConfigureFunctionMatch.Success) {
        $result = Test-ConfigureGateForPlugin -PluginName "Qobuzarr"

        Write-TestResult -TestName "Configure gate ambiguity (env vars present) -> ComponentType=downloadclient" `
            -Passed ($result.Details.ComponentType -eq "downloadclient")
        Write-TestResult -TestName "Configure gate ambiguity (env vars present) -> ErrorCode=E2E_COMPONENT_AMBIGUOUS (downloadclient)" `
            -Passed ($result.Details.ErrorCode -eq "E2E_COMPONENT_AMBIGUOUS")
    }
}
catch {
    Write-TestResult -TestName "Configure gate ambiguity (env vars present) - Qobuzarr download client" -Passed $false -Message $_.Exception.Message
}
finally {
    $env:QOBUZARR_AUTH_TOKEN = $originalQobuzToken
    $env:QOBUZ_AUTH_TOKEN = $originalQobuzTokenAlt
}

try {
    # -----------------------------
    # Case 10c: Brainarr import list ambiguous
    # -----------------------------
    $originalBrainarrUrl = $env:BRAINARR_LLM_BASE_URL
    $env:BRAINARR_LLM_BASE_URL = "http://localhost:1234"

    $script:mockComponents = @{
        indexer = @()
        downloadclient = @()
        importlist = @(
            [PSCustomObject]@{ id = 301; implementationName = "Brainarr"; implementation = "Brainarr"; name = "Brainarr A" },
            [PSCustomObject]@{ id = 302; implementationName = "Brainarr"; implementation = "Brainarr"; name = "Brainarr B" }
        )
    }

    if ($testConfigureFunctionMatch.Success) {
        $result = Test-ConfigureGateForPlugin -PluginName "Brainarr"

        Write-TestResult -TestName "Configure gate ambiguity (env vars present) -> ComponentType=importlist" `
            -Passed ($result.Details.ComponentType -eq "importlist")
        Write-TestResult -TestName "Configure gate ambiguity (env vars present) -> ErrorCode=E2E_COMPONENT_AMBIGUOUS (importlist)" `
            -Passed ($result.Details.ErrorCode -eq "E2E_COMPONENT_AMBIGUOUS")
    }
}
catch {
    Write-TestResult -TestName "Configure gate ambiguity (env vars present) - Brainarr importlist" -Passed $false -Message $_.Exception.Message
}
finally {
    $env:BRAINARR_LLM_BASE_URL = $originalBrainarrUrl
}

Write-Host ""


# =============================================================================
# Test 11: AppleMusicarr Get-PluginEnvConfig - Missing env vars
# =============================================================================
Write-Host "Test Group: AppleMusicarr Get-PluginEnvConfig Missing Env Vars" -ForegroundColor Yellow

# Clear AppleMusicarr env vars
$originalAppleTeamId = $env:APPLEMUSICARR_TEAM_ID
$originalAppleKeyId = $env:APPLEMUSICARR_KEY_ID
$originalApplePrivateKey = $env:APPLEMUSICARR_PRIVATE_KEY_B64
$originalAppleMusicUserToken = $env:APPLEMUSICARR_MUSIC_USER_TOKEN

$env:APPLEMUSICARR_TEAM_ID = $null
$env:APPLEMUSICARR_KEY_ID = $null
$env:APPLEMUSICARR_PRIVATE_KEY_B64 = $null
$env:APPLEMUSICARR_MUSIC_USER_TOKEN = $null

try {
    if ($functionMatch.Success) {
        $config = Get-PluginEnvConfig -PluginName "AppleMusicarr"

        Write-TestResult -TestName "AppleMusicarr: HasRequiredEnvVars is false when all env vars missing" `
            -Passed ($config.HasRequiredEnvVars -eq $false)

        Write-TestResult -TestName "AppleMusicarr: MissingRequired contains APPLEMUSICARR_TEAM_ID" `
            -Passed ($config.MissingRequired -contains "APPLEMUSICARR_TEAM_ID")

        Write-TestResult -TestName "AppleMusicarr: MissingRequired contains APPLEMUSICARR_KEY_ID" `
            -Passed ($config.MissingRequired -contains "APPLEMUSICARR_KEY_ID")

        Write-TestResult -TestName "AppleMusicarr: MissingRequired contains APPLEMUSICARR_PRIVATE_KEY_B64" `
            -Passed ($config.MissingRequired -contains "APPLEMUSICARR_PRIVATE_KEY_B64")

        Write-TestResult -TestName "AppleMusicarr: MissingRequired contains APPLEMUSICARR_MUSIC_USER_TOKEN" `
            -Passed ($config.MissingRequired -contains "APPLEMUSICARR_MUSIC_USER_TOKEN")

        Write-TestResult -TestName "AppleMusicarr: ImportListFields is empty hashtable" `
            -Passed ($config.ImportListFields.Count -eq 0)
    }
} catch {
    Write-TestResult -TestName "AppleMusicarr missing env vars test" -Passed $false -Message $_.Exception.Message
}

Write-Host ""

# =============================================================================
# Test 12: AppleMusicarr Get-PluginEnvConfig - With valid env vars (base64 decode)
# =============================================================================
Write-Host "Test Group: AppleMusicarr Get-PluginEnvConfig With Valid Env Vars" -ForegroundColor Yellow

# Set valid AppleMusicarr env vars
# privateKey is base64 encoded: "-----BEGIN PRIVATE KEY-----\ntest-key-content\n-----END PRIVATE KEY-----"
$testPrivateKeyPem = "-----BEGIN PRIVATE KEY-----`ntest-key-content`n-----END PRIVATE KEY-----"
$testPrivateKeyB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($testPrivateKeyPem))

$env:APPLEMUSICARR_TEAM_ID = "TEAM123"
$env:APPLEMUSICARR_KEY_ID = "KEY456"
$env:APPLEMUSICARR_PRIVATE_KEY_B64 = $testPrivateKeyB64
$env:APPLEMUSICARR_MUSIC_USER_TOKEN = "music-user-token-789"

try {
    if ($functionMatch.Success) {
        $config = Get-PluginEnvConfig -PluginName "AppleMusicarr"

        Write-TestResult -TestName "AppleMusicarr: HasRequiredEnvVars is true when all env vars set" `
            -Passed ($config.HasRequiredEnvVars -eq $true)

        Write-TestResult -TestName "AppleMusicarr: MissingRequired is empty" `
            -Passed ($config.MissingRequired.Count -eq 0)

        Write-TestResult -TestName "AppleMusicarr: ImportListFields contains teamId" `
            -Passed ($config.ImportListFields.ContainsKey("teamId"))

        Write-TestResult -TestName "AppleMusicarr: ImportListFields.teamId matches env var" `
            -Passed ($config.ImportListFields["teamId"] -eq "TEAM123")

        Write-TestResult -TestName "AppleMusicarr: ImportListFields contains keyId" `
            -Passed ($config.ImportListFields.ContainsKey("keyId"))

        Write-TestResult -TestName "AppleMusicarr: ImportListFields.keyId matches env var" `
            -Passed ($config.ImportListFields["keyId"] -eq "KEY456")

        Write-TestResult -TestName "AppleMusicarr: ImportListFields contains privateKey (decoded)" `
            -Passed ($config.ImportListFields.ContainsKey("privateKey"))

        # Verify base64 was decoded correctly
        $decodedKey = $config.ImportListFields["privateKey"]
        $expectedKeyContent = $decodedKey -like "*BEGIN PRIVATE KEY*" -and $decodedKey -like "*test-key-content*"
        Write-TestResult -TestName "AppleMusicarr: privateKey is base64-decoded PEM content" `
            -Passed $expectedKeyContent

        Write-TestResult -TestName "AppleMusicarr: ImportListFields contains musicUserToken" `
            -Passed ($config.ImportListFields.ContainsKey("musicUserToken"))

        Write-TestResult -TestName "AppleMusicarr: ImportListFields.musicUserToken matches env var" `
            -Passed ($config.ImportListFields["musicUserToken"] -eq "music-user-token-789")

        # Verify IndexerFields and DownloadClientFields are empty (AppleMusicarr is import list only)
        Write-TestResult -TestName "AppleMusicarr: IndexerFields is empty (import list only)" `
            -Passed ($config.IndexerFields.Count -eq 0)

        Write-TestResult -TestName "AppleMusicarr: DownloadClientFields is empty (import list only)" `
            -Passed ($config.DownloadClientFields.Count -eq 0)
    }
} catch {
    Write-TestResult -TestName "AppleMusicarr valid env vars test" -Passed $false -Message $_.Exception.Message
}

Write-Host ""

# =============================================================================
# Test 13: AppleMusicarr Get-PluginEnvConfig - Invalid base64 handling
# =============================================================================
Write-Host "Test Group: AppleMusicarr Invalid Base64 Handling" -ForegroundColor Yellow

# Set env vars with invalid base64 for private key
$env:APPLEMUSICARR_TEAM_ID = "TEAM123"
$env:APPLEMUSICARR_KEY_ID = "KEY456"
$env:APPLEMUSICARR_PRIVATE_KEY_B64 = "not-valid-base64!!!"  # Invalid base64
$env:APPLEMUSICARR_MUSIC_USER_TOKEN = "music-user-token-789"

try {
    if ($functionMatch.Success) {
        $config = Get-PluginEnvConfig -PluginName "AppleMusicarr"

        Write-TestResult -TestName "AppleMusicarr: Invalid base64 -> HasRequiredEnvVars is false" `
            -Passed ($config.HasRequiredEnvVars -eq $false)

        $hasInvalidB64Error = $config.MissingRequired | Where-Object { $_ -like "*APPLEMUSICARR_PRIVATE_KEY_B64*invalid*" }
        Write-TestResult -TestName "AppleMusicarr: Invalid base64 -> MissingRequired mentions invalid base64" `
            -Passed ([bool]$hasInvalidB64Error)
    }
} catch {
    Write-TestResult -TestName "AppleMusicarr invalid base64 test" -Passed $false -Message $_.Exception.Message
}

Write-Host ""

# =============================================================================
# Test 14: AppleMusicarr PassIfAlreadyConfigured
# =============================================================================
Write-Host "Test Group: AppleMusicarr PassIfAlreadyConfigured" -ForegroundColor Yellow

try {
    # Update mock to return AppleMusicarr import list
    $script:mockComponents = @{
        indexer = @()
        downloadclient = @()
        importlist = @(
            [PSCustomObject]@{ id = 401; implementation = "AppleMusicarr"; name = "AppleMusicarr" }
        )
    }

    # Clear env vars to test PassIfAlreadyConfigured path
    $env:APPLEMUSICARR_TEAM_ID = $null
    $env:APPLEMUSICARR_KEY_ID = $null
    $env:APPLEMUSICARR_PRIVATE_KEY_B64 = $null
    $env:APPLEMUSICARR_MUSIC_USER_TOKEN = $null

    if ($testConfigureFunctionMatch.Success) {
        $result = Test-ConfigureGateForPlugin -PluginName "AppleMusicarr" -PassIfAlreadyConfigured

        Write-TestResult -TestName "AppleMusicarr + PassIfAlreadyConfigured + import list exists: Outcome=success" `
            -Passed ($result.Outcome -eq "success")

        Write-TestResult -TestName "AppleMusicarr + PassIfAlreadyConfigured: importListId populated" `
            -Passed ($result.Details.componentIds["importListId"] -eq 401)

        Write-TestResult -TestName "AppleMusicarr + PassIfAlreadyConfigured: alreadyConfigured=true" `
            -Passed ($result.Details.alreadyConfigured -eq $true)
    }

    # Test with missing import list
    $script:mockComponents.importlist = @()

    if ($testConfigureFunctionMatch.Success) {
        $result = Test-ConfigureGateForPlugin -PluginName "AppleMusicarr" -PassIfAlreadyConfigured

        Write-TestResult -TestName "AppleMusicarr + no import list: Outcome=skipped" `
            -Passed ($result.Outcome -eq "skipped")

        Write-TestResult -TestName "AppleMusicarr + no import list: SkipReason mentions missing env vars" `
            -Passed ($result.SkipReason -like "*Missing env vars*")
    }
} catch {
    Write-TestResult -TestName "AppleMusicarr PassIfAlreadyConfigured test" -Passed $false -Message $_.Exception.Message
}

# Restore env vars
$env:APPLEMUSICARR_TEAM_ID = $originalAppleTeamId
$env:APPLEMUSICARR_KEY_ID = $originalAppleKeyId
$env:APPLEMUSICARR_PRIVATE_KEY_B64 = $originalApplePrivateKey
$env:APPLEMUSICARR_MUSIC_USER_TOKEN = $originalAppleMusicUserToken

Write-Host ""

# =============================================================================
# Test 15: AppleMusicarr Idempotent Configure (same config -> no changes)
# =============================================================================
Write-Host "Test Group: AppleMusicarr Idempotent Configure" -ForegroundColor Yellow

# Set valid env vars
$testPrivateKeyPem = "-----BEGIN PRIVATE KEY-----`ntest-key-content`n-----END PRIVATE KEY-----"
$testPrivateKeyB64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($testPrivateKeyPem))

$env:APPLEMUSICARR_TEAM_ID = "TEAM123"
$env:APPLEMUSICARR_KEY_ID = "KEY456"
$env:APPLEMUSICARR_PRIVATE_KEY_B64 = $testPrivateKeyB64
$env:APPLEMUSICARR_MUSIC_USER_TOKEN = "music-user-token-789"

try {
    if ($functionMatch.Success) {
        # Call Get-PluginEnvConfig twice and verify identical output
        $config1 = Get-PluginEnvConfig -PluginName "AppleMusicarr"
        $config2 = Get-PluginEnvConfig -PluginName "AppleMusicarr"

        Write-TestResult -TestName "AppleMusicarr: Idempotent - HasRequiredEnvVars matches" `
            -Passed ($config1.HasRequiredEnvVars -eq $config2.HasRequiredEnvVars)

        Write-TestResult -TestName "AppleMusicarr: Idempotent - teamId matches" `
            -Passed ($config1.ImportListFields["teamId"] -eq $config2.ImportListFields["teamId"])

        Write-TestResult -TestName "AppleMusicarr: Idempotent - keyId matches" `
            -Passed ($config1.ImportListFields["keyId"] -eq $config2.ImportListFields["keyId"])

        Write-TestResult -TestName "AppleMusicarr: Idempotent - privateKey matches" `
            -Passed ($config1.ImportListFields["privateKey"] -eq $config2.ImportListFields["privateKey"])

        Write-TestResult -TestName "AppleMusicarr: Idempotent - musicUserToken matches" `
            -Passed ($config1.ImportListFields["musicUserToken"] -eq $config2.ImportListFields["musicUserToken"])
    }
} catch {
    Write-TestResult -TestName "AppleMusicarr idempotent test" -Passed $false -Message $_.Exception.Message
}

# Restore env vars
$env:APPLEMUSICARR_TEAM_ID = $originalAppleTeamId
$env:APPLEMUSICARR_KEY_ID = $originalAppleKeyId
$env:APPLEMUSICARR_PRIVATE_KEY_B64 = $originalApplePrivateKey
$env:APPLEMUSICARR_MUSIC_USER_TOKEN = $originalAppleMusicUserToken

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
