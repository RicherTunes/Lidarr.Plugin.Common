<#
.SYNOPSIS
    Host assembly version checking utilities for plugin compatibility validation.

.DESCRIPTION
    This module provides functions to compare host (Lidarr) assembly versions against
    plugin-pinned package versions. Used to detect version drift that would cause
    runtime failures due to type identity mismatches.

    Host-coupled packages (types that cross the plugin boundary):
    - FluentValidation: ValidationFailure returned by DownloadClientBase.Test()
    - NLog: Logger type injected by Lidarr's DI container

.NOTES
    Part of Lidarr.Plugin.Common E2E infrastructure.
    See: docs/ECOSYSTEM_PARITY_ROADMAP.md
#>

# Default host-coupled packages that must match between host and plugin
$script:DefaultHostCoupledPackages = @(
    @{ PackageId = 'FluentValidation'; DllName = 'FluentValidation.dll'; Reason = 'ValidationFailure type crosses plugin boundary in DownloadClientBase.Test()' }
    @{ PackageId = 'NLog'; DllName = 'NLog.dll'; Reason = 'Logger type injected by Lidarr DI container' }
)

function Get-HostAssemblyVersions {
    <#
    .SYNOPSIS
        Retrieves assembly versions from host assemblies directory or Docker image.

    .PARAMETER HostAssembliesDir
        Path to directory containing host assemblies (e.g., ext/Lidarr/_output/net8.0).

    .PARAMETER ExtractFrom
        Docker image tag to extract assemblies from (e.g., pr-plugins-3.1.1.4884).
        If specified, extracts assemblies to a temp directory and reads from there.

    .PARAMETER Packages
        Array of package definitions to check. Each should have PackageId and DllName.
        Defaults to FluentValidation and NLog.

    .OUTPUTS
        Array of PSCustomObject with: PackageId, DllName, AssemblyVersion, FileVersion, ProductVersion

    .EXAMPLE
        Get-HostAssemblyVersions -HostAssembliesDir "ext/Lidarr/_output/net8.0"

    .EXAMPLE
        Get-HostAssemblyVersions -ExtractFrom "pr-plugins-3.1.1.4884"
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$HostAssembliesDir,

        [Parameter(Mandatory = $false)]
        [string]$ExtractFrom,

        [Parameter(Mandatory = $false)]
        [array]$Packages = $script:DefaultHostCoupledPackages
    )

    if (-not $HostAssembliesDir -and -not $ExtractFrom) {
        throw "Either -HostAssembliesDir or -ExtractFrom must be specified."
    }

    $effectiveDir = $HostAssembliesDir

    # Extract from Docker if requested
    if ($ExtractFrom) {
        $effectiveDir = Join-Path ([System.IO.Path]::GetTempPath()) "lidarr-host-extract-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        New-Item -ItemType Directory -Path $effectiveDir -Force | Out-Null

        $containerName = "lidarr-extract-$([guid]::NewGuid().ToString('N').Substring(0,8))"
        try {
            Write-Verbose "Extracting assemblies from ghcr.io/hotio/lidarr:$ExtractFrom..."
            docker create --name $containerName "ghcr.io/hotio/lidarr:$ExtractFrom" 2>&1 | Out-Null
            if ($LASTEXITCODE -ne 0) {
                throw "Failed to create container from ghcr.io/hotio/lidarr:$ExtractFrom"
            }

            foreach ($pkg in $Packages) {
                $dllName = $pkg.DllName
                docker cp "${containerName}:/app/bin/$dllName" "$effectiveDir/$dllName" 2>&1 | Out-Null
                if ($LASTEXITCODE -eq 0) {
                    Write-Verbose "  Extracted: $dllName"
                }
            }
        }
        finally {
            docker rm $containerName 2>&1 | Out-Null
        }
    }

    if (-not (Test-Path $effectiveDir)) {
        throw "Host assemblies directory not found: $effectiveDir"
    }

    $results = @()
    foreach ($pkg in $Packages) {
        $dllPath = Join-Path $effectiveDir $pkg.DllName
        $result = [PSCustomObject]@{
            PackageId = $pkg.PackageId
            DllName = $pkg.DllName
            Reason = $pkg.Reason
            AssemblyVersion = $null
            FileVersion = $null
            ProductVersion = $null
            Found = $false
        }

        if (Test-Path $dllPath) {
            try {
                $fileInfo = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($dllPath)
                $result.FileVersion = $fileInfo.FileVersion
                $result.ProductVersion = $fileInfo.ProductVersion

                # Try to load assembly for AssemblyVersion (may fail on non-Windows or locked files)
                try {
                    $bytes = [System.IO.File]::ReadAllBytes($dllPath)
                    $assembly = [System.Reflection.Assembly]::Load($bytes)
                    $result.AssemblyVersion = $assembly.GetName().Version.ToString()
                }
                catch {
                    Write-Verbose "Could not load assembly for AssemblyVersion: $_"
                }

                $result.Found = $true
            }
            catch {
                Write-Warning "Failed to read version from $dllPath`: $_"
            }
        }
        else {
            Write-Verbose "Assembly not found: $dllPath"
        }

        $results += $result
    }

    # Cleanup temp directory if we extracted
    if ($ExtractFrom -and $effectiveDir -and (Test-Path $effectiveDir)) {
        Remove-Item -Path $effectiveDir -Recurse -Force -ErrorAction SilentlyContinue
    }

    return $results
}

