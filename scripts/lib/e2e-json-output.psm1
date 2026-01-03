# E2E JSON Output Module
# Generates machine-readable run manifests for CI parsing
# Schema ID: richer-tunes.lidarr.e2e-run-manifest
# Schema Version: 1.2

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Import diagnostics module for redaction
# Use -Global so it remains available after this module loads
$diagnosticsPath = Join-Path $PSScriptRoot "e2e-diagnostics.psm1"
if (Test-Path $diagnosticsPath) {
    Import-Module $diagnosticsPath -Force -Global
}

# Import shared classifier (single source of truth for errorCode inference)
$classifierPath = Join-Path $PSScriptRoot "e2e-error-classifier.psm1"
if (Test-Path $classifierPath) {
    Import-Module $classifierPath -Force -Global
}

# Host bug detection patterns with tiered classification
# ALC = True Assembly Load Context bugs (host/runtime issue, not plugin code)
# ABI_MISMATCH = Plugin compiled against different assembly version
# DEPENDENCY_DRIFT = Version conflict between plugins or with host
$script:HostBugPatterns = @(
    # TRUE ALC BUGS - These are genuine host/runtime issues
    @{ Pattern = 'AssemblyLoadContext.*unload'; Classification = 'ALC'; Severity = 'host_bug' }
    @{ Pattern = 'AssemblyLoadContext.*collectible'; Classification = 'ALC'; Severity = 'host_bug' }
    @{ Pattern = 'Cannot unload.*AssemblyLoadContext'; Classification = 'ALC'; Severity = 'host_bug' }
    @{ Pattern = 'Assembly .+ is already loaded.*different.*context'; Classification = 'ALC'; Severity = 'host_bug' }

    # ABI MISMATCH - Plugin compiled against wrong assembly version
    @{ Pattern = 'Could not load type .+ from assembly'; Classification = 'ABI_MISMATCH'; Severity = 'plugin_rebuild' }
    @{ Pattern = 'MissingMethodException'; Classification = 'ABI_MISMATCH'; Severity = 'plugin_rebuild' }
    @{ Pattern = 'MissingFieldException'; Classification = 'ABI_MISMATCH'; Severity = 'plugin_rebuild' }
    @{ Pattern = 'TypeLoadException'; Classification = 'ABI_MISMATCH'; Severity = 'plugin_rebuild' }
    @{ Pattern = 'Method not found.*assembly'; Classification = 'ABI_MISMATCH'; Severity = 'plugin_rebuild' }

    # DEPENDENCY DRIFT - Version conflicts between assemblies
    @{ Pattern = 'FileLoadException.*version'; Classification = 'DEPENDENCY_DRIFT'; Severity = 'version_conflict' }
    @{ Pattern = 'Could not load file or assembly.*Version='; Classification = 'DEPENDENCY_DRIFT'; Severity = 'version_conflict' }
    @{ Pattern = 'assembly.*version.*mismatch'; Classification = 'DEPENDENCY_DRIFT'; Severity = 'version_conflict' }

    # LOAD FAILURE - Generic assembly load issues (investigate further)
    @{ Pattern = 'FileLoadException'; Classification = 'LOAD_FAILURE'; Severity = 'investigate' }
    @{ Pattern = 'Could not load file or assembly'; Classification = 'LOAD_FAILURE'; Severity = 'investigate' }
    @{ Pattern = 'BadImageFormatException'; Classification = 'LOAD_FAILURE'; Severity = 'investigate' }
    @{ Pattern = 'ReflectionTypeLoadException'; Classification = 'LOAD_FAILURE'; Severity = 'investigate' }
    @{ Pattern = 'TypeInitializationException'; Classification = 'TYPE_INIT_FAILURE'; Severity = 'investigate' }
)

<#
.SYNOPSIS
    Detects assembly loading issues in error messages or logs with tiered classification.
.DESCRIPTION
    Scans error arrays and log content for known .NET assembly loading failure patterns.
    Returns detection info with classification indicating issue type:
    - ALC: True Assembly Load Context bugs (host/runtime issue)
    - ABI_MISMATCH: Plugin compiled against different assembly version
    - DEPENDENCY_DRIFT: Version conflict between plugins or with host
    - LOAD_FAILURE: Generic assembly load issues
    - TYPE_INIT_FAILURE: Type initialization errors
.PARAMETER Errors
    Array of error strings to scan.
.PARAMETER LogContent
    Optional log content string to scan.
.OUTPUTS
    Hashtable with: detected (bool), classification (string), severity (string), matchedLine (string)
#>
function Test-ALCPattern {
    param(
        [array]$Errors,
        [string]$LogContent
    )

    $result = @{
        detected = $false
        classification = $null
        severity = $null
        matchedLine = $null
    }

    # Combine errors and log content for scanning
    $textToScan = @()
    if ($Errors) { $textToScan += $Errors }
    if ($LogContent) { $textToScan += $LogContent -split "`n" }

    foreach ($line in $textToScan) {
        if ([string]::IsNullOrWhiteSpace($line)) { continue }

        foreach ($pattern in $script:HostBugPatterns) {
            if ($line -match $pattern.Pattern) {
                $result.detected = $true
                $result.classification = $pattern.Classification
                $result.severity = $pattern.Severity
                # Sanitize the matched line before storing
                $result.matchedLine = Invoke-ErrorSanitization -ErrorString ($line.Trim().Substring(0, [Math]::Min(200, $line.Trim().Length)))
                return $result
            }
        }
    }

    return $result
}

