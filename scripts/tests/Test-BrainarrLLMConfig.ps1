Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot "../lib/e2e-brainarr-config.psm1") -Force

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,
        [Parameter(Mandatory)]
        [string]$Message
    )
    if (-not $Condition) { throw $Message }
}

$tempDir = Join-Path ([System.IO.Path]::GetTempPath()) ("brainarr-llm-config-tests-" + [Guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Path $tempDir -Force | Out-Null

try {
    $cfgPath = Join-Path $tempDir "config.json"
    Set-Content -Path $cfgPath -Value '{"baseUrl":"http://localhost:1234","expectedModelId":"mistralai/ministral-3-3b"}' -Encoding UTF8
    $cfg = Read-BrainarrLLMConfigFile -Path $cfgPath
    Assert-True ($cfg.baseUrl -eq 'http://localhost:1234') "baseUrl parsed incorrectly"
    Assert-True ($cfg.expectedModelId -eq 'mistralai/ministral-3-3b') "expectedModelId parsed incorrectly"
    Assert-True (-not [string]::IsNullOrWhiteSpace("$($cfg.resolvedPath)")) "resolvedPath missing"

    $cfgPath2 = Join-Path $tempDir "config2.json"
    Set-Content -Path $cfgPath2 -Value '{"base_url":"http://127.0.0.1:11434","expected_model_id":"qwen2.5:latest"}' -Encoding UTF8
    $cfg2 = Read-BrainarrLLMConfigFile -Path $cfgPath2
    Assert-True ($cfg2.baseUrl -eq 'http://127.0.0.1:11434') "base_url alias not parsed"
    Assert-True ($cfg2.expectedModelId -eq 'qwen2.5:latest') "expected_model_id alias not parsed"

    $badPath = Join-Path $tempDir "bad.json"
    Set-Content -Path $badPath -Value '{not json' -Encoding UTF8
    $threw = $false
    try { $null = Read-BrainarrLLMConfigFile -Path $badPath } catch { $threw = $true }
    Assert-True $threw "Expected invalid JSON to throw"

    Write-Host "PASS: Test-BrainarrLLMConfig"
}
finally {
    Remove-Item -Path $tempDir -Recurse -Force -ErrorAction SilentlyContinue
}

