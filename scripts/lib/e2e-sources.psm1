<#
.SYNOPSIS
    Source provenance helpers for E2E run manifests.
.DESCRIPTION
    Produces best-effort repo SHAs (git) and deployed plugin versions (env/container)
    for inclusion in the E2E run manifest `sources` block.

    Invariants:
    - `SourceProvenance.*` values MUST be one of: git | env | unknown
    - `SourceShas.*` values are git short SHAs (7 chars) or $null
    - `SourceFullShas.*` values are git full SHAs (40 chars) or $null (for reproducibility)
    - Optional `SourceVersions.*` values come from deployed plugin.json (container) when available
#>

function Get-GitShortSha {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    try {
        if (-not (Test-Path -LiteralPath $RepoRoot)) { return $null }
        $isGit = git -C $RepoRoot rev-parse --is-inside-work-tree 2>$null
        if ("$isGit".Trim().ToLowerInvariant() -ne 'true') { return $null }

        $sha = git -C $RepoRoot rev-parse --short=7 HEAD 2>$null
        $sha = "$sha".Trim()
        if ([string]::IsNullOrWhiteSpace($sha)) { return $null }
        return $sha
    }
    catch {
        return $null
    }
}

function Get-GitFullSha {
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot
    )

    try {
        if (-not (Test-Path -LiteralPath $RepoRoot)) { return $null }
        $isGit = git -C $RepoRoot rev-parse --is-inside-work-tree 2>$null
        if ("$isGit".Trim().ToLowerInvariant() -ne 'true') { return $null }

        $sha = git -C $RepoRoot rev-parse HEAD 2>$null
        $sha = "$sha".Trim()
        if ([string]::IsNullOrWhiteSpace($sha)) { return $null }
        return $sha
    }
    catch {
        return $null
    }
}

function Find-RepoPath {
    param(
        [Parameter(Mandatory = $true)]
        [string]$CommonRepoRoot,
        [Parameter(Mandatory = $true)]
        [string]$RepoName,
        [Parameter(Mandatory = $false)]
        [string]$ExplicitPath
    )

    if (-not [string]::IsNullOrWhiteSpace($ExplicitPath) -and (Test-Path -LiteralPath $ExplicitPath)) {
        return $ExplicitPath
    }

    $candidates = @()
    # Common is often checked out with sibling repos (local dev) or as subfolders (CI).
    $repoNameLower = $RepoName.ToLowerInvariant()

    # Prefer exact casing first, but also probe lowercase to work on case-sensitive filesystems (Linux CI).
    $candidates += (Join-Path $CommonRepoRoot $RepoName)
    $candidates += (Join-Path $CommonRepoRoot $repoNameLower)
    $candidates += (Join-Path (Split-Path $CommonRepoRoot -Parent) $RepoName)
    $candidates += (Join-Path (Split-Path $CommonRepoRoot -Parent) $repoNameLower)

    foreach ($path in $candidates) {
        if (-not (Test-Path -LiteralPath $path)) { continue }
        $sha = Get-GitShortSha -RepoRoot $path
        if ($sha) { return $path }
    }

    return $null
}

function Get-DeployedPluginVersionFromContainer {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ContainerName,
        [Parameter(Mandatory = $true)]
        [string]$Vendor,
        [Parameter(Mandatory = $true)]
        [string]$PluginName
    )

    try {
        if ([string]::IsNullOrWhiteSpace($ContainerName)) { return $null }
        $pluginJsonPath = "/config/plugins/$Vendor/$PluginName/plugin.json"
        $raw = docker exec $ContainerName sh -c "cat '$pluginJsonPath' 2>/dev/null" 2>$null
        if ([string]::IsNullOrWhiteSpace("$raw")) { return $null }
        $json = $raw | ConvertFrom-Json -ErrorAction Stop
        $version = $json.version
        if ([string]::IsNullOrWhiteSpace("$version")) { return $null }
        return "$version".Trim()
    }
    catch {
        return $null
    }
}

