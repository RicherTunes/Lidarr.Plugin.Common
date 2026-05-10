<#
.SYNOPSIS
    Plugin Abstractions validation for multi-plugin E2E testing.

.DESCRIPTION
    Validates that all plugins ship identical Lidarr.Plugin.Abstractions.dll
    to prevent type identity conflicts at runtime.
#>

function Normalize-PluginAbstractions {
    <#
    .SYNOPSIS
        Validates Lidarr.Plugin.Abstractions.dll consistency across plugins.

    .DESCRIPTION
        Ensures all plugins ship byte-identical Abstractions.dll to prevent
        type identity conflicts at runtime. Fails with E2E_ABSTRACTIONS_SHA_MISMATCH
        if SHA256 hashes differ.

    .PARAMETER PluginsRoot
        Root directory containing plugin folders.
    #>
    param([Parameter(Mandatory = $true)][string]$PluginsRoot)

    $abstractionDlls = @(Get-ChildItem -LiteralPath $PluginsRoot -Recurse -File -Filter 'Lidarr.Plugin.Abstractions.dll' -ErrorAction SilentlyContinue)
    if (-not $abstractionDlls -or $abstractionDlls.Count -eq 0) {
        # Post multi-plugin co-existence fix (PR #485, see docs/dev-guide/ALC_MULTIPLUGIN_FIX.md):
        # Lidarr.Plugin.Abstractions.dll is now ILRepacked + internalized into each plugin's
        # merged DLL — sidecar Abstractions.dll is intentionally absent. That's the new
        # normal and is REQUIRED for multi-plugin co-existence (the sidecar caused the
        # 0x80131509 cross-ALC conflict). The old "must ship it" requirement is wrong now.
        Write-Host "No sidecar Lidarr.Plugin.Abstractions.dll found under '$PluginsRoot' — assuming merged-DLL builds (post PR #485). Skipping cross-plugin Abstractions identity check." -ForegroundColor DarkGray
        return
    }

    if ($abstractionDlls.Count -eq 1) {
        # Mixed-mode: one plugin still ships sidecar (legacy build) while others are merged.
        # Tolerated; each plugin's merged DLL self-resolves Abstractions internally and the
        # one sidecar gets loaded into the default ALC for the legacy plugin.
        return
    }

    $identities = $abstractionDlls | ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName
            FullName = [System.Reflection.AssemblyName]::GetAssemblyName($_.FullName).FullName
        }
    }

    $uniqueIdentities = @($identities | Group-Object FullName)
    if ($uniqueIdentities.Count -gt 1) {
        $details = $uniqueIdentities | ForEach-Object {
            $paths = ($_.Group | Select-Object -ExpandProperty Path) -join "`n  - "
            "$($_.Name):`n  - $paths"
        } | Out-String

        throw "Multiple DIFFERENT Lidarr.Plugin.Abstractions identities detected. All plugins must reference the same Abstractions assembly identity to avoid type identity conflicts.`n$details"
    }

    $hashes = $abstractionDlls | ForEach-Object {
        [pscustomobject]@{
            Path = $_.FullName
            Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
        }
    }

    $uniqueHashes = @($hashes | Group-Object Hash)
    if ($uniqueHashes.Count -gt 1) {
        # E2E_ABSTRACTIONS_SHA_MISMATCH: Different SHA256 across plugins causes type identity issues
        # at runtime even if assembly FullName matches (deterministic multi-plugin load failure).
        $details = $uniqueHashes | ForEach-Object {
            $paths = ($_.Group | ForEach-Object { "    - $($_.Path)" }) -join "`n"
            "  SHA256: $($_.Name.Substring(0, 16))...`n$paths"
        } | Out-String

        $errorMsg = @"
E2E_ABSTRACTIONS_SHA_MISMATCH: Multiple Lidarr.Plugin.Abstractions.dll copies with DIFFERENT SHA256 hashes detected.

All plugins must ship byte-identical Abstractions.dll to avoid type identity conflicts at runtime.
This typically happens when plugins are built from different Lidarr.Plugin.Common commits.

FIX: Rebuild all plugins from the same Common submodule SHA.

Details:
$details
"@
        throw $errorMsg
    } else {
        Write-Host "Multiple identical Lidarr.Plugin.Abstractions.dll copies detected ($($abstractionDlls.Count)); OK." -ForegroundColor Green
    }
}

Export-ModuleMember -Function Normalize-PluginAbstractions
