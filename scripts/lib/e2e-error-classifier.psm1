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
      - This is best-effort heuristic classification. Prefer explicit error codes from gates when possible.
      - Do NOT log raw credential values; this module only returns codes/booleans.
#>

# Standard error codes for CI triage (namespace: E2E_)
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

function Get-E2EErrorClassification {
    <#
    .SYNOPSIS
        Classifies one or more messages for StrictPrereqs and errorCode inference.
    .PARAMETER Messages
        One or more strings (errors, skip reasons, outcome reasons) to classify.
    .OUTPUTS
        Hashtable:
          - isCredentialPrereq (bool)
          - errorCode (string|null)
    #>
    param(
        [AllowNull()]
        [array]$Messages
    )

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

    return @{
        isCredentialPrereq = $isCredentialPrereq
        errorCode = $errorCode
    }
}

Export-ModuleMember -Function Get-E2EErrorClassification
