#!/usr/bin/env pwsh
# Test that secrets are properly redacted in JSON output

$ErrorActionPreference = 'Stop'
Import-Module (Join-Path $PSScriptRoot ".." "lib" "e2e-json-output.psm1") -Force
Import-Module (Join-Path $PSScriptRoot ".." "lib" "e2e-diagnostics.psm1") -Force

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Secret Redaction Audit" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

# Create test with various secret patterns
$testResults = @(
    [PSCustomObject]@{
        Gate = 'Search'
        PluginName = 'Qobuzarr'
        Outcome = 'skipped'
        Success = $false
        Errors = @()
        Details = @{
            SkipReason = 'Missing env vars: authToken'
            CredentialAllOf = @('authToken', 'userId', 'apiKey')
            authToken = 'secret123abc'
            password = 'mypassword'
            apiKey = 'abcd1234567890abcd1234567890ab' # gitleaks:allow
        }
        StartTime = [DateTime]::UtcNow.AddSeconds(-1)
        EndTime = [DateTime]::UtcNow
    }
    ,
    [PSCustomObject]@{
        Gate = 'ImportList'
        PluginName = 'AppleMusicarr'
        Outcome = 'skipped'
        Success = $false
        Errors = @()
        Details = @{
            SkipReason = 'Missing env vars: privateKey'
            CredentialAllOf = @('teamId', 'keyId', 'privateKey', 'musicUserToken')
            teamId = 'TEAMID12345'
            keyId = 'KEYID12345'
            privateKey = '-----BEGIN PRIVATE KEY-----\nabc\n-----END PRIVATE KEY-----'
            privateKeyB64 = 'LS0tLS1CRUdJTiBQUklWQVRFIEtFWS0tLS0tCmFiYwotLS0tLUVORCBQUklWQVRFIEtFWS0tLS0t' # base64 of the PEM above
            musicUserToken = 'music-user-token-abc'
            baseUrl = 'http://internal.example.local:1234'
        }
        StartTime = [DateTime]::UtcNow.AddSeconds(-1)
        EndTime = [DateTime]::UtcNow
    }
)

$testContext = @{
    LidarrUrl = 'http://192.168.1.100:8686'
    ContainerName = 'test'
    ContainerId = 'abc123'
    ContainerStartedAt = [DateTime]::UtcNow.AddMinutes(-5)
    ImageTag = 'test'
    ImageId = 'sha256:img123'
    ImageDigest = 'sha256:abc'
    RequestedGate = 'search'
    Plugins = @('Qobuzarr', 'AppleMusicarr')
    EffectiveGates = @('Search')
    EffectivePlugins = @('Qobuzarr', 'AppleMusicarr')
    StopReason = $null
    RedactionSelfTestExecuted = $true
    RedactionSelfTestPassed = $true
    RunnerArgs = @('-ApiKey', 'abcd1234567890abcd1234567890ab', '-Gate', 'search') # gitleaks:allow
    DiagnosticsBundlePath = $null
    DiagnosticsIncludedFiles = @()
    LidarrVersion = '2.9.6'
    LidarrBranch = 'plugins'
    SourceShas = @{ Common = 'abc'; Qobuzarr = $null; Tidalarr = $null; Brainarr = $null; AppleMusicarr = $null }
    SourceProvenance = @{ Common = 'git'; Qobuzarr = 'unknown'; Tidalarr = 'unknown'; Brainarr = 'unknown'; AppleMusicarr = 'unknown' }
}

$json = ConvertTo-E2ERunManifest -Results $testResults -Context $testContext
$obj = $json | ConvertFrom-Json

$passed = 0
$failed = 0

function Test-Assertion {
    param([bool]$Condition, [string]$Message)
    if ($Condition) {
        Write-Host "  [PASS] $Message" -ForegroundColor Green
        $script:passed++
    } else {
        Write-Host "  [FAIL] $Message" -ForegroundColor Red
        $script:failed++
    }
}

Write-Host ""
Write-Host "Test Group: Secret Value Leakage" -ForegroundColor Yellow

Test-Assertion ($json -notmatch 'secret123abc') "authToken value not leaked"
Test-Assertion ($json -notmatch 'mypassword') "password value not leaked"
Test-Assertion ($json -notmatch 'abcd1234567890abcd1234567890ab') "API key not leaked in raw form"
Test-Assertion ($json -notmatch '192\.168\.1\.100') "Private IP not leaked"
Test-Assertion ($json -notmatch 'TEAMID12345') "Apple Music teamId not leaked"
Test-Assertion ($json -notmatch 'KEYID12345') "Apple Music keyId not leaked"
Test-Assertion ($json -notmatch 'music-user-token-abc') "Apple Music user token not leaked"
Test-Assertion ($json -notmatch 'BEGIN PRIVATE KEY') "Apple Music private key PEM not leaked"
Test-Assertion ($json -notmatch 'LS0tLS1CRUdJTiBQUklWQVRFIEtFWS0tLS0t') "Apple Music private key base64 not leaked"
Test-Assertion ($json -notmatch 'internal\\.example\\.local') "Apple Music baseUrl not leaked"

