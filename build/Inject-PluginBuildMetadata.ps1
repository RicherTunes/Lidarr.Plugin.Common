#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Injects gitSha + buildTimestamp into a plugin manifest (plugin.json).
.DESCRIPTION
    Extracted from an inline MSBuild Exec command. Inline pwsh commands break on
    Linux because MSBuild runs them through /bin/sh, which expands the PowerShell
    `$variable` sigils as (empty) shell variables before pwsh parses them — e.g.
    `$manifest | Add-Member` becomes ` | Add-Member` ("empty pipe element").
    Passing the values as -File parameters avoids that entirely.
#>
param(
    [Parameter(Mandatory = $true)][string]$ManifestPath,
    [Parameter(Mandatory = $true)][string]$GitSha,
    [Parameter(Mandatory = $true)][string]$BuildTimestamp
)

$ErrorActionPreference = 'Stop'

$manifest = Get-Content -LiteralPath $ManifestPath -Raw | ConvertFrom-Json
$manifest | Add-Member -NotePropertyName 'gitSha' -NotePropertyValue $GitSha -Force
$manifest | Add-Member -NotePropertyName 'buildTimestamp' -NotePropertyValue $BuildTimestamp -Force
$manifest | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath $ManifestPath -Encoding UTF8
