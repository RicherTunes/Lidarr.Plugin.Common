# snippet-skip-compile
# snippet:plugin-pack
function Get-PluginOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Csproj,

        [string]$Framework = 'net8.0',
        [string]$Configuration = 'Release'
    )

    $projectPath = Resolve-Path -LiteralPath $Csproj
    $projectDirectory = Split-Path -Parent $projectPath
    $publishDirectory = Join-Path $projectDirectory "artifacts/publish/$Framework/$Configuration"
    # Always start from a clean publish folder. `dotnet build -o` does not remove
    # stale files from previous runs, which can accidentally ship old manifests or assets.
    if (Test-Path -LiteralPath $publishDirectory) {
        Remove-Item -LiteralPath $publishDirectory -Recurse -Force -ErrorAction SilentlyContinue
    }
    New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null

    # Use `dotnet build` instead of `dotnet publish` so projects using PluginPackaging.targets
    # (ILRepack) produce the same merged output that is used in real plugin deployment.
    dotnet build $projectPath -c $Configuration -f $Framework -o $publishDirectory `
        /p:CopyLocalLockFileAssemblies=true `
        /p:ContinuousIntegrationBuild=true | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet build failed for $Csproj ($Framework|$Configuration)."
    }

    return $publishDirectory
}

function Test-PluginManifest {
    <#
    .SYNOPSIS
    Validates plugin manifest against schema and optionally verifies entrypoints.

    .PARAMETER Csproj
        Path to the .csproj project file.

    .PARAMETER Manifest
        Path to the plugin.json manifest file.

    .PARAMETER AbstractionsPackage
        Name of the Abstractions NuGet package. Default: "Lidarr.Plugin.Abstractions"

    .PARAMETER PublishPath
        Path to the publish output directory (for entrypoint resolution).

    .PARAMETER ResolveEntryPoints
        When specified, verifies entrypoint types exist in the built assembly.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Csproj,

        [Parameter(Mandatory = $true)]
        [string]$Manifest,

        [string]$AbstractionsPackage = 'Lidarr.Plugin.Abstractions',

        [string]$PublishPath,

        [switch]$ResolveEntryPoints
    )

    $scriptRoot = Split-Path -Parent $PSCommandPath
    $manifestScript = Join-Path $scriptRoot 'ManifestCheck.ps1'

    $params = @{
        ProjectPath = $Csproj
        ManifestPath = $Manifest
        AbstractionsPackage = $AbstractionsPackage
    }

    if ($PublishPath) {
        $params['PublishPath'] = $PublishPath
    }
    if ($ResolveEntryPoints) {
        $params['ResolveEntryPoints'] = $true
    }

    & $manifestScript @params
    if ($LASTEXITCODE -ne 0) {
        throw "Manifest validation failed for $Manifest."
    }
}