Write-Host ""
Write-Host "Test Group: Redaction Applied" -ForegroundColor Yellow

Test-Assertion ($obj.lidarr.url -match '\[PRIVATE-IP\]') "lidarr.url has private IP redacted"
Test-Assertion (($obj.runner.args -join ' ') -match '\[REDACTED\]') "runner.args has API key redacted"
Test-Assertion ($obj.results[0].outcomeReason -ne $null) "outcomeReason is present"
Test-Assertion ($obj.results[0].outcomeReason -notmatch '=[a-zA-Z0-9]{8,}') "outcomeReason has no secret values"

Write-Host ""
Write-Host "Test Group: Credential Field Names Only" -ForegroundColor Yellow

$creds = $obj.results[0].details.credentialAllOf
Test-Assertion ($creds -contains 'authToken') "credentialAllOf contains field name 'authToken'"
Test-Assertion ($creds -contains 'userId') "credentialAllOf contains field name 'userId'"
Test-Assertion ($creds -notcontains 'secret123abc') "credentialAllOf does not contain secret values"

# ============================================================================
# Error String Sanitization Tests
# ============================================================================
Write-Host ""
Write-Host "Test Group: Error String Sanitization (Invoke-ErrorSanitization)" -ForegroundColor Yellow

# Test URL query param redaction
$urlWithToken = "https://api.example.com/data?access_token=abc123secret456&user=test"
$sanitized = Invoke-ErrorSanitization -ErrorString $urlWithToken
Test-Assertion ($sanitized -notmatch 'abc123secret456') "URL access_token param redacted"
Test-Assertion ($sanitized -match '\[REDACTED\]') "URL param replaced with [REDACTED]"

# Test api_key in URL
$urlWithApiKey = "Error fetching https://api.service.com/v1/search?api_key=secretkey12345678901234&q=test"
$sanitized = Invoke-ErrorSanitization -ErrorString $urlWithApiKey
Test-Assertion ($sanitized -notmatch 'secretkey12345678901234') "URL api_key param redacted"

# Test Authorization header in error message
$authHeaderError = "Request failed: Authorization: Bearer eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJzdWIiOiIxMjM0NTY3ODkwIn0.dozjgNryP4J3jVmNHl0w5N_XgL0n3I9PlFUP0THsR8U" # gitleaks:allow
$sanitized = Invoke-ErrorSanitization -ErrorString $authHeaderError
Test-Assertion ($sanitized -match '\[REDACTED\]' -or $sanitized -match '\[JWT-REDACTED\]') "JWT token in Authorization header redacted"
Test-Assertion ($sanitized -notmatch 'eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9') "JWT payload not present in output"

# Test private IP in URL
$privateIpError = "Connection refused to http://192.168.1.50:8686/api/v1/status"
$sanitized = Invoke-ErrorSanitization -ErrorString $privateIpError
Test-Assertion ($sanitized -match '\[PRIVATE-IP\]') "Private IP in error URL redacted"

# Test hex API key in error message
$hexKeyError = "API call failed with key=abcdef1234567890abcdef1234567890 in header" # gitleaks:allow
$sanitized = Invoke-ErrorSanitization -ErrorString $hexKeyError
Test-Assertion ($sanitized -notmatch 'abcdef1234567890abcdef1234567890') "32-char hex key redacted from error"

# Test multiple secrets in one string
$multiSecretError = "Failed: https://api.test.com?token=secret1&password=secret2 with auth=secret3"
$sanitized = Invoke-ErrorSanitization -ErrorString $multiSecretError
Test-Assertion ($sanitized -notmatch 'secret1') "First secret redacted"
Test-Assertion ($sanitized -notmatch 'secret2') "Second secret redacted"

# ============================================================================
# Over-Sanitization Protection Tests
# ============================================================================
Write-Host ""
Write-Host "Test Group: Over-Sanitization Protection (preserve public data)" -ForegroundColor Yellow

# Public API URLs should remain intact (only query params with secrets scrubbed)
$tidalPublicUrl = "Error from https://api.tidal.com/v1/albums/12345678/tracks"
$sanitized = Invoke-ErrorSanitization -ErrorString $tidalPublicUrl
Test-Assertion ($sanitized -match 'api\.tidal\.com') "Public Tidal API domain preserved"
Test-Assertion ($sanitized -match '/albums/12345678/tracks') "Album ID path preserved"

# Qobuz public URL
$qobuzPublicUrl = "Failed to fetch https://www.qobuz.com/api.json/0.2/album/get?album_id=abc123def"
$sanitized = Invoke-ErrorSanitization -ErrorString $qobuzPublicUrl
Test-Assertion ($sanitized -match 'qobuz\.com') "Public Qobuz domain preserved"
Test-Assertion ($sanitized -match 'album_id=abc123def') "Non-secret query param preserved"

