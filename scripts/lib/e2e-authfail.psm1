# e2e-authfail.psm1 - Auth failure and log redaction verification for E2E authfail-redaction gate
# Tests failure handling, HTTP error surfacing, and secret leak prevention
# Supports submodes: 401 (auth), 403 (forbidden), 429 (rate limit)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

# Import sanitization module for redaction verification
$sanitizePath = Join-Path $PSScriptRoot "e2e-sanitize.psm1"
if (Test-Path $sanitizePath) {
    Import-Module $sanitizePath -Force
}

# Import shared helpers
$helpersPath = Join-Path $PSScriptRoot "e2e-helpers.psm1"
if (Test-Path $helpersPath) {
    Import-Module $helpersPath -Force
}

# Auth failure mode definitions
$script:AuthFailModes = @{
    "401" = @{
        Name = "Authentication Failure"
        ExpectedCodes = @(400, 401)
        Description = "Invalid credentials rejected"
        ErrorPatterns = @('401', 'Unauthorized', 'invalid.*credential', 'authentication.*failed')
    }
    "403" = @{
        Name = "Authorization Failure"
        ExpectedCodes = @(403)
        Description = "Access denied/forbidden"
        ErrorPatterns = @('403', 'Forbidden', 'access.*denied', 'not.*authorized')
    }
    "429" = @{
        Name = "Rate Limit"
        ExpectedCodes = @(429)
        Description = "Too many requests with Retry-After"
        ErrorPatterns = @('429', 'TooManyRequests', 'rate.*limit', 'retry.*after')
        RequiresRetryAfterHeader = $true
    }
}

# Patterns that indicate secrets leaked in logs
$script:SecretLeakPatterns = @(
    # Bearer tokens
    '(?i)bearer\s+[A-Za-z0-9_-]{20,}',
    # API keys in headers (not redacted)
    '(?i)x-api-key:\s*[A-Za-z0-9_-]{20,}',
    '(?i)authorization:\s*[^\s\[\]]{20,}',
    # Query params with secrets (not redacted)
    '(?i)[?&](access_token|api_key|apikey|token|auth|secret|password|refresh_token|client_secret)=(?!\[REDACTED\])[^\s&]{8,}',
    # OAuth codes
    '(?i)[?&]code=(?!\[REDACTED\])[A-Za-z0-9_-]{20,}',
    # JWT tokens
    '(?i)eyJ[A-Za-z0-9_-]{10,}\.eyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}'
)

# Patterns that indicate expected (redacted) secrets - these are OK
$script:RedactedPatterns = @(
    '\[REDACTED\]',
    '\*{3,}',
    '<HIDDEN>',
    '\[HIDDEN\]'
)

<#
.SYNOPSIS
    Configures an indexer with intentionally bad credentials.
.PARAMETER LidarrUrl
    Base URL for Lidarr API.
.PARAMETER Headers
    Hashtable with API headers.
.PARAMETER Implementation
    Indexer implementation name (e.g., "QobuzIndexer").
.PARAMETER BadCredentials
    Hashtable of field name -> intentionally bad value.
.OUTPUTS
    PSCustomObject with Success, IndexerId, and Error properties.