function Get-PluginPinnedVersions {
    <#
    .SYNOPSIS
        Reads pinned package versions from Directory.Packages.props.

    .PARAMETER RepoRoot
        Path to the plugin repository root (containing Directory.Packages.props).

    .PARAMETER PackagesPropsPath
        Direct path to Directory.Packages.props file. If specified, RepoRoot is ignored.

    .PARAMETER Packages
        Array of package IDs to look up. Defaults to FluentValidation and NLog.

    .OUTPUTS
        Array of PSCustomObject with: PackageId, PinnedVersion

    .EXAMPLE
        Get-PluginPinnedVersions -RepoRoot "D:/repos/qobuzarr"
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $false)]
        [string]$RepoRoot,

        [Parameter(Mandatory = $false)]
        [string]$PackagesPropsPath,

        [Parameter(Mandatory = $false)]
        [array]$Packages = $script:DefaultHostCoupledPackages
    )

    if (-not $PackagesPropsPath) {
        if (-not $RepoRoot) {
            throw "Either -RepoRoot or -PackagesPropsPath must be specified."
        }
        $PackagesPropsPath = Join-Path $RepoRoot "Directory.Packages.props"
    }

    if (-not (Test-Path $PackagesPropsPath)) {
        throw "Directory.Packages.props not found at: $PackagesPropsPath"
    }

    [xml]$doc = Get-Content -Path $PackagesPropsPath -Raw
    $pinnedVersions = @{}

    foreach ($node in $doc.Project.ItemGroup.PackageVersion) {
        if ($node.Include -and $node.Version) {
            $pinnedVersions[$node.Include] = $node.Version
        }
    }

    $results = @()
    foreach ($pkg in $Packages) {
        $packageId = if ($pkg -is [hashtable] -or $pkg -is [PSCustomObject]) { $pkg.PackageId } else { $pkg }
        $results += [PSCustomObject]@{
            PackageId = $packageId
            PinnedVersion = $pinnedVersions[$packageId]
        }
    }

    return $results
}

