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

function Resolve-E2EComponentIdsInstanceKey {
    param(
        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [string]$ContainerName,

        [string]$InstanceSalt = "",

        # Optional explicit override for the instance key (power users).
        # If invalid (empty/whitespace or contains unsafe characters), falls back to computed hashing.
        [AllowNull()]
        [string]$ExplicitInstanceKey
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitInstanceKey)) {
        $candidate = $ExplicitInstanceKey.Trim()
        if ($candidate.Length -gt 0 -and $candidate.Length -le 64 -and $candidate -match '^[A-Za-z0-9][A-Za-z0-9_.:-]{0,63}$') {
            return $candidate
        }
    }

    return Get-E2EComponentIdsInstanceKey -LidarrUrl $LidarrUrl -ContainerName $ContainerName -InstanceSalt $InstanceSalt
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
                    lidarrBranch = ($instanceObj.PSObject.Properties["lidarrBranch"]?.Value)
                    imageTag = ($instanceObj.PSObject.Properties["imageTag"]?.Value)
                    imageDigest = ($instanceObj.PSObject.Properties["imageDigest"]?.Value)
                    imageId = ($instanceObj.PSObject.Properties["imageId"]?.Value)
                    containerId = ($instanceObj.PSObject.Properties["containerId"]?.Value)
                    containerStartedAt = ($instanceObj.PSObject.Properties["containerStartedAt"]?.Value)
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
                lidarrBranch = $null
                imageTag = $null
                imageDigest = $null
                imageId = $null
                containerId = $null
                containerStartedAt = $null
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

<#
.SYNOPSIS
    Gets the effective lock policy settings with clamping and source tracking.
.DESCRIPTION
    Returns a hashtable with the effective (clamped) lock policy values and the source.
    This is the single source of truth for lock policy - used by both the state writer
    and the manifest emitter.

    Clamp bounds:
    - TimeoutMs: 0-5000 (default 750)
    - RetryDelayMs: 1-250 (default 50)
    - StaleSeconds: 0-3600 (default 120)
.OUTPUTS
    Hashtable with: TimeoutMs, RetryDelayMs, StaleSeconds, Source (default|env)
#>
function Get-E2EComponentIdsEffectiveLockPolicy {
    $defaults = @{
        TimeoutMs = 750
        RetryDelayMs = 50
        StaleSeconds = 120
    }

    $effective = @{
        TimeoutMs = $defaults.TimeoutMs
        RetryDelayMs = $defaults.RetryDelayMs
        StaleSeconds = $defaults.StaleSeconds
    }
    $source = "default"

    # Parse env vars (only valid numeric values)
    try {
        if ($env:E2E_COMPONENT_IDS_LOCK_TIMEOUT_MS -and $env:E2E_COMPONENT_IDS_LOCK_TIMEOUT_MS -match '^\d+$') {
            $effective.TimeoutMs = [int]$env:E2E_COMPONENT_IDS_LOCK_TIMEOUT_MS
            $source = "env"
        }
        if ($env:E2E_COMPONENT_IDS_LOCK_RETRY_DELAY_MS -and $env:E2E_COMPONENT_IDS_LOCK_RETRY_DELAY_MS -match '^\d+$') {
            $effective.RetryDelayMs = [int]$env:E2E_COMPONENT_IDS_LOCK_RETRY_DELAY_MS
            $source = "env"
        }
        if ($env:E2E_COMPONENT_IDS_LOCK_STALE_SECONDS -and $env:E2E_COMPONENT_IDS_LOCK_STALE_SECONDS -match '^\d+$') {
            $effective.StaleSeconds = [int]$env:E2E_COMPONENT_IDS_LOCK_STALE_SECONDS
            $source = "env"
        }
    } catch { }

    # Clamp to sane bounds (avoid accidental huge waits / tight loops)
    if ($effective.TimeoutMs -lt 0) { $effective.TimeoutMs = 0 }
    if ($effective.TimeoutMs -gt 5000) { $effective.TimeoutMs = 5000 }

    if ($effective.RetryDelayMs -lt 1) { $effective.RetryDelayMs = 1 }
    if ($effective.RetryDelayMs -gt 250) { $effective.RetryDelayMs = 250 }

    if ($effective.StaleSeconds -lt 0) { $effective.StaleSeconds = 0 }
    if ($effective.StaleSeconds -gt 3600) { $effective.StaleSeconds = 3600 }

    return @{
        TimeoutMs = $effective.TimeoutMs
        RetryDelayMs = $effective.RetryDelayMs
        StaleSeconds = $effective.StaleSeconds
        Source = $source
    }
}

