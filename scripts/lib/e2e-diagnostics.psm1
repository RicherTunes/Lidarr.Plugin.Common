# E2E Diagnostics Bundle for Plugin Testing
# Collects logs, config, and state on failure for AI-assisted triage

# Import centralized sanitization module (single source of truth for patterns)
$sanitizePath = Join-Path $PSScriptRoot "e2e-sanitize.psm1"
if (Test-Path $sanitizePath) {
    Import-Module $sanitizePath -Force -Global
}

# DEPRECATED: These patterns are now in e2e-sanitize.psm1
# Kept for backward compatibility - will be removed in future version
# Sensitive field patterns to redact (comprehensive list)
$script:SensitivePatterns = @(
    'password',
    'secret',
    'token',
    'key',
    'apikey',
    'api_key',
    'bearer',
    'credential',
    'auth',
    'redirect',
    'accesstoken',
    'access_token',
    'refreshtoken',
    'refresh_token',
    'clientsecret',
    'client_secret',
    'privatekey',
    'private_key',
    # OAuth/PKCE specific
    'code_verifier',
    'codeverifier',
    'authorization_code',
    'authorizationcode',
    'pkce',
    # Brainarr LLM endpoint (may contain internal IPs)
    'configurationurl',
    'configuration_url'
)

# DEPRECATED: These patterns are now in e2e-sanitize.psm1
# Kept for backward compatibility - will be removed in next major version
# Private/internal endpoint patterns for value-based redaction
# These are matched against STRING VALUES, not field names
$script:PrivateEndpointPatterns = @{
    # RFC1918 IPv4 ranges (private networks)
    'RFC1918_10' = '(?<!\d)10\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})(?!\d)'
    'RFC1918_172' = '(?<!\d)172\.(?:1[6-9]|2\d|3[01])\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})(?!\d)'
    'RFC1918_192' = '(?<!\d)192\.168\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})(?!\d)'
    # Link-local (APIPA)
    'LinkLocal' = '(?<!\d)169\.254\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})(?!\d)'
    # Loopback
    'Loopback' = '(?<!\d)127\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})\.(?:25[0-5]|2[0-4]\d|1?\d{1,2})(?!\d)'
    # Docker/internal hostnames
    'DockerInternal' = '(?i)host\.docker\.internal'
    'Localhost' = '(?i)(?<![a-z0-9])localhost(?![a-z0-9])'
    # IPv6 private/internal addresses (RFC 3986: IPv6 in URLs must be bracketed)
    # Only match bracketed form [::1] to avoid false positives on unrelated text
    'IPv6_Loopback' = '\[::1\]'
    'IPv6_LinkLocal' = '(?i)\[fe80:[0-9a-f:]+\]'
    'IPv6_UniqueLocal' = '(?i)\[f[cd][0-9a-f]{2}:[0-9a-f:]+\]'
}

# DEPRECATED: These patterns are now in e2e-sanitize.psm1
# Kept for backward compatibility - will be removed in next major version
# Query parameters that commonly carry secrets (redact the value, keep the param name)
$script:SecretQueryParams = @(
    'code',
    'code_verifier',
    'refresh_token',
    'access_token',
    'token',
    'apiKey',
    'api_key',
    'secret',
    'password',
    'client_secret'
)

<#
.SYNOPSIS
    Checks if a string value contains a private/internal endpoint.
.DESCRIPTION
    Returns $true if the value contains RFC1918 IPs, link-local, loopback,
    or known internal hostnames like host.docker.internal.
#>
function Test-PrivateEndpoint {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $false }

    foreach ($pattern in $script:PrivateEndpointPatterns.Values) {
        if ($Value -match $pattern) {
            return $true
        }
    }
    return $false
}

<#
.SYNOPSIS
    Redacts private endpoints and secret query params from a string value.
.DESCRIPTION
    - Replaces private IPs (RFC1918, link-local, loopback) with [PRIVATE-IP]
    - Replaces internal hostnames with [INTERNAL-HOST]
    - Redacts values of known secret query parameters
    - Preserves public URLs
