<#
.SYNOPSIS
    Verifies that multiple plugin packages contain the identical canonical Abstractions.dll.

.DESCRIPTION
    Extracts Lidarr.Plugin.Abstractions.dll from each plugin ZIP and compares SHA256 hashes.
    This ensures all plugins use the exact same Abstractions binary, preventing type identity
    conflicts at runtime.

    HARD GATE: Fails if any package has a different hash.

.PARAMETER PackagePaths
    Array of paths to plugin ZIP files to verify.

.PARAMETER ExpectedSha256
    Optional: Expected canonical SHA256 hash. If provided, all packages must match this hash.
    If not provided, all packages must match each other.

.EXAMPLE
    ./Verify-CanonicalAbstractions.ps1 -PackagePaths @("Qobuzarr-1.0.0.zip", "Tidalarr-1.0.0.zip", "Brainarr-1.0.0.zip")

.EXAMPLE
    # Verify against known canonical hash
    ./Verify-CanonicalAbstractions.ps1 -PackagePaths @("*.zip") -ExpectedSha256 "251bf049c28737ac1912074733adf04f099f54c801c914ac9c0e056b2a8232db"
#>
param(
    [Parameter(Mandatory = $true)]
    [string[]]$PackagePaths,

    [string]$ExpectedSha256
)

$ErrorActionPreference = 'Stop'

# Resolve glob patterns
$resolvedPaths = @()
foreach ($pattern in $PackagePaths) {
    $matches = Get-ChildItem -Path $pattern -ErrorAction SilentlyContinue
    if ($matches) {
        $resolvedPaths += $matches.FullName
    }
    elseif (Test-Path $pattern) {
        $resolvedPaths += (Resolve-Path $pattern).Path
    }
    else {
        Write-Warning "No files found matching: $pattern"
    }
}

if ($resolvedPaths.Count -eq 0) {
    Write-Error "No package files found to verify."
    exit 1
}

Write-Host "Verifying canonical Abstractions.dll across $($resolvedPaths.Count) package(s)..." -ForegroundColor Cyan
Write-Host ""

$results = @()

foreach ($zipPath in $resolvedPaths) {
    $zipName = [IO.Path]::GetFileName($zipPath)
    Write-Host "  Checking: $zipName" -ForegroundColor Gray

    $tempDir = Join-Path ([IO.Path]::GetTempPath()) "verify-abstractions-$(Get-Random)"
    try {
        Expand-Archive -LiteralPath $zipPath -DestinationPath $tempDir -Force

        $abstractionsDll = Join-Path $tempDir 'Lidarr.Plugin.Abstractions.dll'
        if (-not (Test-Path $abstractionsDll)) {
            Write-Host "    ⚠️  Abstractions.dll not found in package" -ForegroundColor Yellow
            $results += [pscustomobject]@{
                Package = $zipName
                Hash = $null
                Status = "MISSING"
            }
            continue
        }

        $hash = (Get-FileHash -Path $abstractionsDll -Algorithm SHA256).Hash.ToLower()
        $results += [pscustomobject]@{
            Package = $zipName
            Hash = $hash
            Status = "OK"
        }
        Write-Host "    SHA256: $hash" -ForegroundColor DarkGray
    }
    finally {
        if (Test-Path $tempDir) {
            Remove-Item -LiteralPath $tempDir -Recurse -Force -ErrorAction SilentlyContinue
        }
    }
}

Write-Host ""

# Verify results
$validResults = $results | Where-Object { $_.Status -eq "OK" }
$missingResults = $results | Where-Object { $_.Status -eq "MISSING" }

if ($missingResults.Count -gt 0) {
    Write-Host "⚠️  Packages missing Abstractions.dll:" -ForegroundColor Yellow
    $missingResults | ForEach-Object { Write-Host "    - $($_.Package)" -ForegroundColor Yellow }
    Write-Host ""
}

if ($validResults.Count -eq 0) {
    Write-Error "No packages with valid Abstractions.dll found."
    exit 1
}

$uniqueHashes = $validResults | Select-Object -ExpandProperty Hash -Unique

if ($ExpectedSha256) {
    $expected = $ExpectedSha256.ToLower().Trim()

    $mismatches = $validResults | Where-Object { $_.Hash -ne $expected }
    if ($mismatches.Count -gt 0) {
        Write-Host "╔═══════════════════════════════════════════════════════════════════════════╗" -ForegroundColor Red
        Write-Host "║              CANONICAL ABSTRACTIONS VERIFICATION FAILED                   ║" -ForegroundColor Red
        Write-Host "╠═══════════════════════════════════════════════════════════════════════════╣" -ForegroundColor Red
        Write-Host "║  Expected: $expected" -ForegroundColor Red
        Write-Host "╠═══════════════════════════════════════════════════════════════════════════╣" -ForegroundColor Red
        Write-Host "║  Mismatched packages:                                                      ║" -ForegroundColor Red
        $mismatches | ForEach-Object {
            Write-Host "║    $($_.Package): $($_.Hash)" -ForegroundColor Red
        }
        Write-Host "╚═══════════════════════════════════════════════════════════════════════════╝" -ForegroundColor Red
        exit 1
    }

    Write-Host "╔═══════════════════════════════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║              CANONICAL ABSTRACTIONS VERIFICATION PASSED                   ║" -ForegroundColor Green
    Write-Host "╠═══════════════════════════════════════════════════════════════════════════╣" -ForegroundColor Green
    Write-Host "║  All $($validResults.Count) package(s) contain canonical Abstractions.dll              ║" -ForegroundColor Green
    Write-Host "║  SHA256: $expected" -ForegroundColor Green
    Write-Host "╚═══════════════════════════════════════════════════════════════════════════╝" -ForegroundColor Green
}
elseif ($uniqueHashes.Count -gt 1) {
    Write-Host "╔═══════════════════════════════════════════════════════════════════════════╗" -ForegroundColor Red
    Write-Host "║              ABSTRACTIONS SHA256 MISMATCH DETECTED                        ║" -ForegroundColor Red
    Write-Host "╠═══════════════════════════════════════════════════════════════════════════╣" -ForegroundColor Red
    Write-Host "║  Found $($uniqueHashes.Count) different hashes across packages:                           ║" -ForegroundColor Red
    foreach ($hash in $uniqueHashes) {
        $packages = ($validResults | Where-Object { $_.Hash -eq $hash }).Package -join ", "
        Write-Host "║    $hash" -ForegroundColor Red
        Write-Host "║      -> $packages" -ForegroundColor Red
    }
    Write-Host "╠═══════════════════════════════════════════════════════════════════════════╣" -ForegroundColor Red
    Write-Host "║  HARD GATE: All plugins MUST use identical Abstractions.dll               ║" -ForegroundColor Red
    Write-Host "╚═══════════════════════════════════════════════════════════════════════════╝" -ForegroundColor Red
    exit 1
}
else {
    Write-Host "╔═══════════════════════════════════════════════════════════════════════════╗" -ForegroundColor Green
    Write-Host "║              ABSTRACTIONS CONSISTENCY VERIFIED                            ║" -ForegroundColor Green
    Write-Host "╠═══════════════════════════════════════════════════════════════════════════╣" -ForegroundColor Green
    Write-Host "║  All $($validResults.Count) package(s) have identical Abstractions.dll                 ║" -ForegroundColor Green
    Write-Host "║  SHA256: $($uniqueHashes[0])" -ForegroundColor Green
    Write-Host "╚═══════════════════════════════════════════════════════════════════════════╝" -ForegroundColor Green
}

# Return results for use in pipelines
return $results
