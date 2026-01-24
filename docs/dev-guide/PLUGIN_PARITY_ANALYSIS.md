# Plugin Parity Analysis

This document analyzes the implementation parity between Qobuzarr and Tidalarr plugins, identifying gaps and standardization opportunities.

## Quality Selection Contract

### Current State

| Aspect | Qobuzarr | Tidalarr |
|--------|----------|----------|
| **Tiers** | 4: MP3-320, FLAC-CD, FLAC-96, FLAC-192 | 4: Low-AAC-96, High-AAC-320, Lossless-FLAC, HiRes-FLAC |
| **Selection** | Single preferred quality | Single preferred quality |
| **Fallback** | Automatic downgrade chain | Preference-respecting degradation |
| **API Param** | `format_id` (5, 6, 7, 27) | `audioquality` (LOW, HIGH, LOSSLESS, HI_RES_LOSSLESS) |

### Qobuz Fallback Chain

```
FLAC-192 (27) → FLAC-96 (7) → FLAC-CD (6) → MP3-320 (5)
```

Note: Format 27 is effectively broken in Qobuz API and always skips to 7.

### Tidal Degradation

```
Preferred quality is treated as MAXIMUM acceptable
If unavailable → next highest quality ≤ preference
If none available → lowest available quality (fallback)
```

### Subscription Awareness

| Feature | Qobuzarr | Tidalarr |
|---------|----------|----------|
| Tier detection | Sublime, Premier, Free | Implicit via API responses |
| Cap enforcement | Yes (Sublime can't request Hi-Res) | No (relies on API rejection) |
| Sample detection | Yes (30s preview = Free tier) | Not applicable |

### Gap Analysis

1. **Fallback semantics differ**: Qobuz always downgrades aggressively; Tidal respects preference cap
2. **MQA handling**: Tidalarr has separate `IncludeMqa` toggle; Qobuz has no equivalent
3. **Re-encoding**: Tidalarr can re-encode AAC→FLAC container; Qobuz does not

### Recommendation

Document these as **intentional differences** rather than forcing parity. Both approaches are valid for their respective services:
- Qobuz: Aggressive fallback compensates for limited Hi-Res availability
- Tidal: Preference-respecting approach respects bandwidth/storage constraints

---

## Error Surface Parity

### E2E Error Codes (Shared - IDENTICAL)

Both plugins use identical E2E error codes from `Lidarr.Plugin.Common`:

| Code | Description | Gate |
|------|-------------|------|
| `E2E_AUTH_MISSING` | Missing credentials | Search, Grab |
| `E2E_CONFIG_INVALID` | Settings validation failed | Configure |
| `E2E_API_TIMEOUT` | Lidarr/provider API timeout | Any |
| `E2E_DOCKER_UNAVAILABLE` | Docker interaction failed | Bootstrap |
| `E2E_NO_RELEASES_ATTRIBUTED` | Search returns releases but none from plugin | Search |
| `E2E_QUEUE_NOT_FOUND` | Grab triggered but queue item missing | Grab |
| `E2E_ZERO_AUDIO_FILES` | Download completed with zero audio files | Download |
| `E2E_METADATA_MISSING` | Audio files lack required tags | Metadata |
| `E2E_IMPORT_FAILED` | ImportListSync completed with errors | Import |
| `E2E_COMPONENT_AMBIGUOUS` | Multiple components match plugin | Configure |
| `E2E_HOST_PLUGIN_DISCOVERY_DISABLED` | Plugin files exist but not loaded | Schema |

### PluginErrorCode Enum (Shared - IDENTICAL)

```csharp
None, Unknown, ValidationFailed, NotFound, Unauthorized,
AuthenticationExpired, RateLimited, Timeout, Cancelled,
ProviderUnavailable, Unsupported, QuotaExceeded, Conflict,
ParsingFailed, NetworkFailure
```

### Implementation Differences

| Aspect | Qobuzarr | Tidalarr |
|--------|----------|----------|
| **Diagnostic codes** | None | IX000, IX100, IX200, DL100 |
| **Error metadata** | Basic exception message | Structured with service, errorId, quality |
| **Explicit PluginErrorCode usage** | ~6 locations | ~15+ locations |

### Gap Analysis

1. **Tidalarr is more instrumented**: Has diagnostic codes (IX000=valid, IX100=invalid, IX200=auth failed)
2. **Qobuzarr relies on inference**: E2E runner infers error codes from exception text patterns
3. **Metadata richness**: Tidalarr includes quality parameters in error details; Qobuzarr does not

### Recommendation

Qobuzarr should adopt Tidalarr's pattern:

```csharp
// Current Qobuzarr (implicit)
throw new QobuzApiException("Unauthorized access");

// Recommended (explicit)
return PluginOperationResult.Failure(new PluginError(
    PluginErrorCode.Unauthorized,
    "Unauthorized access",
    exception,
    new Dictionary<string, string> {
        ["id"] = "QX200",  // Qobuz diagnostic code
        ["service"] = "Qobuzarr",
        ["endpoint"] = endpointPath
    }));
```

---

## Host Bug Detection (IDENTICAL)

Both plugins share identical host bug detection:

| Classification | Description | Severity |
|----------------|-------------|----------|
| `ALC` | Assembly Load Context failure | `host_bug` |
| `ABI_MISMATCH` | Binary interface mismatch | `plugin_rebuild` |
| `DEPENDENCY_DRIFT` | Dependency version conflict | `version_conflict` |
| `DISCOVERY_DISABLED` | Plugin discovery disabled | `host_bug` |
| `TYPE_INIT_FAILURE` | Static constructor failure | `investigate` |
| `LOAD_FAILURE` | Generic assembly load failure | `investigate` |

---

## Next Steps

### P1 - Must Fix
- [ ] Add diagnostic codes to Qobuzarr error responses (QX000, QX100, QX200)
- [ ] Document quality selection contracts in plugin READMEs

### P2 - Should Fix
- [ ] Add structured error metadata to Qobuzarr PluginError instances
- [ ] Standardize quality badge format in release titles

### P3 - Nice to Have
- [ ] Add MQA-equivalent toggle to Qobuzarr (Hi-Res preference flag)
- [ ] Consider re-encode option for Qobuzarr AAC→FLAC

---

## Appendix: File References

### Quality Selection
- Qobuz: `qobuzarr/src/Core/QobuzQualityId.cs`, `qobuzarr/src/Download/Services/QualityFallbackProvider.cs`
- Tidal: `tidalarr/src/Tidalarr/Core/Models/TidalQuality.cs`, `tidalarr/src/Tidalarr/Domain/Quality/TidalQualityDetector.cs`

### Error Handling
- Shared: `lidarr.plugin.common/src/Abstractions/Results/PluginErrorCode.cs`
- Tidal: `tidalarr/src/Tidalarr/Integration/TidalIndexer.cs` (lines 138-200)
- E2E: `lidarr.plugin.common/scripts/lib/e2e-json-output.psm1` (lines 16-27)

### Golden Fixtures
- `lidarr.plugin.common/tests/fixtures/run-manifests/` (5 fixtures)
