#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Tests for the shared plugin lint-gate CI runner.
#>

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$runner = Join-Path $PSScriptRoot '../ci/run-plugin-lint-gates.ps1'
$testRoot = $null
$passed = 0
$failed = 0

function Assert-True {
    param(
        [string]$Name,
        [bool]$Condition,
        [string]$Details = ''
    )

    if ($Condition) {
        Write-Host "  [PASS] $Name" -ForegroundColor Green
        $script:passed++
        return
    }

    Write-Host "  [FAIL] $Name" -ForegroundColor Red
    if ($Details) {
        Write-Host "         $Details" -ForegroundColor DarkGray
    }
    $script:failed++
}

function Add-GateStub {
    param(
        [string]$CommonRoot,
        [string]$RelativePath,
        [string]$GateName
    )

    $fullPath = Join-Path $CommonRoot $RelativePath
    $dir = Split-Path $fullPath -Parent
    if (-not (Test-Path $dir)) {
        New-Item -ItemType Directory -Path $dir -Force | Out-Null
    }

    $content = @"
#!/usr/bin/env pwsh
`$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

`$line = '$GateName|' + (`$args -join ' ')
Add-Content -Path `$env:LINT_GATE_LOG -Value `$line

if (`$env:LINT_GATE_FAIL -eq '$GateName') {
    Write-Error '$GateName failed by test fixture'
    exit 42
}

exit 0
"@

    Set-Content -Path $fullPath -Value $content -NoNewline
}

function Add-PluginTestStub {
    param(
        [string]$RepoPath,
        [string]$TestName
    )

    $testDir = Join-Path $RepoPath 'scripts/tests'
    New-Item -ItemType Directory -Path $testDir -Force | Out-Null

    $fullPath = Join-Path $testDir $TestName
    $content = @"
#!/usr/bin/env pwsh
`$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

`$line = 'contract|' + `$MyInvocation.MyCommand.Name
Add-Content -Path `$env:LINT_GATE_LOG -Value `$line

if (`$env:LINT_GATE_FAIL -eq 'contract') {
    Write-Error 'contract test failed by test fixture'
    exit 43
}

exit 0
"@

    Set-Content -Path $fullPath -Value $content -NoNewline
}