function Write-E2EComponentIdsState {
    <#
    .SYNOPSIS
        Persists component ID state to disk with structured result.
    .DESCRIPTION
        Returns a hashtable with factual persistence outcome:
        - Attempted: Always $true when this function is called
        - Wrote: $true only when file content actually changed
        - Reason: One of "written", "no_changes", "lock_timeout", "io_error"
    #>
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [hashtable]$State
    )

    $lockAcquired = $false

    try {
        $dir = Split-Path -Parent $Path
        if ([string]::IsNullOrWhiteSpace($dir)) {
            $dir = "."
        }
        if (-not (Test-Path -LiteralPath $dir)) {
            New-Item -ItemType Directory -Path $dir -Force | Out-Null
        }

        # Best-effort file lock to reduce concurrent writer clobbering.
        # Failure to acquire a lock returns structured result (does not throw).
        $lockPath = "$Path.lock"

        # Get effective lock policy from shared function
        $lockPolicy = Get-E2EComponentIdsEffectiveLockPolicy
        $lockTimeoutMs = $lockPolicy.TimeoutMs
        $lockRetryDelayMs = $lockPolicy.RetryDelayMs
        $lockStaleSeconds = $lockPolicy.StaleSeconds

        $deadline = (Get-Date).AddMilliseconds($lockTimeoutMs)

        while (-not $lockAcquired -and (Get-Date) -lt $deadline) {
            try {
                if (Test-Path -LiteralPath $lockPath) {
                    try {
                        $age = (Get-Date -AsUTC) - (Get-Item -LiteralPath $lockPath).LastWriteTimeUtc
                        if ($age.TotalSeconds -ge $lockStaleSeconds) {
                            Remove-Item -LiteralPath $lockPath -Force -ErrorAction SilentlyContinue
                        }
                    } catch { }
                }

                New-Item -ItemType File -Path $lockPath -ErrorAction Stop | Out-Null
                $lockAcquired = $true
            }
            catch {
                Start-Sleep -Milliseconds $lockRetryDelayMs
            }
        }

        if (-not $lockAcquired) {
            return @{ Attempted = $true; Wrote = $false; Reason = "lock_timeout" }
        }

        # Serialize new state using ConvertTo-Json -Compress for stable comparison
        $newJson = $State | ConvertTo-Json -Depth 10 -Compress

        # Check if file already has identical content (no_changes detection)
        if (Test-Path -LiteralPath $Path) {
            try {
                $existingRaw = Get-Content -LiteralPath $Path -Raw -ErrorAction Stop
                if (-not [string]::IsNullOrEmpty($existingRaw)) {
                    # Re-serialize existing content for stable comparison
                    $existingObj = $existingRaw | ConvertFrom-Json -ErrorAction Stop
                    $existingJson = $existingObj | ConvertTo-Json -Depth 10 -Compress
                    if ($existingJson -eq $newJson) {
                        return @{ Attempted = $true; Wrote = $false; Reason = "no_changes" }
                    }
                }
            }
            catch {
                # If we can't read/parse existing file, proceed with write
            }
        }

        # Write with pretty formatting for human readability
        $prettyJson = $State | ConvertTo-Json -Depth 10
        $tmp = "$Path.tmp"
        Set-Content -LiteralPath $tmp -Value $prettyJson -Encoding UTF8 -NoNewline

        Move-Item -LiteralPath $tmp -Destination $Path -Force
        return @{ Attempted = $true; Wrote = $true; Reason = "written" }
    }
    catch {
        # Best-effort: a state persistence failure must never break the E2E run.
        try { Remove-Item -LiteralPath "$Path.tmp" -Force -ErrorAction SilentlyContinue } catch { }
        return @{ Attempted = $true; Wrote = $false; Reason = "io_error" }
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

function Set-E2EComponentIdsInstanceHostFingerprint {
    param(
        [Parameter(Mandatory)]
        [hashtable]$State,

        [Parameter(Mandatory)]
        [string]$InstanceKey,

        [Parameter(Mandatory)]
        [string]$LidarrUrl,

        [Parameter(Mandatory)]
        [string]$ContainerName,

        [AllowNull()]
        [string]$LidarrVersion,

        [AllowNull()]
        [string]$LidarrBranch,

        [AllowNull()]
        [string]$ImageTag,

        [AllowNull()]
        [string]$ImageDigest,

        [AllowNull()]
        [string]$ImageId,

        [AllowNull()]
        [string]$ContainerId,

        [AllowNull()]
        [string]$ContainerStartedAt
    )

    if (-not $State.ContainsKey("instances") -or $State["instances"] -isnot [hashtable]) {
        $State["instances"] = @{}
    }

    if (-not $State["instances"].ContainsKey($InstanceKey) -or $State["instances"][$InstanceKey] -isnot [hashtable]) {
        $State["instances"][$InstanceKey] = @{
            lidarrUrl = $LidarrUrl
            containerName = $ContainerName
            lidarrVersion = $null
            lidarrBranch = $null
            imageTag = $null
            imageDigest = $null
            imageId = $null
            containerId = $null
            containerStartedAt = $null
            updatedAt = (Get-Date).ToString("o")
            plugins = @{}
        }
    }

    $instance = $State["instances"][$InstanceKey]
    $instance["lidarrUrl"] = $LidarrUrl
    $instance["containerName"] = $ContainerName
    $instance["updatedAt"] = (Get-Date).ToString("o")

    if (-not [string]::IsNullOrWhiteSpace($LidarrVersion)) { $instance["lidarrVersion"] = $LidarrVersion }
    if (-not [string]::IsNullOrWhiteSpace($LidarrBranch)) { $instance["lidarrBranch"] = $LidarrBranch }
    if (-not [string]::IsNullOrWhiteSpace($ImageTag)) { $instance["imageTag"] = $ImageTag }
    if (-not [string]::IsNullOrWhiteSpace($ImageDigest)) { $instance["imageDigest"] = $ImageDigest }
    if (-not [string]::IsNullOrWhiteSpace($ImageId)) { $instance["imageId"] = $ImageId }
    if (-not [string]::IsNullOrWhiteSpace($ContainerId)) { $instance["containerId"] = $ContainerId }
    if (-not [string]::IsNullOrWhiteSpace($ContainerStartedAt)) { $instance["containerStartedAt"] = $ContainerStartedAt }
}

function Select-ConfiguredComponent {
    param(
        [AllowNull()]
        $Items,

        [Parameter(Mandatory)]
        [string]$PluginName,

        [AllowNull()]
        [int]$PreferredId,

        # When set, disables fuzzy matching (substring match) on implementation.
        # Useful for CI hardening to avoid selecting the wrong component when multiple exist.
        [switch]$DisableFuzzyMatch
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
    # This match is intentionally optional and should generally be disabled in CI to avoid drift.
    if (-not $DisableFuzzyMatch) {
        $matches = @($arr | Where-Object { (Get-ItemValue -Item $_ -Name "implementation") -like "*$PluginName*" })
        if ($matches.Count -gt 1) {
            $candidateIds = @($matches | ForEach-Object { Get-ItemId -Item $_ } | Where-Object { $null -ne $_ })
            return [PSCustomObject]@{ Component = $null; Resolution = "ambiguousFuzzy"; CandidateIds = $candidateIds }
        }
        $match = $matches | Select-Object -First 1
        if ($match) { return [PSCustomObject]@{ Component = $match; Resolution = "fuzzy"; CandidateIds = @() } }
    }

    return [PSCustomObject]@{ Component = $null; Resolution = "none"; CandidateIds = @() }
}

Export-ModuleMember -Function `
    Get-E2EComponentIdsDefaultPath, `
    Get-E2EComponentIdsInstanceKey, `
    Resolve-E2EComponentIdsInstanceKey, `
    Get-E2EComponentIdsEffectiveLockPolicy, `
    Read-E2EComponentIdsState, `
    Write-E2EComponentIdsState, `
    Get-E2EPreferredComponentId, `
    Set-E2EPreferredComponentId, `
    Set-E2EComponentIdsInstanceHostFingerprint, `
    Select-ConfiguredComponent
