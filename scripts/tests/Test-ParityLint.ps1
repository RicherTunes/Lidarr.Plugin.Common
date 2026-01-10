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

    # Test 4: Skips *.Tests.cs files but scans test helpers
    Write-Host "`n[TEST] Skips *.Tests.cs files..." -ForegroundColor Cyan
    # Unit test files (*.Tests.cs) should be skipped
    Add-ViolationFile -RepoPath $fakeRepo -RelPath 'src\Tests\BadSanitizerTests.cs' -Content @'
public class BadSanitizerTests
{
    var chars = Path.GetInvalidFileNameChars();
}
'@

    $result = & $lintScript -RepoPath $fakeRepo 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        Write-Host "  [PASS] *.Tests.cs files skipped (exit=0)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] *.Tests.cs files not skipped (exit=$exitCode)" -ForegroundColor Red
        $failed++
    }

    # Test 4b: Test helpers in Tests/ directory ARE scanned
    Write-Host "`n[TEST] Scans test helpers in Tests/ directory..." -ForegroundColor Cyan
    Add-ViolationFile -RepoPath $fakeRepo -RelPath 'src\Tests\TestHelper.cs' -Content @'
public class TestHelper
{
    var chars = Path.GetInvalidFileNameChars();
}
'@

    & $lintScript -RepoPath $fakeRepo | Out-Null
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 1) {
        Write-Host "  [PASS] Test helper violations detected (exit=1)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] Test helper violations not detected (exit=$exitCode)" -ForegroundColor Red
        $failed++
    }

    # Test 5: Skips single-line comment matches (// only)
    Write-Host "`n[TEST] Skips // line comments..." -ForegroundColor Cyan
    Remove-Item -Path (Join-Path $fakeRepo 'src\Tests') -Recurse -Force -ErrorAction SilentlyContinue
    Add-ViolationFile -RepoPath $fakeRepo -RelPath 'src\CommentedCode.cs' -Content @'
public class CommentedCode
{
    // Path.GetInvalidFileNameChars() - this is just a comment
    public string DoSomething() => "Hello";
}
'@

    $result = & $lintScript -RepoPath $fakeRepo 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        Write-Host "  [PASS] // comment matches skipped (exit=0)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] // comment matches not skipped (exit=$exitCode)" -ForegroundColor Red
        $failed++
    }

    # Test 5b: Block comments /* */ are NOT skipped (conservative - prefer false positives)
    Write-Host "`n[TEST] Flags /* */ block comments (conservative)..." -ForegroundColor Cyan
    Add-ViolationFile -RepoPath $fakeRepo -RelPath 'src\BlockCommented.cs' -Content @'
public class BlockCommented
{
    /*
     * Path.GetInvalidFileNameChars() in block comment - still flagged
     */
    public string DoSomething() => "Hello";
}
'@

    & $lintScript -RepoPath $fakeRepo | Out-Null
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 1) {
        Write-Host "  [PASS] Block comment match flagged (exit=1, conservative)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] Block comment not flagged - false negative! (exit=$exitCode)" -ForegroundColor Red
        $failed++
    }
    Remove-Item -Path (Join-Path $fakeRepo 'src\BlockCommented.cs') -Force

    # Test 6: Skips excluded directories (bin, obj, docs, scripts)
    Write-Host "`n[TEST] Skips excluded directories..." -ForegroundColor Cyan
    Remove-Item -Path (Join-Path $fakeRepo 'src\CommentedCode.cs') -Force
    Add-ViolationFile -RepoPath $fakeRepo -RelPath 'bin\Debug\BadCode.cs' -Content @'
public class BadCode { var chars = Path.GetInvalidFileNameChars(); }
'@
    Add-ViolationFile -RepoPath $fakeRepo -RelPath 'obj\Release\BadCode.cs' -Content @'
public class BadCode { var chars = Path.GetInvalidFileNameChars(); }
'@
    Add-ViolationFile -RepoPath $fakeRepo -RelPath 'docs\Example.cs' -Content @'
public class BadCode { var chars = Path.GetInvalidFileNameChars(); }
'@

    $result = & $lintScript -RepoPath $fakeRepo 2>&1
    $exitCode = $LASTEXITCODE

    if ($exitCode -eq 0) {
        Write-Host "  [PASS] Excluded directories skipped (exit=0)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] Excluded directories not skipped (exit=$exitCode)" -ForegroundColor Red
        $failed++
    }

    # Test 7: Nested path violations are detected (path normalization verified visually)
    Write-Host "`n[TEST] Nested path violations detected..." -ForegroundColor Cyan
    Remove-Item -Path (Join-Path $fakeRepo 'bin') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path (Join-Path $fakeRepo 'obj') -Recurse -Force -ErrorAction SilentlyContinue
    Remove-Item -Path (Join-Path $fakeRepo 'docs') -Recurse -Force -ErrorAction SilentlyContinue
    Add-ViolationFile -RepoPath $fakeRepo -RelPath 'src\nested\deep\BadCode.cs' -Content @'
public class BadCode { var chars = Path.GetInvalidFileNameChars(); }
'@

    & $lintScript -RepoPath $fakeRepo | Out-Null
    $exitCode = $LASTEXITCODE

    # Verify nested paths are scanned and violations detected
    if ($exitCode -eq 1) {
        Write-Host "  [PASS] Nested path violation detected (exit=1)" -ForegroundColor Green
    } else {
        Write-Host "  [FAIL] Nested path violation not detected (exit=$exitCode)" -ForegroundColor Red
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