function Compare-HostPluginVersions {
    <#
    .SYNOPSIS
        Compares host assembly versions against plugin-pinned versions.

    .PARAMETER HostVersions
        Output from Get-HostAssemblyVersions.

    .PARAMETER PinnedVersions
        Output from Get-PluginPinnedVersions.

    .PARAMETER MatchPolicy
        Version comparison policy:
        - MajorMinor: Passes if Major.Minor match (e.g., 9.5.4 vs 9.5.6 = PASS)
        - Exact: Requires exact version match (e.g., 9.5.4 vs 9.5.6 = FAIL)
        Default: MajorMinor

    .PARAMETER Strict
        Exit with non-zero code on any mismatch. Use in CI pipelines.

    .PARAMETER Format
        Output format: Table or Json.
        Default: Table

    .OUTPUTS
        Comparison results. With -Strict, exits with code 1 on mismatch.

    .EXAMPLE
        $host = Get-HostAssemblyVersions -HostAssembliesDir "./ext/Lidarr/_output/net8.0"
        $pinned = Get-PluginPinnedVersions -RepoRoot "."
        Compare-HostPluginVersions -HostVersions $host -PinnedVersions $pinned -MatchPolicy MajorMinor
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [array]$HostVersions,

        [Parameter(Mandatory = $true)]
        [array]$PinnedVersions,

        [Parameter(Mandatory = $false)]
        [ValidateSet('MajorMinor', 'Exact')]
        [string]$MatchPolicy = 'MajorMinor',

        [Parameter(Mandatory = $false)]
        [switch]$Strict,

        [Parameter(Mandatory = $false)]
        [ValidateSet('Table', 'Json')]
        [string]$Format = 'Table'
    )

    # Build lookup for pinned versions
    $pinnedLookup = @{}
    foreach ($p in $PinnedVersions) {
        $pinnedLookup[$p.PackageId] = $p.PinnedVersion
    }

    $results = @()
    $hasErrors = $false

    foreach ($host in $HostVersions) {
        $pinned = $pinnedLookup[$host.PackageId]

        # Get comparable host version (prefer FileVersion, fall back to ProductVersion, then AssemblyVersion)
        $hostVersion = Get-NormalizedVersion -Value $host.FileVersion
        if (-not $hostVersion) { $hostVersion = Get-NormalizedVersion -Value $host.ProductVersion }
        if (-not $hostVersion) { $hostVersion = Get-NormalizedVersion -Value $host.AssemblyVersion }

        $pinnedNormalized = Get-NormalizedVersion -Value $pinned

        $match = $false
        $status = 'UNKNOWN'

        if (-not $host.Found) {
            $status = 'HOST_NOT_FOUND'
            $hasErrors = $true
        }
        elseif (-not $pinned) {
            $status = 'NOT_PINNED'
            $hasErrors = $true
        }
        elseif (-not $hostVersion) {
            $status = 'HOST_VERSION_UNKNOWN'
            $hasErrors = $true
        }
        else {
            switch ($MatchPolicy) {
                'Exact' {
                    $match = ($hostVersion -eq $pinnedNormalized)
                }
                'MajorMinor' {
                    $hostMajorMinor = Get-MajorMinor -Value $hostVersion
                    $pinnedMajorMinor = Get-MajorMinor -Value $pinnedNormalized
                    $match = ($hostMajorMinor -eq $pinnedMajorMinor)
                }
            }

            if ($match) {
                $status = 'OK'
            }
            else {
                $status = 'MISMATCH'
                $hasErrors = $true
            }
        }

        $results += [PSCustomObject]@{
            PackageId = $host.PackageId
            Reason = $host.Reason
            PinnedVersion = $pinned
            HostVersion = $hostVersion
            HostFileVersion = $host.FileVersion
            HostProductVersion = $host.ProductVersion
            HostAssemblyVersion = $host.AssemblyVersion
            MatchPolicy = $MatchPolicy
            Status = $status
            Match = $match
        }
    }

    # Output results
    if ($Format -eq 'Json') {
        $output = [PSCustomObject]@{
            matchPolicy = $MatchPolicy
            hasErrors = $hasErrors
            results = @($results | ForEach-Object {
                [PSCustomObject]@{
                    packageId = $_.PackageId
                    reason = $_.Reason
                    pinnedVersion = $_.PinnedVersion
                    hostVersion = $_.HostVersion
                    status = $_.Status
                    match = $_.Match
                }
            })
        }
        return ($output | ConvertTo-Json -Depth 5)
    }
    else {
        Write-Host ""
        Write-Host "=== Host Version Compatibility Check ===" -ForegroundColor Cyan
        Write-Host "Match Policy: $MatchPolicy" -ForegroundColor Gray
        Write-Host ""

        foreach ($r in $results) {
            $color = switch ($r.Status) {
                'OK' { 'Green' }
                'MISMATCH' { 'Red' }
                default { 'Yellow' }
            }

            Write-Host "$($r.PackageId)" -ForegroundColor White
            Write-Host "  Reason: $($r.Reason)" -ForegroundColor Gray
            Write-Host "  Pinned: $($r.PinnedVersion)" -ForegroundColor White
            Write-Host "  Host:   $($r.HostVersion)" -ForegroundColor White
            Write-Host "  Status: $($r.Status)" -ForegroundColor $color
            Write-Host ""
        }

        if ($hasErrors) {
            Write-Host "=== ISSUES FOUND ===" -ForegroundColor Red
        }
        else {
            Write-Host "=== ALL VERSIONS OK ===" -ForegroundColor Green
        }

        if ($Strict -and $hasErrors) {
            exit 1
        }

        return $results
    }
}

