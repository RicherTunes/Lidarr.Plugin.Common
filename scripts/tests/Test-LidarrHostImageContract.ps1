param()

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$extractorPath = Join-Path $repoRoot 'scripts\extract-lidarr-assemblies.sh'
$expectedDockerTag = 'nightly-3.1.3.4970'
$expectedTarballFallback = '3.1.3.4968'
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

Assert-Condition (Test-Path -LiteralPath $extractorPath) "Missing extractor: $extractorPath"

if ($failures.Count -eq 0) {
    $content = Get-Content -LiteralPath $extractorPath -Raw

    Assert-Condition ($content -match 'LIDARR_DOCKER_VERSION="\$\{LIDARR_DOCKER_VERSION:-nightly-3\.1\.3\.4970\}"') `
        "Shared extractor must default to the current plugin-capable Lidarr image tag $expectedDockerTag."
    Assert-Condition ($content -notmatch 'pr-plugins-3\.1\.2\.4913') `
        'Shared extractor must not default to the retired pr-plugins host image.'
    Assert-Condition ($content -match '\[\[\s*"\$LIDARR_DOCKER_VERSION"\s*=~\s*\(\[0-9\]\+\\\.\[0-9\]\+\\\.\[0-9\]\+\\\.\[0-9\]\+\)') `
        'Tarball fallback must parse the numeric version from prefixed tags such as nightly-3.1.3.4970.'
    Assert-Condition ($content -match [regex]::Escape($expectedTarballFallback)) `
        "Tarball fallback must include the latest published Lidarr release $expectedTarballFallback before older emergency fallbacks."
}

if ($failures.Count -gt 0) {
    Write-Host 'FAIL: Lidarr host image contract'
    foreach ($failure in $failures) {
        Write-Host " - $failure"
    }
    exit 1
}

Write-Host 'PASS: Lidarr host image contract'