<#
.SYNOPSIS
    Gets the git SHA of the runner script for version tracking.
#>
function Get-RunnerVersion {
    try {
        $scriptDir = Split-Path $PSScriptRoot -Parent
        Push-Location $scriptDir
        $sha = git rev-parse --short HEAD 2>$null
        Pop-Location
        if ($sha) { return $sha }
    } catch { }
    return "unknown"
}

<#
.SYNOPSIS
    Gets the $schema URL pinned to the current git SHA for immutable reference.
.DESCRIPTION
    Returns a raw.githubusercontent.com URL that validators can fetch.
    Pinned to full SHA when available (immutable), falls back to 'main' branch.
#>
function Get-SchemaUrl {
    $baseUrl = "https://raw.githubusercontent.com/RicherTunes/Lidarr.Plugin.Common"
    $schemaPath = "docs/reference/e2e-run-manifest.schema.json"

    try {
        # Compute repo root: $PSScriptRoot (scripts/lib) -> parent (scripts) -> parent (repo root)
        $repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent

        # Guard: verify we're inside a git work tree before attempting SHA lookup
        $isGitRepo = ((git -C $repoRoot rev-parse --is-inside-work-tree 2>$null) ?? '').Trim().ToLower()
        if ($isGitRepo -eq 'true') {
            $fullSha = ((git -C $repoRoot rev-parse HEAD 2>$null) ?? '').Trim()
            if ($fullSha) {
                return "$baseUrl/$fullSha/$schemaPath"
            }
        }
    } catch { }

    # Fallback to main branch (no git, copied scripts/, or error)
    return "$baseUrl/main/$schemaPath"
}

<#
.SYNOPSIS
    Gets the git SHA for a specific repository path.
#>
function Get-RepoSha {
    param([string]$RepoPath)
    if (-not $RepoPath -or -not (Test-Path $RepoPath)) { return $null }
    try {
        Push-Location $RepoPath
        $sha = git rev-parse --short HEAD 2>$null
        Pop-Location
        if ($sha) { return $sha }
    } catch { }
    return $null
}

<#
.SYNOPSIS
    Converts a key to lowerCamelCase.
#>
function ConvertTo-LowerCamelCase {
    param([string]$Key)
    if ([string]::IsNullOrEmpty($Key)) { return $Key }
    if ($Key.Length -eq 1) { return $Key.ToLower() }
    return $Key.Substring(0,1).ToLower() + $Key.Substring(1)
}

# Sensitive field names to filter from details (exact matches only)
# These are fields whose VALUES are secrets and should never appear in JSON
$script:SensitiveDetailFields = @(
    'password', 'secret', 'apikey', 'api_key',
    'authtoken', 'auth_token', 'accesstoken', 'access_token',
    'refreshtoken', 'refresh_token', 'clientsecret', 'client_secret',
    'privatekey', 'private_key', 'bearer', 'credentials',
    # Common single-word credential fields
    'token', 'key'
)

# Fields that are allowed even if they contain sensitive-looking names
# These contain field NAMES not actual secret VALUES
$script:AllowedDetailFields = @(
    'credentialallof', 'credentialanyof', 'skipreason',
    'indexerfound', 'downloadclientfound', 'importlistfound',
    'actions', 'missingtags', 'validatedfiles', 'errorcode',
    'missingenvvars', 'nextstep'
)

<#
.SYNOPSIS
    Checks if a field name is sensitive and should be filtered.
#>
function Test-SensitiveField {
    param([string]$FieldName)
    $lower = $FieldName.ToLower()

    # Allow explicitly safe fields
    if ($lower -in $script:AllowedDetailFields) {
        return $false
    }

    # Check exact matches against sensitive patterns
    if ($lower -in $script:SensitiveDetailFields) {
        return $true
    }

    return $false
}

<#
.SYNOPSIS
    Converts details hashtable keys to lowerCamelCase recursively.
    Filters out sensitive fields that should never appear in JSON.
#>
function ConvertTo-LowerCamelCaseDetails {
    param($Details)

    if ($null -eq $Details) { return @{} }

    $result = [ordered]@{}

    $props = if ($Details -is [hashtable]) {
        $Details.GetEnumerator()
    } elseif ($Details -is [PSCustomObject]) {
        $Details.PSObject.Properties | ForEach-Object { [PSCustomObject]@{ Key = $_.Name; Value = $_.Value } }
    } else {
        return $Details
    }

    foreach ($prop in $props) {
        $key = if ($prop.Key) { $prop.Key } else { $prop.Name }
        $value = if ($prop.PSObject.Properties.Name -contains 'Value') { $prop.Value } else { $prop.Value }

        # Skip sensitive fields entirely (don't include in output)
        if (Test-SensitiveField -FieldName $key) {
            continue
        }

        $newKey = ConvertTo-LowerCamelCase -Key $key

        if ($value -is [hashtable] -or $value -is [PSCustomObject]) {
            $result[$newKey] = ConvertTo-LowerCamelCaseDetails -Details $value
        } elseif ($value -is [array]) {
            $result[$newKey] = @($value | ForEach-Object {
                if ($_ -is [string] -or $_ -is [int] -or $_ -is [bool] -or $_ -is [double] -or $null -eq $_) {
                    $_  # Primitives pass through unchanged
                } elseif ($_ -is [hashtable] -or $_ -is [PSCustomObject]) {
                    ConvertTo-LowerCamelCaseDetails -Details $_
                } else {
                    $_
                }
            })
        } else {
            $result[$newKey] = $value
        }
    }

    return $result
}

