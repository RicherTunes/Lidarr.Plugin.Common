#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Negative test: verifies lint correctly fails when a SHA pin is replaced with @main.
#>

$ErrorActionPreference = 'Stop'
$lintScript = Join-Path $PSScriptRoot '..' 'lint-workflow-sha-pins.ps1'

# Create temp repo with a workflow that has @main instead of SHA
$fakeRepo = Join-Path ([System.IO.Path]::GetTempPath()) "sha-neg-test-$(Get-Random)"
$wfDir = Join-Path $fakeRepo '.github' 'workflows'
New-Item -ItemType Directory -Path $wfDir -Force | Out-Null

# Write a workflow with an unpinned Common ref (no allowlist)
@"
name: Negative Test
on: workflow_dispatch
jobs:
  gates:
    uses: RicherTunes/Lidarr.Plugin.Common/.github/workflows/packaging-gates.yml@main
"@ | Out-File (Join-Path $wfDir 'test.yml') -Encoding utf8

try {
    $result = & $lintScript -Path $fakeRepo -Mode ci
    if ($LASTEXITCODE -ne 0) {
        Write-Host "PASS: Lint correctly failed on unpinned @main ref" -ForegroundColor Green
    } else {
        Write-Host "FAIL: Lint should have failed but passed" -ForegroundColor Red
        exit 1
    }
} finally {
    Remove-Item $fakeRepo -Recurse -Force -ErrorAction SilentlyContinue
}