# MusicBrainz IDs (UUIDs) should not be redacted
$mbidError = "Artist not found: mbid=f27ec8db-af05-4f36-916e-3d57f91ecf5e"
$sanitized = Invoke-ErrorSanitization -ErrorString $mbidError
Test-Assertion ($sanitized -match 'f27ec8db-af05-4f36-916e-3d57f91ecf5e') "MusicBrainz UUID preserved"

# Lidarr localhost URLs - hostname normalized to [LOCALHOST] but path preserved
$localhostUrl = "API call to http://localhost:8686/api/v1/artist/123 failed"
$sanitized = Invoke-ErrorSanitization -ErrorString $localhostUrl
Test-Assertion ($sanitized -match '\[LOCALHOST\]:8686' -or $sanitized -match 'localhost:8686') "localhost normalized or preserved"
Test-Assertion ($sanitized -match '/artist/123') "Artist ID path preserved"

# Short hex strings that are NOT API keys (e.g., container IDs, short hashes)
$shortHex = "Container abc123 exited with code 1"
$sanitized = Invoke-ErrorSanitization -ErrorString $shortHex
Test-Assertion ($sanitized -match 'abc123') "Short hex ID preserved (not an API key)"

# Git commit SHAs (7-char short form) should be preserved
$gitSha = "Built from commit dcaf488"
$sanitized = Invoke-ErrorSanitization -ErrorString $gitSha
Test-Assertion ($sanitized -match 'dcaf488') "7-char git SHA preserved"

# Album/track names with numbers should not be redacted
$albumName = "Failed to import: 1989 (Taylor's Version) - Track 01"
$sanitized = Invoke-ErrorSanitization -ErrorString $albumName
Test-Assertion ($sanitized -match "1989.*Taylor.*Track 01") "Album/track names preserved"

# ============================================================================
# Error Array Sanitization in Manifest
# ============================================================================
Write-Host ""
Write-Host "Test Group: Error Array Sanitization in Manifest" -ForegroundColor Yellow

# Create a test result with secrets in the Errors array
$errorTestResults = @(
    [PSCustomObject]@{
        Gate = 'Search'
        PluginName = 'Qobuzarr'
        Outcome = 'failed'
        Success = $false
        Errors = @(
            "API request failed: https://api.qobuz.com/v1/search?access_token=mysecrettoken123&query=test",
            "Authorization header: Bearer eyJhbGciOiJIUzI1NiJ9.eyJ0ZXN0IjoidmFsdWUifQ.test123signature", # gitleaks:allow
            "Connection to http://192.168.1.100:8686 refused"
        )
        Details = @{ Query = 'test artist' }
        StartTime = [DateTime]::UtcNow.AddSeconds(-2)
        EndTime = [DateTime]::UtcNow
    }
)

$errorTestContext = @{
    LidarrUrl = 'http://localhost:8686'
    ContainerName = 'test'
    ContainerId = 'abc123'
    ContainerStartedAt = [DateTime]::UtcNow.AddMinutes(-5)
    ImageTag = 'test'
    ImageId = 'sha256:img123'
    ImageDigest = 'sha256:abc'
    RequestedGate = 'search'
    Plugins = @('Qobuzarr')
    EffectiveGates = @('Search')
    EffectivePlugins = @('Qobuzarr')
    StopReason = $null
    RedactionSelfTestExecuted = $true
    RedactionSelfTestPassed = $true
    RunnerArgs = @('-Gate', 'search')
    DiagnosticsBundlePath = $null
    DiagnosticsIncludedFiles = @()
    LidarrVersion = '2.9.6'
    LidarrBranch = 'plugins'
    SourceShas = @{ Common = 'abc'; Qobuzarr = $null; Tidalarr = $null; Brainarr = $null }
    SourceProvenance = @{ Common = 'git'; Qobuzarr = 'unknown'; Tidalarr = 'unknown'; Brainarr = 'unknown' }
}

$errorJson = ConvertTo-E2ERunManifest -Results $errorTestResults -Context $errorTestContext

Test-Assertion ($errorJson -notmatch 'mysecrettoken123') "access_token not leaked in errors array"
Test-Assertion ($errorJson -notmatch 'eyJhbGciOiJIUzI1NiJ9') "JWT not leaked in errors array"
Test-Assertion ($errorJson -notmatch '192\.168\.1\.100') "Private IP not leaked in errors array"
Test-Assertion ($errorJson -match '\[REDACTED\]' -or $errorJson -match '\[JWT-REDACTED\]') "Redaction markers present in output"

# Verify outcomeReason is also sanitized (it comes from first error)
$errorObj = $errorJson | ConvertFrom-Json
Test-Assertion ($errorObj.results[0].outcomeReason -notmatch 'mysecrettoken123') "outcomeReason does not leak access_token"
Test-Assertion ($errorObj.results[0].outcomeReason -match '\[REDACTED\]') "outcomeReason has redaction applied"

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Audit Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Passed: $passed" -ForegroundColor Green
Write-Host "Failed: $failed" -ForegroundColor $(if ($failed -gt 0) { 'Red' } else { 'Green' })

if ($failed -gt 0) { exit 1 }
exit 0