<#
.SYNOPSIS
    Redacts sensitive values from runner args.
#>
function Get-RedactedArgs {
    param([string[]]$InputArgs)

    if ($null -eq $InputArgs -or $InputArgs.Count -eq 0) { return @() }

    $sensitiveParams = @(
        '-ApiKey', '-LidarrApiKey', '-Token', '-Password', '-Secret',
        '-AuthToken', '-AccessToken', '-RefreshToken', '-ClientSecret'
    )

    $result = @()
    $skipNext = $false

    for ($i = 0; $i -lt $InputArgs.Count; $i++) {
        if ($skipNext) {
            $result += '[REDACTED]'
            $skipNext = $false
            continue
        }

        $arg = $InputArgs[$i]
        $isSensitive = $false

        foreach ($param in $sensitiveParams) {
            if ($arg -eq $param -or $arg -like "$param=*") {
                $isSensitive = $true
                break
            }
        }

        if ($isSensitive) {
            if ($arg -like "*=*") {
                $parts = $arg -split '=', 2
                $result += "$($parts[0])=[REDACTED]"
            } else {
                $result += $arg
                $skipNext = $true
            }
        } else {
            # Also redact any value that looks like an API key (20-40 hex chars, case-insensitive)
            if ($arg -match '(?i)^[a-f0-9]{20,40}$') {
                $result += '[REDACTED]'
            } else {
                $result += $arg
            }
        }
    }

    return $result
}

# Patterns for secrets that can appear in error messages/URLs
$script:ErrorSanitizationPatterns = @(
    # URL query parameters with secrets
    @{ Pattern = '(?i)([\?&](access_token|api_key|apikey|token|auth|secret|password|bearer|refresh_token|client_secret))=([^\s&"'']+)'; Replace = '$1=[REDACTED]' }
    # Authorization headers in error messages
    @{ Pattern = '(?i)(authorization:\s*(bearer|basic)\s+)([^\s"'']+)'; Replace = '$1[REDACTED]' }
    # Hex strings that look like API keys (20-40 chars) in URLs or messages
    @{ Pattern = '(?i)(?<=[=:/"''])[a-f0-9]{20,40}(?=[&\s"''/?]|$)'; Replace = '[REDACTED]' }
    # Base64-encoded tokens (long alphanumeric with +/=)
    @{ Pattern = '(?i)(?<=[=:/"''])[A-Za-z0-9+/]{40,}={0,2}(?=[&\s"''/?]|$)'; Replace = '[REDACTED]' }
    # Private IPs in URLs
    @{ Pattern = '(?i)(https?://)(192\.168\.\d+\.\d+|10\.\d+\.\d+\.\d+|172\.(1[6-9]|2[0-9]|3[01])\.\d+\.\d+)'; Replace = '$1[PRIVATE-IP]' }
    # JWT tokens (xxx.xxx.xxx format)
    @{ Pattern = '(?i)eyJ[A-Za-z0-9_-]+\.eyJ[A-Za-z0-9_-]+\.[A-Za-z0-9_-]+'; Replace = '[JWT-REDACTED]' }
)

<#
.SYNOPSIS
    Sanitizes error strings to remove secrets before adding to manifest.
.DESCRIPTION
    Scrubs URLs with query params, API keys, tokens, and other secrets from error messages.
    This is the last line of defense against secret leakage in the JSON manifest.
.PARAMETER ErrorString
    The error string to sanitize.
.OUTPUTS
    Sanitized string with secrets replaced by [REDACTED].
#>
function Invoke-ErrorSanitization {
    param(
        [Parameter(ValueFromPipeline)]
        [AllowNull()]
        [AllowEmptyString()]
        [string]$ErrorString
    )

    if ([string]::IsNullOrEmpty($ErrorString)) {
        return $ErrorString
    }

    $result = $ErrorString

    foreach ($pattern in $script:ErrorSanitizationPatterns) {
        $result = $result -replace $pattern.Pattern, $pattern.Replace
    }

    # Also use the diagnostics module's redaction if available
    if (Get-Command 'Invoke-ValueRedaction' -ErrorAction SilentlyContinue) {
        $result = Invoke-ValueRedaction -Value $result
    }

    return $result
}

<#
.SYNOPSIS
    Sanitizes an array of error strings.
#>
function Get-SanitizedErrors {
    param([array]$Errors)

    if ($null -eq $Errors -or $Errors.Count -eq 0) {
        return @()
    }

    return @($Errors | ForEach-Object { Invoke-ErrorSanitization -ErrorString $_ })
}

<#
.SYNOPSIS
    Infers an error code from error messages for CI triage.
.DESCRIPTION
    Matches error text against known patterns to return a structured error code.
    Returns null if no pattern matches.
#>
function Get-ErrorCode {
    param($Result)

    if ($Result.Outcome -ne 'failed') { return $null }

    # Check if ErrorCode is explicitly set in Details
    if ($Result.Details) {
        $explicitCode = $null
        if ($Result.Details -is [hashtable]) {
            $explicitCode = $Result.Details['ErrorCode']
        } elseif ($Result.Details.PSObject.Properties.Name -contains 'ErrorCode') {
            $explicitCode = $Result.Details.ErrorCode
        }
        if ($explicitCode) { return $explicitCode }
    }

    if (Get-Command -Name Get-E2EErrorClassification -ErrorAction SilentlyContinue) {
        $classification = Get-E2EErrorClassification -Messages @($Result.Errors)
        return $classification.errorCode
    }

    return $null
}

