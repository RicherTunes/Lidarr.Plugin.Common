Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

function Get-E2EComponentIdsDefaultPath {
    param(
        [string]$RepoRoot
    )

    if ([string]::IsNullOrWhiteSpace($RepoRoot)) {
        $repoRoot = Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
    }
    else {
        $repoRoot = $RepoRoot
    }

    return (Join-Path $repoRoot ".e2e-bootstrap/e2e-component-ids.json")
}

function Get-E2EComponentIdsInstanceKey {
    param(
        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [string]$ContainerName
,
        [string]$InstanceSalt = ""
    )

    $normalizedUrl = $LidarrUrl.Trim().TrimEnd('/').ToLowerInvariant()
    $normalizedContainer = $ContainerName.Trim().ToLowerInvariant()

    $normalizedSalt = $InstanceSalt.Trim().ToLowerInvariant()
    $input = if ([string]::IsNullOrWhiteSpace($normalizedSalt)) {
        "$normalizedUrl|$normalizedContainer"
    }
    else {
        "$normalizedUrl|$normalizedContainer|$normalizedSalt"
    }

    $sha = [System.Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [System.Text.Encoding]::UTF8.GetBytes($input)
        $hash = $sha.ComputeHash($bytes)
        $hex = -join ($hash | ForEach-Object { $_.ToString("x2") })
        return "i-$($hex.Substring(0, 12))"
    }
    finally {
        $sha.Dispose()
    }
}

function Read-E2EComponentIdsState {
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    try {
        if (-not (Test-Path -LiteralPath $Path)) {
            return @{
                schemaVersion = 2
                instances = @{}
            }
        }

        $raw = Get-Content -LiteralPath $Path -Raw
        if ([string]::IsNullOrWhiteSpace($raw)) {
            return @{
                schemaVersion = 2
                instances = @{}
            }
        }

        $obj = $raw | ConvertFrom-Json -ErrorAction Stop
        if ($null -eq $obj) {
            return @{
                schemaVersion = 2
                instances = @{}
            }
        }

        $schemaProp = $obj.PSObject.Properties["schemaVersion"]
        $instancesProp = $obj.PSObject.Properties["instances"]

        # New format
        if ($null -ne $schemaProp -and $null -ne $instancesProp -and $null -ne $instancesProp.Value) {
            $instances = @{}
            foreach ($instanceEntry in $instancesProp.Value.PSObject.Properties) {
                $instanceKey = $instanceEntry.Name
                $instanceObj = $instanceEntry.Value
                if ($null -eq $instanceObj) { continue }

                $pluginsProp = $instanceObj.PSObject.Properties["plugins"]
                if ($null -eq $pluginsProp -or $null -eq $pluginsProp.Value) { continue }

                $pluginsState = @{}
                foreach ($pluginEntry in $pluginsProp.Value.PSObject.Properties) {
                    $pluginName = $pluginEntry.Name
                    $pluginObj = $pluginEntry.Value
                    if ($null -eq $pluginObj) { continue }

                    $pluginState = @{}
                    foreach ($k in @("indexerId", "downloadClientId", "importListId")) {
                        $prop = $pluginObj.PSObject.Properties[$k]
                        $v = if ($null -ne $prop) { $prop.Value } else { $null }
                        if ($null -ne $v -and "$v" -match '^\d+$') {
                            $pluginState[$k] = [int]$v
                        }
                    }

                    if ($pluginState.Count -gt 0) {
                        $pluginsState[$pluginName] = $pluginState
                    }
                }

                $instances[$instanceKey] = @{
                    lidarrUrl = ($instanceObj.PSObject.Properties["lidarrUrl"]?.Value)
                    containerName = ($instanceObj.PSObject.Properties["containerName"]?.Value)
                    lidarrVersion = ($instanceObj.PSObject.Properties["lidarrVersion"]?.Value)
                    updatedAt = ($instanceObj.PSObject.Properties["updatedAt"]?.Value)
                    plugins = $pluginsState
                }
            }

            return @{
                schemaVersion = 2
                instances = $instances
            }
        }

        # Legacy format: { "Qobuzarr": { "indexerId": 1, ... }, ... }
        $legacyPlugins = @{}
        foreach ($p in $obj.PSObject.Properties) {
            $pluginName = $p.Name
            $pluginObj = $p.Value
            if ($null -eq $pluginObj) { continue }

            $pluginState = @{}
            foreach ($k in @("indexerId", "downloadClientId", "importListId")) {
                $prop = $pluginObj.PSObject.Properties[$k]
                $v = if ($null -ne $prop) { $prop.Value } else { $null }
                if ($null -ne $v -and "$v" -match '^\d+$') {
                    $pluginState[$k] = [int]$v
                }
            }
            if ($pluginState.Count -gt 0) {
                $legacyPlugins[$pluginName] = $pluginState
            }
        }

        $instances = @{}
        if ($legacyPlugins.Count -gt 0) {
            $instances["legacy"] = @{
                lidarrUrl = $null
                containerName = $null
                lidarrVersion = $null
                updatedAt = $null
                plugins = $legacyPlugins
            }
        }

        return @{
            schemaVersion = 2
            instances = $instances
        }
    }
    catch {
        # Do not throw: a corrupted state file should never break E2E runs.
        return @{
            schemaVersion = 2
            instances = @{}
        }
    }
}

