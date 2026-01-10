#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Tests for parity-lint.ps1 - verifies it catches seeded violations.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$lintScript = Join-Path $PSScriptRoot '../parity-lint.ps1'
$testRoot = $null
$failed = 0

function New-TestRepo {
    param([string]$Root, [string]$Name)
    $repoPath = Join-Path $Root $Name
    New-Item -ItemType Directory -Path $repoPath -Force | Out-Null
    return $repoPath
}

function Add-ViolationFile {
    param([string]$RepoPath, [string]$RelPath, [string]$Content)
    $fullPath = Join-Path $RepoPath $RelPath
    $dir = Split-Path $fullPath -Parent
    if (-not (Test-Path $dir)) { New-Item -ItemType Directory -Path $dir -Force | Out-Null }
    Set-Content -Path $fullPath -Value $Content
}

try {
    Write-Host "========================================" -ForegroundColor Cyan
    Write-Host "Test-ParityLint: Seeded Violation Tests" -ForegroundColor Cyan
    Write-Host "========================================" -ForegroundColor Cyan

    # Create temp test structure
    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "parity-lint-test-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

    # Create a fake plugin repo with a seeded violation
    $fakeRepo = New-TestRepo -Root $testRoot -Name 'fakeplugin'

    # Test 1: Catches GetInvalidFileNameChars
    Write-Host "`n[TEST] Catches GetInvalidFileNameChars violation..." -ForegroundColor Cyan
    Add-ViolationFile -RepoPath $fakeRepo -RelPath 'src\BadSanitizer.cs' -Content @'
public class BadSanitizer
{
    public string Sanitize(string fileName)
    {
        var invalidChars = Path.GetInvalidFileNameChars();
        return string.Concat(fileName.Where(c => !invalidChars.Contains(c)));
    }
}
'@

    & $lintScript -RepoPath $fakeRepo | Out-Null
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 1) {
        Write-Host "  [PASS] Caught GetInvalidFileNameChars violation (exit=1)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] Expected exit=1, got exit=$exitCode" -ForegroundColor Red
        $failed++
    }

    # Test 2: Catches FLAC magic bytes
    Write-Host "`n[TEST] Catches FLAC magic bytes violation..." -ForegroundColor Cyan
    Remove-Item -Path (Join-Path $fakeRepo 'src\BadSanitizer.cs') -Force
    Add-ViolationFile -RepoPath $fakeRepo -RelPath 'src\BadValidator.cs' -Content @'
public class BadValidator
{
    private static readonly byte[] FlacMagic = new byte[] { 0x66, 0x4C, 0x61, 0x43 };

    public bool IsFlac(byte[] data)
    {
        return data.Take(4).SequenceEqual(FlacMagic);
    }
}
'@

    & $lintScript -RepoPath $fakeRepo | Out-Null
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 1) {
        Write-Host "  [PASS] Caught FLACMagicBytes violation (exit=1)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] Expected exit=1, got exit=$exitCode" -ForegroundColor Red
        $failed++
    }

    # Test 3: Passes on clean repo
    Write-Host "`n[TEST] Passes on clean repo..." -ForegroundColor Cyan
    Remove-Item -Path (Join-Path $fakeRepo 'src\BadValidator.cs') -Force
    Add-ViolationFile -RepoPath $fakeRepo -RelPath 'src\GoodCode.cs' -Content @'
public class GoodCode
{
    public string DoSomething() => "Hello";
}
'@

    $result = & $lintScript -RepoPath $fakeRepo 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        Write-Host "  [PASS] Clean repo passes (exit=0)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] Clean repo failed unexpectedly (exit=$exitCode)" -ForegroundColor Red
        Write-Host "  Output: $($result -join ' | ')" -ForegroundColor Gray
        $failed++
    }

    # Test 4: Skips test files
    Write-Host "`n[TEST] Skips Test files..." -ForegroundColor Cyan
    Add-ViolationFile -RepoPath $fakeRepo -RelPath 'src\Tests\BadSanitizerTests.cs' -Content @'
public class BadSanitizerTests
{
    // This uses Path.GetInvalidFileNameChars() but should be skipped
    var chars = Path.GetInvalidFileNameChars();
}
'@

    $result = & $lintScript -RepoPath $fakeRepo 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        Write-Host "  [PASS] Test files skipped (exit=0)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] Test files not skipped (exit=$exitCode)" -ForegroundColor Red
        $failed++
    }

    Write-Host "`n========================================" -ForegroundColor Cyan
    if ($failed -gt 0) {
        Write-Host "FAILED: $failed test(s) failed" -ForegroundColor Red
        exit 1
    }
    Write-Host "PASS: Test-ParityLint (all tests passed)" -ForegroundColor Green
    exit 0
}
finally {
    if ($testRoot -and (Test-Path $testRoot)) {
        Write-Host "`nCleaning up..." -ForegroundColor Gray
        Remove-Item -Recurse -Force $testRoot -ErrorAction SilentlyContinue
    }
}