<#
.SYNOPSIS
    Extracts outcomeReason from a gate result.
.DESCRIPTION
    Returns a human-readable reason for skip/failure without exposing secrets.
    All returned values are sanitized to prevent secret leakage.
#>
function Get-OutcomeReason {
    param($Result)

    $outcome = $Result.Outcome

    if ($outcome -eq 'success') {
        return $null
    }

    $reason = $null

    # Check for SkipReason in Details
    if ($Result.Details) {
        if ($Result.Details -is [hashtable]) {
            $reason = $Result.Details['SkipReason']
        } elseif ($Result.Details.PSObject.Properties.Name -contains 'SkipReason') {
            $reason = $Result.Details.SkipReason
        }
    }

    # For failures, summarize errors if no skip reason
    if (-not $reason -and $outcome -eq 'failed' -and $Result.Errors -and $Result.Errors.Count -gt 0) {
        $reason = ($Result.Errors | Select-Object -First 1)
    }

    # Default for skipped without reason
    if (-not $reason -and $outcome -eq 'skipped') {
        $reason = "Gate skipped (no reason provided)"
    }

    # CRITICAL: Sanitize the reason to prevent secret leakage
    if ($reason) {
        $reason = Invoke-ErrorSanitization -ErrorString $reason
    }

    return $reason
}

<#
.SYNOPSIS
    Safely gets a property/key value, returning null if property doesn't exist.
    Works with strict mode enabled. Handles both PSObjects and Hashtables.
#>
function Get-SafeProperty {
    param($Object, [string]$PropertyName)
    if ($null -eq $Object) { return $null }
    # Handle dictionaries (hashtables, OrderedDictionary, etc.)
    if ($Object -is [System.Collections.IDictionary]) {
        if ($Object.Contains($PropertyName)) {
            return $Object[$PropertyName]
        }
        return $null
    }
    # Handle PSObjects
    if ($Object.PSObject.Properties.Name -contains $PropertyName) {
        return $Object.$PropertyName
    }
    return $null
}

<#
.SYNOPSIS
    Transforms details.resolution + details.componentIds into details.componentResolution.
.DESCRIPTION
    Produces a structured componentResolution block with proper camelCase keys,
    selectedId, strategy, candidateIds, and safeToPersist for each component type.

    safeToPersist rules:
    - true: preferredId, created, implementationName, implementation
    - false: fuzzy, ambiguous*, none, updated (action outcome, not selection strategy)
#>
function Get-ComponentResolution {
    param([System.Collections.IDictionary]$Details)

    if (-not $Details) { return $null }

    $resolution = Get-SafeProperty -Object $Details -PropertyName 'resolution'
    $componentIds = Get-SafeProperty -Object $Details -PropertyName 'componentIds'

    if (-not $resolution -and -not $componentIds) { return $null }

    # Canonical strategies (lowercase for case-insensitive matching)
    # Safe to persist: deterministic, unambiguous selection
    # Note: 'updated' is an action outcome, not a selection strategy - excluded intentionally
    $safeStrategies = @('preferredid', 'created', 'implementationname', 'implementation')

    # matchedOn enum: normalized enum derived from strategy
    # Values: preferredId, implementationName, implementation, created, none
    $matchedOnEnumMap = @{
        'preferredid' = 'preferredId'
        'implementationname' = 'implementationName'
        'implementation' = 'implementation'
        'created' = 'created'
    }

    # Helper: coerce value to integer if possible (type stability)
    function CoerceToInt($value) {
        if ($null -eq $value) { return $null }
        try { return [int]$value } catch { return $value }
    }

    # Helper: normalize strategy to canonical form
    function NormalizeStrategy($raw) {
        if ([string]::IsNullOrWhiteSpace($raw)) { return 'none' }
        return $raw.ToString().Trim()
    }

    # Helper: derive matchedOn enum from strategy
    # Returns lowerCamelCase enum value: preferredId, implementationName, implementation, created, none
    function DeriveMatchedOn($strategy, $selectedId) {
        # If no selectedId, always return 'none'
        if ($null -eq $selectedId) { return 'none' }

        # If strategy starts with 'ambiguous', return 'none'
        if ($strategy -match '(?i)^ambiguous') { return 'none' }

        # Normalize to lowercase for lookup
        $strategyLower = $strategy.ToLowerInvariant()

        # Map to canonical enum value if it's a known safe strategy
        if ($matchedOnEnumMap.ContainsKey($strategyLower)) {
            return $matchedOnEnumMap[$strategyLower]
        }

        # Unknown strategy (including 'updated', 'fuzzy', 'none', etc.) → 'none'
        return 'none'
    }

    $result = [ordered]@{}

    # Component types to process (output key names in camelCase)
    $componentTypes = @('indexer', 'downloadClient', 'importList')

    # Process each component type
    foreach ($outputKey in $componentTypes) {
        # Determine lowercase variant
        $lowercaseKey = switch ($outputKey) {
            'downloadClient' { 'downloadclient' }
            'importList' { 'importlist' }
            default { $null }
        }

        # Try both camelCase and lowercase variants when looking up strategy
        $rawStrategy = $null
        if ($resolution) {
            $rawStrategy = Get-SafeProperty -Object $resolution -PropertyName $outputKey
            # Try lowercase variant if not found
            if (-not $rawStrategy -and $lowercaseKey) {
                $rawStrategy = Get-SafeProperty -Object $resolution -PropertyName $lowercaseKey
            }
        }
        $strategy = NormalizeStrategy $rawStrategy

        # Get selectedId and candidateIds from componentIds
        $selectedId = $null
        $candidateIds = @()

        if ($componentIds) {
            # Try camelCase ID key first (e.g., indexerId, downloadClientId)
            $idKey = "${outputKey}Id"
            $rawId = Get-SafeProperty -Object $componentIds -PropertyName $idKey
            $selectedId = CoerceToInt $rawId

            # Try candidate IDs with camelCase
            $candidateKey = "${outputKey}CandidateIds"
            $candidates = Get-SafeProperty -Object $componentIds -PropertyName $candidateKey
            if ($candidates) {
                $candidateIds = @($candidates | ForEach-Object { CoerceToInt $_ })
            }
        }

        # Only emit if we have at least a strategy or selectedId or candidates
        if ($strategy -ne 'none' -or $null -ne $selectedId -or $candidateIds.Count -gt 0) {
            # Case-insensitive check against canonical safe strategies
            $strategyLower = $strategy.ToLowerInvariant()
            $isSafe = $safeStrategies -contains $strategyLower
            $isAmbiguous = $strategy -match '(?i)^ambiguous'

              # Default candidateIds to [selectedId] when known (improves forensic value)
              if ($candidateIds.Count -eq 0 -and $null -ne $selectedId) {
                  $candidateIds = @($selectedId)
              }

              # safeToPersist must be conservative:
              # - requires a selectedId
              # - requires at most 1 candidate (otherwise ambiguity leaked through)
              # - if a candidate exists, it must match selectedId
              $hasSelectedId = ($null -ne $selectedId)
              $hasAtMostOneCandidate = ($candidateIds.Count -le 1)
              $candidateMatchesSelected = ($candidateIds.Count -eq 0) -or
                  ($hasSelectedId -and $candidateIds.Count -eq 1 -and $candidateIds[0] -eq $selectedId)

              # Derive matchedOn enum value
              $matchedOn = DeriveMatchedOn -strategy $strategy -selectedId $selectedId

              $result[$outputKey] = [ordered]@{
                  selectedId = $selectedId
                  strategy = $strategy
                  matchedOn = $matchedOn
                  candidateIds = $candidateIds
                  safeToPersist = ($isSafe -and -not $isAmbiguous -and $hasSelectedId -and $hasAtMostOneCandidate -and $candidateMatchesSelected)
              }
          }
      }

    if ($result.Count -eq 0) { return $null }
    return $result
}

