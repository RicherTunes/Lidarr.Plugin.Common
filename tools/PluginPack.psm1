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
    if (-not (Test-Path -LiteralPath $publishDirectory)) {
        New-Item -ItemType Directory -Path $publishDirectory -Force | Out-Null
    }

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
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$Csproj,

        [Parameter(Mandatory = $true)]
        [string]$Manifest,

        [string]$AbstractionsPackage = 'Lidarr.Plugin.Abstractions'
    )

    $scriptRoot = Split-Path -Parent $PSCommandPath
    $manifestScript = Join-Path $scriptRoot 'ManifestCheck.ps1'
    & $manifestScript -ProjectPath $Csproj -ManifestPath $Manifest -AbstractionsPackage $AbstractionsPackage
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
        [string]$InternalizeExclude = 'tools/internalize.exclude'
    )

    $csprojPath = Resolve-Path -LiteralPath $Csproj
    $manifestPath = Resolve-Path -LiteralPath $Manifest
    $publishPath = Get-PluginOutput -Csproj $csprojPath -Framework $Framework -Configuration $Configuration

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
    Invoke-PluginCleanup -PublishPath $publishPath -AssemblyName $assemblyName

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
    return $zipPath
}

function Invoke-PluginCleanup {
    <#
    .SYNOPSIS
    Cleans up the publish output to match PluginPackaging.targets behavior.
    
    .DESCRIPTION
    Removes assemblies that should not be in the final package:
    - Host assemblies (Lidarr.Core, Lidarr.Common, etc.)
    - Extra dependencies that were merged into the plugin DLL
    - System.* and other runtime assemblies provided by the host
    
    Keeps:
    - Plugin assembly (merged)
    - Lidarr.Plugin.Abstractions.dll (required for plugin discovery/loading; host image does not ship it)

    NOTE: Do NOT ship host-provided / cross-boundary assemblies (type identity).
    In multi-plugin scenarios, shipping these can cause load failures when a second plugin
    attempts to load another copy into the same load context.
      - Microsoft.Extensions.DependencyInjection.Abstractions.dll
      - Microsoft.Extensions.Logging.Abstractions.dll
      - FluentValidation.dll
    If a plugin ships its own FluentValidation.dll, ValidationFailure will have
    different type identity than the host's copy and can cause TypeLoadException
    such as: "Method 'Test' ... does not have an implementation."
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string]$PublishPath,
        [Parameter(Mandatory = $true)]
        [string]$AssemblyName
    )

    # Assemblies to KEEP in the package (must match PluginPackaging.targets _PluginRuntimeDeps).
    $keepAssemblies = @(
        "$AssemblyName.dll",                  # Plugin itself (merged)
        'Lidarr.Plugin.Abstractions.dll'       # Required for plugin discovery/loading
    )

    # Remove everything except kept assemblies
    $allDlls = Get-ChildItem -LiteralPath $PublishPath -Filter '*.dll'
    foreach ($dll in $allDlls) {
        if ($keepAssemblies -notcontains $dll.Name) {
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

Export-ModuleMember -Function Get-PluginOutput, Test-PluginManifest, New-PluginPackage, Invoke-PluginCleanup
# end-snippet
