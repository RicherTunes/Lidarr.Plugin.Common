param(
    [string]$AssemblyVersion = "10.0.0.35686",
    [string]$FileVersion = "10.0.0.35686",
    [string]$OutputPath
)

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path $scriptRoot -Parent

if (-not $OutputPath -or [string]::IsNullOrWhiteSpace($OutputPath)) {
    $OutputPath = [System.IO.Path]::Combine($repoRoot, '..', 'Lidarr', '_output', 'net6.0')
}

$resolvedOutput = [System.IO.Path]::GetFullPath($OutputPath)
New-Item -ItemType Directory -Path $resolvedOutput -Force | Out-Null

$workingDir = [System.IO.Path]::Combine($repoRoot, 'artifacts', 'host-stub-build')
if (Test-Path $workingDir) {
    Remove-Item -Path $workingDir -Recurse -Force
}
New-Item -ItemType Directory -Path $workingDir | Out-Null

$projectFile = Join-Path $workingDir 'HostStub.csproj'
$projectContent = @"
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net6.0</TargetFramework>
    <ImplicitUsings>disable</ImplicitUsings>
    <Nullable>disable</Nullable>
    <AssemblyVersion>$AssemblyVersion</AssemblyVersion>
    <FileVersion>$FileVersion</FileVersion>
    <GenerateDocumentationFile>false</GenerateDocumentationFile>
  </PropertyGroup>
</Project>
"@
Set-Content -Path $projectFile -Value $projectContent -Encoding UTF8

$sourceFile = Join-Path $workingDir 'AssemblyAnchor.cs'
$sourceContent = @"
namespace Lidarr.Plugin.HostStub;

public static class AssemblyAnchor
{
    public static string Version => "$AssemblyVersion";
}
"@
Set-Content -Path $sourceFile -Value $sourceContent -Encoding UTF8

$buildResult = & dotnet build $projectFile -c Release --nologo 2>&1
if ($LASTEXITCODE -ne 0) {
    Write-Error "Failed to build host stub:`n$buildResult"
    exit $LASTEXITCODE
}

$builtAssembly = Join-Path $workingDir 'bin/Release/net6.0/HostStub.dll'
if (-not (Test-Path $builtAssembly)) {
    Write-Error "Expected stub assembly at $builtAssembly was not produced."
    exit 1
}

$destination = Join-Path $resolvedOutput 'Lidarr.HostStub.dll'
Copy-Item -LiteralPath $builtAssembly -Destination $destination -Force

Write-Host "Generated stub host assembly at $destination (AssemblyVersion=$AssemblyVersion)."