<#
.SYNOPSIS
    Calculates duration in milliseconds from StartTime/EndTime.
#>
function Get-DurationMs {
    param($Result)

    $startTime = Get-SafeProperty -Object $Result -PropertyName 'StartTime'
    $endTime = Get-SafeProperty -Object $Result -PropertyName 'EndTime'
    if ($startTime -and $endTime) {
        $duration = ($endTime - $startTime).TotalMilliseconds
        return [int][Math]::Max(0, $duration)
    }

    return 0
}

<#
.SYNOPSIS
    Formats a DateTime to ISO 8601 string or returns null.
#>
function Format-Timestamp {
    param($DateTime)
    if ($null -eq $DateTime) { return $null }
    if ($DateTime -is [DateTime]) {
        return $DateTime.ToUniversalTime().ToString('o')
    }
    return $null
}

<#
.SYNOPSIS
    Converts gate results and context to JSON run manifest.
.DESCRIPTION
    Generates a machine-readable JSON manifest following schema v1.2.
    All sensitive data is redacted before serialization.
.PARAMETER Results
    Array of gate result objects from the test run.
.PARAMETER Context
    Hashtable containing run context (LidarrUrl, ContainerName, etc.)
.OUTPUTS
    JSON string conforming to schema richer-tunes.lidarr.e2e-run-manifest v1.2