function Get-E2ESourcesContext {
    <#
    .SYNOPSIS
        Computes best-effort SourceShas/SourceProvenance/SourceVersions for E2E manifests.
    .PARAMETER CommonRepoRoot
        Root of the lidarr.plugin.common repo.
    .PARAMETER Plugins
        Plugin names requested for the run (e.g. Qobuzarr, Tidalarr, Brainarr).
    .PARAMETER ContainerName
        Optional docker container name; when provided, reads deployed plugin.json versions.
    .PARAMETER Vendor
        Vendor folder under /config/plugins (default: RicherTunes).
    .PARAMETER RepoPathOverrides
        Optional map of pluginName -> repo path override.
    #>
    param(
        [Parameter(Mandatory = $true)]
        [string]$CommonRepoRoot,
        [Parameter(Mandatory = $true)]
        [string[]]$Plugins,
        [Parameter(Mandatory = $false)]
        [string]$ContainerName,
        [Parameter(Mandatory = $false)]
        [string]$Vendor = 'RicherTunes',
        [Parameter(Mandatory = $false)]
        [hashtable]$RepoPathOverrides
    )

    $sourceShas = @{}
    $sourceFullShas = @{}
    $sourceProvenance = @{}
    $sourceVersions = @{}

    $commonSha = Get-GitShortSha -RepoRoot $CommonRepoRoot
    $commonFullSha = Get-GitFullSha -RepoRoot $CommonRepoRoot
    $sourceShas['Common'] = $commonSha
    $sourceFullShas['Common'] = $commonFullSha
    $sourceProvenance['Common'] = 'git'

    foreach ($plugin in $Plugins) {
        if ([string]::IsNullOrWhiteSpace($plugin)) { continue }
        $pluginKey = "$plugin".Trim()
        # Canonicalize known plugin names to stable casing for manifest context keys
        $pluginKeyLower = $pluginKey.ToLowerInvariant()
        if ($pluginKeyLower -eq 'qobuzarr') { $pluginKey = 'Qobuzarr' }
        elseif ($pluginKeyLower -eq 'tidalarr') { $pluginKey = 'Tidalarr' }
        elseif ($pluginKeyLower -eq 'brainarr') { $pluginKey = 'Brainarr' }

        # Repo SHA (git)
        $overridePath = $null
        if ($RepoPathOverrides -and $RepoPathOverrides.ContainsKey($pluginKey)) {
            $overridePath = $RepoPathOverrides[$pluginKey]
        }

        $repoPath = Find-RepoPath -CommonRepoRoot $CommonRepoRoot -RepoName $pluginKey -ExplicitPath $overridePath
        $sha = if ($repoPath) { Get-GitShortSha -RepoRoot $repoPath } else { $null }
        $fullSha = if ($repoPath) { Get-GitFullSha -RepoRoot $repoPath } else { $null }

        $sourceShas[$pluginKey] = $sha
        $sourceFullShas[$pluginKey] = $fullSha
        if ($sha) {
            $sourceProvenance[$pluginKey] = 'git'
        } else {
            $sourceProvenance[$pluginKey] = 'unknown'
        }

        # Deployed version (optional, container-probed)
        $deployedVersion = $null
        if (-not [string]::IsNullOrWhiteSpace($ContainerName)) {
            $deployedVersion = Get-DeployedPluginVersionFromContainer -ContainerName $ContainerName -Vendor $Vendor -PluginName $pluginKey
        }

        if ($deployedVersion) {
            $sourceVersions[$pluginKey] = $deployedVersion
            if (-not $sha) {
                $sourceProvenance[$pluginKey] = 'env'
            }
        } else {
            $sourceVersions[$pluginKey] = $null
        }
    }

    return @{
        SourceShas = $sourceShas
        SourceFullShas = $sourceFullShas
        SourceProvenance = $sourceProvenance
        SourceVersions = $sourceVersions
    }
}

Export-ModuleMember -Function Get-E2ESourcesContext
