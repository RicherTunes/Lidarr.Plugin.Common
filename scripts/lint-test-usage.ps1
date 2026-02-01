<#
.SYNOPSIS
    Lint workflow files for raw dotnet test usage without the shared runner.

.DESCRIPTION
    This script scans GitHub Actions workflow files for raw `dotnet test` commands
    that don't use the shared test runner (ext/Lidarr.Plugin.Common/scripts/test.ps1).

    Allowlist entries require an expiry date and reason. Expired entries fail the lint.

.PARAMETER WorkflowDir
    Directory containing workflow files. Defaults to .github/workflows

.PARAMETER AllowlistFile
    Path to allowlist JSON file. Defaults to .github/dotnet-test-allowlist.json

.NOTES
    CANONICAL LOCATION: ext/Lidarr.Plugin.Common/scripts/lint-test-usage.ps1
    Do NOT copy this file - always reference from Common submodule.

    Allowlist format:
    {
      "entries": [
        {
          "file": "ci.yml",
          "reason": "tooling-required",
          "expires": "2026-06-30",
          "context": "ReportGenerator needs dotnet test /p:CollectCoverage=true"
        }
      ]
    }
#>

param(
    [string]$WorkflowDir = ".github/workflows",
    [string]$AllowlistFile = ".github/dotnet-test-allowlist.json",
    [switch]$CI
)

$ErrorActionPreference = "Stop"

# Load allowlist if exists
$allowlist = @()
if (Test-Path $AllowlistFile) {
    $json = Get-Content $AllowlistFile -Raw | ConvertFrom-Json
    $allowlist = $json.entries
}

# Check for expired allowlist entries
$today = Get-Date
$expiredEntries = @()
foreach ($entry in $allowlist) {
    if ($entry.expires) {
        $expiryDate = [DateTime]::Parse($entry.expires)
        if ($expiryDate -lt $today) {
            $expiredEntries += $entry
        }
    }
}

if ($expiredEntries.Count -gt 0) {
    Write-Host "::error::EXPIRED ALLOWLIST ENTRIES:" -ForegroundColor Red
    foreach ($e in $expiredEntries) {
        Write-Host "  - $($e.file): expired $($e.expires) (reason: $($e.reason))" -ForegroundColor Red
    }
    if ($CI) { exit 1 }
}

# Get all workflow files
if (-not (Test-Path $WorkflowDir)) {
    Write-Host "[SKIP] No workflow directory found at $WorkflowDir"
    exit 0
}

$workflowFiles = Get-ChildItem -Path $WorkflowDir -Filter "*.yml" -ErrorAction SilentlyContinue
if ($workflowFiles.Count -eq 0) {
    Write-Host "[SKIP] No workflow files found"
    exit 0
}

# Pattern to detect raw dotnet test usage
$rawTestPattern = 'dotnet\s+test(?!\s*-)'  # 'dotnet test' not immediately followed by continuation
$sharedRunnerPatterns = @(
    'ext/Lidarr.Plugin.Common/scripts/test.ps1',
    'ext\\Lidarr.Plugin.Common\\scripts\\test.ps1',
    'scripts/test.ps1'  # Local wrapper that calls shared runner
)

$violations = @()

foreach ($file in $workflowFiles) {
    $content = Get-Content $file.FullName -Raw
    $lines = Get-Content $file.FullName

    # Skip if file uses shared runner
    $usesSharedRunner = $false
    foreach ($pattern in $sharedRunnerPatterns) {
        if ($content -match [regex]::Escape($pattern)) {
            $usesSharedRunner = $true
            break
        }
    }

    # Check for raw dotnet test
    for ($i = 0; $i -lt $lines.Count; $i++) {
        $line = $lines[$i]

        # Skip YAML metadata lines (comments, descriptions, names)
        if ($line -match '^\s*#' -or $line -match '^\s*description:' -or $line -match '^\s*name:' -or $line -match "^\s*-\s+name:") {
            continue
        }

        if ($line -match 'dotnet\s+test') {
            # Check if this is an allowlisted file
            $isAllowlisted = $false
            foreach ($entry in $allowlist) {
                if ($file.Name -eq $entry.file) {
                    $expiryDate = if ($entry.expires) { [DateTime]::Parse($entry.expires) } else { $null }
                    if ($null -eq $expiryDate -or $expiryDate -ge $today) {
                        $isAllowlisted = $true
                        Write-Host "[ALLOW] $($file.Name):$($i+1) - allowlisted until $($entry.expires) ($($entry.reason))"
                    }
                    break
                }
            }

            if (-not $isAllowlisted) {
                $violations += [PSCustomObject]@{
                    File = $file.Name
                    Line = $i + 1
                    Content = $line.Trim()
                    UsesSharedRunner = $usesSharedRunner
                }
            }
        }
    }
}

if ($violations.Count -gt 0) {
    Write-Host ""
    Write-Host "::error::RAW DOTNET TEST VIOLATIONS FOUND:" -ForegroundColor Red
    Write-Host ""
    foreach ($v in $violations) {
        Write-Host "  $($v.File):$($v.Line)" -ForegroundColor Yellow
        Write-Host "    $($v.Content)" -ForegroundColor Gray
        if ($v.UsesSharedRunner) {
            Write-Host "    Note: File also uses shared runner - consider removing raw call" -ForegroundColor Cyan
        }
    }
    Write-Host ""
    Write-Host "To fix: Use ext/Lidarr.Plugin.Common/scripts/test.ps1 instead of raw dotnet test" -ForegroundColor Cyan
    Write-Host "To allowlist: Add entry to $AllowlistFile with expiry date and reason" -ForegroundColor Cyan
    Write-Host ""

    if ($CI) { exit 1 }
    return $false
}

Write-Host "[OK] No raw dotnet test violations found in $($workflowFiles.Count) workflow files" -ForegroundColor Green
return $true
