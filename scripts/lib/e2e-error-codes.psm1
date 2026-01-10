$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

<#
.SYNOPSIS
    Canonical E2E error code definitions - single source of truth.
.DESCRIPTION
    This module is the AUTHORITATIVE list of E2E error codes that may be
    emitted into run-manifest.json. All gates, classifiers, docs, and tests
    should reference this module rather than hardcoding error codes.

    IMPORTANT:
    - When adding a new error code, add it here FIRST
    - Update docs/E2E_ERROR_CODES.md with matching row
    - Run Test-ErrorCodeDocSync.ps1 to verify sync
#>

# Canonical error code definitions
# Format: Code => @{ Description; Severity; Category }
$script:E2EErrorCodes = [ordered]@{
    # ============================================================================
    # Authentication / Credential Errors
    # ============================================================================
    'E2E_AUTH_MISSING' = @{
        Description = 'Required credentials are missing for the requested gate'
        Severity = 'blocking'
        Category = 'credential'
    }

    # ============================================================================
    # Configuration Errors
    # ============================================================================
    'E2E_CONFIG_INVALID' = @{
        Description = 'Configuration exists but fails validation (server-side reject)'
        Severity = 'blocking'
        Category = 'config'
    }

    # ============================================================================
    # Infrastructure Errors
    # ============================================================================
    'E2E_API_TIMEOUT' = @{
        Description = 'A Lidarr API call or polling loop timed out'
        Severity = 'transient'
        Category = 'infra'
    }

    'E2E_LIDARR_UNREACHABLE' = @{
        Description = 'Lidarr API is unreachable (transport failure)'
        Severity = 'blocking'
        Category = 'infra'
    }

    'E2E_DOCKER_UNAVAILABLE' = @{
        Description = 'Docker interaction required but not available'
        Severity = 'blocking'
        Category = 'infra'
    }

    # ============================================================================
    # Plugin Discovery / Loading Errors
    # ============================================================================
    'E2E_SCHEMA_MISSING_IMPLEMENTATION' = @{
        Description = 'Schema endpoint accessible but plugin implementation not found'
        Severity = 'blocking'
        Category = 'plugin'
        # Note: Use details.discoveryDiagnosis to distinguish root cause
    }

    'E2E_HOST_PLUGIN_DISCOVERY_DISABLED' = @{
        Description = 'Host has plugin discovery disabled (confirmed via host capabilities)'
        Severity = 'blocking'
        Category = 'host'
        # Note: Only emit when there is AFFIRMATIVE evidence from host capabilities
    }

    'E2E_LOAD_FAILURE' = @{
        Description = 'Plugin failed to load during schema discovery'
        Severity = 'blocking'
        Category = 'plugin'
    }

    'E2E_INTERNAL_ERROR' = @{
        Description = 'Internal runner error (should not occur in normal operation)'
        Severity = 'blocking'
        Category = 'runner'
        # Note: Used for edge cases like hash computation failure
        # Reason field should be enum-ish (e.g., ExpectedModelHashComputationFailed)
    }

    'E2E_ABSTRACTIONS_SHA_MISMATCH' = @{
        Description = 'Plugins ship non-identical Lidarr.Plugin.Abstractions.dll bytes'
        Severity = 'blocking'
        Category = 'packaging'
    }

    # ============================================================================
    # Search / Indexer Errors
    # ============================================================================
    'E2E_NO_RELEASES_ATTRIBUTED' = @{
        Description = 'AlbumSearch returned releases but none attributed to the target plugin'
        Severity = 'blocking'
        Category = 'search'
    }

    # ============================================================================
    # Download / Queue Errors
    # ============================================================================
    'E2E_QUEUE_NOT_FOUND' = @{
        Description = 'Grab triggered but expected queue item could not be correlated'
        Severity = 'blocking'
        Category = 'download'
    }

    'E2E_ZERO_AUDIO_FILES' = @{
        Description = 'Download completed but produced zero validated audio files'
        Severity = 'blocking'
        Category = 'download'
    }

    # ============================================================================
    # Metadata Errors
    # ============================================================================
    'E2E_METADATA_MISSING' = @{
        Description = 'Audio file(s) exist but required tags are missing'
        Severity = 'blocking'
        Category = 'metadata'
    }

    # ============================================================================
    # Import List Errors
    # ============================================================================
    'E2E_IMPORT_FAILED' = @{
        Description = 'ImportListSync completed with errors or post-sync state indicates failure'
        Severity = 'blocking'
        Category = 'import'
    }

    # ============================================================================
    # Component Resolution Errors
    # ============================================================================
    'E2E_COMPONENT_AMBIGUOUS' = @{
        Description = 'Multiple components match selection criteria for a plugin'
        Severity = 'blocking'
        Category = 'config'
    }

    # ============================================================================
    # External Provider Errors
    # ============================================================================
    'E2E_PROVIDER_UNAVAILABLE' = @{
        Description = 'Expected external provider (e.g., LLM model) not found'
        Severity = 'blocking'
        Category = 'external'
    }
}

function Get-E2EErrorCodes {
    <#
    .SYNOPSIS
        Returns the canonical list of E2E error codes.
    .OUTPUTS
        String[] - Array of error code names
    #>
    return $script:E2EErrorCodes.Keys
}

function Get-E2EErrorCodeDefinition {
    <#
    .SYNOPSIS
        Returns the definition for a specific error code.
    .PARAMETER Code
        The error code to look up
    .OUTPUTS
        Hashtable with Description, Severity, Category
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Code
    )

    if ($script:E2EErrorCodes.Contains($Code)) {
        return $script:E2EErrorCodes[$Code]
    }
    return $null
}

function Test-E2EErrorCodeValid {
    <#
    .SYNOPSIS
        Validates that an error code is in the canonical list.
    .PARAMETER Code
        The error code to validate
    .OUTPUTS
        Boolean - true if code is valid
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Code
    )

    return $script:E2EErrorCodes.Contains($Code)
}

function Get-E2EErrorCodesByCategory {
    <#
    .SYNOPSIS
        Returns error codes filtered by category.
    .PARAMETER Category
        The category to filter by (credential, config, infra, plugin, host, search, download, metadata, import, external)
    .OUTPUTS
        String[] - Array of error code names in that category
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Category
    )

    return $script:E2EErrorCodes.Keys | Where-Object {
        $script:E2EErrorCodes[$_].Category -eq $Category
    }
}

Export-ModuleMember -Function Get-E2EErrorCodes, Get-E2EErrorCodeDefinition, Test-E2EErrorCodeValid, Get-E2EErrorCodesByCategory