# E2E Error Codes

This document describes the structured `errorCode` values emitted into `run-manifest.json` by the E2E runner.

These codes are intended for:

- CI triage (job summaries, dashboards)
- consistent human debugging without downloading full artifacts

For diagnostics bundle structure, see `docs/DIAGNOSTICS_BUNDLE_CONTRACT.md`.

## Error Code Reference

| `errorCode` | Meaning | Common Causes | First Fix To Try |
|---|---|---|---|
| `E2E_AUTH_MISSING` | Required credentials are missing for the requested gate. | Secrets/env vars not set; Lidarr component not configured; masked `********` values but no overwrite requested. | Set required secrets/env vars; rerun Configure; use `E2E_FORCE_CONFIG_UPDATE=1` when rotating tokens. |
| `E2E_CONFIG_INVALID` | Configuration exists but fails validation (server-side reject). | Wrong `redirectUrl`; invalid market; missing download path; invalid field shape. | Check component fields in Lidarr UI/API; rerun with `E2E_FORCE_CONFIG_UPDATE=1`; ensure schema field names match. |
| `E2E_API_TIMEOUT` | A Lidarr API call or polling loop timed out. | Lidarr slow startup; Docker host under load; network hiccup; gate timeout too low. | Increase timeout input; re-run; inspect `container-logs.txt` and command status endpoints. |
| `E2E_LIDARR_UNREACHABLE` | Lidarr API is unreachable (transport failure). | Lidarr not running; wrong port mapping; DNS failure; TLS/cert error; firewall. | Verify `-LidarrUrl` is correct and reachable; check container port mappings; inspect container logs; then re-run. |
| `E2E_DOCKER_UNAVAILABLE` | Docker interaction required but not available. | Docker not installed/running; insufficient permissions; wrong container name. | Verify Docker daemon; set correct `-ContainerName`; run schema gate without docker-only features. |
| `E2E_NO_RELEASES_ATTRIBUTED` | AlbumSearch returned releases but none attributed to the target plugin. | Indexer not actually used; parser regression (missing indexerId/indexer); wrong indexer id; cached results from other indexers. | Re-run AlbumSearch; check `indexerId` attribution warnings; verify plugin schema + configured indexer id. |
| `E2E_QUEUE_NOT_FOUND` | Grab triggered but expected queue item could not be correlated. | Download client didn’t enqueue; correlation logic mismatch; Lidarr API change; auth failure masked as enqueue. | Inspect `queue-state.json`; check download client logs; verify DownloadProtocol matches release protocol. |
| `E2E_ZERO_AUDIO_FILES` | Download completed but produced zero validated audio files. | Download client bug; output path wrong; partial files only; upstream stream failure; file extension filtering mismatch. | Inspect output directory; check `container-logs.txt` for download errors; verify validator settings and file naming. |
| `E2E_METADATA_MISSING` | Audio file(s) exist but required tags are missing. | Metadata applier not wired; TagLib limitations for container format; incomplete track model mapping. | Verify `IAudioMetadataApplier` wiring; validate `StreamingTrack` fields; inspect TagLib warnings. |
| `E2E_IMPORT_FAILED` | ImportListSync completed with errors or post-sync state indicates failure. | Provider unreachable; LLM endpoint down; Brainarr import list misconfigured; auth expired. | Check `lastSyncError`; verify LLM endpoint reachability; rerun with longer timeout. |
| `E2E_COMPONENT_AMBIGUOUS` | Multiple components match selection criteria for a plugin. | Multiple indexers/download clients configured for same plugin; no preferred ID set. | Use `-ComponentIdsInstanceSalt` or pin preferred IDs via `E2E_COMPONENT_IDS_PATH`. |
| `E2E_ABSTRACTIONS_SHA_MISMATCH` | Plugins ship non-identical `Lidarr.Plugin.Abstractions.dll` bytes. | Plugins built from different Common submodule commits. | Rebuild all plugins from same Common SHA; rerun packaging preflight. |
| `E2E_SCHEMA_MISSING_IMPLEMENTATION` | Schema endpoint accessible but plugin implementation not found. | Plugin not deployed; wrong plugin folder; bad plugin.json; DLL name mismatch; load failure. | Check `details.discoveryDiagnosis` in manifest for root cause indicators (pluginPackagePresent, pluginJsonPresent, etc.). |
| `E2E_HOST_PLUGIN_DISCOVERY_DISABLED` | Host has plugin discovery disabled (confirmed via host capabilities). | Host `EnablePlugins` setting is `false`. | Check `config.xml` for `<EnablePlugins>true</EnablePlugins>`. Only emitted with affirmative host evidence. |
| `E2E_PROVIDER_UNAVAILABLE` | Expected external provider (e.g., LLM model) not found. | LLM endpoint reachable but expected model not loaded. | Load expected model in LM Studio/Ollama; verify `expectedModelId` in config. |
| `E2E_LOAD_FAILURE` | Plugin failed to load during schema discovery. | `ReflectionTypeLoadException`; ALC lifecycle issue; missing dependencies; TFM mismatch. | Check `container-logs.txt` for load exceptions; see `hostBugSuspected.classification` in manifest; verify plugin built against correct Lidarr tag. |