#>
function Invoke-ValueRedaction {
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $Value }

    $result = $Value

    # Redact RFC1918 and other private IPs
    foreach ($key in $script:PrivateEndpointPatterns.Keys) {
        $pattern = $script:PrivateEndpointPatterns[$key]
        if ($key -match '^IPv6_') {
            # IPv6 patterns - check first to avoid conflict with 'Loopback' match
            $result = $result -replace $pattern, '[PRIVATE-IPv6]'
        } elseif ($key -match 'RFC1918|LinkLocal|Loopback') {
            $result = $result -replace $pattern, '[PRIVATE-IP]'
        } elseif ($key -eq 'DockerInternal') {
            $result = $result -replace $pattern, '[INTERNAL-HOST]'
        } elseif ($key -eq 'Localhost') {
            $result = $result -replace $pattern, '[LOCALHOST]'
        }
    }

    # Redact secret query parameters (keep param name, redact value)
    foreach ($param in $script:SecretQueryParams) {
        # Match: param=value (value is anything until & or end of string)
        $result = $result -replace "(?i)([?&])($param)=([^&]*)", '$1$2=[REDACTED]'
    }

    return $result
}

function Invoke-SecretRedaction {
    <#
    .SYNOPSIS
        Redacts sensitive fields from objects before serialization.

    .DESCRIPTION
        Uses a denylist of known sensitive field patterns.
        Recursively processes nested objects and arrays.
        Safe for JSON serialization after redaction.
    #>
    param(
        [Parameter(Mandatory = $false)]
        [AllowNull()]
        $Object
    )

    if ($null -eq $Object) { return $null }

    # Handle arrays
    if ($Object -is [array]) {
        $redacted = @($Object | ForEach-Object { Invoke-SecretRedaction -Object $_ })
        Write-Output -NoEnumerate $redacted
        return
    }

    # Handle PSCustomObject or hashtable-like objects
    if ($Object -is [PSCustomObject] -or $Object -is [hashtable]) {
        $result = [ordered]@{}

        $properties = if ($Object -is [hashtable]) { $Object.Keys } else { $Object.PSObject.Properties.Name }

        foreach ($prop in $properties) {
            $value = if ($Object -is [hashtable]) { $Object[$prop] } else { $Object.$prop }

            # Check if property name matches sensitive patterns
            $isSensitive = $false
            foreach ($pattern in $script:SensitivePatterns) {
                if ($prop -match $pattern) {
                    $isSensitive = $true
                    break
                }
            }

            if ($isSensitive -and $null -ne $value -and $value -ne '') {
                $result[$prop] = '[REDACTED]'
                continue
            }

            if ($value -is [array]) {
                $result[$prop] = @($value | ForEach-Object { Invoke-SecretRedaction -Object $_ })
                continue
            }

            if ($value -is [PSCustomObject] -or $value -is [hashtable]) {
                $result[$prop] = Invoke-SecretRedaction -Object $value
                continue
            }

            # Apply value-based redaction (private IPs, secret query params) to strings
            if ($value -is [string]) {
                $result[$prop] = Invoke-ValueRedaction -Value $value
            } else {
                $result[$prop] = $value
            }
        }

        # Special handling for Lidarr 'fields' array pattern
        # Lidarr uses an array of { name, value } items; normalize to an array even if a single item was provided.
        if ($result.Contains('fields') -and $null -ne $result['fields']) {
            $fields = $result['fields']
            if (-not ($fields -is [array])) {
                $fields = @($fields)
            }

            $result['fields'] = @($fields | ForEach-Object {
                $field = $_

                $fieldName = if ($field -is [hashtable]) { $field['name'] } else { $field.name }
                $fieldValue = if ($field -is [hashtable]) { $field['value'] } else { $field.value }

                $isSensitive = $false
                if ($fieldName) {
                    foreach ($pattern in $script:SensitivePatterns) {
                        if ($fieldName -match $pattern) {
                            $isSensitive = $true
                            break
                        }
                    }
                }

                if ($isSensitive -and -not [string]::IsNullOrWhiteSpace("$fieldValue")) {
                    if ($field -is [hashtable]) {
                        $field['value'] = '[REDACTED]'
                    }
                    else {
                        $field.value = '[REDACTED]'
                    }
                }

                $field
            })
        }

        return [PSCustomObject]$result
    }

    # Handle string primitives - check for private endpoints and secret query params
    if ($Object -is [string]) {
        return Invoke-ValueRedaction -Value $Object
    }

    # Return other primitives as-is
    return $Object
}

