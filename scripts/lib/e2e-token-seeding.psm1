# E2E Token Seeding Module
# Handles seeded token files for CI without manual OAuth

<#
.SYNOPSIS
    Writes a seeded token file from base64-encoded content.
.DESCRIPTION
    Decodes base64 content and writes to the specified path.
    Handles existing files based on Force parameter.
    Never echoes decoded content - only reports byte length.
.PARAMETER Base64Content
    The base64-encoded token file content (single-line).
.PARAMETER DestinationPath
    Full path where the token file should be written.
.PARAMETER Force
    If set, overwrites existing file. Otherwise skips if file exists.
.OUTPUTS
    PSCustomObject with:
    - Success: bool
    - Action: 'written' | 'skipped' | 'overwritten' | 'failed'
    - BytesWritten: int (0 if skipped/failed)
    - Error: string or $null
#>
function Write-SeededTokenFileFromB64 {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string]$Base64Content,

        [Parameter(Mandatory)]
        [string]$DestinationPath,

        [switch]$Force
    )

    $result = [PSCustomObject]@{
        Success = $false
        Action = 'failed'
        BytesWritten = 0
        Error = $null
    }

    # Validate input is not empty
    if ([string]::IsNullOrWhiteSpace($Base64Content)) {
        $result.Error = "Base64Content is empty or whitespace"
        return $result
    }

    # Check if file already exists
    if (Test-Path $DestinationPath) {
        if (-not $Force) {
            $result.Success = $true
            $result.Action = 'skipped'
            $result.Error = $null
            return $result
        }
    }

    # Decode base64 - never echo the content
    try {
        $decodedBytes = [System.Convert]::FromBase64String($Base64Content.Trim())
    }
    catch {
        # Report error without echoing the input
        $inputLength = $Base64Content.Length
        $result.Error = "Invalid base64 (input length: $inputLength chars)"
        return $result
    }

    # Ensure parent directory exists
    $parentDir = Split-Path -Parent $DestinationPath
    if (-not [string]::IsNullOrWhiteSpace($parentDir) -and -not (Test-Path $parentDir)) {
        try {
            New-Item -ItemType Directory -Path $parentDir -Force | Out-Null
        }
        catch {
            $result.Error = "Failed to create directory: $parentDir"
            return $result
        }
    }

    # Write file
    try {
        [System.IO.File]::WriteAllBytes($DestinationPath, $decodedBytes)

        # Set permissions on Linux (600 = owner read/write only)
        if ($IsLinux -or $IsMacOS) {
            chmod 600 $DestinationPath 2>$null
        }

        $result.Success = $true
        $result.Action = if (Test-Path $DestinationPath) { 'overwritten' } else { 'written' }
        # Fix: action should be based on whether file existed before write
        $result.Action = if ($Force -and (Test-Path $DestinationPath)) { 'overwritten' } else { 'written' }
        $result.BytesWritten = $decodedBytes.Length
    }
    catch {
        $result.Error = "Failed to write file: $($_.Exception.Message)"
    }

    return $result
}

<#
.SYNOPSIS
    Gets the expected token file path for a plugin.
.PARAMETER PluginName
    Name of the plugin (e.g., 'Tidalarr').
.PARAMETER ConfigPath
    Base config path (e.g., from TIDALARR_CONFIG_PATH).
.OUTPUTS
    Full path to the token file.
#>
function Get-SeededTokenFilePath {
    param(
        [Parameter(Mandatory)]
        [ValidateSet("Tidalarr")]
        [string]$PluginName,

        [Parameter(Mandatory)]
        [string]$ConfigPath
    )

    switch ($PluginName) {
        "Tidalarr" {
            return Join-Path $ConfigPath "tidal_tokens.json"
        }
    }
}

Export-ModuleMember -Function Write-SeededTokenFileFromB64, Get-SeededTokenFilePath