function New-PluginPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Csproj,

        [Parameter(Mandatory = $true)]
        [string]$Manifest,

        [string]$Framework = 'net8.0',
        [string]$Configuration = 'Release',

        [switch]$MergeAssemblies,
        [string]$IlRepackRsp = 'tools/ilrepack.rsp',
        [string]$InternalizeExclude = 'tools/internalize.exclude',

        # Canonical Abstractions injection parameters
        [string]$CanonicalAbstractionsVersion,
        [string]$CanonicalAbstractionsSha256,
        [string]$CanonicalAbstractionsPath,
        [switch]$RequireCanonicalAbstractions,

        # Entrypoint validation
        [switch]$ResolveEntryPoints,

        # Optional: keep additional runtime assemblies as separate DLLs (advanced).
        # Most plugins should prefer merging/internalizing via PluginPackaging.targets.
        [string[]]$AdditionalKeepAssemblies = @()
    )

    $csprojPath = Resolve-Path -LiteralPath $Csproj
    $manifestPath = Resolve-Path -LiteralPath $Manifest
    $publishPath = Get-PluginOutput -Csproj $csprojPath -Framework $Framework -Configuration $Configuration

    # Basic manifest validation first (before any assembly modifications)
    Test-PluginManifest -Csproj $csprojPath -Manifest $manifestPath

    # Ensure the validated manifest is included in the final package.
    # Some repos generate `plugin.json` outside the publish output (e.g., bin/plugin.json),
    # which would otherwise pass validation but never be shipped.
    Copy-Item -LiteralPath $manifestPath -Destination (Join-Path $publishPath 'plugin.json') -Force

    # Parse project metadata FIRST (before any cleanup/merge operations)  
    $projectXml = [xml](Get-Content -LiteralPath $csprojPath)
    $assemblyName = $projectXml.Project.PropertyGroup.AssemblyName | Select-Object -Last 1
    if (-not $assemblyName) { $assemblyName = [IO.Path]::GetFileNameWithoutExtension($csprojPath) }
    
    # Resolve Version: try XML parsing first, then MSBuild evaluation as fallback
    # MSBuild evaluation handles Directory.Build.props, VERSION files, and conditional properties
    $version = $null
    $versionNode = $projectXml.Project.PropertyGroup.Version | Select-Object -Last 1
    if (-not $versionNode) { $versionNode = $projectXml.Project.PropertyGroup.AssemblyVersion | Select-Object -Last 1 }
    if ($versionNode) {
        $rawVersion = $null
        if ($versionNode -is [System.Xml.XmlNode]) {
            $rawVersion = $versionNode.InnerText
        } else {
            $rawVersion = "$versionNode"
        }
        $rawVersion = $rawVersion.Trim()
        # Check if the value contains unresolved MSBuild expressions like $(Version), $(VersionPrefix), etc.
        if ($rawVersion -and $rawVersion -notmatch '\$\(') {
            $version = $rawVersion
        }
    }
    
    # Fallback: Use MSBuild to evaluate the Version property (handles Directory.Build.props, VERSION files, expressions, etc.)
    if (-not $version) {
        try {
            $msbuildOutput = & dotnet msbuild $csprojPath -getProperty:Version -nologo 2>&1
            if ($LASTEXITCODE -eq 0 -and $msbuildOutput) {
                $rawVersion = ($msbuildOutput | Out-String).Trim()
                # Extract semver pattern (X.Y.Z or X.Y.Z-suffix) from potentially noisy output
                if ($rawVersion -match '(\d+\.\d+\.\d+(?:-[\w\.\+]+)?)') {
                    $version = $matches[1]
                }
            }
        } catch {
            # MSBuild evaluation failed, continue to fallback
        }
    }
    
    # Final validation: ensure version is a valid semver-like string
    if (-not $version -or $version -notmatch '^\d+\.\d+\.\d+') { 
        $version = '0.0.0' 
        Write-Host "Warning: Could not determine valid version, using $version" -ForegroundColor Yellow
    }

    # Step 1: Merge assemblies (if requested) - BEFORE cleanup so deps exist
    if ($MergeAssemblies.IsPresent) {
        Invoke-PluginMerge -PublishPath $publishPath -AssemblyName $assemblyName -IlRepackRsp $IlRepackRsp -InternalizeExclude $InternalizeExclude
    }

    # Step 2: Clean up publish output AFTER merge - removes extra deps, keeps runtime deps
    # This matches PluginPackaging.targets behavior
    Invoke-PluginCleanup -PublishPath $publishPath -AssemblyName $assemblyName -AdditionalKeep $AdditionalKeepAssemblies

    # Step 3: Inject canonical Abstractions (if requested or required)
    # This ensures all plugins ship with the exact same Abstractions binary
    # Priority: 1) explicit parameters, 2) canonical-abstractions.json, 3) skip if not required
    $doCanonicalInjection = $CanonicalAbstractionsVersion -or $CanonicalAbstractionsPath -or $RequireCanonicalAbstractions

    $canonicalResult = $null
    if ($doCanonicalInjection) {
        $installParams = @{
            PublishPath = $publishPath
        }
        if ($CanonicalAbstractionsVersion) {
            $installParams['CommonVersion'] = $CanonicalAbstractionsVersion
        }
        if ($CanonicalAbstractionsSha256) {
            $installParams['ExpectedSha256'] = $CanonicalAbstractionsSha256
        }
        if ($CanonicalAbstractionsPath) {
            $installParams['CanonicalAbstractionsPath'] = $CanonicalAbstractionsPath
        }
        $canonicalResult = Install-CanonicalAbstractions @installParams
        Write-Host "Canonical Abstractions installed: v$($canonicalResult.Version)" -ForegroundColor Cyan
    }

    # Step 4: Entrypoint validation (after all assembly modifications)
    # Verifies that declared entrypoint types exist in the built assembly
    if ($ResolveEntryPoints) {
        Write-Host "Validating entrypoint types..." -ForegroundColor Cyan
        Test-PluginManifest -Csproj $csprojPath -Manifest $manifestPath -PublishPath $publishPath -ResolveEntryPoints
        Write-Host "Entrypoint validation passed" -ForegroundColor Green
    }

    $manifestJson = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $pluginId = if ($manifestJson.id) { $manifestJson.id } else { $assemblyName }

    $packageRoot = Join-Path (Split-Path -Parent $csprojPath) 'artifacts/packages'
    if (-not (Test-Path -LiteralPath $packageRoot)) {
        New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    }

    # Emit package metadata capturing assembly versions and commit SHA for smoke verification
    try {
        $commit = (git rev-parse --short=8 HEAD) 2>$null
        if (-not $commit) { $commit = $env:GITHUB_SHA.Substring(0,8) }
    } catch { $commit = $null }

    $assemblyInfos = @()
    Get-ChildItem -LiteralPath $publishPath -Filter *.dll | ForEach-Object {
        $fv = [System.Diagnostics.FileVersionInfo]::GetVersionInfo($_.FullName)
        $assemblyInfos += [pscustomobject]@{
            name = $_.Name
            fileVersion = $fv.FileVersion
            productVersion = $fv.ProductVersion
        }
    }

    $metadata = [pscustomobject]@{
        packageId = $pluginId
        version = $version
        framework = $Framework
        build = @{ commit = $(if ($commit) { $commit } else { 'unknown' }); date = (Get-Date).ToString('s') }
        assemblies = $assemblyInfos
    }
    $metadataPath = Join-Path $publishPath 'package-metadata.json'
    $metadata | ConvertTo-Json -Depth 5 | Set-Content -Path $metadataPath -Encoding UTF8

    $zipName = "{0}-{1}-{2}.zip" -f $pluginId, $version, $Framework
    $zipPath = Join-Path $packageRoot $zipName
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $zipPath
    Write-Host "Created plugin package: $zipPath" -ForegroundColor Green

    # Post-package verification: if canonical Abstractions was installed, verify it's in the ZIP
    if ($doCanonicalInjection -and $canonicalResult) {
        Write-Host "Verifying canonical Abstractions in package..." -ForegroundColor Cyan
        $verifyDir = Join-Path ([IO.Path]::GetTempPath()) "verify-package-$(Get-Random)"
        try {
            Expand-Archive -LiteralPath $zipPath -DestinationPath $verifyDir -Force
            Assert-CanonicalAbstractions -Path $verifyDir -ExpectedSha256 $canonicalResult.Sha256 | Out-Null
            Write-Host "[OK] Package verification passed" -ForegroundColor Green
        }
        finally {
            if (Test-Path $verifyDir) {
                Remove-Item -LiteralPath $verifyDir -Recurse -Force -ErrorAction SilentlyContinue
            }
        }
    }

    return $zipPath
}

