# E2E Troubleshooting Guide

This guide helps diagnose and fix common E2E test failures. For error code reference, see `E2E_ERROR_CODES.md`.

## Quick Diagnosis Flow

```
E2E Failure
    │
    ├── Check run-manifest.json for `errorCode`
    │   └── Look up code in E2E_ERROR_CODES.md
    │
    ├── Check container-logs.txt for exceptions
    │   └── Search for "ReflectionTypeLoadException", "FileNotFoundException"
    │
    └── Check diagnostics-bundle.zip for detailed state
        └── Contains queue-state.json, component configs, etc.
```

## Common Failures by Error Code

### E2E_SCHEMA_MISSING_IMPLEMENTATION

**Symptoms**: Plugin not visible in Lidarr UI, schema endpoint returns empty.

**Diagnosis**:
```powershell
# Check if plugin files exist
ls X:\lidarr-test\plugins\RicherTunes\MyPlugin

# Expected files:
# - Lidarr.Plugin.MyPlugin.dll
# - Lidarr.Plugin.Abstractions.dll
# - plugin.json
```

**Common Causes**:
1. Plugin not deployed to correct path
2. `plugin.json` has wrong `main` field
3. Assembly load failure (see container logs)

**Fix**:
```powershell
# Rebuild and redeploy
./build.ps1 -Deploy

# Or verify manifest
ManifestCheck.ps1 -ProjectPath *.csproj -ManifestPath plugin.json -ResolveEntryPoints
```

### E2E_LOAD_FAILURE

**Symptoms**: Plugin discovery starts but throws exception.

**Diagnosis**:
```powershell
# Check container logs for load exceptions
docker logs lidarr-test 2>&1 | Select-String "ReflectionTypeLoadException|TypeLoadException"
```

**Common Causes**:
1. Abstractions.dll version mismatch
2. Missing required dependency
3. Wrong .NET target framework

**Fix**:
```powershell
# Verify canonical Abstractions
lidarr.plugin.common/scripts/Verify-CanonicalAbstractions.ps1

# Rebuild with canonical injection
./build.ps1 -Package  # Uses -RequireCanonicalAbstractions
```

### E2E_ABSTRACTIONS_SHA_MISMATCH

**Symptoms**: Multi-plugin test fails with Abstractions mismatch.

**Diagnosis**:
```powershell
# Compare hashes across plugins
lidarr.plugin.common/scripts/Verify-CanonicalAbstractions.ps1
```

**Fix**:
```powershell
# Rebuild all plugins from same Common commit
cd qobuzarr && git submodule update --init
cd tidalarr && git submodule update --init
cd brainarr && git submodule update --init

# Rebuild each with canonical Abstractions
foreach ($plugin in @('qobuzarr', 'tidalarr', 'brainarr')) {
    Push-Location $plugin
    ./build.ps1 -Package
    Pop-Location
}
```

### E2E_AUTH_MISSING

**Symptoms**: Gate skipped due to missing credentials.

**Diagnosis**:
```powershell
# Check required environment variables
$env:QOBUZ_APP_ID
$env:QOBUZ_APP_SECRET
$env:TIDAL_CLIENT_ID
```

**Fix**:
1. Set required environment variables
2. Or configure component in Lidarr UI first
3. Re-run E2E with credentials

### E2E_CONFIG_INVALID

**Symptoms**: Component creation/update fails with 400 error.

**Diagnosis**:
```json
// Check run-manifest.json details
{
  "errorCode": "E2E_CONFIG_INVALID",
  "details": {
    "validationErrors": ["Redirect URL is required", "..."]
  }
}
```

**Common Causes**:
1. OAuth redirect URL not set or invalid
2. Required field missing
3. Field value format incorrect

**Fix**:
1. Check Lidarr UI for component settings
2. Correct the invalid field
3. Re-run with `E2E_FORCE_CONFIG_UPDATE=1` if needed

### E2E_API_TIMEOUT

**Symptoms**: Polling loop exceeded timeout.

**Diagnosis**:
```powershell
# Check if Lidarr is responsive
Invoke-RestMethod http://localhost:8686/api/v1/system/status -Headers @{...}
```

**Common Causes**:
1. Lidarr startup slow (Docker cold start)
2. System under heavy load
3. Network issues

**Fix**:
```powershell
# Increase timeout
./e2e-runner.ps1 -Timeout 300

# Or wait for Lidarr to fully start
Start-Sleep 30
```

## Container Log Patterns

### ReflectionTypeLoadException

```
ReflectionTypeLoadException: Unable to load one or more of the requested types.
```

**Cause**: Assembly binding failure, usually version mismatch.

**Fix**: Rebuild plugin against correct Lidarr branch/version.

### FileNotFoundException for Abstractions

```
FileNotFoundException: Could not load file or assembly 'Lidarr.Plugin.Abstractions'
```

**Cause**: Abstractions.dll not included in package.

**Fix**: Ensure `New-PluginPackage -RequireCanonicalAbstractions` is used.

### Method not found

```
MissingMethodException: Method 'Foo' not found
```

**Cause**: Plugin compiled against different Lidarr API version.

**Fix**: Rebuild against correct `ext/Lidarr-source` branch.

## Diagnostic Bundle Contents

When E2E fails, check `diagnostics-bundle.zip`:

| File | Contents |
|------|----------|
| `run-manifest.json` | Error codes, timing, gate results |
| `container-logs.txt` | Lidarr stdout/stderr |
| `queue-state.json` | Download queue at failure time |
| `component-configs/` | Indexer/client configurations |

## Running E2E Locally

```powershell
# Single-plugin E2E (recommended)
./scripts/e2e-runner.ps1 -Plugin qobuzarr -Gate schema

# Multi-plugin E2E (may be unstable)
./scripts/e2e-runner.ps1 -Plugins qobuzarr,tidalarr -Gate schema

# Full gate sequence
./scripts/e2e-runner.ps1 -Plugin qobuzarr -Gate schema,search,albumsearch,grab
```

## Getting Help

1. Check `E2E_ERROR_CODES.md` for structured error reference
2. Check `PERSISTENT_E2E_TESTING.md` for E2E setup details
3. Check `ECOSYSTEM_PARITY_ROADMAP.md` for known issues
4. Open issue with run-manifest.json attached
