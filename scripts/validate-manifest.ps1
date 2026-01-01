#!/usr/bin/env pwsh
<#
.SYNOPSIS
    Validates E2E run manifest JSON against the local schema.

.DESCRIPTION
    Validates run-manifest.json files against docs/reference/e2e-run-manifest.schema.json.
    Uses the local schema by default (no network fetch), even if the manifest has a
    SHA-pinned $schema URL.

    Runtime detection order:
    1. Test-Json -SchemaFile (PowerShell 7.3+, fastest, no dependencies)
    2. Python jsonschema (if python3 + jsonschema installed)
    3. Node ajv-cli (if node + ajv-cli installed)
    4. Minimal structural fallback (checks required fields only, still exits 2)

.PARAMETER ManifestPath
    Path to the manifest JSON file to validate.

.PARAMETER SchemaPath
    Path to the JSON schema file. Defaults to docs/reference/e2e-run-manifest.schema.json
    relative to the repository root.

.PARAMETER Validator
    Force a specific validator: 'auto', 'powershell', 'python', 'node', 'structural'.
    Default is 'auto' (tries each in order).

.PARAMETER Quiet
    Suppress informational output; only show errors.

.OUTPUTS
    Exit code 0: Valid manifest
    Exit code 1: Invalid manifest (schema violations or JSON parse error)
    Exit code 2: No validator available (actionable install hint provided)

.EXAMPLE
    ./scripts/validate-manifest.ps1 -ManifestPath ./diagnostics/run-manifest.json

.EXAMPLE
    ./scripts/validate-manifest.ps1 -ManifestPath ./test.json -Validator python
#>
param(
    [Parameter(Mandatory, Position = 0)]
    [string]$ManifestPath,

    [string]$SchemaPath,

    [ValidateSet('auto', 'powershell', 'python', 'node', 'structural')]
    [string]$Validator = 'auto',

    [switch]$Quiet
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Resolve paths
$scriptRoot = Split-Path -Parent $PSScriptRoot
if (-not $SchemaPath) {
    $SchemaPath = Join-Path $scriptRoot 'docs/reference/e2e-run-manifest.schema.json'
}

# Validate inputs
if (-not (Test-Path $ManifestPath)) {
    Write-Error "Manifest file not found: $ManifestPath"
    exit 1
}

if (-not (Test-Path $SchemaPath)) {
    Write-Error "Schema file not found: $SchemaPath"
    exit 1
}

$ManifestPath = Resolve-Path $ManifestPath
$SchemaPath = Resolve-Path $SchemaPath

function Write-Info {
    param([string]$Message)
    if (-not $Quiet) {
        Write-Host $Message -ForegroundColor Cyan
    }
}

function Write-Success {
    param([string]$Message)
    Write-Host $Message -ForegroundColor Green
}

function Write-ValidationError {
    param([string]$Message)
    Write-Host "VALIDATION ERROR: $Message" -ForegroundColor Red
}

# ============================================================================
# Validator: PowerShell Test-Json (7.3+)
# ============================================================================
function Test-PowerShellValidator {
    # Test-Json with -SchemaFile requires PS 7.3+
    if ($PSVersionTable.PSVersion.Major -lt 7) { return $false }
    if ($PSVersionTable.PSVersion.Major -eq 7 -and $PSVersionTable.PSVersion.Minor -lt 3) { return $false }
    return $true
}

function Invoke-PowerShellValidator {
    param([string]$Manifest, [string]$Schema)

    Write-Info "Validator: PowerShell Test-Json ($($PSVersionTable.PSVersion))"

    try {
        $content = Get-Content -Path $Manifest -Raw -ErrorAction Stop
    }
    catch {
        Write-ValidationError "Failed to read manifest: $_"
        return 1
    }

    try {
        $result = Test-Json -Json $content -SchemaFile $Schema -ErrorAction Stop
        if ($result) {
            Write-Success "Manifest is valid"
            return 0
        }
        else {
            Write-ValidationError "Manifest is invalid"
            return 1
        }
    }
    catch {
        $errorMsg = $_.Exception.Message
        # Parse Test-Json error for better output
        if ($errorMsg -match 'JSON is not valid') {
            Write-ValidationError "JSON parse error: $errorMsg"
        }
        else {
            Write-ValidationError $errorMsg
        }
        return 1
    }
}

# ============================================================================
# Validator: Python jsonschema
# ============================================================================
function Get-PythonCommand {
    # On Windows, prefer py -3 (Python Launcher) to avoid Microsoft Store stub
    if ($IsWindows -or $env:OS -match 'Windows') {
        try {
            $job = Start-Job { py -3 --version 2>&1 }
            $result = $job | Wait-Job -Timeout 5 | Receive-Job
            Remove-Job $job -Force -ErrorAction SilentlyContinue
            if ($LASTEXITCODE -eq 0 -and $result -match 'Python') {
                return 'py -3'
            }
        }
        catch { }
    }

    # Try python3 (Linux/macOS default)
    try {
        $job = Start-Job { python3 --version 2>&1 }
        $result = $job | Wait-Job -Timeout 5 | Receive-Job
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -eq 0 -and $result -match 'Python') {
            return 'python3'
        }
    }
    catch { }

    # Fallback to python
    try {
        $job = Start-Job { python --version 2>&1 }
        $result = $job | Wait-Job -Timeout 5 | Receive-Job
        Remove-Job $job -Force -ErrorAction SilentlyContinue
        if ($LASTEXITCODE -eq 0 -and $result -match 'Python') {
            return 'python'
        }
    }
    catch { }

    return $null
}