function Get-NormalizedVersion {
    <#
    .SYNOPSIS
        Extracts numeric version prefix from version string.
    #>
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }

    # Remove build metadata (everything after +)
    $cleaned = ($Value -split '\+')[0]

    # Extract Major.Minor.Patch or Major.Minor.Patch.Build
    $match = [regex]::Match($cleaned, '(\d+\.\d+\.\d+(?:\.\d+)?)')
    if ($match.Success) {
        return $match.Groups[1].Value
    }

    # Fall back to Major.Minor
    $match = [regex]::Match($cleaned, '(\d+\.\d+)')
    if ($match.Success) {
        return $match.Groups[1].Value
    }

    return $null
}

function Get-MajorMinor {
    <#
    .SYNOPSIS
        Extracts Major.Minor from version string.
    #>
    param([string]$Value)

    if ([string]::IsNullOrWhiteSpace($Value)) { return $null }

    $parts = $Value -split '\.'
    if ($parts.Count -ge 2) {
        return "$($parts[0]).$($parts[1])"
    }

    return $null
}

# Convenience function for single-call usage
function Test-HostVersionCompatibility {
    <#
    .SYNOPSIS
        One-stop function to check host version compatibility.

    .DESCRIPTION
        Combines Get-HostAssemblyVersions, Get-PluginPinnedVersions, and
        Compare-HostPluginVersions into a single convenient call.

    .PARAMETER RepoRoot
        Path to the plugin repository root.

    .PARAMETER HostAssembliesDir
        Path to host assemblies directory.

    .PARAMETER ExtractFrom
        Docker image tag to extract host assemblies from.

    .PARAMETER MatchPolicy
        Version comparison policy: MajorMinor or Exact. Default: MajorMinor

    .PARAMETER Strict
        Exit with non-zero code on mismatch.

    .PARAMETER Format
        Output format: Table or Json. Default: Table

    .EXAMPLE
        Test-HostVersionCompatibility -RepoRoot "." -HostAssembliesDir "ext/Lidarr/_output/net8.0"

    .EXAMPLE
        Test-HostVersionCompatibility -RepoRoot "." -ExtractFrom "pr-plugins-3.1.1.4884" -Strict
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$RepoRoot,

        [Parameter(Mandatory = $false)]
        [string]$HostAssembliesDir,

        [Parameter(Mandatory = $false)]
        [string]$ExtractFrom,

        [Parameter(Mandatory = $false)]
        [ValidateSet('MajorMinor', 'Exact')]
        [string]$MatchPolicy = 'MajorMinor',

        [Parameter(Mandatory = $false)]
        [switch]$Strict,

        [Parameter(Mandatory = $false)]
        [ValidateSet('Table', 'Json')]
        [string]$Format = 'Table'
    )

    $hostParams = @{}
    if ($HostAssembliesDir) { $hostParams.HostAssembliesDir = $HostAssembliesDir }
    if ($ExtractFrom) { $hostParams.ExtractFrom = $ExtractFrom }

    $hostVersions = Get-HostAssemblyVersions @hostParams
    $pinnedVersions = Get-PluginPinnedVersions -RepoRoot $RepoRoot

    Compare-HostPluginVersions `
        -HostVersions $hostVersions `
        -PinnedVersions $pinnedVersions `
        -MatchPolicy $MatchPolicy `
        -Strict:$Strict `
        -Format $Format
}

Export-ModuleMember -Function @(
    'Get-HostAssemblyVersions',
    'Get-PluginPinnedVersions',
    'Compare-HostPluginVersions',
    'Test-HostVersionCompatibility'
)