function Invoke-PluginCleanup {
    <#
    .SYNOPSIS
    Cleans up the publish output to match PluginPackaging.targets behavior.

    .DESCRIPTION
    Implements the canonical plugin packaging policy (matches `build/PluginPackaging.targets`).

    MUST SHIP:
      - Lidarr.Plugin.<Name>.dll (merged assembly)
      - plugin.json
      - Lidarr.Plugin.Abstractions.dll (host does NOT provide this)

    MUST NOT SHIP:
      - FluentValidation.dll (host provides; shipping causes type-identity conflicts)
      - Microsoft.Extensions.DependencyInjection.Abstractions.dll (host provides; shipping breaks DI contracts)
      - Microsoft.Extensions.Logging.Abstractions.dll (host provides; shipping breaks ILogger contracts)
      - System.Text.Json.dll (cross-boundary type identity risk)
      - Host assemblies (Lidarr.*.dll, NzbDrone.*.dll, etc.)
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishPath,
        [Parameter(Mandatory = $true)]
        [string]$AssemblyName,
        [string[]]$AdditionalKeep = @()
    )

    # Canonical keep list - these assemblies belong in the package.
    # Everything else is either merged/internalized or host-provided.
    $keepPatterns = @(
        "$AssemblyName.dll",                                    # Plugin itself (merged)
        'Lidarr.Plugin.Abstractions.dll'                        # Required - host does NOT provide this
    )

    # Add any plugin-specific additional assemblies (maps to PluginPackagingAdditionalKeep)
    if ($AdditionalKeep.Count -gt 0) {
        $keepPatterns += $AdditionalKeep
    }

    # Guardrail: refuse to keep assemblies that are explicitly forbidden by the packaging policy.
    $forbiddenNames = @(
        'FluentValidation.dll',
        'Microsoft.Extensions.DependencyInjection.Abstractions.dll',
        'Microsoft.Extensions.Logging.Abstractions.dll',
        'System.Text.Json.dll'
    )
    foreach ($forbidden in $forbiddenNames) {
        $shouldKeepForbidden = $keepPatterns | Where-Object { $forbidden -like $_ } | Select-Object -First 1
        if ($shouldKeepForbidden) {
            throw "Refusing to keep forbidden host-shared assembly '$forbidden' (matches keep pattern '$shouldKeepForbidden')."
        }
    }

    # Remove everything except kept assemblies
    $allDlls = Get-ChildItem -LiteralPath $PublishPath -Filter '*.dll'
    foreach ($dll in $allDlls) {
        $isKept = $false
        foreach ($pattern in $keepPatterns) {
            if ($dll.Name -like $pattern) {
                $isKept = $true
                break
            }
        }
        if (-not $isKept) {
            Write-Host "  Removing: $($dll.Name)" -ForegroundColor DarkGray
            Remove-Item -LiteralPath $dll.FullName -Force -ErrorAction SilentlyContinue
            
            # Also remove matching .pdb and .xml
            $pdb = [IO.Path]::ChangeExtension($dll.FullName, '.pdb')
            $xml = [IO.Path]::ChangeExtension($dll.FullName, '.xml')
            if (Test-Path -LiteralPath $pdb) { Remove-Item -LiteralPath $pdb -Force -ErrorAction SilentlyContinue }
            if (Test-Path -LiteralPath $xml) { Remove-Item -LiteralPath $xml -Force -ErrorAction SilentlyContinue }
        }
    }

    # Remove deps.json (not needed for plugin)
    Get-ChildItem -LiteralPath $PublishPath -Filter '*.deps.json' | Remove-Item -Force -ErrorAction SilentlyContinue

    # Remove NuGet pack artifacts (not used at runtime, and can appear in output when OutDir is overridden)
    Get-ChildItem -LiteralPath $PublishPath -Filter '*.nupkg' | Remove-Item -Force -ErrorAction SilentlyContinue
    Get-ChildItem -LiteralPath $PublishPath -Filter '*.snupkg' | Remove-Item -Force -ErrorAction SilentlyContinue

    # Remove runtimes folder (native dependencies should be handled by host)
    $runtimesPath = Join-Path $PublishPath 'runtimes'
    if (Test-Path -LiteralPath $runtimesPath) {
        Remove-Item -LiteralPath $runtimesPath -Recurse -Force -ErrorAction SilentlyContinue
    }

    Write-Host "Cleanup complete. Kept assemblies:" -ForegroundColor Cyan
    Get-ChildItem -LiteralPath $PublishPath -Filter '*.dll' | ForEach-Object { Write-Host "  $($_.Name)" -ForegroundColor Green }
}