function Write-E2EComponentIdsState {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [hashtable]$State
    )

    try {
        $dir = Split-Path -Parent $Path
        if ([string]::IsNullOrWhiteSpace($dir)) {
            $dir = "."
        }
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }

        # Best-effort file lock to reduce concurrent writer clobbering.
        # Failure to acquire a lock must not break runs; we just return $false.
        $lockPath = "$Path.lock"
        $lockAcquired = $false
        $deadline = (Get-Date).AddMilliseconds(750)

        while (-not $lockAcquired -and (Get-Date) -lt $deadline) {
            try {
                if (Test-Path -LiteralPath $lockPath) {
                    try {
                        $age = (Get-Date -AsUTC) - (Get-Item -LiteralPath $lockPath).LastWriteTimeUtc
                        if ($age.TotalSeconds -ge 120) {
                            Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
                        }
                    } catch { }
                }

                New-Item -ItemType File -Path $lockPath -ErrorAction Stop | Out-Null
                $lockAcquired = $true
            }
            catch {
                Start-Sleep -Milliseconds 50
            }
        }

        if (-not $lockAcquired) {
            return $false
        }

        $json = $State | ConvertTo-Json -Depth 10
        $tmp = "$Path.tmp"
        Set-Content -LiteralPath $tmp -Value $json -Encoding UTF8 -NoNewline    

        Move-Item -LiteralPath $tmp -Destination $Path -Force
        return $true
    }
    catch {
        # Best-effort: a state persistence failure must never break the E2E run.
        try { Remove-Item -LiteralPath "$Path.tmp" -Force -ErrorAction SilentlyContinue } catch { }
        return $false
    }
    finally {
        if ($lockAcquired) {
            try { Remove-Item -LiteralPath "$Path.lock" -Force -ErrorAction SilentlyContinue } catch { }
        }
    }
}

function Get-E2EPreferredComponentId {
    param(
        [Parameter(Mandatory)]
        [hashtable]$State,

        [Parameter(Mandatory)]
        [string]$InstanceKey,

        [Parameter(Mandatory)]
        [string]$PluginName,

        [Parameter(Mandatory)]
        [ValidateSet("indexer", "downloadclient", "importlist")]
        [string]$Type
    )

    $key = switch ($Type) {
        "indexer" { "indexerId" }
        "downloadclient" { "downloadClientId" }
        "importlist" { "importListId" }
    }

    if (-not $State.ContainsKey("instances")) { return $null }
    $instances = $State["instances"]
    if ($instances -isnot [hashtable]) { return $null }

    $instance = $null
    if ($instances.ContainsKey($InstanceKey)) {
        $instance = $instances[$InstanceKey]
    }
    elseif ($instances.ContainsKey("legacy") -and $instances.Count -eq 1) {
        $instance = $instances["legacy"]
    }

    if ($null -eq $instance -or $instance -isnot [hashtable]) { return $null }
    if (-not $instance.ContainsKey("plugins")) { return $null }
    $plugins = $instance["plugins"]
    if ($plugins -isnot [hashtable]) { return $null }

    if (-not $plugins.ContainsKey($PluginName)) { return $null }
    $pluginState = $plugins[$PluginName]
    if ($pluginState -isnot [hashtable]) { return $null }
    if (-not $pluginState.ContainsKey($key)) { return $null }
    return $pluginState[$key]
}

function Set-E2EPreferredComponentId {
    param(
        [Parameter(Mandatory)]
        [hashtable]$State,

        [Parameter(Mandatory)]
        [string]$InstanceKey,

        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [string]$ContainerName,

        [Parameter(Mandatory)]
        [string]$PluginName,

        [Parameter(Mandatory)]
        [ValidateSet("indexer", "downloadclient", "importlist")]
        [string]$Type,

        [Parameter(Mandatory)]
        [int]$Id
    )

    $key = switch ($Type) {
        "indexer" { "indexerId" }
        "downloadclient" { "downloadClientId" }
        "importlist" { "importListId" }
    }

    if (-not $State.ContainsKey("instances") -or $State["instances"] -isnot [hashtable]) {
        $State["instances"] = @{}
    }

    if (-not $State["instances"].ContainsKey($InstanceKey) -or $State["instances"][$InstanceKey] -isnot [hashtable]) {
        $State["instances"][$InstanceKey] = @{
            lidarrUrl = $LidarrUrl
            containerName = $ContainerName
            lidarrVersion = $null
            updatedAt = (Get-Date).ToString("o")
            plugins = @{}
        }
    }

    $instance = $State["instances"][$InstanceKey]
    $instance["lidarrUrl"] = $LidarrUrl
    $instance["containerName"] = $ContainerName
    $instance["updatedAt"] = (Get-Date).ToString("o")

    if (-not $instance.ContainsKey("plugins") -or $instance["plugins"] -isnot [hashtable]) {
        $instance["plugins"] = @{}
    }
    $plugins = $instance["plugins"]

    if (-not $plugins.ContainsKey($PluginName) -or $plugins[$PluginName] -isnot [hashtable]) {
        $plugins[$PluginName] = @{}
    }
    $plugins[$PluginName][$key] = $Id
}

