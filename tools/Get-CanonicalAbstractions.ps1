<#
.SYNOPSIS
    Downloads the canonical Lidarr.Plugin.Abstractions.dll from GitHub Releases.

.DESCRIPTION
    Fetches the byte-identical Abstractions DLL from a Common release and verifies
    its SHA256 checksum. This ensures all plugins use the exact same Abstractions
    binary, eliminating "byte-identical Abstractions" drift.

.PARAMETER Version
    The Common version to download (e.g., "1.5.0"). Required.

.PARAMETER OutputPath
    Directory to save the DLL. Defaults to current directory.

.PARAMETER Repository
    GitHub repository in owner/repo format. Defaults to "RicherTunes/Lidarr.Plugin.Common".

.PARAMETER SkipVerify
    Skip SHA256 verification (not recommended).

.EXAMPLE
    ./Get-CanonicalAbstractions.ps1 -Version 1.5.0 -OutputPath ./lib

.EXAMPLE
    # Use ext-common-sha.txt to determine version
    $sha = Get-Content ext-common-sha.txt -Raw | ForEach-Object { $_.Trim() }
    ./Get-CanonicalAbstractions.ps1 -Version (Get-VersionFromSha $sha) -OutputPath ./lib
#>
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [string]$OutputPath = ".",

    [string]$Repository = "RicherTunes/Lidarr.Plugin.Common",

    [switch]$SkipVerify
)

$ErrorActionPreference = 'Stop'

# Normalize version (strip leading 'v' if present)
$Version = $Version.TrimStart('v')
$Tag = "v$Version"

# Create output directory if needed
if (-not (Test-Path $OutputPath)) {
    New-Item -ItemType Directory -Path $OutputPath -Force | Out-Null
}

$DllPath = Join-Path $OutputPath "Lidarr.Plugin.Abstractions.dll"
$Sha256Path = Join-Path $OutputPath "Lidarr.Plugin.Abstractions.dll.sha256"
$PdbPath = Join-Path $OutputPath "Lidarr.Plugin.Abstractions.pdb"

Write-Host "Downloading canonical Abstractions from $Repository $Tag..." -ForegroundColor Cyan

# Check if gh CLI is available
$ghAvailable = $null -ne (Get-Command gh -ErrorAction SilentlyContinue)

$downloaded = $false

if ($ghAvailable) {
    # Use gh CLI for authenticated downloads (handles rate limiting better)
    Write-Host "Using GitHub CLI for download"

    try {
        & gh release download $Tag `
            --repo $Repository `
            --pattern "Lidarr.Plugin.Abstractions.dll" `
            --pattern "Lidarr.Plugin.Abstractions.dll.sha256" `
            --pattern "Lidarr.Plugin.Abstractions.pdb" `
            --dir $OutputPath `
            --clobber

        if ($LASTEXITCODE -ne 0) {
            throw "gh release download failed with exit code $LASTEXITCODE"
        }
        $downloaded = $true
    }
    catch {
        Write-Host "gh download failed: $_" -ForegroundColor Yellow
        Write-Host "Falling back to direct HTTP download..." -ForegroundColor Yellow
    }
}

if (-not $downloaded) {
    # Fallback to direct HTTP download (works for public repos without authentication)
    Write-Host "Using direct HTTP download"

    $BaseUrl = "https://github.com/$Repository/releases/download/$Tag"

    try {
        Invoke-WebRequest -Uri "$BaseUrl/Lidarr.Plugin.Abstractions.dll" -OutFile $DllPath
        Invoke-WebRequest -Uri "$BaseUrl/Lidarr.Plugin.Abstractions.dll.sha256" -OutFile $Sha256Path
        Invoke-WebRequest -Uri "$BaseUrl/Lidarr.Plugin.Abstractions.pdb" -OutFile $PdbPath -ErrorAction SilentlyContinue
    }
    catch {
        $statusCode = $_.Exception.Response.StatusCode.value__
        if ($statusCode -eq 404) {
            Write-Error "Release $Tag not found or missing Abstractions assets."
            Write-Host "Ensure version $Version has been released with Abstractions DLL."
        }
        elseif ($statusCode -eq 403) {
            Write-Error "Rate limited by GitHub. Try again later or install gh CLI for authenticated access."
        }
        else {
            Write-Error "HTTP error $statusCode while downloading: $_"
        }
        exit 1
    }
}

# Verify SHA256
if (-not $SkipVerify) {
    if (-not (Test-Path $Sha256Path)) {
        Write-Error "SHA256 checksum file not found. Cannot verify integrity."
        exit 1
    }

    Write-Host "Verifying SHA256 checksum..." -ForegroundColor Cyan

    # Read expected hash from file (format: "<hash>  <filename>" or "<hash> *<filename>")
    $checksumContent = Get-Content $Sha256Path -Raw
    $expectedHash = ($checksumContent -split '\s+')[0].Trim().ToLower()

    # Calculate actual hash
    $actualHash = (Get-FileHash -Path $DllPath -Algorithm SHA256).Hash.ToLower()

    if ($expectedHash -ne $actualHash) {
        Write-Error "SHA256 MISMATCH!"
        Write-Host "Expected: $expectedHash" -ForegroundColor Red
        Write-Host "Actual:   $actualHash" -ForegroundColor Red
        Write-Host "The downloaded file may be corrupted or tampered with."
        Remove-Item $DllPath -Force -ErrorAction SilentlyContinue
        exit 1
    }

    Write-Host "✓ SHA256 verified: $expectedHash" -ForegroundColor Green
}

# Output summary
$fileInfo = Get-Item $DllPath
Write-Host ""
Write-Host "✓ Downloaded canonical Abstractions DLL" -ForegroundColor Green
Write-Host "  Version:  $Version"
Write-Host "  Size:     $([math]::Round($fileInfo.Length / 1KB, 2)) KB"
Write-Host "  Path:     $DllPath"

# Return the DLL path for use in scripts
return $DllPath
