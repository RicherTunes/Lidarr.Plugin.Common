# Telemetry DI Contract

This document specifies the telemetry signal contract between plugins using `Lidarr.Plugin.Common` and the E2E smoke test telemetry DI gate.

## Purpose

The telemetry DI gate verifies that `IDownloadTelemetryService` from Common was successfully:
1. Resolved via dependency injection in the merged/internalized plugin assembly
2. Invoked during an actual download operation

This catches type-identity mismatches and DI registration failures that would otherwise silently break telemetry.

## Dual-Signal Approach

The gate uses a dual-signal approach for robustness:

| Signal | Priority | Stability | Description |
|--------|----------|-----------|-------------|
| Structured JSON marker | Primary | High | Deterministic, machine-parseable |
| Human-readable log patterns | Fallback | Medium | Regex-based, covers older versions |

### Why Dual Signals?

- **Structured markers** are deterministic and unlikely to break on log wording changes
- **Regex fallback** provides backward compatibility with older plugin versions that pre-date the structured marker

## Structured Signal Specification

### Marker Format

```
[LPC_TELEMETRY] {"event":"telemetry_emitted","service":"<service>","track":"<trackId>","success":<bool>}
```

### Components

| Field | Type | Description |
|-------|------|-------------|
| `[LPC_TELEMETRY]` | Prefix | Constant marker prefix for grep-ability |
| `event` | string | Always `"telemetry_emitted"` |
| `service` | string | Service name (e.g., `"Qobuzarr"`, `"Tidalarr"`) |
| `track` | string | Track identifier |
| `success` | boolean | `true` for completed downloads, `false` for failures |

### Example Log Lines

Successful download:
```
[Debug] [LPC_TELEMETRY] {"event":"telemetry_emitted","service":"Qobuzarr","track":"12345678","success":true}
```

Failed download:
```
[Debug] [LPC_TELEMETRY] {"event":"telemetry_emitted","service":"Tidalarr","track":"track-xyz","success":false}
```

### Log Level

The structured marker is emitted at **Debug** level. Ensure your Lidarr instance has debug logging enabled for the plugin namespace to capture these markers.

## Fallback Signal Specification

The fallback uses regex patterns matching the human-readable log format:

### Success Pattern
```regex
Download completed:.*track=.*bytes=.*elapsed=.*rate=
```

Example match:
```
Download completed: track=12345678 album=abc123 bytes=5242880 elapsed=2.50s rate=2048.0KB/s retries=0 429s=0
```

### Failure Pattern
```regex
Download failed:.*track=.*elapsed=.*retries=
```

Example match:
```
Download failed: track=12345678 album=abc123 elapsed=5.00s retries=3 429s=2 error=Connection timeout
```

## For Plugin Authors

### Requirements

Plugins using `SimpleDownloadOrchestrator` or `IDownloadTelemetryService` directly will automatically emit telemetry signals when:

1. The plugin registers `IDownloadTelemetryService` (or uses Common's DI registration)
2. A download operation completes (success or failure)
3. The telemetry service is invoked with `LogDownloadTelemetry()`

### Verification

To verify your plugin emits telemetry correctly:

1. Run the multi-plugin Docker smoke test with `-RunTelemetryDIGate`:
   ```powershell
   .\scripts\multi-plugin-docker-smoke-test.ps1 `
     -PluginZip "yourplugin=path/to/plugin.zip" `
     -RunTelemetryDIGate
   ```

2. Check which signal method succeeded:
   - **structured JSON marker (primary)** - Ideal, indicates modern Common version
   - **human-readable log regex (fallback)** - Works but may be fragile

### Troubleshooting

If the telemetry DI gate fails:

1. **No structured marker found**
   - Ensure you're using a Common version that includes the structured marker (v1.6.0+)
   - Check that Debug-level logging is enabled

2. **No fallback regex match**
   - Verify `IDownloadTelemetryService` is registered in DI
   - Confirm the download path invokes `LogDownloadTelemetry()`
   - Check for type-identity issues with ILRepack/ILMerge

3. **Both signals missing**
   - Review DI registration in your plugin's startup
   - Ensure the telemetry service is not being null-coalesced away
   - Check container logs for DI resolution errors

## Implementation Details

### Source Files

- **Service**: `src/Services/Download/DownloadTelemetryService.cs`
- **Interface**: `src/Services/Download/IDownloadTelemetryService.cs`
- **E2E Gate**: `scripts/multi-plugin-docker-smoke-test.ps1` (telemetry DI gate section)

### Constant Reference

The marker prefix is defined as a public constant for programmatic access:

```csharp
// In DownloadTelemetryService.cs
public const string StructuredMarkerPrefix = "[LPC_TELEMETRY]";
```

## Version History

| Version | Change |
|---------|--------|
| 1.6.0 | Added structured JSON marker as primary signal |
| 1.5.x | Human-readable log patterns only (now fallback) |
