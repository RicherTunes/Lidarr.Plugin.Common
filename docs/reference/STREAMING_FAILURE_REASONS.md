# Streaming Failure Reasons (Draft v1)

This document defines a **neutral, cross-plugin failure taxonomy** for streaming plugins (e.g., Qobuzarr, Tidalarr) and streaming-like importers.

Goals:
- Provide stable, semantic failure reasons that plugins can emit without relying on exception-message parsing.
- Allow E2E gates and CI to map failures consistently to `E2E_*` error codes.
- Avoid copying plugin-specific diagnostic code systems (e.g., `IX*`) into other plugins.

Non-goals:
- Replace `E2E_*` codes (those remain the CI/gate layer).
- Standardize service-specific details (Qobuz tiers vs Tidal tiers).

## Contract

- Plugins SHOULD surface a `StreamingFailureReason` whenever a failure is user/actionable and not a generic exception.
- E2E gates SHOULD prefer an explicit reason/code emitted by the plugin, and only fall back to regex classification for legacy paths.
- Reasons are **intentionally coarse**; details belong in logs/diagnostics (redacted) or plugin-local codes.

## Table

| Reason | When to use | Typical causes | Gate(s) | E2E mapping | Notes |
|---|---|---|---|---|---|
| `StreamingConfigMissing` | A required setting is empty/missing and the operation cannot proceed. | Missing `authToken`, missing `ConfigPath`, missing LLM URL. | Configure, Search, ImportList | `E2E_AUTH_MISSING` or `E2E_CONFIG_INVALID` | Use for user-config inputs (not transient network). |
| `StreamingConfigInvalid` | A setting is present but invalid (wrong format/value). | Malformed redirect URL, invalid market code, invalid file path. | Configure, Search, ImportList | `E2E_CONFIG_INVALID` | Prefer fail-fast and include which field (not value). |
| `StreamingAuthMissing` | No credentials/tokens available. | No stored tokens, no session, empty auth fields. | Search, AlbumSearch, Grab, ImportList | `E2E_AUTH_MISSING` | “Not authenticated” style errors. |
| `StreamingAuthExpired` | Tokens exist but are expired and refresh is required. | Access token expiry, session TTL elapsed. | Search, AlbumSearch, Grab, ImportList | `E2E_AUTH_EXPIRED` (or `E2E_AUTH_MISSING` if refresh not possible) | If refresh succeeds, this reason should not bubble. |
| `StreamingAuthInvalid` | Credentials exist but are rejected. | Wrong password, revoked token, invalid_grant. | Search, AlbumSearch, Grab, ImportList | `E2E_AUTH_MISSING` | Keep reason distinct from missing for UX; E2E may map both to `E2E_AUTH_MISSING`. |
| `StreamingRateLimited` | The service explicitly rate limits the request. | HTTP 429, Retry-After. | Search, AlbumSearch, Grab, ImportList | `E2E_RATE_LIMITED` | Prefer retry/backoff internally before surfacing. |
| `StreamingServiceUnavailable` | Service is unavailable or failing upstream. | HTTP 5xx, gateway errors. | Search, AlbumSearch, Grab, ImportList | `E2E_SERVICE_UNAVAILABLE` | Distinct from client timeout. |
| `StreamingApiTimeout` | Request timed out (client-side). | Network stalls, slow proxies. | Search, AlbumSearch, Grab, ImportList | `E2E_API_TIMEOUT` | Include timeout value (not secrets) in diagnostics. |
| `StreamingCatalogEmpty` | Search/lookup returns 0 results for a valid query (not an auth/config error). | Service legitimately has no match. | Search, AlbumSearch | *(no error by default)* | E2E should not treat this as success if expecting attributed releases. |
| `StreamingTrackUnavailable` | Track/album is not available to the account/market. | Region lock, subscription limits. | AlbumSearch, Grab | `E2E_CONTENT_UNAVAILABLE` | Used when service returns explicit restriction. |
| `StreamingQualityUnavailable` | Requested quality is unavailable; a fallback may exist. | Tidal tier restriction, Qobuz format restriction. | Grab | *(none if fallback succeeds)* | If fallback succeeds, this should be a warning; if no fallback, map to `E2E_CONTENT_UNAVAILABLE`. |
| `StreamingDownloadPayloadInvalid` | Download returned non-audio payload or invalid container. | HTML/JSON error page, invalid magic bytes. | Grab, PostRestartGrab | `E2E_DOWNLOAD_INVALID_PAYLOAD` | Should include only redacted snippet + content-type in diagnostics. |
| `StreamingDownloadTimeout` | Download stalled/timed out during transfer. | CDN issues, Range/resume errors. | Grab, PostRestartGrab | `E2E_DOWNLOAD_TIMEOUT` | Distinct from API timeout (metadata endpoints). |
| `StreamingMetadataWriteFailed` | Audio downloaded, but tagging failed. | TagLib limitations, write permissions. | Grab, Metadata | `E2E_METADATA_MISSING` (only if required) | Prefer to log warning and continue unless gate explicitly requires metadata. |

## Mapping guidance

- `E2E_*` codes are *gate-level*. A single `StreamingFailureReason` may map to different `E2E_*` codes depending on gate context.
- Example default mapping:
  - `StreamingAuthMissing|Expired|Invalid` → `E2E_AUTH_MISSING` (E2E cares that auth prereq is unmet)
  - `StreamingRateLimited` → `E2E_RATE_LIMITED`
  - `StreamingApiTimeout` → `E2E_API_TIMEOUT`
  - `StreamingDownloadPayloadInvalid` → `E2E_DOWNLOAD_INVALID_PAYLOAD`
  - `StreamingMetadataWriteFailed` → `E2E_METADATA_MISSING` (only when `-ValidateMetadata`)

## Plugin-local codes (optional)

Plugins MAY emit additional local diagnostic codes (e.g., `IX200`) for internal triage, but they should map to a stable `StreamingFailureReason` so cross-plugin tooling stays consistent.

