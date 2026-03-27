# Bridge Runtime Contracts

Behavioral specifications for the bridge runtime contracts in `Lidarr.Plugin.Abstractions.Contracts`. These define when each contract fires, its reliability semantics, and what hosts may assume.

## Default Implementations

Common ships default implementations for all three contracts via `AddBridgeDefaults()`:

| Contract | Default | Behavior |
|----------|---------|----------|
| `IAuthFailureHandler` | `DefaultAuthFailureHandler` | Tracks status transitions, logs events |
| `IIndexerStatusReporter` | `DefaultIndexerStatusReporter` | Tracks status transitions, clears errors on non-error transitions |
| `IRateLimitReporter` | `DefaultRateLimitReporter` | Tracks rate limit state, logs backoff events |

`AddBridgeDefaults()` uses `TryAddSingleton` so plugins that register custom implementations first take precedence.

## IAuthFailureHandler

### Auth: triggers

| Method | Trigger |
|--------|---------|
| `HandleFailureAsync` | Authentication fails: token expired, refresh rejected, invalid credentials, network error during auth |
| `HandleSuccessAsync` | Authentication succeeds: initial login, token refresh, session validation passes |
| `RequestReauthenticationAsync` | Plugin determines user intervention is needed (e.g., revoked consent, changed password) |

### Auth: reliability

**Best-effort.** Callers SHOULD call these methods but failure to do so does not break functionality. The default implementation logs and tracks state only.

### Auth: recoverable errors

All authentication failures are considered potentially recoverable via re-authentication. `AuthFailure.CanReauthenticate` indicates whether automatic recovery is possible.

### Auth: host assumptions

The host may query `Status` to display authentication state in the UI. The host does NOT call these methods — they are for plugin-internal use, with state exposed to the host via the property.

## IIndexerStatusReporter

### Status: triggers

| Method | Trigger |
|--------|---------|
| `ReportStatusAsync(Searching, ...)` | Before initiating a search operation |
| `ReportStatusAsync(Authenticating)` | Before authentication checks |
| `ReportStatusAsync(Idle)` | After completing an operation successfully |
| `ReportStatusAsync(RateLimited)` | When rate-limited by the provider |
| `ReportErrorAsync(exception)` | When an operation fails with an exception |

### Status: reliability

**Best-effort.** Missing reports only affect observability, not correctness.

### Status: recoverable errors

`IndexerStatus.Error` is recoverable — subsequent operations attempt normally. Status auto-clears on next `ReportStatusAsync` with a non-error status. `LastError` is cleared on non-error transitions.

### Status: host assumptions

The host reads `CurrentStatus` for UI display. Transitions should be fast — don't hold `Searching` for minutes without progress updates.

## IRateLimitReporter

### RateLimit: triggers

| Method | Trigger |
|--------|---------|
| `ReportRateLimitAsync(retryAfter)` | HTTP 429 response received, with the `Retry-After` duration |
| `ReportRateLimitClearedAsync` | Successful response follows a rate-limited period |
| `ReportBackoffAsync(delay, reason)` | Plugin applies exponential backoff (not necessarily HTTP 429) |

### RateLimit: reliability

**Best-effort.** Rate limiting is handled by retry logic regardless of whether reporting succeeds.

### RateLimit: recoverable errors

Rate limits are always transient and recoverable. `ResetAt` provides the expected recovery time.

### RateLimit: host assumptions

The host may display `Status.IsRateLimited` and `Status.ResetAt` in a warning banner. The host does NOT enforce rate limits — that is the plugin's responsibility.