function Invoke-PluginMerge {
    <#
    .SYNOPSIS
    Merges plugin dependencies using ILRepack, matching PluginPackaging.targets behavior.
    
    .DESCRIPTION
    Merges assemblies that should be internalized into the plugin:
    - Lidarr.Plugin.Common.dll
    - Polly*.dll
    - TagLibSharp*.dll
    - Microsoft.Extensions.DependencyInjection.dll (implementation, not abstractions)
    - Microsoft.Extensions.Caching.*.dll
    - Microsoft.Extensions.Options.dll
    - Microsoft.Extensions.Primitives.dll
    - Microsoft.Extensions.Http.dll
    
    Does NOT merge (type identity with host):
    - Lidarr.Plugin.Abstractions.dll
    - FluentValidation.dll
    - Microsoft.Extensions.DependencyInjection.Abstractions.dll
    - Microsoft.Extensions.Logging.Abstractions.dll
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishPath,
        [Parameter(Mandatory = $true)]
        [string]$AssemblyName,
        [Parameter(Mandatory = $true)]
        [string]$IlRepackRsp,
        [Parameter(Mandatory = $true)]
        [string]$InternalizeExclude
    )

    $pluginAssembly = Join-Path $PublishPath "$AssemblyName.dll"
    if (-not (Test-Path -LiteralPath $pluginAssembly)) {
        throw "Plugin assembly '$AssemblyName.dll' not found under $PublishPath."
    }

    # Assemblies to merge (matching PluginPackaging.targets _PluginDeps)
    # These get internalized into the plugin assembly
    # NOTE: Do NOT merge System.Text.Json - its types may cross the host/plugin boundary
    #       and cause TypeLoadException or weird serialization issues
    $mergePatterns = @(
        'Lidarr.Plugin.Common.dll',
        'Polly.dll',
        'Polly.Core.dll', 
        'Polly.Extensions.Http.dll',
        'TagLibSharp*.dll',
        'Microsoft.Extensions.DependencyInjection.dll',  # Implementation, not abstractions
        'Microsoft.Extensions.Caching.Abstractions.dll',
        'Microsoft.Extensions.Caching.Memory.dll',
        'Microsoft.Extensions.Options.dll',
        'Microsoft.Extensions.Primitives.dll',
        'Microsoft.Extensions.Http.dll'
        # System.Text.Json.dll - EXCLUDED: types cross host/plugin boundary
    )

    $mergeCandidates = @()
    foreach ($pattern in $mergePatterns) {
        $matches = Get-ChildItem -LiteralPath $PublishPath -Filter $pattern -ErrorAction SilentlyContinue
        $mergeCandidates += $matches
    }

    if ($mergeCandidates.Count -eq 0) {
        Write-Host 'No candidate assemblies found for ILRepack. Skipping merge.' -ForegroundColor Yellow
        return
    }

    # Verify ilrepack is available before attempting merge
    $ilrepackPath = Get-Command 'ilrepack' -ErrorAction SilentlyContinue
    if (-not $ilrepackPath) {
        throw @"
ILRepack not found on PATH. The -MergeAssemblies flag requires ILRepack to be installed.

Options:
  1. (Recommended) Don't use -MergeAssemblies. MSBuild PluginPackaging.targets handles
     assembly merging during the build step automatically.
  2. Install an ILRepack CLI and ensure the `ilrepack` executable is on PATH.

Most plugins should NOT use -MergeAssemblies since the csproj build already merges.
"@
    }

    Write-Host "Merging assemblies:" -ForegroundColor Cyan
    $mergeCandidates | ForEach-Object { Write-Host "  $($_.Name)" -ForegroundColor DarkGray }

    $mergeOut = Join-Path $PublishPath "$AssemblyName.merged.dll"
    $resolvedRsp = Resolve-Path -LiteralPath $IlRepackRsp
    $resolvedExclude = Resolve-Path -LiteralPath $InternalizeExclude

    $rspContent = Get-Content -LiteralPath $resolvedRsp -Raw
    $rspContent = $rspContent.Replace('$(PublishDir)', $PublishPath).Replace('$(MergeOut)', $mergeOut).Replace('$(InternalizeExclude)', $resolvedExclude)
    $tempRsp = Join-Path ([IO.Path]::GetTempPath()) ([IO.Path]::GetRandomFileName() + '.rsp')
    Set-Content -Path $tempRsp -Value $rspContent -Encoding UTF8

    $argumentList = @("@$tempRsp")
    $argumentList += $pluginAssembly
    $argumentList += $mergeCandidates.FullName

    & ilrepack $argumentList
    $exit = $LASTEXITCODE

    Remove-Item -LiteralPath $tempRsp -ErrorAction SilentlyContinue

    if ($exit -ne 0) {
        throw "ilrepack failed with exit code $exit."
    }

    Move-Item -LiteralPath $mergeOut -Destination $pluginAssembly -Force
    $mergedPdb = [IO.Path]::ChangeExtension($mergeOut, '.pdb')
    if (Test-Path -LiteralPath $mergedPdb) {
        $targetPdb = [IO.Path]::ChangeExtension($pluginAssembly, '.pdb')
        Move-Item -LiteralPath $mergedPdb -Destination $targetPdb -Force
    }

    foreach ($candidate in $mergeCandidates) {
        Remove-Item -LiteralPath $candidate.FullName -Force
        $pdbPath = [IO.Path]::ChangeExtension($candidate.FullName, '.pdb')
        if (Test-Path -LiteralPath $pdbPath) {
            Remove-Item -LiteralPath $pdbPath -Force
        }
    }
}

