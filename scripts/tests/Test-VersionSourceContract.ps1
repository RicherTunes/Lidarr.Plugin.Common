param()

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$versionPath = Join-Path $repoRoot 'VERSION'
$propsPath = Join-Path $repoRoot 'Directory.Build.props'
$contractDocPath = Join-Path $repoRoot 'docs\ECOSYSTEM_VERSION_CONTRACT.md'
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

Assert-Condition (Test-Path -LiteralPath $versionPath) "Missing VERSION file: $versionPath"
Assert-Condition (Test-Path -LiteralPath $propsPath) "Missing Directory.Build.props file: $propsPath"
Assert-Condition (Test-Path -LiteralPath $contractDocPath) "Missing ecosystem version contract doc: $contractDocPath"

if ((Test-Path -LiteralPath $versionPath) -and (Test-Path -LiteralPath $propsPath)) {
    $versionFileValue = (Get-Content -LiteralPath $versionPath -Raw).Trim()
    $propsContent = Get-Content -LiteralPath $propsPath -Raw
    $versionMatches = [regex]::Matches($propsContent, '<Version>([^<]+)</Version>')
    Assert-Condition ($versionMatches.Count -eq 1) `
        'Directory.Build.props must contain a single <Version> value.'

    if ($versionMatches.Count -gt 0) {
        $propsVersion = $versionMatches[0].Groups[1].Value.Trim()
        Assert-Condition ($versionFileValue -eq $propsVersion) `
            "VERSION ($versionFileValue) must match Directory.Build.props <Version> ($propsVersion)."
    }

    Assert-Condition ($propsContent -notmatch 'change the value here only') `
        'Directory.Build.props must not tell release authors to bump only Directory.Build.props; VERSION must stay in lockstep.'
}

if (Test-Path -LiteralPath $contractDocPath) {
    $contractDoc = Get-Content -LiteralPath $contractDocPath -Raw
    Assert-Condition ($contractDoc -notmatch '-Check\s+PackageContents') `
        'Ecosystem version docs must not advertise an unsupported ecosystem-parity-lint PackageContents scope.'
    Assert-Condition ($contractDoc -notmatch 'lint inspects the built ZIP') `
        'Ecosystem version docs must not claim ecosystem-parity-lint inspects package ZIP contents.'
    Assert-Condition ($contractDoc -match 'VERSION') `
        'Ecosystem version docs must mention the VERSION file lockstep contract.'
    Assert-Condition ($contractDoc -match 'expected-contents') `
        'Ecosystem version docs must point package-content validation at expected-contents checks.'
}

if ($failures.Count -gt 0) {
    Write-Host 'FAIL: version source contract'
    foreach ($failure in $failures) {
        Write-Host " - $failure"
    }
    exit 1
}

Write-Host 'PASS: version source contract'
