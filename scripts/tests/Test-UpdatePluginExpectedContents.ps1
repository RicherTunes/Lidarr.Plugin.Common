param()

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$scriptPath = Join-Path $repoRoot 'scripts\update-plugin-expected-contents.ps1'
$failures = New-Object System.Collections.Generic.List[string]

function Assert-Condition {
    param(
        [bool]$Condition,
        [string]$Message
    )

    if (-not $Condition) {
        $script:failures.Add($Message)
    }
}

function New-FakeCommonRoot {
    param([string]$BasePath)

    $commonRoot = Join-Path $BasePath 'ext\Lidarr.Plugin.Common'
    $toolsPath = Join-Path $commonRoot 'tools'
    $scriptsPath = Join-Path $commonRoot 'scripts'
    New-Item -ItemType Directory -Path $toolsPath, $scriptsPath -Force | Out-Null

    Set-Content -LiteralPath (Join-Path $toolsPath 'PluginPack.psm1') -Encoding UTF8 -Value @'
function New-PluginPackage {
    param(
        [string]$Csproj,
        [string]$Manifest,
        [string]$Framework,
        [string]$Configuration,
        [switch]$ResolveEntryPoints,
        [string[]]$ExtraBuildArgs,
        [switch]$RequireCanonicalAbstractions,
        [string]$CanonicalAbstractionsVersion,
        [string]$CanonicalAbstractionsSha256,
        [string]$CanonicalAbstractionsPath
    )

    $capture = @{
        Csproj = $Csproj
        Manifest = $Manifest
        Framework = $Framework
        Configuration = $Configuration
        ResolveEntryPoints = [bool]$ResolveEntryPoints
        ExtraBuildArgs = @($ExtraBuildArgs)
        RequireCanonicalAbstractions = [bool]$RequireCanonicalAbstractions
        CanonicalAbstractionsVersion = $CanonicalAbstractionsVersion
        CanonicalAbstractionsSha256 = $CanonicalAbstractionsSha256
        CanonicalAbstractionsPath = $CanonicalAbstractionsPath
    }
    $capture | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $env:PLUGIN_PACK_CAPTURE -Encoding UTF8
    New-Item -ItemType File -Path $env:PLUGIN_PACK_ZIP -Force | Out-Null
    return $env:PLUGIN_PACK_ZIP
}

Export-ModuleMember -Function New-PluginPackage
'@

    Set-Content -LiteralPath (Join-Path $scriptsPath 'generate-expected-contents.ps1') -Encoding UTF8 -Value @'
param(
    [string]$ZipPath,
    [string]$ManifestPath,
    [switch]$Update,
    [switch]$Check
)

$capture = @{
    ZipPath = $ZipPath
    ManifestPath = $ManifestPath
    Update = [bool]$Update
    Check = [bool]$Check
}
$capture | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $env:EXPECTED_CONTENTS_CAPTURE -Encoding UTF8
if ($env:EXPECTED_CONTENTS_EXIT_CODE) {
    exit ([int]$env:EXPECTED_CONTENTS_EXIT_CODE)
}
'@

    return $commonRoot
}

