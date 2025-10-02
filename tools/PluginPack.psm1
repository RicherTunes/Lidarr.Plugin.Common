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

    dotnet publish $projectPath -c $Configuration -f $Framework -o $publishDirectory `
        /p:CopyLocalLockFileAssemblies=true `
        /p:ContinuousIntegrationBuild=true | Out-Null

    if ($LASTEXITCODE -ne 0) {
        throw "dotnet publish failed for $Csproj ($Framework|$Configuration)."
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

    Get-ChildItem -LiteralPath $publishPath -Filter 'Lidarr.Plugin.Abstractions*.dll' | Remove-Item -Force -ErrorAction SilentlyContinue

    $projectXml = [xml](Get-Content -LiteralPath $csprojPath)
    $assemblyName = $projectXml.Project.PropertyGroup.AssemblyName | Select-Object -Last 1
    if (-not $assemblyName) { $assemblyName = [IO.Path]::GetFileNameWithoutExtension($csprojPath) }
    $versionNode = $projectXml.Project.PropertyGroup.Version | Select-Object -Last 1
    if (-not $versionNode) { $versionNode = $projectXml.Project.PropertyGroup.AssemblyVersion | Select-Object -Last 1 }
    $version = $versionNode
    if (-not $version) { $version = '0.0.0' }

    if ($MergeAssemblies.IsPresent) {
        Invoke-PluginMerge -PublishPath $publishPath -AssemblyName $assemblyName -IlRepackRsp $IlRepackRsp -InternalizeExclude $InternalizeExclude
    }

    $manifestJson = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    $pluginId = if ($manifestJson.id) { $manifestJson.id } else { $assemblyName }

    $packageRoot = Join-Path (Split-Path -Parent $csprojPath) 'artifacts/packages'
    if (-not (Test-Path -LiteralPath $packageRoot)) {
        New-Item -ItemType Directory -Path $packageRoot -Force | Out-Null
    }

    $zipName = "{0}-{1}-{2}.zip" -f $pluginId, $version, $Framework
    $zipPath = Join-Path $packageRoot $zipName
    if (Test-Path -LiteralPath $zipPath) {
        Remove-Item -LiteralPath $zipPath -Force
    }

    Compress-Archive -Path (Join-Path $publishPath '*') -DestinationPath $zipPath
    Write-Host "Created plugin package: $zipPath" -ForegroundColor Green
    return $zipPath
}

function Invoke-PluginMerge {
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

    $mergeCandidates = Get-ChildItem -LiteralPath $PublishPath -Filter *.dll |
        Where-Object {
            $_.FullName -ne $pluginAssembly -and
            $_.Name -notmatch '^System\.' -and
            $_.Name -notmatch '^Microsoft\.' -and
            $_.Name -notmatch '^Lidarr\.Plugin\.Abstractions'
        } |
        Sort-Object Name

    if ($mergeCandidates.Count -eq 0) {
        Write-Host 'No candidate assemblies found for ILRepack. Skipping merge.' -ForegroundColor Yellow
        return
    }

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

Export-ModuleMember -Function Get-PluginOutput, Test-PluginManifest, New-PluginPackage
# end-snippet