function Test-PythonValidator {
    $pythonCmd = Get-PythonCommand
    if (-not $pythonCmd) { return $false }

    try {
        # Check if jsonschema module is installed (with timeout)
        $checkScript = 'import jsonschema; print("ok")'
        if ($pythonCmd -eq 'py -3') {
            $job = Start-Job -ScriptBlock { param($s) py -3 -c $s 2>&1 } -ArgumentList $checkScript
        }
        else {
            $job = Start-Job -ScriptBlock { param($cmd, $s) & $cmd -c $s 2>&1 } -ArgumentList $pythonCmd, $checkScript
        }
        $result = $job | Wait-Job -Timeout 10 | Receive-Job
        Remove-Job $job -Force -ErrorAction SilentlyContinue

        return $result -match 'ok'
    }
    catch {
        return $false
    }
}

function Invoke-PythonValidator {
    param([string]$Manifest, [string]$Schema)

    $pythonCmd = Get-PythonCommand
    Write-Info "Validator: python-jsonschema ($pythonCmd)"

    $pythonScript = @'
import sys
import json
from jsonschema import validate, ValidationError, SchemaError

manifest_path = sys.argv[1]
schema_path = sys.argv[2]

try:
    with open(manifest_path, 'r', encoding='utf-8') as f:
        manifest = json.load(f)
except json.JSONDecodeError as e:
    print(f"JSON parse error at line {e.lineno}, column {e.colno}: {e.msg}", file=sys.stderr)
    sys.exit(1)
except Exception as e:
    print(f"Failed to read manifest: {e}", file=sys.stderr)
    sys.exit(1)

try:
    with open(schema_path, 'r', encoding='utf-8') as f:
        schema = json.load(f)
except Exception as e:
    print(f"Failed to read schema: {e}", file=sys.stderr)
    sys.exit(1)

try:
    validate(instance=manifest, schema=schema)
    print("Manifest is valid")
    sys.exit(0)
except ValidationError as e:
    path = ' -> '.join(str(p) for p in e.absolute_path) if e.absolute_path else '(root)'
    print(f"Validation error at '{path}': {e.message}", file=sys.stderr)
    sys.exit(1)
except SchemaError as e:
    print(f"Schema error: {e.message}", file=sys.stderr)
    sys.exit(1)
'@

    try {
        # Write script to temp file to avoid piping issues
        $tempScript = [System.IO.Path]::GetTempFileName() + '.py'
        $pythonScript | Out-File -FilePath $tempScript -Encoding UTF8 -Force

        if ($pythonCmd -eq 'py -3') {
            $output = & py -3 $tempScript $Manifest $Schema 2>&1
        }
        else {
            $output = & $pythonCmd $tempScript $Manifest $Schema 2>&1
        }
        $exitCode = $LASTEXITCODE

        Remove-Item $tempScript -Force -ErrorAction SilentlyContinue

        if ($exitCode -eq 0) {
            Write-Success "Manifest is valid"
            return 0
        }
        else {
            foreach ($line in $output) {
                Write-ValidationError $line
            }
            return 1
        }
    }
    catch {
        Write-ValidationError "Python validator failed: $_"
        return 1
    }
}

