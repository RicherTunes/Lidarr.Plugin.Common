# E2E JSON Output Module
# Generates machine-readable run manifests for CI parsing
# Schema ID: richer-tunes.lidarr.e2e-run-manifest
# Schema Version: 1.1

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Import diagnostics module for redaction
$diagnosticsPath = Join-Path $PSScriptRoot "e2e-diagnostics.psm1"
if (Test-Path $diagnosticsPath) {
    Import-Module $diagnosticsPath -Force
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
    'actions', 'missingtags', 'validatedfiles'
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

<#
.SYNOPSIS
    Extracts outcomeReason from a gate result.
.DESCRIPTION
    Returns a human-readable reason for skip/failure without exposing secrets.
#>
function Get-OutcomeReason {
    param($Result)

    $outcome = $Result.Outcome

    if ($outcome -eq 'success') {
        return $null
    }

    # Check for SkipReason in Details
    $skipReason = $null
    if ($Result.Details) {
        if ($Result.Details -is [hashtable]) {
            $skipReason = $Result.Details['SkipReason']
        } elseif ($Result.Details.PSObject.Properties.Name -contains 'SkipReason') {
            $skipReason = $Result.Details.SkipReason
        }
    }

    if ($skipReason) {
        return $skipReason
    }

    # For failures, summarize errors
    if ($outcome -eq 'failed' -and $Result.Errors -and $Result.Errors.Count -gt 0) {
        return ($Result.Errors | Select-Object -First 1)
    }

    if ($outcome -eq 'skipped') {
        return "Gate skipped (no reason provided)"
    }

    return $null
}

<#
.SYNOPSIS
    Calculates duration in milliseconds from StartTime/EndTime.
#>
function Get-DurationMs {
    param($Result)

    if ($Result.StartTime -and $Result.EndTime) {
        $duration = ($Result.EndTime - $Result.StartTime).TotalMilliseconds
        return [int][Math]::Max(0, $duration)
    }

    return 0
}

<#
.SYNOPSIS
    Converts gate results and context to JSON run manifest.
.DESCRIPTION
    Generates a machine-readable JSON manifest following schema v1.1.
    All sensitive data is redacted before serialization.
.PARAMETER Results
    Array of gate result objects from the test run.
.PARAMETER Context
    Hashtable containing run context (LidarrUrl, ContainerName, etc.)
.OUTPUTS
    JSON string conforming to schema richer-tunes.lidarr.e2e-run-manifest v1.1
#>
function ConvertTo-E2ERunManifest {
    param(
        [Parameter(Mandatory)]
        [array]$Results,

        [Parameter(Mandatory)]
        [hashtable]$Context
    )

    # Redact URL if it contains private IPs
    $redactedUrl = $Context.LidarrUrl
    if (Get-Command 'Invoke-ValueRedaction' -ErrorAction SilentlyContinue) {
        $redactedUrl = Invoke-ValueRedaction -Value $Context.LidarrUrl
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

        $jsonResults += [ordered]@{
            gate = $result.Gate
            plugin = $result.PluginName
            outcome = $result.Outcome
            outcomeReason = (Get-OutcomeReason -Result $result)
            durationMs = (Get-DurationMs -Result $result)
            errors = @($result.Errors)
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

    # Build manifest
    $manifest = [ordered]@{
        schemaVersion = "1.1"
        schemaId = "richer-tunes.lidarr.e2e-run-manifest"
        timestamp = (Get-Date).ToUniversalTime().ToString('o')
        runId = ([Guid]::NewGuid()).ToString('N').Substring(0, 12)
        runner = [ordered]@{
            name = "lidarr.plugin.common:e2e-runner.ps1"
            version = (Get-RunnerVersion)
            args = @(Get-RedactedArgs -InputArgs $Context.RunnerArgs)
        }
        lidarr = [ordered]@{
            url = $redactedUrl
            containerName = $Context.ContainerName
            imageTag = $Context.ImageTag
            imageDigest = $Context.ImageDigest
            version = $Context.LidarrVersion
            branch = $Context.LidarrBranch
        }
        request = [ordered]@{
            gate = $Context.RequestedGate
            plugins = @($Context.Plugins)
        }
        effective = [ordered]@{
            gates = @($Context.EffectiveGates)
            plugins = @($Context.EffectivePlugins)
        }
        redaction = [ordered]@{
            selfTestExecuted = [bool]$Context.RedactionSelfTestExecuted
            selfTestPassed = [bool]$Context.RedactionSelfTestPassed
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
            bundlePath = $Context.DiagnosticsBundlePath
            bundleCreated = ($null -ne $Context.DiagnosticsBundlePath)
            redactionApplied = $true
            redactionSelfTestPassed = [bool]$Context.RedactionSelfTestPassed
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

Export-ModuleMember -Function ConvertTo-E2ERunManifest, Write-E2ERunManifest, Get-RunnerVersion
