function Get-PluginOutput {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = True)]
        [string],

        [string] = 'net8.0',
        [string] = 'Release'
    )

     = Join-Path (Split-Path ) "artifacts/publish/"
    dotnet publish  -c  -f  -o  
        /p:CopyLocalLockFileAssemblies=true /p:ContinuousIntegrationBuild=true | Out-Null
    return 
}

function Test-PluginManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = True)]
        [string],

        [Parameter(Mandatory = True)]
        [string],

        [string] = 'Lidarr.Plugin.Abstractions'
    )

    & "/ManifestCheck.ps1" -ProjectPath  -ManifestPath  -AbstractionsPackage 
}

function New-PluginPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = True)]
        [string],

        [Parameter(Mandatory = True)]
        [string],

        [string] = 'net8.0',
        [string] = 'Release'
    )

     = Get-PluginOutput -Csproj  -Framework  -Configuration 
    Test-PluginManifest -Csproj  -Manifest 

    # Remove host-owned assemblies if they slipped into the publish directory
    Get-ChildItem  -Filter 'Lidarr.Plugin.Abstractions*.dll' | Remove-Item -Force -ErrorAction SilentlyContinue

    [xml] = Get-Content 
     = .Project.PropertyGroup.Version | Select-Object -Last 1
    if (-not ) {  = .Project.PropertyGroup.AssemblyVersion | Select-Object -Last 1 }
     = .InnerText
     = Split-Path  -LeafBase

     = Join-Path (Split-Path ) 'artifacts/packages'
    if (-not (Test-Path )) {
        New-Item -ItemType Directory -Path  | Out-Null
    }

     = "--.zip"
     = Join-Path  
    if (Test-Path ) {
        Remove-Item  -Force
    }

    Compress-Archive -Path (Join-Path  '*') -DestinationPath 
    Write-Host "Created plugin package: " -ForegroundColor Green
    return 
}

Export-ModuleMember -Function Get-PluginOutput, Test-PluginManifest, New-PluginPackage
