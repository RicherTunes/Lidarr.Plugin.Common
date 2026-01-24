#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Unit tests for token seeding functionality.
.DESCRIPTION
    Tests Write-SeededTokenFileFromB64 and Get-SeededTokenFilePath functions.
#>

$ErrorActionPreference = "Stop"
$script:TestsPassed = 0
$script:TestsFailed = 0

Import-Module (Join-Path $PSScriptRoot ".." "lib" "e2e-token-seeding.psm1") -Force

function Write-TestResult {
    param(
        [string]$TestName,
        [bool]$Passed,
        [string]$Message = ""
    )

    if ($Passed) {
        Write-Host "  [PASS] $TestName" -ForegroundColor Green
        $script:TestsPassed++
    } else {
        Write-Host "  [FAIL] $TestName" -ForegroundColor Red
        if ($Message) {
            Write-Host "         $Message" -ForegroundColor Yellow
        }
        $script:TestsFailed++
    }
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Token Seeding Unit Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

# Create temp directory for tests
$testDir = Join-Path $env:TEMP "e2e-token-seeding-tests-$([Guid]::NewGuid().ToString('N').Substring(0,8))"
New-Item -ItemType Directory -Path $testDir -Force | Out-Null

try {
    # ==========================================================================
    # Test Group: Valid base64 → writes correct bytes
    # ==========================================================================
    Write-Host "Test Group: Valid Base64 Writing" -ForegroundColor Yellow

    $testContent = '{"access_token":"test123","refresh_token":"refresh456"}'
    $testBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($testContent))
    $testPath = Join-Path $testDir "valid-token.json"

    $result = Write-SeededTokenFileFromB64 -Base64Content $testBase64 -DestinationPath $testPath

    Write-TestResult -TestName "Valid base64 → Success=true" -Passed ($result.Success -eq $true)
    Write-TestResult -TestName "Valid base64 → Action=written" -Passed ($result.Action -eq 'written')
    Write-TestResult -TestName "Valid base64 → BytesWritten > 0" -Passed ($result.BytesWritten -gt 0)
    Write-TestResult -TestName "Valid base64 → Error is null" -Passed ($null -eq $result.Error)
    Write-TestResult -TestName "Valid base64 → File exists" -Passed (Test-Path $testPath)

    if (Test-Path $testPath) {
        $writtenContent = Get-Content $testPath -Raw
        Write-TestResult -TestName "Valid base64 → Content matches original" -Passed ($writtenContent.Trim() -eq $testContent)
    } else {
        Write-TestResult -TestName "Valid base64 → Content matches original" -Passed $false -Message "File not created"
    }

    Write-Host ""

    # ==========================================================================
    # Test Group: Invalid base64 → fails without echoing input
    # ==========================================================================
    Write-Host "Test Group: Invalid Base64 Handling" -ForegroundColor Yellow

    $invalidBase64 = "not-valid-base64!!!"
    $invalidPath = Join-Path $testDir "invalid-token.json"

    $result = Write-SeededTokenFileFromB64 -Base64Content $invalidBase64 -DestinationPath $invalidPath

    Write-TestResult -TestName "Invalid base64 → Success=false" -Passed ($result.Success -eq $false)
    Write-TestResult -TestName "Invalid base64 → Action=failed" -Passed ($result.Action -eq 'failed')
    Write-TestResult -TestName "Invalid base64 → BytesWritten=0" -Passed ($result.BytesWritten -eq 0)
    Write-TestResult -TestName "Invalid base64 → Error mentions 'Invalid base64'" -Passed ($result.Error -like "*Invalid base64*")
    Write-TestResult -TestName "Invalid base64 → Error does NOT contain raw input" -Passed ($result.Error -notmatch 'not-valid-base64')
    Write-TestResult -TestName "Invalid base64 → Error contains input length" -Passed ($result.Error -match 'length')
    Write-TestResult -TestName "Invalid base64 → File NOT created" -Passed (-not (Test-Path $invalidPath))

    Write-Host ""

    # ==========================================================================
    # Test Group: Existing file + no force → no overwrite
    # ==========================================================================
    Write-Host "Test Group: Existing File No Force" -ForegroundColor Yellow

    $existingPath = Join-Path $testDir "existing-token.json"
    $originalContent = '{"original":"content"}'
    Set-Content -Path $existingPath -Value $originalContent -NoNewline

    $newContent = '{"new":"content"}'
    $newBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($newContent))

    $result = Write-SeededTokenFileFromB64 -Base64Content $newBase64 -DestinationPath $existingPath

    Write-TestResult -TestName "Existing + no force → Success=true" -Passed ($result.Success -eq $true)
    Write-TestResult -TestName "Existing + no force → Action=skipped" -Passed ($result.Action -eq 'skipped')
    Write-TestResult -TestName "Existing + no force → BytesWritten=0" -Passed ($result.BytesWritten -eq 0)

    $currentContent = Get-Content $existingPath -Raw
    Write-TestResult -TestName "Existing + no force → Original content preserved" -Passed ($currentContent.Trim() -eq $originalContent)

    Write-Host ""

    # ==========================================================================
    # Test Group: Existing file + force → overwrite
    # ==========================================================================
    Write-Host "Test Group: Existing File With Force" -ForegroundColor Yellow

    $forceExistingPath = Join-Path $testDir "force-existing-token.json"
    $forceOriginalContent = '{"original":"force-content"}'
    Set-Content -Path $forceExistingPath -Value $forceOriginalContent -NoNewline

    $forceNewContent = '{"new":"force-content"}'
    $forceNewBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($forceNewContent))

    $result = Write-SeededTokenFileFromB64 -Base64Content $forceNewBase64 -DestinationPath $forceExistingPath -Force

    Write-TestResult -TestName "Existing + force → Success=true" -Passed ($result.Success -eq $true)
    Write-TestResult -TestName "Existing + force → Action=overwritten" -Passed ($result.Action -eq 'overwritten')
    Write-TestResult -TestName "Existing + force → BytesWritten > 0" -Passed ($result.BytesWritten -gt 0)

    $forceCurrentContent = Get-Content $forceExistingPath -Raw
    Write-TestResult -TestName "Existing + force → New content written" -Passed ($forceCurrentContent.Trim() -eq $forceNewContent)

    Write-Host ""

    # ==========================================================================
    # Test Group: Empty/whitespace input
    # ==========================================================================
    Write-Host "Test Group: Empty Input Handling" -ForegroundColor Yellow

    $emptyPath = Join-Path $testDir "empty-token.json"

    $result = Write-SeededTokenFileFromB64 -Base64Content "" -DestinationPath $emptyPath

    Write-TestResult -TestName "Empty input → Success=false" -Passed ($result.Success -eq $false)
    Write-TestResult -TestName "Empty input → Action=failed" -Passed ($result.Action -eq 'failed')
    Write-TestResult -TestName "Empty input → Error mentions empty" -Passed ($result.Error -like "*empty*")

    $result = Write-SeededTokenFileFromB64 -Base64Content "   " -DestinationPath $emptyPath

    Write-TestResult -TestName "Whitespace input → Success=false" -Passed ($result.Success -eq $false)
    Write-TestResult -TestName "Whitespace input → Error mentions empty/whitespace" -Passed ($result.Error -like "*empty*" -or $result.Error -like "*whitespace*")

    Write-Host ""

    # ==========================================================================
    # Test Group: Directory creation
    # ==========================================================================
    Write-Host "Test Group: Directory Creation" -ForegroundColor Yellow

    $nestedPath = Join-Path $testDir "nested" "deep" "path" "token.json"
    $nestedContent = '{"nested":"content"}'
    $nestedBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($nestedContent))

    $result = Write-SeededTokenFileFromB64 -Base64Content $nestedBase64 -DestinationPath $nestedPath

    Write-TestResult -TestName "Nested path → Success=true" -Passed ($result.Success -eq $true)
    Write-TestResult -TestName "Nested path → File exists" -Passed (Test-Path $nestedPath)
    Write-TestResult -TestName "Nested path → Parent directory created" -Passed (Test-Path (Split-Path -Parent $nestedPath))

    Write-Host ""

    # ==========================================================================
    # Test Group: Get-SeededTokenFilePath
    # ==========================================================================
    Write-Host "Test Group: Get-SeededTokenFilePath" -ForegroundColor Yellow

    $tidalPath = Get-SeededTokenFilePath -PluginName "Tidalarr" -ConfigPath "/config/plugins/RicherTunes/Tidalarr"

    Write-TestResult -TestName "Tidalarr → Returns tidal_tokens.json" -Passed ($tidalPath -like "*tidal_tokens.json")
    Write-TestResult -TestName "Tidalarr → Contains config path" -Passed ($tidalPath -like "*/config/plugins/RicherTunes/Tidalarr/*" -or $tidalPath -like "*\config\plugins\RicherTunes\Tidalarr\*")

    Write-Host ""

    # ==========================================================================
    # Test Group: Base64 with whitespace/newlines (single-line tolerance)
    # ==========================================================================
    Write-Host "Test Group: Base64 Whitespace Handling" -ForegroundColor Yellow

    $trimTestContent = '{"trim":"test"}'
    $trimTestBase64 = [Convert]::ToBase64String([System.Text.Encoding]::UTF8.GetBytes($trimTestContent))
    $trimTestPath = Join-Path $testDir "trim-token.json"

    # Add leading/trailing whitespace
    $paddedBase64 = "  $trimTestBase64  "
    $result = Write-SeededTokenFileFromB64 -Base64Content $paddedBase64 -DestinationPath $trimTestPath

    Write-TestResult -TestName "Padded base64 → Success=true" -Passed ($result.Success -eq $true)
    Write-TestResult -TestName "Padded base64 → Trims and writes correctly" -Passed ((Get-Content $trimTestPath -Raw).Trim() -eq $trimTestContent)

    Write-Host ""

} finally {
    # Cleanup
    if (Test-Path $testDir) {
        Remove-Item -Path $testDir -Recurse -Force -ErrorAction SilentlyContinue
    }
}

# ==========================================================================
# Summary
# ==========================================================================
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Test Summary" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Passed: $script:TestsPassed" -ForegroundColor Green
Write-Host "Failed: $script:TestsFailed" -ForegroundColor $(if ($script:TestsFailed -eq 0) { "Green" } else { "Red" })

if ($script:TestsFailed -gt 0) {
    exit 1
}
exit 0