#>
function ConvertTo-E2ERunManifest {
    param(
        [Parameter(Mandatory)]
        [array]$Results,

        [Parameter(Mandatory)]
        [hashtable]$Context
    )

    # Redact URL if it contains private IPs
    $lidarrUrl = Get-SafeProperty -Object $Context -PropertyName 'LidarrUrl'
    $redactedUrl = $lidarrUrl
    if ($lidarrUrl -and (Get-Command 'Invoke-ValueRedaction' -ErrorAction SilentlyContinue)) {
        $redactedUrl = Invoke-ValueRedaction -Value $lidarrUrl
    }

    # Get sensitive patterns count
    $patternsCount = 22  # Default
    if (Get-Variable 'SensitivePatterns' -Scope Script -ErrorAction SilentlyContinue) {
        $patternsCount = $script:SensitivePatterns.Count
    }

    # Build results array with lowerCamelCase details
    $jsonResults = @()
    foreach ($result in $Results) {
        $details = ConvertTo-LowerCamelCaseDetails -Details $result.Details

        # Remove skipReason from details (it goes to outcomeReason)
        if ($details.Contains('skipReason')) {
            $details.Remove('skipReason')
        }
        # Remove errorCode from details (it is a top-level field now)
        if ($details.Contains('errorCode')) {
            $details.Remove('errorCode')
        }

        # Add componentResolution from resolution + componentIds
        # Pass the converted details since we need the camelCase keys
        $componentResolution = Get-ComponentResolution -Details $details
        if ($componentResolution) {
            $details['componentResolution'] = $componentResolution
        }

        # Remove legacy persistedIdsUpdated from details if present (moved to componentIds block)
        if ($details.Contains('persistedIdsUpdated')) {
            $details.Remove('persistedIdsUpdated')
        }

        $jsonResults += [ordered]@{
            gate = $result.Gate
            plugin = $result.PluginName
            outcome = $result.Outcome
            errorCode = (Get-ErrorCode -Result $result)
            outcomeReason = (Get-OutcomeReason -Result $result)
            startedAt = (Format-Timestamp -DateTime (Get-SafeProperty -Object $result -PropertyName 'StartTime'))
            endedAt = (Format-Timestamp -DateTime (Get-SafeProperty -Object $result -PropertyName 'EndTime'))
            durationMs = (Get-DurationMs -Result $result)
            errors = @(Get-SanitizedErrors -Errors $result.Errors)
            details = $details
        }
    }

    # Calculate summary
    $passed = @($Results | Where-Object { $_.Outcome -eq 'success' }).Count
    $failed = @($Results | Where-Object { $_.Outcome -eq 'failed' }).Count
    $skipped = @($Results | Where-Object { $_.Outcome -eq 'skipped' }).Count
    $totalDuration = 0
    foreach ($jr in $jsonResults) {
        $totalDuration += $jr['durationMs']
    }

    # Build sources block (v1.2) with provenance tracking
    # source values: git (from checkout), env (from environment var), unknown (not available)
    $prov = Get-SafeProperty -Object $Context -PropertyName 'SourceProvenance'
    $sourceShas = Get-SafeProperty -Object $Context -PropertyName 'SourceShas'

    $commonSha = Get-SafeProperty -Object $sourceShas -PropertyName 'Common'
    $qobuzarrSha = Get-SafeProperty -Object $sourceShas -PropertyName 'Qobuzarr'
    $tidalarrSha = Get-SafeProperty -Object $sourceShas -PropertyName 'Tidalarr'
    $brainarrSha = Get-SafeProperty -Object $sourceShas -PropertyName 'Brainarr'

    $sources = [ordered]@{
        common = [ordered]@{
            sha = if ($commonSha) { $commonSha } else { (Get-RunnerVersion) }
            source = if ($prov -and (Get-SafeProperty $prov 'Common')) { (Get-SafeProperty $prov 'Common') } elseif ($commonSha) { 'git' } else { 'git' }
        }
        qobuzarr = [ordered]@{
            sha = $qobuzarrSha
            source = if ($prov -and (Get-SafeProperty $prov 'Qobuzarr')) { (Get-SafeProperty $prov 'Qobuzarr') } elseif ($qobuzarrSha) { 'git' } else { 'unknown' }
        }
        tidalarr = [ordered]@{
            sha = $tidalarrSha
            source = if ($prov -and (Get-SafeProperty $prov 'Tidalarr')) { (Get-SafeProperty $prov 'Tidalarr') } elseif ($tidalarrSha) { 'git' } else { 'unknown' }
        }
        brainarr = [ordered]@{
            sha = $brainarrSha
            source = if ($prov -and (Get-SafeProperty $prov 'Brainarr')) { (Get-SafeProperty $prov 'Brainarr') } elseif ($brainarrSha) { 'git' } else { 'unknown' }
        }
    }

    # Build manifest - use Get-SafeProperty for all Context accesses (strict mode compatible)
    $runnerArgs = Get-SafeProperty -Object $Context -PropertyName 'RunnerArgs'
    $containerName = Get-SafeProperty -Object $Context -PropertyName 'ContainerName'
    $containerId = Get-SafeProperty -Object $Context -PropertyName 'ContainerId'
    $containerStartedAt = Get-SafeProperty -Object $Context -PropertyName 'ContainerStartedAt'
    $imageTag = Get-SafeProperty -Object $Context -PropertyName 'ImageTag'
    $imageId = Get-SafeProperty -Object $Context -PropertyName 'ImageId'
    $imageDigest = Get-SafeProperty -Object $Context -PropertyName 'ImageDigest'
    $lidarrVersion = Get-SafeProperty -Object $Context -PropertyName 'LidarrVersion'
    $lidarrBranch = Get-SafeProperty -Object $Context -PropertyName 'LidarrBranch'
    $requestedGate = Get-SafeProperty -Object $Context -PropertyName 'RequestedGate'
    $plugins = Get-SafeProperty -Object $Context -PropertyName 'Plugins'
    $effectiveGates = Get-SafeProperty -Object $Context -PropertyName 'EffectiveGates'
    $effectivePlugins = Get-SafeProperty -Object $Context -PropertyName 'EffectivePlugins'

    # CRITICAL: Ensure effectiveGates/effectivePlugins are always arrays (prevents JSON scalar unwrapping)
    # PowerShell can auto-unwrap single-element arrays in hashtables during ConvertTo-Json
    if ($null -eq $effectiveGates) {
        $effectiveGates = @()
    } elseif ($effectiveGates -isnot [array]) {
        $effectiveGates = @($effectiveGates)
    }
    if ($null -eq $effectivePlugins) {
        $effectivePlugins = @()
    } elseif ($effectivePlugins -isnot [array]) {
        $effectivePlugins = @($effectivePlugins)
    }
    $stopReason = Get-SafeProperty -Object $Context -PropertyName 'StopReason'
    $redactionSelfTestExecuted = Get-SafeProperty -Object $Context -PropertyName 'RedactionSelfTestExecuted'
    $redactionSelfTestPassed = Get-SafeProperty -Object $Context -PropertyName 'RedactionSelfTestPassed'
    $diagBundlePath = Get-SafeProperty -Object $Context -PropertyName 'DiagnosticsBundlePath'
    $diagIncludedFiles = Get-SafeProperty -Object $Context -PropertyName 'DiagnosticsIncludedFiles'

    $manifest = [ordered]@{
        '$schema' = (Get-SchemaUrl)
        schemaVersion = "1.2"
        schemaId = "richer-tunes.lidarr.e2e-run-manifest"
        timestamp = (Get-Date).ToUniversalTime().ToString('o')
        runId = ([Guid]::NewGuid()).ToString('N').Substring(0, 12)
        runner = [ordered]@{
            name = "lidarr.plugin.common:e2e-runner.ps1"
            version = (Get-RunnerVersion)
            args = if ($runnerArgs) { @(Get-RedactedArgs -InputArgs $runnerArgs) } else { @() }
        }
        sources = $sources
        lidarr = [ordered]@{
            url = $redactedUrl
            containerName = $containerName
            containerId = $containerId
            startedAt = (Format-Timestamp -DateTime $containerStartedAt)
            imageTag = $imageTag
            imageId = $imageId
            imageDigest = $imageDigest
            version = $lidarrVersion
            branch = $lidarrBranch
            # Host override info (for multi-plugin ALC bug workaround)
            hostOverride = [ordered]@{
                used = ($env:E2E_HOST_OVERRIDE_MOUNTED -eq 'true')
                reason = if ($env:HOST_OVERRIDE_REASON) { $env:HOST_OVERRIDE_REASON } else { $null }
                sourceRepo = if ($env:INPUT_LIDARR_OVERRIDE_REPO) { $env:INPUT_LIDARR_OVERRIDE_REPO } else { $null }
                sourceRef = if ($env:INPUT_LIDARR_OVERRIDE_REF) { $env:INPUT_LIDARR_OVERRIDE_REF } else { $null }
                sha = if ($env:HOST_OVERRIDE_SHA) { $env:HOST_OVERRIDE_SHA } else { $null }
                dllSha256 = if ($env:HOST_OVERRIDE_DLL_SHA256) { $env:HOST_OVERRIDE_DLL_SHA256 } else { $null }
            }
        }
        # Token seeding status (for pre-authorized CI tokens)
        tokenSeeding = [ordered]@{
            tidal = [ordered]@{
                # Status: deployed, skipped, failed, or null (not applicable)
                status = if ($env:TIDAL_TOKEN_SEED_STATUS) { $env:TIDAL_TOKEN_SEED_STATUS } else { $null }
                # Reason: no_secret_provided, invalid_base64, invalid_json, file_exists, or null
                reason = if ($env:TIDAL_TOKEN_SEED_REASON) { $env:TIDAL_TOKEN_SEED_REASON } else { $null }
                # Path is redacted (just indicates whether seeding wrote a file)
                deployed = ($env:TIDALARR_SEEDED_TOKENS_PATH -ne $null -and $env:TIDALARR_SEEDED_TOKENS_PATH -ne '')
            }
        }
        request = [ordered]@{
            gate = $requestedGate
            plugins = if ($plugins) { @($plugins) } else { @() }
        }
        effective = [ordered]@{
            # CRITICAL: Cast to Object[] prevents ConvertTo-Json from unwrapping single-element arrays
            gates = [object[]]$effectiveGates
            plugins = [object[]]$effectivePlugins
            stopReason = $stopReason
        }
        redaction = [ordered]@{
            selfTestExecuted = [bool]$redactionSelfTestExecuted
            selfTestPassed = [bool]$redactionSelfTestPassed
            patternsCount = $patternsCount
        }
        results = $jsonResults
        summary = [ordered]@{
            overallSuccess = ($failed -eq 0)
            totalGates = $Results.Count
            passed = $passed
            failed = $failed
            skipped = $skipped
            totalDurationMs = [int]$totalDuration
        }
        diagnostics = [ordered]@{
            bundlePath = $diagBundlePath
            bundleCreated = ($null -ne $diagBundlePath)
            includedFiles = if ($diagIncludedFiles) { @($diagIncludedFiles) } else { @() }
            redactionApplied = $true
            redactionSelfTestPassed = [bool]$redactionSelfTestPassed
        }
    }

    # Assembly issue detection: scan all errors for loading failures with tiered classification
    # Always include hostBugSuspected for schema consistency (detected: false when clean)
    $allErrors = @()
    foreach ($r in $Results) {
        if ($r.Errors) { $allErrors += $r.Errors }
    }
    $logContent = if ($Context.ContainsKey('ContainerLogContent')) { $Context.ContainerLogContent } else { $null }
    $alcDetection = Test-ALCPattern -Errors $allErrors -LogContent $logContent

    if ($alcDetection.detected) {
        # Build description based on classification
        $description = switch ($alcDetection.classification) {
            'ALC'              { "True Assembly Load Context bug - host/runtime issue, not plugin code" }
            'ABI_MISMATCH'     { "Plugin ABI mismatch - plugin may need rebuild against current host version" }
            'DEPENDENCY_DRIFT' { "Dependency version conflict - check assembly binding redirects" }
            'LOAD_FAILURE'     { "Assembly load failure - investigate assembly resolution" }
            'TYPE_INIT_FAILURE' { "Type initialization failure - check static constructors and config" }
            default            { "Assembly loading issue detected" }
        }
        $manifest['hostBugSuspected'] = [ordered]@{
            detected = $true
            classification = $alcDetection.classification
            severity = $alcDetection.severity
            matchedLine = $alcDetection.matchedLine
            description = $description
        }
    } else {
        # Include quiet stub when no issues detected (schema consistency, no summary noise)
        $manifest['hostBugSuspected'] = [ordered]@{
            detected = $false
        }
    }

    # Component IDs provenance tracking (optional - only if context provides it)
    $componentIdsContext = Get-SafeProperty -Object $Context -PropertyName 'ComponentIds'
    if ($componentIdsContext) {
        $instanceKey = Get-SafeProperty -Object $componentIdsContext -PropertyName 'InstanceKey'
        $instanceKeySource = Get-SafeProperty -Object $componentIdsContext -PropertyName 'InstanceKeySource'
        $lockPolicy = Get-SafeProperty -Object $componentIdsContext -PropertyName 'LockPolicy'
        $lockPolicySource = Get-SafeProperty -Object $componentIdsContext -PropertyName 'LockPolicySource'
        $persistenceEnabled = Get-SafeProperty -Object $componentIdsContext -PropertyName 'PersistenceEnabled'

        $componentIdsBlock = [ordered]@{}

        if ($instanceKey) {
            $componentIdsBlock['instanceKey'] = $instanceKey
        }
        if ($instanceKeySource) {
            $componentIdsBlock['instanceKeySource'] = $instanceKeySource
        }
        if ($lockPolicy) {
            $componentIdsBlock['lockPolicy'] = [ordered]@{
                timeoutMs = Get-SafeProperty -Object $lockPolicy -PropertyName 'TimeoutMs'
                retryDelayMs = Get-SafeProperty -Object $lockPolicy -PropertyName 'RetryDelayMs'
                staleSeconds = Get-SafeProperty -Object $lockPolicy -PropertyName 'StaleSeconds'
            }
        }
        if ($lockPolicySource) {
            $componentIdsBlock['lockPolicySource'] = $lockPolicySource
        }
        if ($null -ne $persistenceEnabled) {
            $componentIdsBlock['persistenceEnabled'] = [bool]$persistenceEnabled
        }

        # Add factual persistence outcome fields from ComponentIdsPersistence
        # These reflect what ACTUALLY happened, not what was eligible to happen
        $persistenceResult = Get-SafeProperty -Object $Context -PropertyName 'ComponentIdsPersistence'

        # Defaults when no persistence result provided
        $eligible = $false
        $attempted = $false
        $updated = $false
        $reason = "unknown"
        $hasExplicitResult = $false

        if ($persistenceResult) {
            $hasExplicitResult = $true
            $eligible = [bool](Get-SafeProperty -Object $persistenceResult -PropertyName 'Eligible')
            $attempted = [bool](Get-SafeProperty -Object $persistenceResult -PropertyName 'Attempted')
            $updated = [bool](Get-SafeProperty -Object $persistenceResult -PropertyName 'Wrote')
            $reason = (Get-SafeProperty -Object $persistenceResult -PropertyName 'Reason')
            if (-not $reason) { $reason = "unknown" }
        }

        # Apply invariants:
        # 1. If persistence disabled → attempted=false, updated=false, reason="disabled"
        if ($persistenceEnabled -eq $false) {
            $attempted = $false
            $updated = $false
            $reason = "disabled"
        }
        # 2. If not eligible AND we have explicit result → attempted=false, updated=false, reason="not_eligible"
        #    (only override if we got an actual result saying not eligible; missing result stays "unknown")
        elseif ($hasExplicitResult -and -not $eligible) {
            $attempted = $false
            $updated = $false
            $reason = "not_eligible"
        }
        # 3. If updated=true → attempted must also be true (invariant)
        if ($updated) {
            $attempted = $true
        }

        $componentIdsBlock['persistenceEligible'] = $eligible
        $componentIdsBlock['persistedIdsUpdateAttempted'] = $attempted
        $componentIdsBlock['persistedIdsUpdated'] = $updated
        $componentIdsBlock['persistedIdsUpdateReason'] = $reason

        if ($componentIdsBlock.Count -gt 0) {
            $manifest['componentIds'] = $componentIdsBlock
        }
    }

    return $manifest | ConvertTo-Json -Depth 10
}

<#
.SYNOPSIS
    Writes the run manifest to a file.
.PARAMETER Results
    Array of gate result objects.
.PARAMETER Context
    Run context hashtable.
.PARAMETER OutputPath
    Path to write the JSON file.
#>
function Write-E2ERunManifest {
    param(
        [Parameter(Mandatory)]
        [array]$Results,

        [Parameter(Mandatory)]
        [hashtable]$Context,

        [Parameter(Mandatory)]
        [string]$OutputPath
    )

    $json = ConvertTo-E2ERunManifest -Results $Results -Context $Context

    # Ensure directory exists
    $dir = Split-Path $OutputPath -Parent
    if ($dir -and -not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    $json | Out-File -FilePath $OutputPath -Encoding UTF8 -Force

    Write-Host "Run manifest written to: $OutputPath" -ForegroundColor Green

    return $OutputPath
}

Export-ModuleMember -Function ConvertTo-E2ERunManifest, Write-E2ERunManifest, Get-RunnerVersion, Get-RepoSha, Invoke-ErrorSanitization, Test-ALCPattern