## Structured Details Contract

Each explicit error code includes a `results[].details` object with stable, machine-consumable fields intended for triage automation.

**General rules**
- Endpoint fields (e.g. `details.endpoint`) are **path-only** (scheme/host stripped) and **query secrets redacted**.
- Lists are capped; when a list is capped, `...Count` and `...Capped` fields are provided where applicable.
- `results[].errors[]` is **human context only**. Automation should use `errorCode` + `details.*`.

### `E2E_AUTH_MISSING`
| Field | Type | Notes |
|---|---:|---|
| `skipReason` | string | Human-readable description of the missing credential(s); no secret values. |

### `E2E_CONFIG_INVALID`
| Field | Type | Notes |
|---|---:|---|
| `pluginName` | string | Plugin under test. |
| `componentType` | enum | `indexer` \| `downloadClient` \| `importList`. |
| `operation` | enum | `create` \| `update`. |
| `endpoint` | string | Path-only endpoint (secrets redacted). |
| `phase` | string | Configure sub-phase (e.g. `Configure:Create:Post`). |
| `httpStatus` | int | HTTP status when available (typically 400). |
| `validationErrors` | string[] | Capped list of server-side validation messages (sanitized). |
| `validationErrorCount` | int | Total validation errors before capping. |
| `validationErrorsCapped` | boolean | Whether `validationErrors` was capped. |
| `fieldNames` | string[] | Capped list of field names involved in validation failures. |
| `fieldNameCount` | int | Total field names before capping. |
| `fieldNamesCapped` | boolean | Whether `fieldNames` was capped. |
| `schemaContract` | string | Lidarr schema contract name for the settings model. |

### `E2E_API_TIMEOUT`
| Field | Type | Notes |
|---|---:|---|
| `timeoutType` | enum | `http` \| `commandPoll` \| `queuePoll` \| `queueCompletion`. |
| `timeoutSeconds` | int | Timeout threshold used. |
| `endpoint` | string | Path-only endpoint or logical endpoint (secrets redacted). |
| `operation` | string | Operation name (e.g. `AlbumSearch`). |
| `pluginName` | string | Plugin under test. |
| `phase` | string | Gate sub-phase (e.g. `AlbumSearch:PollCommand`). |
| `indexerId` | int? | When applicable. |
| `downloadClientId` | int? | When applicable. |
| `commandId` | int? | When polling a command. |
| `attempts` | int? | Retry attempts when applicable. |
| `elapsedMs` | int? | Measured elapsed time when available. |

### `E2E_LIDARR_UNREACHABLE`
| Field | Type | Notes |
|---|---:|---|
| `phase` | string | Always emitted at preflight (`LidarrApi:Preflight`). |
| `operation` | string | Always `LidarrApiPreflight`. |
| `endpoint` | string | Path-only endpoint (secrets redacted). |
| `timeoutSeconds` | int | Preflight timeout. |
| `unreachableKind` | string | Transport classification (e.g. `connectionRefused`). |
| `exceptionType` | string | Exception type name (sanitized). |
| `suggestion` | string | First fix to try (no secrets). |

### `E2E_DOCKER_UNAVAILABLE`
| Field | Type | Notes |
|---|---:|---|
| `phase` | string | Gate phase requiring Docker. |
| `operation` | string | Docker operation attempted (e.g. `docker restart`). |
| `containerName` | string | Container name provided to the runner. |
| `dockerPhase` | string | Internal phase (e.g. `Docker:DetectDaemon`). |
| `dockerFailureKind` | string | Classified failure kind (e.g. `daemon_unavailable`). |
| `dockerExitCode` | int | Exit code when available. |
| `dockerStderr` | string | Stderr excerpt (sanitized). |
| `suggestion` | string | Remediation hint. |

### `E2E_NO_RELEASES_ATTRIBUTED`
| Field | Type | Notes |
|---|---:|---|
| `searchQuery` | string | Query used for AlbumSearch. |
| `totalReleases` | int | Total releases returned by Lidarr. |
| `attributedReleases` | int | Releases attributed to the target plugin. |
| `expectedIndexerName` | string | Plugin indexer name expected. |
| `expectedIndexerId` | int | Configured indexer ID under test. |
| `foundIndexerNames` | string[] | Capped list of other indexer names observed. |
| `foundIndexerNameCount` | int | Total unique names before capping. |
| `foundIndexerNamesCapped` | boolean | Whether `foundIndexerNames` was capped. |
| `nullIndexerReleaseCount` | int | Count of releases with `indexer` empty/null or `indexerId=0`. |
| `nullIndexerSamples` | object[] | Up to 3 items: `{ title, indexer, indexerId }`. |

### `E2E_QUEUE_NOT_FOUND`
| Field | Type | Notes |
|---|---:|---|
| `queueTimeoutSec` | int | Queue polling timeout. |
| `queueCount` | int | Queue items seen at failure time. |
| `downloadId` | string | Download correlation ID (may be empty if missing). |
| `albumId` | int | Album ID under test when available. |
| `indexerName` | string | Plugin indexer name under test. |

