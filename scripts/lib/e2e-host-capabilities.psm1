<#
.SYNOPSIS
    Host capability detection for E2E testing.

.DESCRIPTION
    Probes the Lidarr host (in Docker container) to detect runtime capabilities
    that affect multi-plugin E2E behavior.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Assemblies that may contain PluginLoader (varies by Lidarr version/refactoring)
$script:ProbeTargets = @(
    '/app/bin/NzbDrone.Common.dll'
    '/app/bin/Lidarr.Common.dll'
)

function Test-HostALCFix {
    <#
    .SYNOPSIS
        Detects whether the Lidarr host has the ALC GC-prevention fix.

    .DESCRIPTION
        The ALC fix adds a static List<PluginLoadContext> to PluginLoader.cs
        which prevents premature GC of plugin contexts during multi-plugin load.

        Without this fix, multi-plugin installations can experience deterministic
        AssemblyLoadContext failures due to GC collecting WeakReference targets.

        PR: https://github.com/Lidarr/Lidarr/pull/5662

    .PARAMETER ContainerName
        Docker container name running Lidarr.

    .OUTPUTS
        PSCustomObject with:
        - HasFix: bool - Whether the fix is present
        - Method: string - Detection method used
        - Details: string - Additional detection details
        - ProbedFiles: array - Files that were probed
        - MatchedFile: string - File where token was found (null if not found)
        - MatchedToken: string - The token searched for
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ContainerName
    )

    $result = [PSCustomObject]@{
        HasFix       = $false
        Method       = 'unknown'
        Details      = $null
        ProbedFiles  = @()
        MatchedFile  = $null
        MatchedToken = 'PluginContexts'
    }

    $probedFiles = @()
    $matchedFile = $null

    try {
        # Probe each potential assembly for PluginContexts field
        # The fix adds: private static readonly List<PluginLoadContext> PluginContexts = new();
        foreach ($dllPath in $script:ProbeTargets) {
            $probeResult = [ordered]@{
                path   = $dllPath
                exists = $false
                matches = 0
            }

            # Check if file exists
            $existsCheck = docker exec $ContainerName sh -c "test -f '$dllPath' && echo 'exists'" 2>$null
            if ($existsCheck -eq 'exists') {
                $probeResult.exists = $true

                # Probe for PluginContexts using strings
                $matchCount = docker exec $ContainerName sh -c "strings '$dllPath' 2>/dev/null | grep -c PluginContexts || echo 0" 2>$null
                $probeResult.matches = [int]($matchCount ?? 0)

                if ($probeResult.matches -gt 0 -and -not $matchedFile) {
                    $matchedFile = $dllPath
                }
            }

            $probedFiles += $probeResult
        }

        $result.ProbedFiles = $probedFiles

        if ($matchedFile) {
            $result.HasFix = $true
            $result.Method = 'strings-probe'
            $result.MatchedFile = $matchedFile
            $matchInfo = $probedFiles | Where-Object { $_.path -eq $matchedFile } | Select-Object -First 1
            $result.Details = "Found PluginContexts in $matchedFile ($($matchInfo.matches) occurrences)"
        } else {
            $result.HasFix = $false
            $result.Method = 'strings-probe'
            $existingFiles = ($probedFiles | Where-Object { $_.exists } | ForEach-Object { $_.path }) -join ', '
            if ($existingFiles) {
                $result.Details = "PluginContexts not found in: $existingFiles"
            } else {
                $result.Details = "No probe target assemblies found in container"
            }
        }
    }
    catch {
        # Fallback: check if we're using a known-fixed image tag
        try {
            $imageInfo = docker inspect $ContainerName --format '{{.Config.Image}}' 2>$null
            if ($imageInfo -match 'richertunes') {
                # RicherTunes fork images have the fix
                $result.HasFix = $true
                $result.Method = 'image-inference'
                $result.Details = "RicherTunes image detected: $imageInfo"
            } else {
                $result.Method = 'probe-failed'
                $result.Details = "Probe failed: $_"
            }
        }
        catch {
            $result.Method = 'probe-failed'
            $result.Details = "Container inspection failed: $_"
        }
    }

    return $result
}

function Get-HostCapabilities {
    <#
    .SYNOPSIS
        Gets comprehensive host capability report for E2E manifest.

    .DESCRIPTION
        Aggregates host capability detection results for inclusion in the
        E2E run manifest. Includes ALC fix detection and other runtime checks.

    .PARAMETER ContainerName
        Docker container name running Lidarr.

    .OUTPUTS
        Ordered hashtable suitable for JSON serialization.
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ContainerName
    )

    $alcResult = Test-HostALCFix -ContainerName $ContainerName

    return [ordered]@{
        alcFix = [ordered]@{
            present      = $alcResult.HasFix
            method       = $alcResult.Method
            details      = $alcResult.Details
            probedFiles  = $alcResult.ProbedFiles
            matchedFile  = $alcResult.MatchedFile
            matchedToken = $alcResult.MatchedToken
            prUrl        = 'https://github.com/Lidarr/Lidarr/pull/5662'
        }
        probeTimestamp = (Get-Date -Format 'o')
    }
}

Export-ModuleMember -Function Test-HostALCFix, Get-HostCapabilities
