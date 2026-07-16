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

    # Drift-free-starting-point contract: a 6th plugin must be BORN with the ecosystem
    # guards. Assert the parity test subclass, both CI workflow scaffolds, and the
    # parity-checked root files all materialized (dotnet-new default excludes could
    # silently drop dot-directories; this catches that class of regression).
    $requiredScaffoldFiles = @(
        "tests/$PluginName.Tests/${PluginName}EcosystemParityTests.cs",
        ".gitea/workflows/ci.yml",
        ".github/workflows/ci.yml",
        "Directory.Build.props",
        "Directory.Packages.props",
        "global.json",
        "VERSION"
    )
    foreach ($relative in $requiredScaffoldFiles) {
        $candidate = Join-Path $outputRoot $relative
        if (-not (Test-Path -LiteralPath $candidate)) {
            throw "Materialized template is missing required scaffold file: $relative"
        }
    }

    # Structural sanity for the CI scaffolds (same cheap checks as the F2 workflow gate:
    # a workflow without on:/jobs: keys is silently ignored by both platforms).
    foreach ($workflowRelative in @(".gitea/workflows/ci.yml", ".github/workflows/ci.yml")) {
        $workflowText = Get-Content -LiteralPath (Join-Path $outputRoot $workflowRelative) -Raw
        if ($workflowText -notmatch "(?m)^on:") {
            throw "$workflowRelative is missing a top-level 'on:' trigger key"
        }
        if ($workflowText -notmatch "(?m)^jobs:") {
            throw "$workflowRelative is missing a top-level 'jobs:' key"
        }
        if ($workflowText -notmatch [regex]::Escape($PluginName)) {
            throw "$workflowRelative was not template-tokenized (no '$PluginName' project paths)"
        }
    }
    $githubMirrorText = Get-Content -LiteralPath (Join-Path $outputRoot ".github/workflows/ci.yml") -Raw
    if ($githubMirrorText -notmatch [regex]::Escape("github.server_url == 'https://github.com'")) {
        throw ".github/workflows/ci.yml mirror is missing the github.com server guard"
    }

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

    # Prove the parity guard actually EXECUTED (a filter that matches nothing still
    # exits 0, so a silently-dropped parity class would otherwise go unnoticed).
    Write-Host "> dotnet test $testProject -c $Configuration --no-build --filter Category=Parity"
    $parityOutput = & dotnet test $testProject -c $Configuration --no-build --filter "Category=Parity" 2>&1 | ForEach-Object { "$_" }
    $parityOutput | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) {
        throw "Parity test lane failed with exit code $LASTEXITCODE"
    }
    if (-not ($parityOutput -match 'Passed:\s*[1-9]')) {
        throw "Parity lane ran zero tests - the ${PluginName}EcosystemParityTests class did not execute"
    }
} finally {
    $env:DOTNET_CLI_HOME = $oldDotnetHome
    $env:NUGET_PACKAGES = $oldNugetPackages
}