function Test-SecretRedaction {
    <#
    .SYNOPSIS
        Verifies the redactor removes common sensitive patterns.

    .DESCRIPTION
        Self-test function to validate redaction logic.
        Call this to verify the redactor is working correctly.

    .OUTPUTS
        $true if all patterns are properly redacted, throws otherwise.
    #>
    $testCases = @(
        @{ Input = @{ password = 'secret123' }; Field = 'password' }
        @{ Input = @{ apiKey = 'key123' }; Field = 'apiKey' }
        @{ Input = @{ accessToken = 'token123' }; Field = 'accessToken' }       
        @{ Input = @{ client_secret = 'secret' }; Field = 'client_secret' }     
        @{ Input = @{ bearerToken = 'bearer123' }; Field = 'bearerToken' }      
        @{ Input = @{ fields = @(@{ name = 'password'; value = 'secret' }) }; Field = 'fields[0].value' }
        @{
            Input = @{
                implementation = 'QobuzIndexer'
                name = 'Qobuzarr Test'
                fields = @(
                    @{ name = 'password'; value = 'p@ssw0rd' }
                    @{ name = 'apiKey'; value = 'abc123' }
                    @{ name = 'host'; value = 'example.test' }
                )
            }
            Field = 'lidarrSchema.fields[0].value'
        }
        # Brainarr configurationUrl test - verifies LLM endpoint URLs are redacted
        @{ Input = @{ configurationUrl = 'http://192.168.1.100:11434' }; Field = 'configurationUrl' }
        @{
            Input = @{
                implementation = 'BrainarrImportList'
                name = 'Brainarr Test'
                fields = @(
                    @{ name = 'configurationUrl'; value = 'http://10.0.0.5:11434' }
                    @{ name = 'manualModelId'; value = 'llama3' }
                )
            }
            Field = 'brainarrSchema'
        }
        # =====================================================================
        # RFC1918 / Private Endpoint Redaction Tests
        # =====================================================================
        # Test: Private IPs in URL values should be redacted
        @{
            Input = @{ endpoint = 'http://192.168.1.100:11434/api' }
            Field = 'privateIP_192'
            Expected = 'http://[PRIVATE-IP]:11434/api'
        }
        @{
            Input = @{ url = 'http://10.0.0.5:1234/test' }
            Field = 'privateIP_10'
            Expected = 'http://[PRIVATE-IP]:1234/test'
        }
        @{
            Input = @{ server = 'http://172.20.10.2:8080/health' }
            Field = 'privateIP_172'
            Expected = 'http://[PRIVATE-IP]:8080/health'
        }
        @{
            Input = @{ llmUrl = 'http://host.docker.internal:11434' }
            Field = 'dockerInternal'
            Expected = 'http://[INTERNAL-HOST]:11434'
        }
        @{
            Input = @{ localApi = 'http://localhost:8686/api' }
            Field = 'localhost'
            Expected = 'http://[LOCALHOST]:8686/api'
        }
        @{
            Input = @{ loopback = 'http://127.0.0.1:3000/test' }
            Field = 'loopback'
            Expected = 'http://[PRIVATE-IP]:3000/test'
        }
        # Test: Public URLs should NOT be redacted
        @{
            Input = @{ publicApi = 'https://api.tidal.com/v1/search' }
            Field = 'publicUrl_tidal'
            Expected = 'https://api.tidal.com/v1/search'
        }
        @{
            Input = @{ externalUrl = 'https://api.qobuz.com/0.2/album/get' }
            Field = 'publicUrl_qobuz'
            Expected = 'https://api.qobuz.com/0.2/album/get'
        }
        # Test: Query param secrets should be redacted
        @{
            Input = @{ callbackUrl = 'https://example.com/callback?code=abc123&state=xyz' }
            Field = 'queryParam_code'
            Expected = 'https://example.com/callback?code=[REDACTED]&state=xyz'
        }
        @{
            Input = @{ serviceUrl1 = 'https://auth.example.com?access_token=secret123&type=bearer' }
            Field = 'queryParam_token'
            Expected = 'https://auth.example.com?access_token=[REDACTED]&type=bearer'
        }
        @{
            Input = @{ serviceUrl2 = 'https://api.test.com?refresh_token=xyz789&client_id=app' }
            Field = 'queryParam_refresh'
            Expected = 'https://api.test.com?refresh_token=[REDACTED]&client_id=app'
        }
        # Test: Nested fields with private IPs
        @{
            Input = @{
                implementation = 'TestPlugin'
                fields = @(
                    @{ name = 'baseUrl'; value = 'http://192.168.1.50:8080' }
                    @{ name = 'publicUrl'; value = 'https://api.public.com' }
                )
            }
            Field = 'nestedPrivateIP'
        }
        # =====================================================================
        # IPv6 Private Address Tests
        # =====================================================================
        @{
            Input = @{ ipv6Loopback = 'http://[::1]:1234/api' }
            Field = 'ipv6_loopback'
            Expected = 'http://[PRIVATE-IPv6]:1234/api'
        }
        @{
            Input = @{ ipv6LinkLocal = 'http://[fe80::1]:11434/test' }
            Field = 'ipv6_linklocal'
            Expected = 'http://[PRIVATE-IPv6]:11434/test'
        }
        @{
            Input = @{ ipv6UniqueLocal = 'http://[fd00::1]:8080/health' }
            Field = 'ipv6_uniquelocal'
            Expected = 'http://[PRIVATE-IPv6]:8080/health'
        }
        # =====================================================================
        # Multi-param Query String Tests
        # =====================================================================
        @{
            Input = @{ multiParam = 'https://auth.com?code=abc123&refresh_token=xyz789&client_id=app' }
            Field = 'multiParam'
            Expected = 'https://auth.com?code=[REDACTED]&refresh_token=[REDACTED]&client_id=app'
        }
        @{
            Input = @{ mixedCase = 'https://api.com?Access_Token=secret&TYPE=bearer' }
            Field = 'mixedCase'
            Expected = 'https://api.com?Access_Token=[REDACTED]&TYPE=bearer'
        }
    )

    foreach ($case in $testCases) {
        $inputObject = [PSCustomObject]$case.Input
        $result = Invoke-SecretRedaction -Object $inputObject

        if ($case.Field -eq 'fields[0].value') {
            if ($result.fields[0].value -ne '[REDACTED]') {
                throw "Redaction failed for nested field pattern: $($case.Field)"
            }
        }
        elseif ($case.Field -eq 'lidarrSchema.fields[0].value') {
            if ($result.fields[0].value -ne '[REDACTED]') {
                throw "Redaction failed for Lidarr schema fields[0].value pattern"
            }
            if ($result.fields[1].value -ne '[REDACTED]') {
                throw "Redaction failed for Lidarr schema fields[1].value pattern"
            }
            if ($result.fields[2].value -eq '[REDACTED]') {
                throw "Redaction incorrectly redacted non-sensitive field value"
            }
        }
        elseif ($case.Field -eq 'brainarrSchema') {
            # Verify configurationUrl (LLM endpoint) is redacted
            if ($result.fields[0].value -ne '[REDACTED]') {
                throw "Redaction failed for Brainarr configurationUrl field"
            }
            # Verify manualModelId is NOT redacted (not sensitive)
            if ($result.fields[1].value -eq '[REDACTED]') {
                throw "Redaction incorrectly redacted Brainarr manualModelId field"
            }
        }
        elseif ($case.Field -match '^ipv6_') {
            # Verify IPv6 addresses are redacted
            $fieldName = ($case.Input.Keys | Select-Object -First 1)
            $actualValue = $result.$fieldName
            $expectedValue = $case.Expected
            if ($actualValue -ne $expectedValue) {
                throw "IPv6 redaction failed for $($case.Field): expected '$expectedValue', got '$actualValue'"
            }
        }
        elseif ($case.Field -eq 'multiParam' -or $case.Field -eq 'mixedCase') {
            # Verify multi-param and mixed case query strings
            $fieldName = ($case.Input.Keys | Select-Object -First 1)
            $actualValue = $result.$fieldName
            $expectedValue = $case.Expected
            if ($actualValue -ne $expectedValue) {
                throw "Multi-param/mixed case redaction failed for $($case.Field): expected '$expectedValue', got '$actualValue'"
            }
        }
        elseif ($case.Field -match '^privateIP_|^dockerInternal$|^localhost$|^loopback$') {
            # Verify private IPs/hosts are redacted in the value
            $fieldName = ($case.Input.Keys | Select-Object -First 1)
            $actualValue = $result.$fieldName
            $expectedValue = $case.Expected
            if ($actualValue -ne $expectedValue) {
                throw "Private endpoint redaction failed for $($case.Field): expected '$expectedValue', got '$actualValue'"
            }
        }
        elseif ($case.Field -match '^publicUrl_') {
            # Verify public URLs are NOT redacted
            $fieldName = ($case.Input.Keys | Select-Object -First 1)
            $actualValue = $result.$fieldName
            $expectedValue = $case.Expected
            if ($actualValue -ne $expectedValue) {
                throw "Public URL was incorrectly modified for $($case.Field): expected '$expectedValue', got '$actualValue'"
            }
        }
        elseif ($case.Field -match '^queryParam_') {
            # Verify query params with secrets are redacted
            $fieldName = ($case.Input.Keys | Select-Object -First 1)
            $actualValue = $result.$fieldName
            $expectedValue = $case.Expected
            if ($actualValue -ne $expectedValue) {
                throw "Query param redaction failed for $($case.Field): expected '$expectedValue', got '$actualValue'"
            }
        }
        elseif ($case.Field -eq 'nestedPrivateIP') {
            # Verify nested fields with private IPs are redacted
            if ($result.fields[0].value -notmatch '\[PRIVATE-IP\]') {
                throw "Nested private IP redaction failed: $($result.fields[0].value)"
            }
            if ($result.fields[1].value -ne 'https://api.public.com') {
                throw "Nested public URL was incorrectly modified: $($result.fields[1].value)"
            }
        }
        else {
            $fieldName = $case.Field
            if ($result.$fieldName -ne '[REDACTED]') {
                throw "Redaction failed for pattern: $fieldName"
            }
        }
    }

    return $true
}

