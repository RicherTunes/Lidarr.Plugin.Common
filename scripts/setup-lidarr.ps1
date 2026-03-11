[CmdletBinding()]
param(
    [string]$Branch = "plugins",
    [string]$Repository = "https://github.com/Lidarr/Lidarr.git",
    [string]$Destination = "..\Lidarr",
    [switch]$SkipBuild,
    [switch]$SkipStub
)

$ErrorActionPreference = 'Stop'

function Assert-Command {
    param([Parameter(Mandatory = $true)][string]$Name)
    if (-not (Get-Command $Name -ErrorAction SilentlyContinue)) {
        throw "Required command '$Name' was not found in PATH."
    }
}

$scriptRoot = Split-Path -Parent $MyInvocation.MyCommand.Path
$repoRoot = Split-Path $scriptRoot -Parent
if (-not (Test-Path (Join-Path $repoRoot 'lidarr.plugin.common.sln'))) {
    throw 'Run scripts/setup-lidarr.ps1 from the repository root.'
}

Push-Location $repoRoot
try {
    Assert-Command git
    Assert-Command dotnet

    $destPath = if ([System.IO.Path]::IsPathRooted($Destination)) { $Destination } else { Join-Path $repoRoot $Destination }
    $destPath = [System.IO.Path]::GetFullPath($destPath)
    $destParent = Split-Path -Parent $destPath
    if (-not (Test-Path $destParent)) {
        [System.IO.Directory]::CreateDirectory($destParent) | Out-Null
    }

    $gitDir = Join-Path $destPath '.git'
    if ((Test-Path $destPath) -and -not (Test-Path $gitDir)) {
        Write-Host "Destination exists but is not a git checkout; removing $destPath" -ForegroundColor Yellow
        Remove-Item -Recurse -Force $destPath
    }

    if (-not (Test-Path $destPath)) {
        Write-Host "Cloning Lidarr repository ($Branch) to $destPath" -ForegroundColor Yellow
        git clone --branch $Branch --depth 1 $Repository $destPath
    }
    else {
        Write-Host "Updating Lidarr repository at $destPath" -ForegroundColor Yellow
        Push-Location $destPath
        try {
            git fetch origin $Branch
            git reset --hard origin/$Branch
        }
        finally {
            Pop-Location
        }
    }

    if (-not $SkipBuild) {
        Write-Host "Building Lidarr (Release)" -ForegroundColor Yellow
        Push-Location (Join-Path $destPath 'src')
        try {
            dotnet restore Lidarr.sln
            dotnet build Lidarr.sln -c Release
        }
        finally {
            Pop-Location
        }
    }

    $lidarrOutputs = @(
        Join-Path $destPath '_output/net8.0'
        Join-Path $destPath 'src/Lidarr/bin/Release/net8.0'
    )

    $lidarrPath = $null
    foreach ($candidate in $lidarrOutputs) {
        if (Test-Path (Join-Path $candidate 'Lidarr.Core.dll')) {
            $lidarrPath = [System.IO.Path]::GetFullPath($candidate)
            break
        }
    }

    if (-not $lidarrPath) {
        $stubPath = [System.IO.Path]::Combine($destPath, '_output/net8.0')
        if (-not $SkipStub) {
            Write-Host "No Lidarr build output detected; generating host stub" -ForegroundColor Yellow
            & (Join-Path $repoRoot 'scripts/prepare-host-stub.ps1') -OutputPath $stubPath | Out-Null
        }
        if (Test-Path (Join-Path $stubPath 'Lidarr.HostStub.dll')) {
            $lidarrPath = [System.IO.Path]::GetFullPath($stubPath)
        }
    }

    if (-not $lidarrPath) {
        throw 'Unable to locate Lidarr host assemblies after setup.'
    }

    $env:LIDARR_PATH = $lidarrPath
    [Environment]::SetEnvironmentVariable('LIDARR_PATH', $lidarrPath, 'Process')
    try { [Environment]::SetEnvironmentVariable('LIDARR_PATH', $lidarrPath, 'User') } catch { }
    Write-Host "LIDARR_PATH set to $lidarrPath" -ForegroundColor Green
    return $lidarrPath
}
finally {
    Pop-Location
}