function Invoke-SharedUpdater {
    param(
        [string]$RepoPath,
        [string]$CommonRoot,
        [string[]]$Arguments
    )

    $output = & pwsh -NoProfile -File $scriptPath `
        -RepoPath $RepoPath `
        -CommonRoot $CommonRoot `
        @Arguments 2>&1

    return @{
        ExitCode = $LASTEXITCODE
        Output = ($output | Out-String)
    }
}

$tempRoot = Join-Path ([IO.Path]::GetTempPath()) "update-expected-contents-contract-$([Guid]::NewGuid())"
New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
try {
    Assert-Condition (Test-Path -LiteralPath $scriptPath) "Missing shared updater script: $scriptPath"

    $repoPath = Join-Path $tempRoot 'plugin'
    New-Item -ItemType Directory -Path $repoPath -Force | Out-Null
    $commonRoot = New-FakeCommonRoot -BasePath $repoPath

    $env:PLUGIN_PACK_CAPTURE = Join-Path $tempRoot 'plugin-pack.json'
    $env:EXPECTED_CONTENTS_CAPTURE = Join-Path $tempRoot 'generator.json'
    $env:PLUGIN_PACK_ZIP = Join-Path $tempRoot 'plugin.zip'
    $env:EXPECTED_CONTENTS_EXIT_CODE = $null

    try {
        & $scriptPath `
            -RepoPath $repoPath `
            -CommonRoot $commonRoot `
            -Csproj 'src/Plugin/Plugin.csproj' `
            -Manifest 'manifest/plugin.json' `
            -Framework 'net8.0' `
            -Configuration 'Release' `
            -ExpectedContentsFile 'packaging/custom-expected.txt' `
            -ResolveEntryPoints `
            -RequireCanonicalAbstractions `
            -CanonicalAbstractionsVersion '1.2.3' `
            -CanonicalAbstractionsSha256 'abc123' `
            -CanonicalAbstractionsPath 'deps/canonical-abstractions.json' `
            -ExtraBuildArgs @('-p:SkipHostBridge=true', '-p:UseSharedCompilation=false') `
            -Update
        Assert-Condition $true 'Shared updater returned from direct PowerShell invocation.'
    }
    catch {
        Assert-Condition $false "Shared updater should not throw during direct PowerShell invocation: $_"
    }
    Assert-Condition (Test-Path -LiteralPath $env:PLUGIN_PACK_CAPTURE) 'New-PluginPackage capture was not written.'
    Assert-Condition (Test-Path -LiteralPath $env:EXPECTED_CONTENTS_CAPTURE) 'Generator capture was not written.'

    if ((Test-Path -LiteralPath $env:PLUGIN_PACK_CAPTURE) -and (Test-Path -LiteralPath $env:EXPECTED_CONTENTS_CAPTURE)) {
        $pack = Get-Content -LiteralPath $env:PLUGIN_PACK_CAPTURE -Raw | ConvertFrom-Json
        $gen = Get-Content -LiteralPath $env:EXPECTED_CONTENTS_CAPTURE -Raw | ConvertFrom-Json
        Assert-Condition ($pack.Csproj -eq 'src/Plugin/Plugin.csproj') 'Csproj was not passed to New-PluginPackage.'
        Assert-Condition ($pack.Manifest -eq 'manifest/plugin.json') 'Manifest was not passed to New-PluginPackage.'
        Assert-Condition ($pack.Framework -eq 'net8.0') 'Framework was not passed to New-PluginPackage.'
        Assert-Condition ($pack.Configuration -eq 'Release') 'Configuration was not passed to New-PluginPackage.'
        Assert-Condition ([bool]$pack.ResolveEntryPoints) 'ResolveEntryPoints was not passed to New-PluginPackage.'
        Assert-Condition ([bool]$pack.RequireCanonicalAbstractions) 'RequireCanonicalAbstractions was not passed to New-PluginPackage.'
        Assert-Condition ($pack.CanonicalAbstractionsVersion -eq '1.2.3') 'CanonicalAbstractionsVersion was not passed to New-PluginPackage.'
        Assert-Condition ($pack.CanonicalAbstractionsSha256 -eq 'abc123') 'CanonicalAbstractionsSha256 was not passed to New-PluginPackage.'
        Assert-Condition ($pack.CanonicalAbstractionsPath -eq 'deps/canonical-abstractions.json') 'CanonicalAbstractionsPath was not passed to New-PluginPackage.'
        Assert-Condition (@($pack.ExtraBuildArgs) -contains '-p:SkipHostBridge=true') 'ExtraBuildArgs did not include SkipHostBridge.'
        Assert-Condition (@($pack.ExtraBuildArgs) -contains '-p:UseSharedCompilation=false') 'ExtraBuildArgs did not include UseSharedCompilation.'
        Assert-Condition ($gen.ZipPath -eq $env:PLUGIN_PACK_ZIP) 'ZipPath was not passed to generator.'
        Assert-Condition ($gen.ManifestPath -eq 'packaging/custom-expected.txt') 'ExpectedContentsFile was not passed as ManifestPath.'
        Assert-Condition ([bool]$gen.Update) 'Update switch was not passed to generator.'
        Assert-Condition (-not [bool]$gen.Check) 'Check switch should not be set during Update mode.'
    }

    Remove-Item -LiteralPath $env:PLUGIN_PACK_CAPTURE, $env:EXPECTED_CONTENTS_CAPTURE, $env:PLUGIN_PACK_ZIP -Force -ErrorAction SilentlyContinue
    $env:EXPECTED_CONTENTS_EXIT_CODE = '23'
    $result = Invoke-SharedUpdater -RepoPath $repoPath -CommonRoot $commonRoot -Arguments @(
        '-Csproj', 'src/Plugin/Plugin.csproj',
        '-Check'
    )

    Assert-Condition ($result.ExitCode -eq 23) "Shared updater must propagate generator exit code 23, got $($result.ExitCode)."
}
finally {
    Remove-Item -LiteralPath $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
    $env:PLUGIN_PACK_CAPTURE = $null
    $env:EXPECTED_CONTENTS_CAPTURE = $null
    $env:PLUGIN_PACK_ZIP = $null
    $env:EXPECTED_CONTENTS_EXIT_CODE = $null
}

if ($failures.Count -gt 0) {
    Write-Host 'FAIL: update-plugin-expected-contents contract'
    foreach ($failure in $failures) {
        Write-Host " - $failure"
    }
    exit 1
}

Write-Host 'PASS: update-plugin-expected-contents contract'
