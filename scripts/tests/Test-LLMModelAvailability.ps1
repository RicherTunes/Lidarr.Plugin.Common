Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

Import-Module (Join-Path $PSScriptRoot "../lib/e2e-gates.psm1") -Force

function Assert-True {
    param(
        [Parameter(Mandatory)]
        [bool]$Condition,
        [Parameter(Mandatory)]
        [string]$Message
    )
    if (-not $Condition) { throw $Message }
}

Assert-True (Test-LLMModelAvailability -Models @('mistralai/ministral-3-3b', 'qwen2.5:latest') -ExpectedModelId 'mistralai/ministral-3-3b') "Exact match failed"
Assert-True (Test-LLMModelAvailability -Models @('QWEN2.5:latest') -ExpectedModelId 'qwen2.5:latest') "Case-insensitive match failed"
Assert-True (-not (Test-LLMModelAvailability -Models @('qwen2.5:latest') -ExpectedModelId 'qwen2.5')) "Substring match should fail"
Assert-True (Test-LLMModelAvailability -Models @() -ExpectedModelId '') "Empty expected should be permissive"

Write-Host "PASS: Test-LLMModelAvailability"