### `E2E_ZERO_AUDIO_FILES`
| Field | Type | Notes |
|---|---:|---|
| `outputPath` | string | Output directory inspected. |
| `totalFilesFound` | int | Total candidate audio files found (post-filter). |
| `validatedFiles` | string[] | Files validated (empty when zero-audio). |

### `E2E_METADATA_MISSING`
| Field | Type | Notes |
|---|---:|---|
| `audioFilesValidated` | int | Count of files examined for tags. |
| `audioFilesWithMissingTags` | int | Count of files missing required tags. |
| `missingTags` | string[] | Required tag identifiers missing. |
| `presentTags` | string[] | Tag identifiers present (for context). |
| `sampleFile` | string | Deterministic sample file associated with the failure. |

### `E2E_IMPORT_FAILED`
| Field | Type | Notes |
|---|---:|---|
| `pluginName` | string | Plugin under test (ImportList gate). |
| `importListId` | int | Import list ID under test. |
| `operation` | string | Always `ImportListSync`. |
| `phase` | enum | `ImportList:TriggerCommand` \| `ImportList:PollCommand` \| `ImportList:PostSyncVerify`. |
| `endpoint` | string | Path-only endpoint (secrets redacted). |
| `commandId` | int? | Command ID when available. |
| `commandStatus` | string? | Command status when available (e.g. `failed`). |
| `preSyncImportListFound` | boolean | Whether the import list existed before sync was triggered. |
| `postSyncVerified` | boolean | Whether post-sync verification succeeded. |
| `lastSyncError` | string? | Sanitized error string when present. |
| `attempts` | int? | Retry attempts when applicable. |
| `elapsedMs` | int? | Measured elapsed time when available. |

### `E2E_COMPONENT_AMBIGUOUS`
| Field | Type | Notes |
|---|---:|---|
| `componentType` | enum | `indexer` \| `downloadClient` \| `importList`. |
| `resolution` | string | Resolution field used (e.g. `implementationName`). |
| `candidateIds` | int[] | Candidate IDs found (≥2). |

### `E2E_ABSTRACTIONS_SHA_MISMATCH`
| Field | Type | Notes |
|---|---:|---|
| `abstractionsShas` | object | Map of plugin name → short SHA/hex digest. |
| `expectedSha` | string | Expected digest (selected baseline). |
| `mismatchedPlugins` | string[] | Plugin names that differ from expected. |
| `fixInstructions` | string | Stable human instruction string (no secrets). |

### `E2E_SCHEMA_MISSING_IMPLEMENTATION`
| Field | Type | Notes |
|---|---:|---|
| `indexerFound` | boolean | Whether plugin indexer schema was found. |
| `downloadClientFound` | boolean | Whether plugin download client schema was found. |
| `importListFound` | boolean | Whether plugin import list schema was found. |
| `discoveryDiagnosis` | object | Diagnostic fields (e.g. `schemaEndpointReachable`, counts, and file presence booleans). |

### `E2E_HOST_PLUGIN_DISCOVERY_DISABLED`
| Field | Type | Notes |
|---|---:|---|
| `indexerFound` | boolean | Whether plugin indexer schema was found. |
| `downloadClientFound` | boolean | Whether plugin download client schema was found. |
| `importListFound` | boolean | Whether plugin import list schema was found. |
| `discoveryDiagnosis` | object | Includes affirmative evidence fields: `hostPluginDiscoveryEnabled=false`, plus `detectionBasis` and `detectionEvidence`. |

### `E2E_PROVIDER_UNAVAILABLE`
| Field | Type | Notes |
|---|---:|---|
| `llmKind` | string? | LLM endpoint kind (Brainarr LLM gate). |
| `modelsCount` | int? | Model count reported by endpoint. |
| `expectedModelIdHash` | string? | SHA256 prefix hash of expected model ID (no raw ID). |
| `expectedModelFound` | boolean? | Whether the expected model was found. |

### `E2E_LOAD_FAILURE`
| Field | Type | Notes |
|---|---:|---|
| `note` | string? | Optional human context for synthetic fixtures. |

For load failures, the primary machine-consumable classification is `hostBugSuspected` at the top-level of the manifest (`classification`, `severity`, `matchedLine`).

## Related Runner Toggles

These are not `errorCode` values, but common toggles referenced in logs/docs:   	

- `E2E_FORCE_CONFIG_UPDATE=1` — overwrite masked `********` fields during Configure.
- `E2E_VALIDATE_METADATA=1` — run opt-in metadata gate after successful Grab.
- `E2E_POST_RESTART_GRAB=1` — perform an additional post-restart Grab validation.
- `E2E_RUN_BRAINARR_LLM=1` — enable Brainarr LLM gate when `BRAINARR_LLM_BASE_URL` is set.
- `E2E_STRICT_BRAINARR=1` — treat Brainarr LLM failures as FAIL instead of SKIP.

## Host/Load Failures

If a run fails very early (e.g. schema cannot load plugins), consult:

- `hostBugSuspected` in `run-manifest.json` (classification + severity)
- `docs/HOST_FAILURE_CLASSIFICATION.md`