# ============================================================================
# Validator: Node ajv-cli (local install only, no network/npx)
# ============================================================================
function Test-NodeValidator {
    # Only check for locally installed ajv command - avoid npx which can trigger network
    # Check if ajv is directly callable (global install)
    try {
        $ajvPath = Get-Command 'ajv' -ErrorAction SilentlyContinue
        if ($ajvPath) {
            $null = & ajv --version 2>&1
            if ($LASTEXITCODE -eq 0) {
                return $true
            }
        }
    }
    catch { }

    # Check for ajv-cli in local node_modules/.bin
    $localAjv = Join-Path $PWD 'node_modules/.bin/ajv'
    if ($IsWindows -or $env:OS -match 'Windows') {
        $localAjv = Join-Path $PWD 'node_modules/.bin/ajv.cmd'
    }
    if (Test-Path $localAjv) {
        return $true
    }

    # Do NOT fall back to npx - it can trigger network installs
    return $false
}

function Get-AjvCommand {
    # Check global install first
    $ajvPath = Get-Command 'ajv' -ErrorAction SilentlyContinue
    if ($ajvPath) {
        return 'ajv'
    }

    # Check local install
    $localAjv = Join-Path $PWD 'node_modules/.bin/ajv'
    if ($IsWindows -or $env:OS -match 'Windows') {
        $localAjv = Join-Path $PWD 'node_modules/.bin/ajv.cmd'
    }
    if (Test-Path $localAjv) {
        return $localAjv
    }

    return $null
}

function Invoke-NodeValidator {
    param([string]$Manifest, [string]$Schema)

    $ajvCmd = Get-AjvCommand
    Write-Info "Validator: ajv-cli ($ajvCmd)"

    try {
        # ajv validate -s schema.json -d manifest.json
        $output = & $ajvCmd validate -s $Schema -d $Manifest --spec=draft2020 2>&1
        $exitCode = $LASTEXITCODE

        if ($exitCode -eq 0) {
            Write-Success "Manifest is valid"
            return 0
        }
        else {
            foreach ($line in $output) {
                if ($line -match 'error|invalid' -or $line -notmatch '^\s*$') {
                    Write-ValidationError $line
                }
            }
            return 1
        }
    }
    catch {
        Write-ValidationError "Node validator failed: $_"
        return 1
    }
}