function Select-ConfiguredComponent {
    param(
        [AllowNull()]
        $Items,

        [Parameter(Mandatory)]
        [string]$PluginName,

        [AllowNull()]
        [int]$PreferredId
    )

    $arr = if ($Items -is [array]) { @($Items) } elseif ($null -ne $Items) { @($Items) } else { @() }

    function Get-ItemValue {
        param(
            [Parameter(Mandatory)]
            $Item,

            [Parameter(Mandatory)]
            [string]$Name
        )

        if ($Item -is [hashtable]) {
            foreach ($k in @($Item.Keys)) {
                if ([string]::Equals("$k", $Name, [StringComparison]::OrdinalIgnoreCase)) {
                    return $Item[$k]
                }
            }
            return $null
        }

        $prop = $Item.PSObject.Properties[$Name]
        if ($null -ne $prop) { return $prop.Value }
        return $null
    }

    function Get-ItemId {
        param(
            [Parameter(Mandatory)]
            $Item
        )

        $id = Get-ItemValue -Item $Item -Name "id"
        if ($null -ne $id -and "$id" -match '^\d+$') { return [int]$id }
        return $null
    }

    # Resolution by preferred ID is strict: only accept exact implementationName match.
    if ($PreferredId -gt 0) {
        $idMatch = $arr | Where-Object { (Get-ItemValue -Item $_ -Name "id") -eq $PreferredId } | Select-Object -First 1
        if ($idMatch -and (Get-ItemValue -Item $idMatch -Name "implementationName") -eq $PluginName) {
            return [PSCustomObject]@{ Component = $idMatch; Resolution = "preferredId"; CandidateIds = @() }
        }
    }

    # Priority 1: Exact implementationName match (most reliable). Require uniqueness.
    $matches = @($arr | Where-Object { (Get-ItemValue -Item $_ -Name "implementationName") -eq $PluginName })
    if ($matches.Count -gt 1) {
        $candidateIds = @($matches | ForEach-Object { Get-ItemId -Item $_ } | Where-Object { $null -ne $_ })
        return [PSCustomObject]@{ Component = $null; Resolution = "ambiguousImplementationName"; CandidateIds = $candidateIds }
    }
    $match = $matches | Select-Object -First 1
    if ($match) { return [PSCustomObject]@{ Component = $match; Resolution = "implementationName"; CandidateIds = @() } }

    # Priority 2: Exact implementation match. Require uniqueness.
    $matches = @($arr | Where-Object { (Get-ItemValue -Item $_ -Name "implementation") -eq $PluginName })
    if ($matches.Count -gt 1) {
        $candidateIds = @($matches | ForEach-Object { Get-ItemId -Item $_ } | Where-Object { $null -ne $_ })
        return [PSCustomObject]@{ Component = $null; Resolution = "ambiguousImplementation"; CandidateIds = $candidateIds }
    }
    $match = $matches | Select-Object -First 1
    if ($match) { return [PSCustomObject]@{ Component = $match; Resolution = "implementation"; CandidateIds = @() } }

    # Priority 3: Fuzzy match on implementation only (backward compatibility). Require uniqueness.
    $matches = @($arr | Where-Object { (Get-ItemValue -Item $_ -Name "implementation") -like "*$PluginName*" })
    if ($matches.Count -gt 1) {
        $candidateIds = @($matches | ForEach-Object { Get-ItemId -Item $_ } | Where-Object { $null -ne $_ })
        return [PSCustomObject]@{ Component = $null; Resolution = "ambiguousFuzzy"; CandidateIds = $candidateIds }
    }
    $match = $matches | Select-Object -First 1
    if ($match) { return [PSCustomObject]@{ Component = $match; Resolution = "fuzzy"; CandidateIds = @() } }

    return [PSCustomObject]@{ Component = $null; Resolution = "none"; CandidateIds = @() }
}

Export-ModuleMember -Function `
    Get-E2EComponentIdsDefaultPath, `
    Get-E2EComponentIdsInstanceKey, `
    Read-E2EComponentIdsState, `
    Write-E2EComponentIdsState, `
    Get-E2EPreferredComponentId, `
    Set-E2EPreferredComponentId, `
    Select-ConfiguredComponent