#>
function New-BadCredentialIndexer {
    param(
        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [Parameter(Mandatory)]
        [string]$Implementation,

        [Parameter(Mandatory)]
        [hashtable]$BadCredentials
    )

    $result = [PSCustomObject]@{
        Success = $false
        IndexerId = $null
        Error = $null
    }

    try {
        # Get schema for implementation
        $schemas = Invoke-RestMethod -Uri "$LidarrUrl/api/v1/indexer/schema" -Headers $Headers -TimeoutSec 30 -ErrorAction Stop
        $schema = $schemas | Where-Object { $_.implementation -eq $Implementation } | Select-Object -First 1

        if (-not $schema) {
            throw "Schema not found for implementation: $Implementation"
        }

        # Clone and configure with bad credentials
        $payload = $schema | ConvertTo-Json -Depth 50 | ConvertFrom-Json
        $payload.name = "AuthFail-Test-$Implementation"
        $payload.enable = $true

        foreach ($fieldName in $BadCredentials.Keys) {
            $field = $payload.fields | Where-Object {
                $_.name -and $_.name.ToString().Equals($fieldName, [StringComparison]::OrdinalIgnoreCase)
            } | Select-Object -First 1

            if ($field) {
                if ($field.PSObject.Properties.Match("value").Count -gt 0) {
                    $field.value = $BadCredentials[$fieldName]
                }
                else {
                    $field | Add-Member -NotePropertyName "value" -NotePropertyValue $BadCredentials[$fieldName] -Force
                }
            }
        }

        # Create indexer (should succeed - just storing config)
        $created = Invoke-RestMethod -Uri "$LidarrUrl/api/v1/indexer" -Method POST -Headers $Headers -Body ($payload | ConvertTo-Json -Depth 50) -ContentType "application/json" -TimeoutSec 30 -ErrorAction Stop

        $result.IndexerId = $created.id
        $result.Success = $true
    }
    catch {
        $result.Error = $_.Exception.Message
    }

    return $result
}

<#
.SYNOPSIS
    Tests an indexer and expects it to fail with auth error.
.PARAMETER LidarrUrl
    Base URL for Lidarr API.
.PARAMETER Headers
    Hashtable with API headers.
.PARAMETER IndexerId
    ID of the indexer to test.
.PARAMETER FailureMode
    Expected failure mode: "401", "403", "429", or "any" (default).
.PARAMETER ExpectedHttpCodes
    Array of expected HTTP status codes (overrides FailureMode if provided).
.OUTPUTS
    PSCustomObject with Success, FailedAsExpected, HttpCode, ErrorMessage, RetryAfter, and Error properties.