function Get-CanonicalAbstractionsConfig {
    <#
    .SYNOPSIS
    Reads the canonical-abstractions.json configuration file.

    .DESCRIPTION
    Returns the pinned version, Common SHA, and Abstractions SHA256 from the
    canonical-abstractions.json file. This ensures reproducible builds.
    #>
    [CmdletBinding()]
    param()

    $scriptRoot = Split-Path -Parent $PSCommandPath
    $configPath = Join-Path $scriptRoot 'canonical-abstractions.json'

    if (-not (Test-Path $configPath)) {
        return $null
    }

    try {
        $config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
        return @{
            Version = $config.version
            CommonSha = $config.commonSha
            AbstractionsSha256 = $config.abstractionsSha256
            ReleaseUrl = $config.releaseUrl
        }
    }
    catch {
        Write-Warning "Failed to parse canonical-abstractions.json: $_"
        return $null
    }
}

function Install-CanonicalAbstractions {
    <#
    .SYNOPSIS
    Injects the canonical Lidarr.Plugin.Abstractions.dll into the publish output.

    .DESCRIPTION
    Downloads the canonical Abstractions DLL from a Common release (or uses a local cache),
    replaces the build output's copy, and verifies the SHA256 hash matches. This ensures all
    plugins use the exact same Abstractions binary, eliminating drift.

    HARD GATE: Build fails if verification fails.

    .PARAMETER PublishPath
        Directory containing the plugin build output.

    .PARAMETER CommonVersion
        Version of Common to download Abstractions from (e.g., "1.5.0").
        If not provided, reads from canonical-abstractions.json.

    .PARAMETER ExpectedSha256
        Expected SHA256 hash. If not provided, reads from canonical-abstractions.json
        or uses the hash from the release's .sha256 file.

    .PARAMETER CanonicalAbstractionsPath
        Optional local path to a pre-downloaded Abstractions.dll.
        Use this for offline builds or Docker environments without GitHub access.

    .PARAMETER CacheDirectory
        Optional directory to cache downloaded Abstractions. Defaults to $env:TEMP/canonical-abstractions-cache.
        If the file exists and hash matches, download is skipped.

    .PARAMETER Repository
        GitHub repository for Common. Defaults to "RicherTunes/Lidarr.Plugin.Common".
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishPath,

        [string]$CommonVersion,

        [string]$ExpectedSha256,

        [string]$CanonicalAbstractionsPath,

        [string]$CacheDirectory,

        [string]$Repository = "RicherTunes/Lidarr.Plugin.Common"
    )

    $ErrorActionPreference = 'Stop'
    $scriptRoot = Split-Path -Parent $PSCommandPath

    # Load config from canonical-abstractions.json if parameters not provided
    $config = Get-CanonicalAbstractionsConfig
    if (-not $CommonVersion -and $config) {
        $CommonVersion = $config.Version
        Write-Host "Using version from canonical-abstractions.json: $CommonVersion" -ForegroundColor DarkGray
    }
    if (-not $ExpectedSha256 -and $config) {
        $ExpectedSha256 = $config.AbstractionsSha256
        $sha256Preview = $ExpectedSha256.Substring(0, 16) + '...'
        Write-Host "Using SHA256 from canonical-abstractions.json: $sha256Preview" -ForegroundColor DarkGray
    }

    if (-not $CommonVersion) {
        throw "CommonVersion is required. Provide -CommonVersion or ensure canonical-abstractions.json exists."
    }

    Write-Host "Installing canonical Abstractions from Common v$CommonVersion..." -ForegroundColor Cyan

    # Option 1: Use local path if provided
    if ($CanonicalAbstractionsPath -and (Test-Path $CanonicalAbstractionsPath)) {
        Write-Host "Using local Abstractions from: $CanonicalAbstractionsPath" -ForegroundColor DarkGray
        $canonicalDll = $CanonicalAbstractionsPath
    }
    else {
        # Option 2: Check cache directory
        if (-not $CacheDirectory) {
            $CacheDirectory = Join-Path ([IO.Path]::GetTempPath()) 'canonical-abstractions-cache'
        }
        $cachedDll = Join-Path $CacheDirectory "v$CommonVersion" 'Lidarr.Plugin.Abstractions.dll'

        if ((Test-Path $cachedDll) -and $ExpectedSha256) {
            $cachedHash = (Get-FileHash -Path $cachedDll -Algorithm SHA256).Hash.ToLower()
            if ($cachedHash -eq $ExpectedSha256.ToLower()) {
                Write-Host "Using cached Abstractions (hash verified)" -ForegroundColor DarkGray
                $canonicalDll = $cachedDll
            }
        }

        # Option 3: Download from GitHub
        if (-not $canonicalDll -or -not (Test-Path $canonicalDll)) {
            $tempDir = Join-Path ([IO.Path]::GetTempPath()) "canonical-abstractions-$(Get-Random)"
            New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

            try {
                $getCanonicalScript = Join-Path $scriptRoot 'Get-CanonicalAbstractions.ps1'

                if (-not (Test-Path $getCanonicalScript)) {
                    throw "Get-CanonicalAbstractions.ps1 not found at $getCanonicalScript"
                }

                $canonicalDll = & $getCanonicalScript -Version $CommonVersion -OutputPath $tempDir -Repository $Repository
                if (-not $canonicalDll -or -not (Test-Path $canonicalDll)) {
                    throw "Failed to download canonical Abstractions.dll"
                }

                # Cache for future use
                $cacheDir = Join-Path $CacheDirectory "v$CommonVersion"
                if (-not (Test-Path $cacheDir)) {
                    New-Item -ItemType Directory -Path $cacheDir -Force | Out-Null
                }
                Copy-Item -LiteralPath $canonicalDll -Destination (Join-Path $cacheDir 'Lidarr.Plugin.Abstractions.dll') -Force
                $pdbPath = Join-Path $tempDir 'Lidarr.Plugin.Abstractions.pdb'
                if (Test-Path $pdbPath) {
                    Copy-Item -LiteralPath $pdbPath -Destination (Join-Path $cacheDir 'Lidarr.Plugin.Abstractions.pdb') -Force
                }
                Write-Host "Cached Abstractions for future builds" -ForegroundColor DarkGray
            }
            finally {
                if ((Test-Path $tempDir) -and $tempDir -ne $CacheDirectory) {
                    Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
                }
            }
        }
    }

    # Calculate hash of DLL
    $actualHash = (Get-FileHash -Path $canonicalDll -Algorithm SHA256).Hash.ToLower()

    # Verify against expected hash if provided
    if ($ExpectedSha256) {
        $expected = $ExpectedSha256.ToLower().Trim()
        if ($actualHash -ne $expected) {
            throw "CANONICAL ABSTRACTIONS SHA256 MISMATCH!`nExpected: $expected`nActual:   $actualHash`nThis is a HARD GATE failure - packaging cannot proceed."
        }
        Write-Host "[OK] SHA256 verified against expected hash" -ForegroundColor Green
    }

    # Replace the build output's Abstractions.dll
    $targetDll = Join-Path $PublishPath 'Lidarr.Plugin.Abstractions.dll'
    Copy-Item -LiteralPath $canonicalDll -Destination $targetDll -Force

    # Also copy PDB if available (check cache or local path)
    $pdbSource = $null
    if ($CanonicalAbstractionsPath) {
        $pdbSource = [IO.Path]::ChangeExtension($CanonicalAbstractionsPath, '.pdb')
    }
    elseif ($CacheDirectory) {
        $pdbSource = Join-Path $CacheDirectory "v$CommonVersion" 'Lidarr.Plugin.Abstractions.pdb'
    }
    if ($pdbSource -and (Test-Path $pdbSource)) {
        $targetPdb = Join-Path $PublishPath 'Lidarr.Plugin.Abstractions.pdb'
        Copy-Item -LiteralPath $pdbSource -Destination $targetPdb -Force
    }

    Write-Host "[OK] Installed canonical Abstractions.dll (SHA256: $actualHash)" -ForegroundColor Green

    return @{
        Path = $targetDll
        Sha256 = $actualHash
        Version = $CommonVersion
    }
}

