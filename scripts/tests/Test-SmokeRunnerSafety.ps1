#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Exercises the real smoke runner's role and ownership contracts without Docker.
.DESCRIPTION
    Imports only the runner's function ASTs and literal role table into an isolated
    module. The Docker boundary is a recording fake; production function bodies
    are never copied into the test. No container or network operation is performed.
#>
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$root = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$path = Join-Path $root 'scripts/multi-plugin-docker-smoke-test.ps1'
$tokens = $null
$errors = $null
$ast = [System.Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors.Message -join "`n") }
$definitions = @($ast.FindAll({ param($node)
    $node -is [System.Management.Automation.Language.FunctionDefinitionAst] -and
    $node.Name -in @('Cleanup', 'Get-PluginFolderName', 'Start-SmokeContainer', 'Assert-SmokeSchemas', 'Initialize-SmokeWorkRoot', 'Assert-SmokeContainerNameAvailable', 'Get-SmokeArtifactPath')
}, $false))
$table = $ast.Find({ param($node)
    $node -is [System.Management.Automation.Language.AssignmentStatementAst] -and
    $node.Left.Extent.Text -eq '$expectations'
}, $false)
if (-not $table) { throw 'Runner must declare its role contract.' }

$module = New-Module -ArgumentList $definitions, $table -ScriptBlock {
    param($Definitions, $Table)
    . ([scriptblock]::Create($Table.Extent.Text))
    foreach ($definition in $Definitions) { . ([scriptblock]::Create($definition.Extent.Text)) }
    $script:KeepRunning = $false
    $script:ContainerName = 'foreign-existing-container'
    $script:SmokeRunId = 'test-owner'
    $script:OwnedContainerId = $null
    $script:EphemeralWorkRoot = $null
    $script:WorkRootLease = $null
    $script:CreateWarning = $false
    $script:ExistingNames = @()
    $script:Calls = [System.Collections.Generic.List[object]]::new()
    $script:Failures = @{}
    $script:LabelOwner = 'test-owner'
    function docker {
        $argv = @($args)
        $script:Calls.Add($argv)
        $verb = [string]$argv[0]
        $global:LASTEXITCODE = if ($script:Failures.ContainsKey($verb)) { $script:Failures[$verb] } else { 0 }
        if ($global:LASTEXITCODE -ne 0) { return "fake $verb failure" }
        switch ($verb) {
            'create' {
                $cidIndex = [Array]::IndexOf($argv, '--cidfile')
                if ($cidIndex -ge 0) { Set-Content -LiteralPath $argv[$cidIndex + 1] -Value ('a' * 64) }
                if ($script:CreateWarning) { 'WARNING: diagnostic emitted on successful create' }
                return ('a' * 64)
            }
            'ps' { return $script:ExistingNames }
            'start' { return ('a' * 64) }
            'inspect' { return (@{ 'org.richertunes.smoke-run' = $script:LabelOwner } | ConvertTo-Json -Compress) }
            'rm' { return ('a' * 64) }
            default { throw "Unexpected Docker operation in contract test: $verb" }
        }
    }
    function Reset-Fake {
        $script:Calls.Clear()
        $script:Failures = @{}
        $script:KeepRunning = $false
        $script:OwnedContainerId = $null
        $script:EphemeralWorkRoot = $null
        if ($script:WorkRootLease) { $script:WorkRootLease.Dispose(); $script:WorkRootLease = $null }
        $script:CreateWarning = $false
        $script:ExistingNames = @()
        $script:LabelOwner = 'test-owner'
    }
}
$passed = 0
$failed = 0
function Test-Assertion {
    param([string]$Name, [scriptblock]$Body)
    try {
        & $module { Reset-Fake }
        if (-not (& $Body)) { throw 'Assertion returned false.' }
        Write-Host "PASS $Name"
        $script:passed++
    } catch {
        Write-Host "FAIL ${Name}: $($_.Exception.Message)"
        $script:failed++
    }
}