function New-DiagnosticsBundle {
    <#
    .SYNOPSIS
        Creates a diagnostics bundle on E2E test failure.

    .DESCRIPTION
        Collects:
        - Lidarr logs (last 500 lines)
        - Plugin logs (if available)
        - Current indexer/download client configuration
        - Queue state
        - System status
        - Run manifest with gate results

    .PARAMETER OutputPath
        Directory to write the diagnostics bundle.

    .PARAMETER ContainerName
        Docker container name to collect logs from.

    .PARAMETER LidarrApiUrl
        Lidarr API URL.

    .PARAMETER LidarrApiKey
        Lidarr API key.

    .PARAMETER GateResults
        Array of gate result objects from the test run.

    .OUTPUTS
        Path to the created diagnostics bundle zip file.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$OutputPath,

        [Parameter(Mandatory)]
        [string]$ContainerName,

        [Parameter(Mandatory)]
        [string]$LidarrApiUrl,

        [Parameter(Mandatory)]
        [string]$LidarrApiKey,

        [Parameter(Mandatory)]
        [array]$GateResults,

        [string]$RequestedGate = "schema",

        [string[]]$Plugins = @(),

        [string[]]$RunnerArgs = @(),

        [switch]$RedactionSelfTestExecuted,

        [bool]$RedactionSelfTestPassed = $false
    )

    if ($RedactionSelfTestExecuted -and -not $RedactionSelfTestPassed) {
        throw "Diagnostics redaction self-test did not pass; refusing to write a bundle."
    }

    $timestamp = Get-Date -Format "yyyyMMdd-HHmmss-fff"
    $runId = ([Guid]::NewGuid()).ToString("N")
    $bundleDir = Join-Path $OutputPath "diagnostics-$timestamp-$runId"
    New-Item -ItemType Directory -Path $bundleDir -Force | Out-Null

    Write-Host "Creating diagnostics bundle at $bundleDir..." -ForegroundColor Yellow

    # 1. Collect container logs
    try {
        $logsPath = Join-Path $bundleDir "container-logs.txt"
        docker logs --tail 500 $ContainerName 2>&1 | Out-File -FilePath $logsPath -Encoding UTF8
        Write-Host "  - Container logs collected" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect container logs: $_" -ForegroundColor Red
    }

    # 2. Collect Lidarr API state
    $headers = @{
        'X-Api-Key' = $LidarrApiKey
        'Content-Type' = 'application/json'
    }
    $apiUrl = $LidarrApiUrl.TrimEnd('/')

    # System status
    try {
        $status = Invoke-RestMethod -Uri "$apiUrl/api/v1/system/status" -Headers $headers -TimeoutSec 10
        $status | ConvertTo-Json -Depth 12 | Out-File -FilePath (Join-Path $bundleDir "system-status.json") -Encoding UTF8
        Write-Host "  - System status collected" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect system status: $_" -ForegroundColor Red
    }

    # Indexer schemas
    try {
        $indexerSchemas = Invoke-RestMethod -Uri "$apiUrl/api/v1/indexer/schema" -Headers $headers -TimeoutSec 10
        $indexerSchemas | ConvertTo-Json -Depth 12 | Out-File -FilePath (Join-Path $bundleDir "indexer-schemas.json") -Encoding UTF8
        Write-Host "  - Indexer schemas collected" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect indexer schemas: $_" -ForegroundColor Red
    }

    # Download client schemas
    try {
        $clientSchemas = Invoke-RestMethod -Uri "$apiUrl/api/v1/downloadclient/schema" -Headers $headers -TimeoutSec 10
        $clientSchemas | ConvertTo-Json -Depth 12 | Out-File -FilePath (Join-Path $bundleDir "downloadclient-schemas.json") -Encoding UTF8
        Write-Host "  - Download client schemas collected" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect download client schemas: $_" -ForegroundColor Red
    }

    # Import list schemas
    try {
        $importListSchemas = Invoke-RestMethod -Uri "$apiUrl/api/v1/importlist/schema" -Headers $headers -TimeoutSec 10
        $importListSchemas | ConvertTo-Json -Depth 12 | Out-File -FilePath (Join-Path $bundleDir "importlist-schemas.json") -Encoding UTF8
        Write-Host "  - Import list schemas collected" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect import list schemas: $_" -ForegroundColor Red
    }

    # Configured indexers
    try {
        $indexers = Invoke-RestMethod -Uri "$apiUrl/api/v1/indexer" -Headers $headers -TimeoutSec 10
        $indexers = Invoke-SecretRedaction -Object $indexers
        $indexers | ConvertTo-Json -Depth 12 | Out-File -FilePath (Join-Path $bundleDir "configured-indexers.json") -Encoding UTF8
        Write-Host "  - Configured indexers collected (secrets redacted)" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect configured indexers: $_" -ForegroundColor Red
    }

    # Configured download clients
    try {
        $clients = Invoke-RestMethod -Uri "$apiUrl/api/v1/downloadclient" -Headers $headers -TimeoutSec 10
        $clients = Invoke-SecretRedaction -Object $clients
        $clients | ConvertTo-Json -Depth 12 | Out-File -FilePath (Join-Path $bundleDir "configured-downloadclients.json") -Encoding UTF8
        Write-Host "  - Configured download clients collected (secrets redacted)" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect configured download clients: $_" -ForegroundColor Red
    }

    # Queue state
    try {
        $queue = Invoke-RestMethod -Uri "$apiUrl/api/v1/queue" -Headers $headers -TimeoutSec 10
        $queue | ConvertTo-Json -Depth 12 | Out-File -FilePath (Join-Path $bundleDir "queue-state.json") -Encoding UTF8
        Write-Host "  - Queue state collected" -ForegroundColor Green
    }
    catch {
        Write-Host "  - Failed to collect queue state: $_" -ForegroundColor Red
    }

    # 3. Write run manifest
    $failedResults = @(
        $GateResults | Where-Object {
            if ($_.PSObject.Properties['Outcome']) { $_.Outcome -eq 'failed' } else { -not $_.Success }
        }
    )

    $manifest = [PSCustomObject]@{
        schemaVersion = "1.0"
        timestamp = (Get-Date).ToString('o')
        runId = $runId
        runner = @{
            name = "lidarr.plugin.common:e2e-runner.ps1"
            args = $RunnerArgs
        }
        lidarr = @{
            url = $LidarrApiUrl
            containerName = $ContainerName
        }
        requestedGate = $RequestedGate
        plugins = $Plugins
        redaction = @{
            selfTestExecuted = [bool]$RedactionSelfTestExecuted
            selfTestPassed = [bool]$RedactionSelfTestPassed
            patternsCount = $script:SensitivePatterns.Count
        }
        results = $GateResults
        overallSuccess = ($failedResults.Count -eq 0)
        failedGates = @($failedResults | Select-Object -ExpandProperty Gate -ErrorAction SilentlyContinue)
    }
    $manifest | ConvertTo-Json -Depth 8 | Out-File -FilePath (Join-Path $bundleDir "run-manifest.json") -Encoding UTF8
    Write-Host "  - Run manifest created" -ForegroundColor Green

    # 4. Create zip bundle
    $zipPath = "$bundleDir.zip"
    Compress-Archive -Path "$bundleDir\*" -DestinationPath $zipPath -Force
    Remove-Item -Path $bundleDir -Recurse -Force

    Write-Host ""
    Write-Host "Diagnostics bundle created: $zipPath" -ForegroundColor Cyan
    Write-Host "Share this bundle for AI-assisted triage." -ForegroundColor Yellow

    return $zipPath
}

function Get-FailureSummary {
    <#
    .SYNOPSIS
        Generates a human-readable failure summary from gate results.
    #>
    param(
        [Parameter(Mandatory)]
        [array]$GateResults
    )

    $summary = @()
    $summary += "E2E Test Failure Summary"
    $summary += "========================"
    $summary += ""

    foreach ($gate in $GateResults) {
        $status =
            if ($gate.PSObject.Properties['Outcome']) {
                switch ($gate.Outcome) {
                    'success' { "[PASS]" }
                    'skipped' { "[SKIP]" }
                    default { "[FAIL]" }
                }
            }
            elseif ($gate.Success) { "[PASS]" }
            else { "[FAIL]" }
        $summary += "$status $($gate.Gate) Gate"

        if ($gate.Errors -and $gate.Errors.Count -gt 0) {
            foreach ($error in $gate.Errors) {
                $summary += "       - $error"
            }
        }
    }

    return $summary -join "`n"
}

Export-ModuleMember -Function New-DiagnosticsBundle, Get-FailureSummary, Invoke-SecretRedaction, Test-SecretRedaction, Test-PrivateEndpoint, Invoke-ValueRedaction