#>
function Test-IndexerExpectFailure {
    param(
        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [Parameter(Mandatory)]
        [int]$IndexerId,

        [ValidateSet("401", "403", "429", "any")]
        [string]$FailureMode = "any",

        [int[]]$ExpectedHttpCodes = @()
    )

    $result = [PSCustomObject]@{
        Success = $false
        FailedAsExpected = $false
        HttpCode = $null
        ErrorMessage = $null
        RetryAfter = $null
        FailureMode = $FailureMode
        Error = $null
    }

    # Determine expected codes based on mode
    if ($ExpectedHttpCodes.Count -eq 0) {
        if ($FailureMode -eq "any") {
            $ExpectedHttpCodes = @(400, 401, 403, 429, 500)
        }
        elseif ($script:AuthFailModes.ContainsKey($FailureMode)) {
            $ExpectedHttpCodes = $script:AuthFailModes[$FailureMode].ExpectedCodes
        }
    }

    $modeConfig = if ($script:AuthFailModes.ContainsKey($FailureMode)) { $script:AuthFailModes[$FailureMode] } else { $null }

    try {
        # Get the indexer config
        $indexer = Invoke-RestMethod -Uri "$LidarrUrl/api/v1/indexer/$IndexerId" -Headers $Headers -TimeoutSec 30 -ErrorAction Stop

        # Try to test it (should fail)
        try {
            $testResult = Invoke-RestMethod -Uri "$LidarrUrl/api/v1/indexer/test" -Method POST -Headers $Headers -Body ($indexer | ConvertTo-Json -Depth 50) -ContentType "application/json" -TimeoutSec 60 -ErrorAction Stop

            # If we got here without error, the test unexpectedly succeeded
            $result.Error = "Indexer test unexpectedly succeeded (expected $FailureMode failure)"
        }
        catch {
            $httpCode = $null
            $errorBody = $null
            $retryAfter = $null

            try {
                $resp = $_.Exception.Response
                if ($resp) {
                    $httpCode = [int]$resp.StatusCode

                    # Check for Retry-After header (important for 429)
                    try {
                        $retryAfterHeader = $resp.Headers.GetValues("Retry-After")
                        if ($retryAfterHeader -and $retryAfterHeader.Count -gt 0) {
                            $retryAfter = $retryAfterHeader[0]
                        }
                    }
                    catch { }

                    $stream = $resp.GetResponseStream()
                    if ($stream) {
                        $reader = New-Object System.IO.StreamReader($stream)
                        $errorBody = $reader.ReadToEnd()
                    }
                }
            }
            catch { }

            $result.HttpCode = $httpCode
            $result.ErrorMessage = $errorBody
            $result.RetryAfter = $retryAfter

            # Check if this is an expected failure
            $codeMatches = $httpCode -and $ExpectedHttpCodes -contains $httpCode

            # For 429 mode, also verify Retry-After header is present
            if ($FailureMode -eq "429" -and $codeMatches) {
                if (-not $retryAfter) {
                    Write-Host "  Warning: 429 response missing Retry-After header" -ForegroundColor Yellow
                }
                else {
                    Write-Host "  Retry-After: $retryAfter" -ForegroundColor DarkGray
                }
            }

            if ($codeMatches) {
                $result.FailedAsExpected = $true
                $result.Success = $true
            }
            elseif ($modeConfig -and $modeConfig.ErrorPatterns) {
                # Check error patterns for this mode
                $errorText = "$($_.Exception.Message) $errorBody"
                foreach ($pattern in $modeConfig.ErrorPatterns) {
                    if ($errorText -match $pattern) {
                        $result.FailedAsExpected = $true
                        $result.Success = $true
                        break
                    }
                }
            }

            if (-not $result.Success) {
                $result.Error = "Unexpected error for mode '$FailureMode' (HTTP $httpCode, expected: $($ExpectedHttpCodes -join ',')): $($_.Exception.Message)"
            }
        }
    }
    catch {
        $result.Error = $_.Exception.Message
    }

    return $result
}

<#
.SYNOPSIS
    Checks container logs for leaked secrets.
.PARAMETER ContainerName
    Name of the container to check logs.
.PARAMETER TailLines
    Number of recent log lines to check.
.PARAMETER AllowedLeakPatterns
    Patterns that are allowed (e.g., test data).
.OUTPUTS
    PSCustomObject with Success, LeaksFound, LeakDetails, and Error properties.