Test-Assertion 'Five plugins have nonempty, host-native role contracts' {
    & $module {
        $expected = @{
            qobuzarr = @('QobuzIndexer', 'QobuzDownloadClient')
            tidalarr = @('TidalLidarrIndexer', 'TidalLidarrDownloadClient')
            applemusicarr = @('AppleMusicLidarrIndexer', 'AppleMusicLidarrDownloadClient')
            amazonmusicarr = @('AmazonmusicLidarrIndexer', 'AmazonmusicLidarrDownloadClient')
        }
        foreach ($name in $expected.Keys) {
            if (-not $expectations.ContainsKey($name)) { return $false }
            if ($expectations[$name].Indexers -notcontains $expected[$name][0]) { return $false }
            if ($expectations[$name].DownloadClients -notcontains $expected[$name][1]) { return $false }
            if (@($expectations[$name].ImportLists).Count -ne 0) { return $false }
        }
        $expectations.brainarr.ImportLists -contains 'Brainarr' -and @($expectations.brainarr.Indexers).Count -eq 0
    }
}
foreach ($name in @('unknown-plugin', '../outside', 'qobuzarr/../other', '', '.')) {
    $testName = $name
    Test-Assertion "Unknown or unsafe plugin name rejected: '$name'" {
        & $module {
            param($Name)
            try { $null = Get-PluginFolderName -Name $Name; return $false } catch { return $true }
        } $testName
    }
}
Test-Assertion 'Known plugin names remain case insensitive' {
    & $module { (Get-PluginFolderName 'QOBUZARR') -eq 'Qobuzarr' }
}
Test-Assertion 'Cleanup before creation never removes an existing named container' {
    & $module { Cleanup; @($script:Calls | Where-Object { $_[0] -eq 'rm' }).Count -eq 0 }
}
Test-Assertion 'Cleanup uses the owned immutable container ID and verifies its label' {
    & $module {
        $script:OwnedContainerId = 'a' * 64
        Cleanup
        $removals = @($script:Calls | Where-Object { $_[0] -eq 'rm' })
        $inspections = @($script:Calls | Where-Object { $_[0] -eq 'inspect' })
        $removals.Count -eq 1 -and $inspections.Count -eq 1 -and $removals[0][-1] -eq ('a' * 64) -and
            $null -eq $script:OwnedContainerId
    }
}
Test-Assertion 'Mismatched ownership label refuses cleanup' {
    & $module {
        $script:OwnedContainerId = 'a' * 64
        $script:LabelOwner = 'another-run'
        $threw = $false
        try { Cleanup } catch { $threw = $true }
        $threw -and @($script:Calls | Where-Object { $_[0] -eq 'rm' }).Count -eq 0
    }
}
Test-Assertion 'KeepRunning preserves an owned container' {
    & $module {
        $script:OwnedContainerId = 'a' * 64
        $script:KeepRunning = $true
        Cleanup
        $script:Calls.Count -eq 0
    }
}
Test-Assertion 'Failed create does not acquire cleanup authority' {
    & $module {
        $script:Failures.create = 1
        $threw = $false
        try { Start-SmokeContainer -DockerArguments @('--name', 'occupied', 'image:test') } catch { $threw = $_.Exception.Message -match 'create' }
        Cleanup
        $threw -and $null -eq $script:OwnedContainerId -and @($script:Calls | Where-Object { $_[0] -eq 'rm' }).Count -eq 0
    }
}
Test-Assertion 'Failed start retains the created ID for safe cleanup' {
    & $module {
        $script:Failures.start = 1
        $threw = $false
        try { Start-SmokeContainer -DockerArguments @('--name', 'new', 'image:test') } catch { $threw = $_.Exception.Message -match 'start' }
        $owned = $script:OwnedContainerId
        Cleanup
        $removed = @($script:Calls | Where-Object { $_[0] -eq 'rm' })
        $threw -and $owned -eq ('a' * 64) -and $removed.Count -eq 1 -and $removed[0][-1] -eq $owned
    }
}
Test-Assertion 'Create adds the run ownership label before starting by ID' {
    & $module {
        Start-SmokeContainer -DockerArguments @('--name', 'new', 'image:test')
        $created = @($script:Calls | Where-Object { $_[0] -eq 'create' })
        $started = @($script:Calls | Where-Object { $_[0] -eq 'start' })
        $created.Count -eq 1 -and $created[0] -contains 'org.richertunes.smoke-run=test-owner' -and
            $started.Count -eq 1 -and $started[0][-1] -eq ('a' * 64)
    }
}
Test-Assertion 'Successful create with diagnostics still owns and cleans the container' {
    & $module {
        $script:CreateWarning = $true
        Start-SmokeContainer -DockerArguments @('--name', 'new', 'image:test')
        $owned = $script:OwnedContainerId
        Cleanup
        $owned -eq ('a' * 64) -and @($script:Calls | Where-Object { $_[0] -eq 'rm' }).Count -eq 1
    }
}
$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "smoke-safety-$([guid]::NewGuid().ToString('N'))"
New-Item -ItemType Directory -Path $tempRoot | Out-Null
try {
    Test-Assertion 'Disposable state is removed and creates no persistent lock file' {
        & $module {
            param($Root)
            $state = Initialize-SmokeWorkRoot -WorkRootBase $Root -Name 'ephemeral'
            Set-Content -LiteralPath (Join-Path $state 'owned.txt') -Value 'owned'
            Cleanup
            -not (Test-Path -LiteralPath $state) -and @(Get-ChildItem -LiteralPath $Root -Filter '*.lock' -Force).Count -eq 0
        } $tempRoot
    }
    Test-Assertion 'KeepRunning retains disposable mounted state' {
        & $module {
            param($Root)
            $state = Initialize-SmokeWorkRoot -WorkRootBase $Root -Name 'keep'
            $script:KeepRunning = $true
            Cleanup
            Test-Path -LiteralPath $state
        } $tempRoot
    }
    Test-Assertion 'Drift reports survive disposable mounted-state cleanup' {
        & $module {
            param($Root)
            $state = Initialize-SmokeWorkRoot -WorkRootBase $Root -Name 'report'
            $script:SmokeArtifactRoot = Join-Path $Root 'retained-reports'
            $report = Get-SmokeArtifactPath -FileName 'drift-sentinel.json'
            Set-Content -LiteralPath $report -Value '{"driftCount":1}'
            Cleanup
            (Test-Path -LiteralPath $report) -and -not (Test-Path -LiteralPath $state)
        } $tempRoot
    }
    Test-Assertion 'Persistent state survives cleanup' {
        & $module {
            param($Root)
            $state = Initialize-SmokeWorkRoot -WorkRootBase $Root -Name 'persistent' -Preserve
            Set-Content -LiteralPath (Join-Path $state 'original.txt') -Value 'original'
            Cleanup
            Test-Path -LiteralPath (Join-Path $state 'original.txt')
        } $tempRoot
    }
    Test-Assertion 'A container appearing under the lease prevents persistent-state deletion' {
        & $module {
            param($Root)
            $state = Join-Path $Root 'raced'
            New-Item -ItemType Directory -Path $state | Out-Null
            Set-Content -LiteralPath (Join-Path $state 'original.txt') -Value 'original'
            $script:ExistingNames = @('raced')
            $refused = $false
            try { $null = Initialize-SmokeWorkRoot -WorkRootBase $Root -Name 'raced' -Clean }
            catch { $refused = $_.Exception.Message -match 'already exists' }
            $refused -and (Test-Path -LiteralPath (Join-Path $state 'original.txt'))
        } $tempRoot
    }
    Test-Assertion 'Concurrent state lease acquisition is refused' {
        & $module {
            param($Root)
            $null = Initialize-SmokeWorkRoot -WorkRootBase $Root -Name 'locked' -Preserve
            $refused = $false
            try { $null = Initialize-SmokeWorkRoot -WorkRootBase $Root -Name 'locked' -Preserve }
            catch { $refused = $true }
            $refused
        } $tempRoot
    }
} finally {
    & $module { if ($script:WorkRootLease) { $script:WorkRootLease.Dispose(); $script:WorkRootLease = $null } }
    Remove-Item -LiteralPath $tempRoot -Recurse -Force
}
foreach ($name in @('applemusicarr', 'amazonmusicarr', 'qobuzarr', 'tidalarr', 'brainarr')) {
    $testName = $name
    Test-Assertion "Missing schemas fail closed for $name" {
        & $module {
            param($Name)
            try { Assert-SmokeSchemas -PluginNames @($Name) -IndexerSchemas @() -DownloadClientSchemas @() -ImportListSchemas @(); return $false }
            catch { $_.Exception.Message -match 'Missing plugin implementations' }
        } $testName
    }
}
Test-Assertion 'All five host-native schema sets pass together' {
    & $module {
        Assert-SmokeSchemas -PluginNames @('qobuzarr', 'tidalarr', 'applemusicarr', 'amazonmusicarr', 'brainarr') `
            -IndexerSchemas @('QobuzIndexer', 'TidalLidarrIndexer', 'AppleMusicLidarrIndexer', 'AmazonmusicLidarrIndexer' | ForEach-Object { @{ implementation = $_ } }) `
            -DownloadClientSchemas @('QobuzDownloadClient', 'TidalLidarrDownloadClient', 'AppleMusicLidarrDownloadClient', 'AmazonmusicLidarrDownloadClient' | ForEach-Object { @{ implementation = $_ } }) `
            -ImportListSchemas @(@{ implementation = 'Brainarr' })
        $true
    }
}
Test-Assertion 'Local CI delegates smoke rather than running its own container' {
    $local = Get-Content (Join-Path $root 'scripts/local-ci.ps1') -Raw
    $local -match 'multi-plugin-docker-smoke-test\.ps1' -and $local -notmatch '& docker run -d' -and
        $local -notmatch 'localhost:8686/api/v1/importlist/schema'
}
Test-Assertion 'Imports are not wrapped in a masking script trap' {
    @($ast.FindAll({ param($node) $node -is [System.Management.Automation.Language.TrapStatementAst] }, $false)).Count -eq 0
}
Remove-Module $module -ErrorAction SilentlyContinue
Write-Host "Results: $passed passed, $failed failed"
if ($failed) { exit 1 }
