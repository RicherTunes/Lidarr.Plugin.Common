param()

$ErrorActionPreference = 'Stop'

$repoRoot = Resolve-Path (Join-Path $PSScriptRoot '..\..')
$apiBaselineGenerator = Join-Path $repoRoot 'tools\ApiBaselineGenerator\ApiBaselineGenerator.csproj'
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

Assert-Condition (Test-Path -LiteralPath $apiBaselineGenerator) "Missing API baseline generator project: $apiBaselineGenerator"

if ($failures.Count -eq 0) {
    $content = Get-Content -LiteralPath $apiBaselineGenerator -Raw
    Assert-Condition ($content -match '<TargetFramework>net8\.0</TargetFramework>') `
        'API baseline generator must target net8.0 so Common tooling does not require a net9 SDK.'
    Assert-Condition ($content -notmatch '<TargetFramework>net9\.0</TargetFramework>') `
        'API baseline generator must not target a newer TFM while Lidarr plugin/runtime tooling is net8-only.'
    Assert-Condition ($content -notmatch 'System\.Reflection\.MetadataLoadContext"\s+Version="9\.') `
        'API baseline generator must not pin net9 System.Reflection.MetadataLoadContext packages.'
}

if ($failures.Count -gt 0) {
    Write-Host 'FAIL: net8 tooling contract'
    foreach ($failure in $failures) {
        Write-Host " - $failure"
    }
    exit 1
}

Write-Host 'PASS: net8 tooling contract'