#>
function Test-LogsForSecretLeaks {
    param(
        [Parameter(Mandatory)]
        [string]$ContainerName,

        [int]$TailLines = 500,

        [string[]]$AllowedLeakPatterns = @()
    )

    $result = [PSCustomObject]@{
        Success = $false
        LeaksFound = $false
        LeakDetails = @()
        Error = $null
    }

    try {
        $logs = & docker logs $ContainerName --tail $TailLines 2>&1
        if ($LASTEXITCODE -ne 0) {
            throw "Failed to get container logs"
        }

        $logText = $logs -join "`n"
        $leaks = @()

        foreach ($pattern in $script:SecretLeakPatterns) {
            $matches = [regex]::Matches($logText, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
            foreach ($match in $matches) {
                $matchValue = $match.Value
                $isAllowed = $false

                # Check if this matches an allowed pattern
                foreach ($allowedPattern in $AllowedLeakPatterns) {
                    if ($matchValue -match $allowedPattern) {
                        $isAllowed = $true
                        break
                    }
                }

                # Check if it's actually redacted
                foreach ($redactedPattern in $script:RedactedPatterns) {
                    if ($matchValue -match $redactedPattern) {
                        $isAllowed = $true
                        break
                    }
                }

                if (-not $isAllowed) {
                    # Extract context (line containing the leak)
                    $lineStart = $logText.LastIndexOf("`n", [Math]::Max(0, $match.Index - 1)) + 1
                    $lineEnd = $logText.IndexOf("`n", $match.Index)
                    if ($lineEnd -lt 0) { $lineEnd = $logText.Length }
                    $contextLine = $logText.Substring($lineStart, [Math]::Min(200, $lineEnd - $lineStart))

                    $leaks += [PSCustomObject]@{
                        Pattern = $pattern
                        Match = if ($matchValue.Length -gt 50) { $matchValue.Substring(0, 50) + "..." } else { $matchValue }
                        Context = $contextLine
                    }
                }
            }
        }

        if ($leaks.Count -gt 0) {
            $result.LeaksFound = $true
            $result.LeakDetails = $leaks
            $result.Error = "Found $($leaks.Count) potential secret leak(s) in logs"
        }
        else {
            $result.Success = $true
        }
    }
    catch {
        $result.Error = $_.Exception.Message
    }

    return $result
}

<#
.SYNOPSIS
    Verifies that error responses have secrets properly redacted.
.PARAMETER ErrorResponse
    Error response string to check.
.OUTPUTS
    PSCustomObject with Success, HasLeaks, and LeakDetails properties.
#>
function Test-ErrorResponseRedaction {
    param(
        [Parameter(Mandatory)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$ErrorResponse
    )

    $result = [PSCustomObject]@{
        Success = $false
        HasLeaks = $false
        LeakDetails = @()
    }

    if ([string]::IsNullOrWhiteSpace($ErrorResponse)) {
        $result.Success = $true
        return $result
    }

    $leaks = @()

    foreach ($pattern in $script:SecretLeakPatterns) {
        $matches = [regex]::Matches($ErrorResponse, $pattern, [System.Text.RegularExpressions.RegexOptions]::IgnoreCase)
        foreach ($match in $matches) {
            $matchValue = $match.Value
            $isRedacted = $false

            foreach ($redactedPattern in $script:RedactedPatterns) {
                if ($matchValue -match $redactedPattern) {
                    $isRedacted = $true
                    break
                }
            }

            if (-not $isRedacted) {
                $leaks += [PSCustomObject]@{
                    Pattern = $pattern
                    Match = if ($matchValue.Length -gt 30) { $matchValue.Substring(0, 30) + "..." } else { $matchValue }
                }
            }
        }
    }

    if ($leaks.Count -gt 0) {
        $result.HasLeaks = $true
        $result.LeakDetails = $leaks
    }
    else {
        $result.Success = $true
    }

    return $result
}

<#
.SYNOPSIS
    Cleans up test indexer created for auth failure testing.
.PARAMETER LidarrUrl
    Base URL for Lidarr API.
.PARAMETER Headers
    Hashtable with API headers.
.PARAMETER IndexerId
    ID of the indexer to delete.
#>
function Remove-TestIndexer {
    param(
        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [Parameter(Mandatory)]
        [int]$IndexerId
    )

    try {
        $null = Invoke-RestMethod -Uri "$LidarrUrl/api/v1/indexer/$IndexerId" -Method DELETE -Headers $Headers -TimeoutSec 30 -ErrorAction SilentlyContinue
    }
    catch {
        Write-Host "Warning: Failed to clean up test indexer $IndexerId : $($_.Exception.Message)" -ForegroundColor Yellow
    }
}

<#
.SYNOPSIS
    Runs the complete authfail-redaction gate for a plugin.
.PARAMETER ContainerName
    Name of the container.
.PARAMETER LidarrUrl
    Base URL for Lidarr API.
.PARAMETER Headers
    Hashtable with API headers.
.PARAMETER Implementation
    Indexer implementation name.
.PARAMETER BadCredentials
    Hashtable of field name -> bad credential value.
.PARAMETER FailureModes
    Array of failure modes to test: "401", "403", "429". Default: @("401").
.OUTPUTS
    PSCustomObject with Success, Steps, ModeResults, and Error properties.
#>
function Invoke-AuthFailRedactionGate {
    param(
        [Parameter(Mandatory)]
        [string]$ContainerName,

        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [hashtable]$Headers,

        [Parameter(Mandatory)]
        [string]$Implementation,

        [Parameter(Mandatory)]
        [hashtable]$BadCredentials,

        [string[]]$FailureModes = @("401")
    )

    $result = [PSCustomObject]@{
        Success = $false
        Steps = @()
        ModeResults = @{}
        Error = $null
    }

    $indexerId = $null
    $allModesPassed = $true

    try {
        # Step 1: Create indexer with bad credentials
        Write-Host "`n=== AuthFail-Redaction Gate: Create bad-credential indexer ===" -ForegroundColor Cyan
        $createResult = New-BadCredentialIndexer -LidarrUrl $LidarrUrl -Headers $Headers -Implementation $Implementation -BadCredentials $BadCredentials
        $result.Steps += [PSCustomObject]@{ Step = "CreateIndexer"; Success = $createResult.Success; Details = $createResult }

        if (-not $createResult.Success) {
            $result.Error = "Failed to create test indexer: $($createResult.Error)"
            return $result
        }
        $indexerId = $createResult.IndexerId
        Write-Host "Created test indexer (id: $indexerId)" -ForegroundColor Green

        # Step 2: Test each failure mode
        foreach ($mode in $FailureModes) {
            $modeName = if ($script:AuthFailModes.ContainsKey($mode)) { $script:AuthFailModes[$mode].Name } else { "Mode $mode" }

            Write-Host "`n=== AuthFail-Redaction Gate: Test $modeName ($mode) ===" -ForegroundColor Cyan

            $testResult = Test-IndexerExpectFailure -LidarrUrl $LidarrUrl -Headers $Headers -IndexerId $indexerId -FailureMode $mode
            $result.ModeResults[$mode] = $testResult

            if ($testResult.Success) {
                Write-Host "Mode $mode: Failed as expected (HTTP $($testResult.HttpCode))" -ForegroundColor Green
                if ($testResult.RetryAfter) {
                    Write-Host "  Retry-After header: $($testResult.RetryAfter)" -ForegroundColor DarkGray
                }
            }
            else {
                Write-Host "Mode $mode: $($testResult.Error)" -ForegroundColor Yellow
                # For non-critical modes, we warn but don't fail the whole gate
                # The test may return a different error code than expected
                if ($mode -eq "401") {
                    # 401 is the primary test - if this fails, the gate fails
                    $allModesPassed = $false
                }
            }
        }

        $result.Steps += [PSCustomObject]@{ Step = "TestFailureModes"; Success = $allModesPassed; Details = $result.ModeResults }

        # Step 3: Check error response for leaked secrets (using last test result)
        Write-Host "`n=== AuthFail-Redaction Gate: Verify error response redaction ===" -ForegroundColor Cyan
        $lastTestResult = $result.ModeResults[$FailureModes[-1]]
        $errorRedactionResult = Test-ErrorResponseRedaction -ErrorResponse $lastTestResult.ErrorMessage
        $result.Steps += [PSCustomObject]@{ Step = "ErrorRedaction"; Success = $errorRedactionResult.Success; Details = $errorRedactionResult }

        if (-not $errorRedactionResult.Success) {
            $result.Error = "Error response contains leaked secrets"
            Write-Host "Error response redaction check FAILED" -ForegroundColor Red
            foreach ($leak in $errorRedactionResult.LeakDetails) {
                Write-Host "  - Pattern: $($leak.Pattern), Match: $($leak.Match)" -ForegroundColor Red
            }
            return $result
        }
        Write-Host "Error response properly redacted" -ForegroundColor Green

        # Step 4: Check container logs for leaked secrets using the helper
        Write-Host "`n=== AuthFail-Redaction Gate: Verify log redaction ===" -ForegroundColor Cyan

        # Use the helper function if available, otherwise fall back to local implementation
        $logRedactionResult = if (Get-Command Test-ContainerLogsForSecrets -ErrorAction SilentlyContinue) {
            Test-ContainerLogsForSecrets -ContainerName $ContainerName -TailLines 500
        }
        else {
            Test-LogsForSecretLeaks -ContainerName $ContainerName -TailLines 500
        }

        $result.Steps += [PSCustomObject]@{ Step = "LogRedaction"; Success = (-not $logRedactionResult.HasLeaks); Details = $logRedactionResult }

        if ($logRedactionResult.HasLeaks) {
            $result.Error = "Container logs contain leaked secrets"
            Write-Host "Log redaction check FAILED" -ForegroundColor Red
            foreach ($leak in $logRedactionResult.LeakDetails | Select-Object -First 5) {
                $patternName = if ($leak.PatternName) { $leak.PatternName } else { $leak.Pattern }
                Write-Host "  - $patternName" -ForegroundColor Red
                if ($leak.Context) {
                    Write-Host "    Context: $($leak.Context)" -ForegroundColor DarkGray
                }
            }
            return $result
        }
        Write-Host "Container logs properly redacted (checked $($logRedactionResult.LinesChecked) lines)" -ForegroundColor Green

        if (-not $allModesPassed) {
            $result.Error = "One or more failure modes did not behave as expected"
            return $result
        }

        $result.Success = $true
        Write-Host "`n AuthFail-Redaction Gate: All checks passed" -ForegroundColor Green
    }
    finally {
        # Cleanup: Remove test indexer
        if ($indexerId) {
            Write-Host "Cleaning up test indexer..." -ForegroundColor DarkGray
            Remove-TestIndexer -LidarrUrl $LidarrUrl -Headers $Headers -IndexerId $indexerId
        }
    }

    return $result
}

<#
.SYNOPSIS
    Gets bad credential presets for known plugin implementations.
.PARAMETER Implementation
    Indexer implementation name.
.OUTPUTS
    Hashtable of field name -> bad credential value, or $null if unknown.
#>
function Get-BadCredentialPreset {
    param(
        [Parameter(Mandatory)]
        [string]$Implementation
    )

    $presets = @{
        "QobuzIndexer" = @{
            "authMethod" = 0
            "email" = "invalid@test.invalid"
            "password" = "FAKE_PASSWORD_12345"
            "appId" = "000000000"
            "appSecret" = "FAKE_APP_SECRET_AAAABBBBCCCC"
        }
        "TidalLidarrIndexer" = @{
            "configPath" = "/config/tidalarr-authfail-test"
            "redirectUrl" = "https://invalid.test.invalid/callback?token=FAKE_TOKEN_12345"
        }
    }

    if ($presets.ContainsKey($Implementation)) {
        return $presets[$Implementation]
    }

    return $null
}

<#
.SYNOPSIS
    Gets the available auth failure modes.
.OUTPUTS
    Array of mode names.
#>
function Get-AuthFailModes {
    return @($script:AuthFailModes.Keys)
}

<#
.SYNOPSIS
    Gets details about a specific auth failure mode.
.PARAMETER Mode
    Mode name: "401", "403", or "429".
.OUTPUTS
    Hashtable with mode details.
#>
function Get-AuthFailModeInfo {
    param(
        [Parameter(Mandatory)]
        [string]$Mode
    )

    if ($script:AuthFailModes.ContainsKey($Mode)) {
        return $script:AuthFailModes[$Mode]
    }

    return $null
}

Export-ModuleMember -Function @(
    'New-BadCredentialIndexer',
    'Test-IndexerExpectFailure',
    'Test-LogsForSecretLeaks',
    'Test-ErrorResponseRedaction',
    'Remove-TestIndexer',
    'Invoke-AuthFailRedactionGate',
    'Get-BadCredentialPreset',
    'Get-AuthFailModes',
    'Get-AuthFailModeInfo'
)
