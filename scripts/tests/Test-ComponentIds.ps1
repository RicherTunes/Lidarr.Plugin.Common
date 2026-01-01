#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Unit tests for preferred component ID state + selection helpers.
#>

$ErrorActionPreference = "Stop"
$script:TestsPassed = 0
$script:TestsFailed = 0

function Write-TestResult {
    param(
        [string]$TestName,
        [bool]$Passed,
        [string]$Message = ""
    )

    if ($Passed) {
        Write-Host "  [PASS] $TestName" -ForegroundColor Green
        $script:TestsPassed++
    }
    else {
        Write-Host "  [FAIL] $TestName" -ForegroundColor Red
        if ($Message) {
            Write-Host "         $Message" -ForegroundColor Yellow
        }
        $script:TestsFailed++
    }
}

Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Component ID Helpers Unit Tests" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan
Write-Host ""

$modulePath = Join-Path (Join-Path $PSScriptRoot "..") "lib/e2e-component-ids.psm1"
Import-Module $modulePath -Force

$tempRoot = Join-Path ([System.IO.Path]::GetTempPath()) ("e2e-component-ids-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null

try {
    $statePath = Join-Path $tempRoot "state.json"
    $instanceKey = Get-E2EComponentIdsInstanceKey -LidarrUrl "http://localhost:8686" -ContainerName "lidarr-test"
    $saltedKey = Get-E2EComponentIdsInstanceKey -LidarrUrl "http://localhost:8686" -ContainerName "lidarr-test" -InstanceSalt "config-cold-123"
    Write-TestResult -TestName "InstanceSalt changes instanceKey" -Passed ($saltedKey -ne $instanceKey)
    $saltedKey2 = Get-E2EComponentIdsInstanceKey -LidarrUrl "http://localhost:8686" -ContainerName "lidarr-test" -InstanceSalt "CONFIG-COLD-123"
    Write-TestResult -TestName "InstanceSalt is case-insensitive" -Passed ($saltedKey2 -eq $saltedKey)

    # =============================================================================
    # Read: missing file
    # =============================================================================
    $state = Read-E2EComponentIdsState -Path $statePath
    Write-TestResult -TestName "Read missing file returns empty instances" -Passed ($state -is [hashtable] -and $state.ContainsKey("instances") -and $state.instances.Count -eq 0)

    # =============================================================================
    # Read: invalid JSON
    # =============================================================================
    Set-Content -LiteralPath $statePath -Value "{" -Encoding UTF8 -NoNewline
    $state = Read-E2EComponentIdsState -Path $statePath
    Write-TestResult -TestName "Read invalid JSON returns empty instances" -Passed ($state -is [hashtable] -and $state.ContainsKey("instances") -and $state.instances.Count -eq 0)

    # =============================================================================
    # Write/Read round-trip
    # =============================================================================
    $state = Read-E2EComponentIdsState -Path $statePath
    Set-E2EPreferredComponentId -State $state -InstanceKey $instanceKey -LidarrUrl "http://localhost:8686" -ContainerName "lidarr-test" -PluginName "Qobuzarr" -Type "indexer" -Id 101
    Set-E2EPreferredComponentId -State $state -InstanceKey $instanceKey -LidarrUrl "http://localhost:8686" -ContainerName "lidarr-test" -PluginName "Qobuzarr" -Type "downloadclient" -Id 201
    Set-E2EPreferredComponentId -State $state -InstanceKey $instanceKey -LidarrUrl "http://localhost:8686" -ContainerName "lidarr-test" -PluginName "Brainarr" -Type "importlist" -Id 301

    # Write should be best-effort and return true on success
    $writeOk = Write-E2EComponentIdsState -Path $statePath -State $state
    Write-TestResult -TestName "Write returns true on success" -Passed ($writeOk -eq $true)
    $roundTrip = Read-E2EComponentIdsState -Path $statePath

    Write-TestResult -TestName "Round-trip preserves Qobuzarr indexerId" -Passed ((Get-E2EPreferredComponentId -State $roundTrip -InstanceKey $instanceKey -PluginName "Qobuzarr" -Type "indexer") -eq 101)
    Write-TestResult -TestName "Round-trip preserves Qobuzarr downloadClientId" -Passed ((Get-E2EPreferredComponentId -State $roundTrip -InstanceKey $instanceKey -PluginName "Qobuzarr" -Type "downloadclient") -eq 201)
    Write-TestResult -TestName "Round-trip preserves Brainarr importListId" -Passed ((Get-E2EPreferredComponentId -State $roundTrip -InstanceKey $instanceKey -PluginName "Brainarr" -Type "importlist") -eq 301)

    $otherInstanceKey = Get-E2EComponentIdsInstanceKey -LidarrUrl "http://localhost:8686" -ContainerName "different-container"
    Write-TestResult -TestName "Different instanceKey does not read IDs" -Passed ($null -eq (Get-E2EPreferredComponentId -State $roundTrip -InstanceKey $otherInstanceKey -PluginName "Qobuzarr" -Type "indexer"))

    # ==========================================================================
    # Write: lock contention is best-effort (returns false, does not throw)
    # ==========================================================================
    Set-Content -LiteralPath "$statePath.lock" -Value "locked" -Encoding UTF8 -NoNewline
    $writeLocked = Write-E2EComponentIdsState -Path $statePath -State $state
    Write-TestResult -TestName "Write returns false when lock is held" -Passed ($writeLocked -eq $false)
    Write-TestResult -TestName "Write does not remove another process lock" -Passed (Test-Path -LiteralPath "$statePath.lock")
    Remove-Item -LiteralPath "$statePath.lock" -Force -ErrorAction SilentlyContinue

    # ==========================================================================
    # Write: stale lock cleanup uses UTC timestamps (avoid time zone bugs)
    # ==========================================================================
    $offsetSeconds = ([datetime]::Now - [datetime]::UtcNow).TotalSeconds
    if ([math]::Abs($offsetSeconds) -lt 0.5) {
        Write-TestResult -TestName "Stale lock cleanup test skipped (UTC time zone)" -Passed $true
    }
    else {
        Set-Content -LiteralPath "$statePath.lock" -Value "locked" -Encoding UTF8 -NoNewline
        (Get-Item -LiteralPath "$statePath.lock").LastWriteTimeUtc = [datetime]::UtcNow.AddSeconds(-121)

        $writeAfterStaleLock = Write-E2EComponentIdsState -Path $statePath -State $state
        Write-TestResult -TestName "Stale lock cleanup allows write" -Passed ($writeAfterStaleLock -eq $true)
        Write-TestResult -TestName "Stale lock cleanup removes lock file" -Passed (-not (Test-Path -LiteralPath "$statePath.lock"))
    }

    # =============================================================================
    # Selection: preferred ID is strict
    # =============================================================================
    $items = @(
        [PSCustomObject]@{ id = 1; name = "X"; implementationName = "NotQobuzarr"; implementation = "Other" },
        [PSCustomObject]@{ id = 2; name = "Q"; implementationName = "Qobuzarr"; implementation = "QobuzIndexer" }
    )

    $sel = Select-ConfiguredComponent -Items $items -PluginName "Qobuzarr" -PreferredId 2
    Write-TestResult -TestName "PreferredId selects exact id when implementationName matches" -Passed ($sel.Component.id -eq 2 -and $sel.Resolution -eq "preferredId")

    $sel = Select-ConfiguredComponent -Items $items -PluginName "Qobuzarr" -PreferredId 1
    Write-TestResult -TestName "PreferredId ignored when implementationName mismatches" -Passed ($sel.Component.id -eq 2 -and $sel.Resolution -eq "implementationName")

    $sel = Select-ConfiguredComponent -Items $items -PluginName "NoSuchPlugin" -PreferredId 2
    Write-TestResult -TestName "No matches returns none" -Passed ($null -eq $sel.Component -and $sel.Resolution -eq "none")

    # ====================================================================
    # Selection: supports hashtable items
    # ====================================================================
    $items = @(
        @{ id = 9; name = "X"; implementationName = "NotQobuzarr"; implementation = "Other" },
        @{ id = 10; name = "Q"; implementationName = "Qobuzarr"; implementation = "QobuzIndexer" }
    )
    $sel = Select-ConfiguredComponent -Items $items -PluginName "Qobuzarr" -PreferredId 10
    Write-TestResult -TestName "PreferredId selects hashtable item" -Passed ($sel.Component.id -eq 10 -and $sel.Resolution -eq "preferredId")

    # ====================================================================
    # Selection: fuzzy does NOT match on user-supplied name alone
    # ====================================================================
    $items = @(
        [PSCustomObject]@{ id = 20; name = "Qobuzarr (user named)"; implementationName = "Other"; implementation = "OtherImpl" }
    )
    $sel = Select-ConfiguredComponent -Items $items -PluginName "Qobuzarr" -PreferredId 0
    Write-TestResult -TestName "Name-only fuzzy does not select unrelated item" -Passed ($null -eq $sel.Component -and $sel.Resolution -eq "none")

    # ====================================================================
    # Selection: ambiguous matches return none (do not guess)
    # ====================================================================
    $items = @(
        [PSCustomObject]@{ id = 31; name = "A"; implementationName = "Qobuzarr"; implementation = "QobuzIndexer" },
        [PSCustomObject]@{ id = 32; name = "B"; implementationName = "Qobuzarr"; implementation = "QobuzIndexer" }
    )
    $sel = Select-ConfiguredComponent -Items $items -PluginName "Qobuzarr" -PreferredId 0
    Write-TestResult -TestName "Ambiguous implementationName returns none" -Passed ($null -eq $sel.Component -and $sel.Resolution -eq "ambiguousImplementationName")
    Write-TestResult -TestName "Ambiguous implementationName includes candidate IDs" -Passed (($sel.CandidateIds | Sort-Object) -join ',' -eq '31,32')

    $items = @(
        [PSCustomObject]@{ id = 41; name = "A"; implementationName = "Other"; implementation = "QobuzarrDownloadClient" },
        [PSCustomObject]@{ id = 42; name = "B"; implementationName = "Other"; implementation = "QobuzarrIndexer" }
    )
    $sel = Select-ConfiguredComponent -Items $items -PluginName "Qobuzarr" -PreferredId 0
    Write-TestResult -TestName "Ambiguous fuzzy returns none" -Passed ($null -eq $sel.Component -and $sel.Resolution -eq "ambiguousFuzzy")
    Write-TestResult -TestName "Ambiguous fuzzy includes candidate IDs" -Passed (($sel.CandidateIds | Sort-Object) -join ',' -eq '41,42')
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host ""
Write-Host "========================================" -ForegroundColor Cyan
Write-Host "Results: $script:TestsPassed passed, $script:TestsFailed failed" -ForegroundColor Cyan
Write-Host "========================================" -ForegroundColor Cyan

if ($script:TestsFailed -gt 0) { exit 1 }
