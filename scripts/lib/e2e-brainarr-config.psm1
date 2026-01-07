Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Read-BrainarrLLMConfigFile {
    <#
    .SYNOPSIS
        Reads optional local Brainarr LLM E2E configuration from JSON.

    .DESCRIPTION
        Loads a JSON file (gitignored) that can provide:
        - baseUrl: LLM endpoint base URL (LM Studio / OpenAI-compatible / Ollama)
        - expectedModelId: model ID to require/pin for Brainarr LLM gate

        Supports aliases:
        - base_url, llmBaseUrl
        - expected_model_id

        The function never echoes file contents in errors.
    #>
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$Path
    )

    $resolvedPath = (Resolve-Path -Path $Path -ErrorAction Stop).Path

    $raw = Get-Content -Path $resolvedPath -Raw -ErrorAction Stop
    try {
        $json = $raw | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        throw "Invalid JSON in Brainarr LLM config file: $resolvedPath"
    }

    $baseUrl = $null
    foreach ($key in @('baseUrl', 'base_url', 'llmBaseUrl')) {
        $prop = $json.PSObject.Properties[$key]
        $value = if ($prop) { $prop.Value } else { $null }
        if (-not [string]::IsNullOrWhiteSpace("$value")) {
            $baseUrl = "$value".Trim()
            break
        }
    }

    $expectedModelId = $null
    foreach ($key in @('expectedModelId', 'expected_model_id')) {
        $prop = $json.PSObject.Properties[$key]
        $value = if ($prop) { $prop.Value } else { $null }
        if (-not [string]::IsNullOrWhiteSpace("$value")) {
            $expectedModelId = "$value".Trim()
            break
        }
    }

    [PSCustomObject]@{
        baseUrl = $baseUrl
        expectedModelId = $expectedModelId
        resolvedPath = $resolvedPath
    }
}

Export-ModuleMember -Function Read-BrainarrLLMConfigFile
