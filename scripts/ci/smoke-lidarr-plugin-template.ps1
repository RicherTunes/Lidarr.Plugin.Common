param(
    [string]$RepoRoot = (Get-Location).Path,
    [string]$Configuration = "Release",
    [string]$PluginName = "SmokeServiceArr"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Invoke-Checked {
    param(
        [Parameter(Mandatory = $true)]
        [string]$FilePath,

        [string[]]$Arguments
    )

    Write-Host "> $FilePath $($Arguments -join ' ')"
    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$FilePath exited with code $LASTEXITCODE"
    }
}

function Get-XmlProperty {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,

        [Parameter(Mandatory = $true)]
        [string]$Name
    )

    $xml = [xml](Get-Content -LiteralPath $Path -Raw)
    $node = $xml.Project.PropertyGroup |
        ForEach-Object { $_.$Name } |
        Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
        Select-Object -First 1

    if ([string]::IsNullOrWhiteSpace($node)) {
        throw "Could not find <$Name> in $Path"
    }

    return [string]$node
}

function Publish-LocalPackage {
    param(
        [Parameter(Mandatory = $true)]
        [string]$ProjectPath
    )

    Invoke-Checked dotnet @(
        "build",
        $ProjectPath,
        "-c",
        $Configuration,
        "-p:EnableSourceLink=false",
        "-p:RunAnalyzersDuringBuild=false",
        "-p:GeneratePackageOnBuild=false")

    Invoke-Checked dotnet @(
        "pack",
        $ProjectPath,
        "-c",
        $Configuration,
        "--no-build",
        "-o",
        $packageSource,
        "-p:EnableSourceLink=false",
        "-p:RunAnalyzersDuringBuild=false")
}

function Assert-NoTemplateBuildArtifacts {
    param(
        [Parameter(Mandatory = $true)]
        [string]$GeneratedRoot
    )

    $artifactDirs = Get-ChildItem -LiteralPath $GeneratedRoot -Directory -Recurse |
        Where-Object { $_.Name -in @("bin", "obj") } |
        Select-Object -ExpandProperty FullName

    if (@($artifactDirs).Count -gt 0) {
        throw "Template materialization included build artifact directories before build: $($artifactDirs -join ', ')"
    }
}

function Assert-NoPackedTemplateBuildArtifacts {
    param(
        [Parameter(Mandatory = $true)]
        [string]$PackagePath
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [System.IO.Compression.ZipFile]::OpenRead($PackagePath)
    try {
        $artifactEntries = $archive.Entries |
            Where-Object { $_.FullName -match '(^|/)(bin|obj)/' } |
            Select-Object -ExpandProperty FullName

        if (@($artifactEntries).Count -gt 0) {
            throw "Template package contains build artifacts: $($artifactEntries -join ', ')"
        }
    } finally {
        $archive.Dispose()
    }
}

$repo = (Resolve-Path -LiteralPath $RepoRoot).Path
$commonVersion = Get-XmlProperty -Path (Join-Path $repo "Directory.Build.props") -Name "Version"
$testKitVersion = Get-XmlProperty -Path (Join-Path $repo "testkit/Lidarr.Plugin.Common.TestKit.csproj") -Name "Version"

$templatePluginProject = Join-Path $repo "templates/lidarr-plugin/src/MyPlugin/MyPlugin.csproj"
$templateTestProject = Join-Path $repo "templates/lidarr-plugin/tests/MyPlugin.Tests/MyPlugin.Tests.csproj"
$pluginProjectText = Get-Content -LiteralPath $templatePluginProject -Raw
$testProjectText = Get-Content -LiteralPath $templateTestProject -Raw

if ($pluginProjectText -notmatch "<LidarrPluginCommonVersion>$([regex]::Escape($commonVersion))</LidarrPluginCommonVersion>") {
    throw "Template plugin project must default LidarrPluginCommonVersion to $commonVersion"
}

if ($testProjectText -notmatch "<LidarrPluginCommonTestKitVersion>$([regex]::Escape($testKitVersion))</LidarrPluginCommonTestKitVersion>") {
    throw "Template test project must default LidarrPluginCommonTestKitVersion to $testKitVersion"
}

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("lidarr-template-smoke-" + [Guid]::NewGuid().ToString("N"))
$packageSource = Join-Path $tempRoot "packages"
$outputRoot = Join-Path $tempRoot "generated"
$dotnetHome = Join-Path $tempRoot "dotnet-home"
$nugetPackages = Join-Path $tempRoot "nuget-packages"

New-Item -ItemType Directory -Path $packageSource, $outputRoot, $dotnetHome, $nugetPackages | Out-Null

$oldDotnetHome = $env:DOTNET_CLI_HOME
$oldNugetPackages = $env:NUGET_PACKAGES
$env:DOTNET_CLI_HOME = $dotnetHome
$env:NUGET_PACKAGES = $nugetPackages

try {
    Publish-LocalPackage -ProjectPath (Join-Path $repo "src/Abstractions/Lidarr.Plugin.Abstractions.csproj")
    Publish-LocalPackage -ProjectPath (Join-Path $repo "src/Lidarr.Plugin.Common.csproj")
    Publish-LocalPackage -ProjectPath (Join-Path $repo "testkit/Lidarr.Plugin.Common.TestKit.csproj")

    Invoke-Checked dotnet @(
        "pack",
        (Join-Path $repo "templates/Lidarr.Plugin.Templates/Lidarr.Plugin.Templates.csproj"),
        "-c",
        $Configuration,
        "-o",
        $packageSource,
        "-p:EnableSourceLink=false")

    $templatePackage = Get-ChildItem -LiteralPath $packageSource -Filter "RicherTunes.Lidarr.Plugin.Templates.*.nupkg" |
        Sort-Object LastWriteTimeUtc -Descending |
        Select-Object -First 1

    if ($null -eq $templatePackage) {
        throw "Template package was not produced in $packageSource"
    }

    Assert-NoPackedTemplateBuildArtifacts -PackagePath $templatePackage.FullName

    Invoke-Checked dotnet @("new", "install", $templatePackage.FullName, "--force")
    Invoke-Checked dotnet @("new", "lidarr-plugin", "-n", $PluginName, "-o", $outputRoot)

    $pluginProject = Join-Path $outputRoot "src/$PluginName/$PluginName.csproj"
    $testProject = Join-Path $outputRoot "tests/$PluginName.Tests/$PluginName.Tests.csproj"

    if (-not (Test-Path -LiteralPath $pluginProject)) {
        throw "Generated plugin project missing: $pluginProject"
    }

    if (-not (Test-Path -LiteralPath $testProject)) {
        throw "Generated test project missing: $testProject"
    }

    Assert-NoTemplateBuildArtifacts -GeneratedRoot $outputRoot

    $nugetConfig = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="local-template-smoke" value="$packageSource" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="lidarr-taglib" value="https://pkgs.dev.azure.com/Lidarr/Lidarr/_packaging/Taglib/nuget/v3/index.json" />
  </packageSources>
</configuration>
"@
    Set-Content -LiteralPath (Join-Path $outputRoot "NuGet.config") -Value $nugetConfig -Encoding UTF8

    Invoke-Checked dotnet @("build", $pluginProject, "-c", $Configuration)
    Invoke-Checked dotnet @("test", $testProject, "-c", $Configuration)
} finally {
    $env:DOTNET_CLI_HOME = $oldDotnetHome
    $env:NUGET_PACKAGES = $oldNugetPackages
}
