<#
.SYNOPSIS
    Host capability detection for E2E testing.

.DESCRIPTION
    Probes the Lidarr host (in Docker container) to detect runtime capabilities
    that affect multi-plugin E2E behavior.
#>

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
    #>
    param(
        [Parameter(Mandatory)]
        [string]$ContainerName
    )

    $result = [PSCustomObject]@{
        HasFix  = $false
        Method  = 'unknown'
        Details = $null
    }

    try {
        # Probe for PluginContexts field in NzbDrone.Common.dll using strings
        # The fix adds: private static readonly List<PluginLoadContext> PluginContexts = new();
        $probe = docker exec $ContainerName sh -c "strings /app/bin/NzbDrone.Common.dll 2>/dev/null | grep -c PluginContexts" 2>$null

        if ($LASTEXITCODE -eq 0 -and $probe -and [int]$probe -gt 0) {
            $result.HasFix = $true
            $result.Method = 'strings-probe'
            $result.Details = "Found PluginContexts field ($probe occurrences)"
        } else {
            $result.HasFix = $false
            $result.Method = 'strings-probe'
            $result.Details = 'PluginContexts field not found - host vulnerable to ALC GC bug'
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
            present = $alcResult.HasFix
            method  = $alcResult.Method
            details = $alcResult.Details
            prUrl   = 'https://github.com/Lidarr/Lidarr/pull/5662'
        }
        probeTimestamp = (Get-Date -Format 'o')
    }
}

Export-ModuleMember -Function Test-HostALCFix, Get-HostCapabilities
