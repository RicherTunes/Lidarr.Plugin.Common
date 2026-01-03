$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Centralized error/skip classification for E2E gates and manifests.
.DESCRIPTION
    This module is the single source of truth for:
      - Determining whether an error/skip is a credential prereq (used by StrictPrereqs)
      - Mapping common error text to a structured E2E errorCode (used in run-manifest.json)

    Keep all pattern lists here to avoid split-brain behavior between:
      - scripts/lib/e2e-gates.psm1 (skip prereq decisions)
      - scripts/lib/e2e-json-output.psm1 (errorCode inference)

    IMPORTANT:
      - Prefer explicit error codes from plugins (via e2eErrorCode metadata key) over pattern matching.
      - Pattern matching is a fallback for legacy code that doesn't emit structured codes.
      - Do NOT log raw credential values; this module only returns codes/booleans.
#>

# Standard metadata key for explicit E2E error codes from plugins
# Plugins should emit this in PluginError.Metadata using E2EErrorCodeExtensions
$script:E2EErrorCodeMetadataKey = 'e2eErrorCode'

# Standard error codes for CI triage (namespace: E2E_)
# Ordered by specificity - more specific patterns should come first
$script:E2EErrorCodePatterns = [ordered]@{
    # Auth/credential prerequisites (missing config, invalid credentials, forbidden)
    'E2E_AUTH_MISSING' = @(
        'credentials not configured',
        'missing env vars',
        'missing/invalid credentials',
        'not authenticated',
        'auth error',
        'invalid_grant',
        'invalid_client',
        'unauthorized',
        'forbidden',
        '\b401\b',
        '\b403\b',
        'credential(s)? file missing',
        'oauth',
        'token',
        'api.?key',
        'credential'
    )

    'E2E_API_TIMEOUT' = @('timeout', 'timed out', 'connection refused', 'unreachable')
    'E2E_NO_RELEASES_ATTRIBUTED' = @('no releases', 'zero releases', 'releases attributed')
    'E2E_QUEUE_NOT_FOUND' = @('queue', 'not found in queue', 'download queue')
    'E2E_ZERO_AUDIO_FILES' = @('audio files', 'no audio', 'zero.*files')
    'E2E_METADATA_MISSING' = @('metadata', 'missing.*field', 'required field')
    'E2E_DOCKER_UNAVAILABLE' = @('docker', 'container', 'daemon')
    'E2E_CONFIG_INVALID' = @('config', 'configuration', 'invalid.*setting')
    'E2E_IMPORT_FAILED' = @('import', 'failed.*import')

    # Additional codes for structured emission (less common in pattern matching)
    'E2E_RATE_LIMITED' = @('rate.?limit', 'too many requests', '\b429\b', 'quota.?exceeded')
    'E2E_PROVIDER_UNAVAILABLE' = @('service.?unavailable', '\b503\b', 'provider.*down', 'upstream.*error')
    'E2E_CANCELLED' = @('cancelled', 'canceled', 'operation.*abort')
    'E2E_COMPONENT_AMBIGUOUS' = @('ambiguous', 'multiple.*match', 'duplicate.*component')
    'E2E_LOAD_FAILURE' = @('assembly.*load', 'type.*load', 'missing.*method', 'missing.*type')
}

# Credential prerequisite patterns (used for StrictPrereqs conversion of SKIP -> FAIL)
$script:CredentialPrereqPatterns = @(
    'credentials not configured',
    'missing env vars',
    'missing/invalid credentials',
    'not authenticated',
    'auth error',
    'invalid_grant',
    'invalid_client',
    'unauthorized',
    'forbidden',
    '\b401\b',
    '\b403\b',
    'credential(s)? file missing'
)

<#
.SYNOPSIS
    Extracts explicit E2E error code from plugin metadata if present.
.PARAMETER Metadata
    Plugin error metadata dictionary (from PluginError.Metadata or Lidarr API response).
.OUTPUTS
    String E2E error code if found, null otherwise.
#>
function Get-ExplicitE2EErrorCode {
    param(
        [AllowNull()]
        $Metadata
    )

    if ($null -eq $Metadata) { return $null }

    # Check for e2eErrorCode key (from E2EErrorCodeExtensions)
    if ($Metadata -is [hashtable]) {
        if ($Metadata.ContainsKey($script:E2EErrorCodeMetadataKey)) {
            return $Metadata[$script:E2EErrorCodeMetadataKey]
        }
        # Also check for ErrorCode (legacy/runner-set codes)
        if ($Metadata.ContainsKey('ErrorCode')) {
            return $Metadata['ErrorCode']
        }
    }
    elseif ($Metadata.PSObject.Properties.Name -contains $script:E2EErrorCodeMetadataKey) {
        return $Metadata.$script:E2EErrorCodeMetadataKey
    }
    elseif ($Metadata.PSObject.Properties.Name -contains 'ErrorCode') {
        return $Metadata.ErrorCode
    }

    return $null
}

function Get-E2EErrorClassification {
    <#
    .SYNOPSIS
        Classifies one or more messages for StrictPrereqs and errorCode inference.
    .DESCRIPTION
        First checks for explicit E2E error codes in metadata, then falls back to pattern matching.
    .PARAMETER Messages
        One or more strings (errors, skip reasons, outcome reasons) to classify.
    .PARAMETER Metadata
        Optional plugin error metadata that may contain explicit E2E error code.
    .OUTPUTS
        Hashtable:
          - isCredentialPrereq (bool)
          - errorCode (string|null)
          - source ('explicit' | 'inferred' | null)
    #>
    param(
        [AllowNull()]
        [array]$Messages,

        [AllowNull()]
        $Metadata
    )

    # First, check for explicit error code from plugin
    $explicitCode = Get-ExplicitE2EErrorCode -Metadata $Metadata
    if ($explicitCode) {
        # Determine if this is a credential prereq based on the code
        $isCredentialPrereq = $explicitCode -eq 'E2E_AUTH_MISSING'

        return @{
            isCredentialPrereq = $isCredentialPrereq
            errorCode = $explicitCode
            source = 'explicit'
        }
    }

    # Fall back to pattern-based inference
    $normalized = @(
        $Messages |
            Where-Object { $null -ne $_ } |
            ForEach-Object { "$_".Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    )

    if ($normalized.Count -eq 0) {
        return @{
            isCredentialPrereq = $false
            errorCode = $null
            source = $null
        }
    }

    $text = ($normalized -join ' ').ToLowerInvariant()

    $isCredentialPrereq = $false
    foreach ($pattern in $script:CredentialPrereqPatterns) {
        if ($text -match $pattern) {
            $isCredentialPrereq = $true
            break
        }
    }

    $errorCode = $null
    foreach ($code in $script:E2EErrorCodePatterns.Keys) {
        foreach ($pattern in $script:E2EErrorCodePatterns[$code]) {
            if ($text -match $pattern) {
                $errorCode = $code
                break
            }
        }
        if ($errorCode) { break }
    }

    $source = if ($errorCode) { 'inferred' } else { $null }

    return @{
        isCredentialPrereq = $isCredentialPrereq
        errorCode = $errorCode
        source = $source
    }
}

<#
.SYNOPSIS
    Gets the standard metadata key for explicit E2E error codes.
.OUTPUTS
    String key name ('e2eErrorCode').
#>
function Get-E2EErrorCodeMetadataKey {
    return $script:E2EErrorCodeMetadataKey
}

Export-ModuleMember -Function Get-E2EErrorClassification, Get-ExplicitE2EErrorCode, Get-E2EErrorCodeMetadataKey
