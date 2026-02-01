#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Hermetic tests for canonical Abstractions injection in tools/PluginPack.psm1.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$pluginPackModule = Join-Path $PSScriptRoot '../../tools/PluginPack.psm1'
$failed = 0
$testRoot = $null

function Write-Pass([string]$message) { Write-Host "  [PASS] $message" -ForegroundColor Green }
function Write-Fail([string]$message) { Write-Host "  [FAIL] $message" -ForegroundColor Red; $script:failed++ }

try {
    Write-Host "===============================================" -ForegroundColor Cyan
    Write-Host "Test-PluginPackCanonicalAbstractions" -ForegroundColor Cyan
    Write-Host "===============================================" -ForegroundColor Cyan

    if (-not (Test-Path -LiteralPath $pluginPackModule)) {
        throw "PluginPack module not found at $pluginPackModule"
    }

    Import-Module $pluginPackModule -Force

    $testRoot = Join-Path ([IO.Path]::GetTempPath()) "pluginpack-canonical-$(New-Guid)"
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

    $publishPath = Join-Path $testRoot 'publish'
    New-Item -ItemType Directory -Path $publishPath -Force | Out-Null

    $localCanonical = Join-Path $testRoot 'Lidarr.Plugin.Abstractions.dll'
    $canonicalBytes = [byte[]](1..128)
    [IO.File]::WriteAllBytes($localCanonical, $canonicalBytes)
    $canonicalHash = (Get-FileHash -Path $localCanonical -Algorithm SHA256).Hash.ToLowerInvariant()

    # 1) Config parsing sanity
    Write-Host "`n[TEST] Get-CanonicalAbstractionsConfig parses pinned config..." -ForegroundColor Cyan
    $cfg = Get-CanonicalAbstractionsConfig
    if ($null -eq $cfg) {
        Write-Fail "Get-CanonicalAbstractionsConfig returned null"
    }
    elseif ([string]::IsNullOrWhiteSpace($cfg.Version) -or [string]::IsNullOrWhiteSpace($cfg.AbstractionsSha256)) {
        Write-Fail "Config missing required fields (Version/AbstractionsSha256)"
    }
    else {
        Write-Pass "Config loaded (v$($cfg.Version), sha=$($cfg.AbstractionsSha256.Substring(0, 8))...)"
    }

    # 2) Install using local path + explicit expected hash (no network)
    Write-Host "`n[TEST] Install-CanonicalAbstractions injects local DLL and verifies SHA..." -ForegroundColor Cyan
    try {
        $result = Install-CanonicalAbstractions `
            -PublishPath $publishPath `
            -CommonVersion '0.0.0-test' `
            -ExpectedSha256 $canonicalHash `
            -CanonicalAbstractionsPath $localCanonical

        $installedDll = Join-Path $publishPath 'Lidarr.Plugin.Abstractions.dll'
        if (-not (Test-Path -LiteralPath $installedDll)) {
            Write-Fail "Abstractions.dll not found in publish output"
        }
        else {
            $installedHash = (Get-FileHash -Path $installedDll -Algorithm SHA256).Hash.ToLowerInvariant()
            if ($installedHash -ne $canonicalHash) {
                Write-Fail "Installed hash mismatch: expected $canonicalHash got $installedHash"
            }
            elseif ($result.Sha256 -ne $canonicalHash) {
                Write-Fail "Result.Sha256 mismatch: expected $canonicalHash got $($result.Sha256)"
            }
            else {
                Write-Pass "DLL injected and verified"
            }
        }
    }
    catch {
        Write-Fail "Install-CanonicalAbstractions threw: $($_.Exception.Message)"
    }

    # 3) Assert hard-gates on mismatch
    Write-Host "`n[TEST] Assert-CanonicalAbstractions hard-gates on mismatch..." -ForegroundColor Cyan
    try {
        $installedDll = Join-Path $publishPath 'Lidarr.Plugin.Abstractions.dll'
        [IO.File]::WriteAllBytes($installedDll, [byte[]](2..129))
        Assert-CanonicalAbstractions -Path $publishPath -ExpectedSha256 $canonicalHash | Out-Null
        Write-Fail "Expected mismatch to throw, but it passed"
    }
    catch {
        Write-Pass "Mismatch threw as expected"
    }
}
finally {
    if ($testRoot -and (Test-Path -LiteralPath $testRoot)) {
        Remove-Item -LiteralPath $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}

if ($failed -gt 0) {
    Write-Host ""
    Write-Host "FAILED: $failed test(s)" -ForegroundColor Red
    exit 1
}

Write-Host ""
Write-Host "All tests passed." -ForegroundColor Green
exit 0