function Add-FailingPesterTestStub {
    param(
        [string]$RepoPath,
        [string]$TestName
    )

    $testDir = Join-Path $RepoPath 'scripts/tests'
    New-Item -ItemType Directory -Path $testDir -Force | Out-Null

    $fullPath = Join-Path $testDir $TestName
    $content = @"
#Requires -Module Pester

Describe 'failing pester plugin contract' {
    It 'fails for runner exit-code regression coverage' {
        `$false | Should -BeTrue
    }
}
"@

    Set-Content -Path $fullPath -Value $content -NoNewline
}

function New-FakePluginRepo {
    param([string]$Root)

    $repo = Join-Path $Root 'fake-plugin'
    New-Item -ItemType Directory -Path $repo -Force | Out-Null
    New-Item -ItemType Directory -Path (Join-Path $repo 'src') -Force | Out-Null
    Set-Content -Path (Join-Path $repo 'src/Fake.cs') -Value 'public sealed class Fake { }' -NoNewline
    Add-PluginTestStub -RepoPath $repo -TestName 'FakeContract.Tests.ps1'
    return $repo
}

function New-FakeCommonRoot {
    param([string]$Path)

    New-Item -ItemType Directory -Path $Path -Force | Out-Null
    Add-GateStub -CommonRoot $Path -RelativePath 'scripts/lint-date-parsing.ps1' -GateName 'date'
    Add-GateStub -CommonRoot $Path -RelativePath 'scripts/lint-sync-over-async.ps1' -GateName 'sync'
    Add-GateStub -CommonRoot $Path -RelativePath 'scripts/lint-test-traits.ps1' -GateName 'traits'
    Add-GateStub -CommonRoot $Path -RelativePath 'scripts/ecosystem-parity-lint.ps1' -GateName 'parity'
    Add-GateStub -CommonRoot $Path -RelativePath 'scripts/lint-doc-script-refs.ps1' -GateName 'doc-refs'
    return $Path
}

function Invoke-Runner {
    param([string[]]$RunnerArgs)

    $output = & pwsh -NoProfile -File $runner @RunnerArgs *>&1
    return @{
        ExitCode = $LASTEXITCODE
        Output = ($output | ForEach-Object { "$_" }) -join "`n"
    }
}

try {
    Write-Host '=================================================' -ForegroundColor Cyan
    Write-Host 'Test-RunPluginLintGates: Shared Runner Contract' -ForegroundColor Cyan
    Write-Host '=================================================' -ForegroundColor Cyan

    $testRoot = Join-Path ([System.IO.Path]::GetTempPath()) "plugin-lint-runner-$([guid]::NewGuid().ToString('N').Substring(0,8))"
    New-Item -ItemType Directory -Path $testRoot -Force | Out-Null

    Write-Host "`n[TEST 1] Invokes every shared lint gate..." -ForegroundColor Cyan
    $repo = New-FakePluginRepo -Root $testRoot
    $common = New-FakeCommonRoot -Path (Join-Path $repo 'ext/Lidarr.Plugin.Common')
    $log = Join-Path $testRoot 'gate.log'
    $env:LINT_GATE_LOG = $log
    $env:LINT_GATE_FAIL = ''

    $result = Invoke-Runner -RunnerArgs @(
        '-RepoPath', $repo,
        '-CommonRoot', 'ext/Lidarr.Plugin.Common',
        '-Mode', 'ci'
    )

    Assert-True 'Runner exits successfully when every gate passes' ($result.ExitCode -eq 0) $result.Output
    $lines = @(if (Test-Path $log) { Get-Content $log } else { @() })
    Assert-True 'Exactly five lint gates and one plugin contract test are invoked' (@($lines).Count -eq 6) ($lines -join "
")
    Assert-True 'Date parsing gate receives repo path and CI mode' (($lines -join "`n") -match 'date\|.*-Path .*fake-plugin.* -Mode ci') ($lines -join "`n")
    Assert-True 'Sync-over-async gate receives repo path and CI mode' (($lines -join "`n") -match 'sync\|.*-Path .*fake-plugin.* -Mode ci') ($lines -join "`n")
    Assert-True 'Trait policy gate receives repo path and CI flag' (($lines -join "`n") -match 'traits\|.*-Path .*fake-plugin.* -CI') ($lines -join "`n")
    Assert-True 'Parity gate receives version-contract check' (($lines -join "`n") -match 'parity\|.*-RepoPath .*fake-plugin.* -CommonRoot .*Lidarr\.Plugin\.Common.* -Check VersionContract\s+-Mode ci') ($lines -join "`n")
    Assert-True 'Doc-refs gate receives plugin repo root and CI flag' (($lines -join "
") -match 'doc-refs\|.*-RepoRoot .*fake-plugin.* -CI') ($lines -join "
")
    Assert-True 'Plugin contract test under scripts/tests is invoked' (($lines -join "`n") -match 'contract\|FakeContract\.Tests\.ps1') ($lines -join "`n")

    Write-Host "`n[TEST 2] Propagates gate failures..." -ForegroundColor Cyan
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $env:LINT_GATE_FAIL = 'sync'
    $result = Invoke-Runner -RunnerArgs @(
        '-RepoPath', $repo,
        '-CommonRoot', $common,
        '-Mode', 'ci'
    )
    Assert-True 'Runner exits non-zero when a gate fails' ($result.ExitCode -ne 0) $result.Output
    Assert-True 'Failure output names the failing gate' ($result.Output -match 'Sync-over-async') $result.Output

    Write-Host "`n[TEST 3] Fails clearly when a required gate script is missing..." -ForegroundColor Cyan
    $env:LINT_GATE_FAIL = ''
    Remove-Item -Path (Join-Path $common 'scripts/lint-date-parsing.ps1') -Force
    $result = Invoke-Runner -RunnerArgs @(
        '-RepoPath', $repo,
        '-CommonRoot', $common,
        '-Mode', 'ci'
    )
    Assert-True 'Runner exits non-zero when a script is missing' ($result.ExitCode -ne 0) $result.Output
    Assert-True 'Missing-script output names the absent script' ($result.Output -match 'lint-date-parsing\.ps1') $result.Output

    Write-Host "`n[TEST 4] Propagates plugin contract test failures..." -ForegroundColor Cyan
    New-FakeCommonRoot -Path $common | Out-Null
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $env:LINT_GATE_FAIL = 'contract'
    $result = Invoke-Runner -RunnerArgs @(
        '-RepoPath', $repo,
        '-CommonRoot', $common,
        '-Mode', 'ci'
    )
    Assert-True 'Runner exits non-zero when a plugin contract test fails' ($result.ExitCode -ne 0) $result.Output
    Assert-True 'Failure output names the failing plugin contract test' ($result.Output -match 'FakeContract\.Tests\.ps1') $result.Output

    Write-Host "`n[TEST 5] Fails on Pester contract test failures..." -ForegroundColor Cyan
    $env:LINT_GATE_FAIL = ''
    Add-FailingPesterTestStub -RepoPath $repo -TestName 'FailingPester.Tests.ps1'
    $result = Invoke-Runner -RunnerArgs @(
        '-RepoPath', $repo,
        '-CommonRoot', $common,
        '-Mode', 'ci'
    )
    Assert-True 'Runner exits non-zero when a Pester contract test fails' ($result.ExitCode -ne 0) $result.Output
    Assert-True 'Pester failure output names the failing contract file' ($result.Output -match 'FailingPester\.Tests\.ps1') $result.Output

    Write-Host "`n=================================================" -ForegroundColor Cyan

    Write-Host "`n[TEST 6] -SkipDocRefs suppresses the doc-refs gate..." -ForegroundColor Cyan
    $env:LINT_GATE_FAIL = ''
    Remove-Item $log -Force -ErrorAction SilentlyContinue
    $result = Invoke-Runner -RunnerArgs @(
        '-RepoPath', $repo,
        '-CommonRoot', $common,
        '-Mode', 'ci',
        '-SkipDocRefs',
        '-SkipPluginContractTests'
    )
    $skipLines = @(if (Test-Path $log) { Get-Content $log } else { @() })
    Assert-True '-SkipDocRefs causes runner to exit successfully' ($result.ExitCode -eq 0) $result.Output
    Assert-True '-SkipDocRefs omits doc-refs gate from invocation log' (-not (($skipLines -join "`n") -match 'doc-refs\|')) ($skipLines -join "`n")

    $total = $passed + $failed
    Write-Host "Results: $passed/$total passed, $failed failed" -ForegroundColor $(if ($failed -eq 0) { 'Green' } else { 'Red' })
    Write-Host '=================================================' -ForegroundColor Cyan

    if ($failed -gt 0) {
        exit 1
    }
    exit 0
}
finally {
    $env:LINT_GATE_LOG = ''
    $env:LINT_GATE_FAIL = ''
    if ($testRoot -and (Test-Path $testRoot)) {
        Remove-Item -Path $testRoot -Recurse -Force -ErrorAction SilentlyContinue
    }
}