# ============================================================================
# Validator: Minimal structural fallback
# ============================================================================
function Invoke-StructuralValidator {
    param([string]$Manifest, [string]$Schema)

    Write-Info "Validator: structural-fallback (partial coverage only)"

    # Required fields from schema
    $requiredFields = @(
        'schemaVersion',
        'schemaId',
        'timestamp',
        'runId',
        'runner',
        'lidarr',
        'request',
        'effective',
        'redaction',
        'results',
        'summary',
        'diagnostics',
        'hostBugSuspected'
    )

    try {
        $content = Get-Content -Path $Manifest -Raw -ErrorAction Stop
    }
    catch {
        Write-ValidationError "Failed to read manifest: $_"
        return 1
    }

    try {
        # Use different variable name to avoid collision with [string]$Manifest parameter
        $manifestObj = $content | ConvertFrom-Json -ErrorAction Stop
    }
    catch {
        Write-ValidationError "JSON parse error: $_"
        return 1
    }

    $errors = @()

    # Helper to safely get property value
    function Get-SafeProperty {
        param($Obj, [string]$Name)
        if ($null -eq $Obj) { return $null }
        if ($Obj.PSObject.Properties.Name -contains $Name) {
            return $Obj.$Name
        }
        return $null
    }

    # Check required fields
    foreach ($field in $requiredFields) {
        if (-not ($manifestObj.PSObject.Properties.Name -contains $field)) {
            $errors += "Missing required field: $field"
        }
    }

    # Check schemaVersion value
    $schemaVersion = Get-SafeProperty $manifestObj 'schemaVersion'
    if ($schemaVersion -and $schemaVersion -ne '1.2') {
        $errors += "Invalid schemaVersion: expected '1.2', got '$schemaVersion'"
    }

    # Check schemaId value
    $schemaId = Get-SafeProperty $manifestObj 'schemaId'
    if ($schemaId -and $schemaId -ne 'richer-tunes.lidarr.e2e-run-manifest') {
        $errors += "Invalid schemaId: expected 'richer-tunes.lidarr.e2e-run-manifest', got '$schemaId'"
    }

    # Check errorCode pattern in results
    $resultsArr = Get-SafeProperty $manifestObj 'results'
    if ($resultsArr) {
        foreach ($result in $resultsArr) {
            $errorCode = Get-SafeProperty $result 'errorCode'
            if ($errorCode -and $errorCode -notmatch '^E2E_[A-Z0-9_]+$') {
                $errors += "Invalid errorCode format: '$errorCode' (must match ^E2E_[A-Z0-9_]+$)"
            }
        }
    }

    if ($errors.Count -gt 0) {
        foreach ($err in $errors) {
            Write-ValidationError $err
        }
        return 1
    }

    Write-Host "Manifest passes structural checks (partial validation only)" -ForegroundColor Yellow
    Write-Host "WARNING: Full schema validation not available. Install one of:" -ForegroundColor Yellow
    Write-Host "  - PowerShell 7.3+ (Test-Json -SchemaFile)" -ForegroundColor Yellow
    Write-Host "  - Python: pip install jsonschema" -ForegroundColor Yellow
    Write-Host "  - Node: npm install -g ajv-cli" -ForegroundColor Yellow
    return 2
}

# ============================================================================
# Main validation logic
# ============================================================================
function Invoke-Validation {
    param([string]$Manifest, [string]$Schema, [string]$ValidatorChoice)

    switch ($ValidatorChoice) {
        'powershell' {
            if (Test-PowerShellValidator) {
                return Invoke-PowerShellValidator -Manifest $Manifest -Schema $Schema
            }
            Write-Error "PowerShell 7.3+ required for Test-Json -SchemaFile"
            return 2
        }
        'python' {
            if (Test-PythonValidator) {
                return Invoke-PythonValidator -Manifest $Manifest -Schema $Schema
            }
            Write-Error "Python jsonschema not available. Install: pip install jsonschema"
            return 2
        }
        'node' {
            if (Test-NodeValidator) {
                return Invoke-NodeValidator -Manifest $Manifest -Schema $Schema
            }
            Write-Error "Node ajv-cli not available. Install: npm install -g ajv-cli"
            return 2
        }
        'structural' {
            return Invoke-StructuralValidator -Manifest $Manifest -Schema $Schema
        }
        'auto' {
            # Try validators in order
            if (Test-PowerShellValidator) {
                return Invoke-PowerShellValidator -Manifest $Manifest -Schema $Schema
            }
            if (Test-PythonValidator) {
                return Invoke-PythonValidator -Manifest $Manifest -Schema $Schema
            }
            if (Test-NodeValidator) {
                return Invoke-NodeValidator -Manifest $Manifest -Schema $Schema
            }
            # Fallback to structural
            return Invoke-StructuralValidator -Manifest $Manifest -Schema $Schema
        }
    }
}

# Run validation
Write-Info "Validating: $ManifestPath"
Write-Info "Schema: $SchemaPath"

$exitCode = Invoke-Validation -Manifest $ManifestPath -Schema $SchemaPath -ValidatorChoice $Validator
exit $exitCode