function Assert-CanonicalAbstractions {
    <#
    .SYNOPSIS
    Verifies the Abstractions.dll in publish output matches the canonical hash.

    .DESCRIPTION
    HARD GATE: Fails the build if the hash doesn't match the expected canonical hash.
    Use this after packaging to verify the ZIP contains the correct Abstractions.

    .PARAMETER Path
        Path to the Abstractions.dll to verify (or directory containing it).

    .PARAMETER ExpectedSha256
        Expected SHA256 hash (canonical hash from Common release).
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$ExpectedSha256
    )

    $ErrorActionPreference = 'Stop'

    # Handle both file and directory paths
    $dllPath = if (Test-Path $Path -PathType Container) {
        Join-Path $Path 'Lidarr.Plugin.Abstractions.dll'
    } else {
        $Path
    }

    if (-not (Test-Path $dllPath)) {
        throw "HARD GATE FAILURE: Abstractions.dll not found at $dllPath"
    }

    $actualHash = (Get-FileHash -Path $dllPath -Algorithm SHA256).Hash.ToLower()
    $expected = $ExpectedSha256.ToLower().Trim()

    if ($actualHash -ne $expected) {
        throw @"
╔═══════════════════════════════════════════════════════════════════════════╗
║                    CANONICAL ABSTRACTIONS SHA256 MISMATCH                  ║
╠═══════════════════════════════════════════════════════════════════════════╣
║  Expected: $expected                         ║
║  Actual:   $actualHash                         ║
╠═══════════════════════════════════════════════════════════════════════════╣
║  HARD GATE: Packaging cannot proceed.                                      ║
║  The Abstractions.dll in the package does not match the canonical binary.  ║
║  Use Install-CanonicalAbstractions to inject the correct version.          ║
╚═══════════════════════════════════════════════════════════════════════════╝
"@
    }

    $hashPrefix = $expected.Substring(0, 16) + '...'
    Write-Host "[OK] Abstractions.dll verified: SHA256 matches canonical ($hashPrefix)" -ForegroundColor Green
    return $true
}

Export-ModuleMember -Function Get-PluginOutput, Test-PluginManifest, New-PluginPackage, Invoke-PluginCleanup, Get-CanonicalAbstractionsConfig, Install-CanonicalAbstractions, Assert-CanonicalAbstractions
# end-snippet
