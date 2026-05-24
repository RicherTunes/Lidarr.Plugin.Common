# Bridge Runtime Contracts

Behavioral specifications for the bridge runtime contracts in `Lidarr.Plugin.Abstractions.Contracts`. These define when each contract fires, its reliability semantics, and what hosts may assume.

## Default Implementations

Common ships default implementations for all four contracts via `AddBridgeDefaults()`:

| Contract | Default | Behavior |
|----------|---------|----------|
| `IAuthFailureHandler` | `DefaultAuthFailureHandler` | Tracks status transitions, logs events |
| `IIndexerStatusReporter` | `DefaultIndexerStatusReporter` | Tracks status transitions, clears errors on non-error transitions |
| `IDownloadStatusReporter` | `DefaultDownloadStatusReporter` | Tracks download progress/completion/failure, clears errors on non-error transitions |
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

### Auth: AuthFailureGate (request-side enforcement)

`IAuthFailureHandler` is notification-only — it tracks state but does NOT gate
outbound requests. Plugins that simply propagate 401/403 to Lidarr's search
loop will keep hammering the upstream at full rate (real-world driver: a Qobuz
user got IP-banned when Lidarr searched while the OAuth session was expired).

`AuthFailureGate` (in `Lidarr.Plugin.Common.Services.Bridge`) is the fail-fast
latch built on top of a registered `IAuthFailureHandler`.

| Method / Property | Behavior |
|---|---|
| `IsHealthy` | `true` when status is `Authenticated` or `Unknown` (initial). |
| `EnsureCanProceed()` | Throws `AuthGatedException` (carrying `RetryAfter`) when status is `Failed`/`Expired`. |
| `TryAcquireProbeSlot()` | When healthy, always `true`. When latched bad, returns `true` at most once per `probeInterval` (default 60s) so the plugin can attempt a single network call to detect that re-auth succeeded. |
| `ForceReset()` | Clear latch + probe budget; used by `IAuthFailureGateRegistry.Reset(key)` for settings-UI "Test Connection" flows. |
| `Metrics` | `AuthFailureGateMetrics` snapshot — counters for `LatchTransitions`, `RecoveryTransitions`, `ProbeAcquired`, `ProbeRejected`, `ProbeRefunded`, plus `LastLatchAt`/`LastRecoveryAt` timestamps. |

#### K-of-N failure threshold

`DefaultAuthFailureHandler` accepts a `failureThreshold` constructor parameter
(default `1` for back-compat). Higher values require K consecutive failures
within a single streak before flipping status to `Failed` — useful when the
upstream is known to flake on edge-side load shedding. An intervening
`HandleSuccessAsync` resets the streak.

#### Auto-wire via DelegatingHandler

`AuthFailureDelegatingHandler` drops the gate into any `HttpClient` pipeline:

```csharp
services.AddTransient<AuthFailureDelegatingHandler>();
services.AddHttpClient<MyApi>(c => c.BaseAddress = new Uri(...))
    .AddHttpMessageHandler<AuthFailureDelegatingHandler>(); // outermost
```

It short-circuits to `AuthGatedException` when latched, marks bad on 401/403,
recovers on first 2xx after bad, and **refunds the probe slot** on pre-network
failures (cancellation, DNS, TLS) so the budget isn't burned on calls that
never reached the upstream.

#### Multi-provider plugins: IAuthFailureGateRegistry

For plugins with N independent credentials (brainarr's 11 LLM providers),
register the registry instead of a single gate:

```csharp
services.AddSingleton<IAuthFailureGateRegistry>(_ =>
    new AuthFailureGateRegistry(TimeProvider.System, TimeSpan.FromSeconds(60), maxKeys: 256));

// Per-provider gate, case-insensitive lookup:
var gate = registry.Get("openai");
// Mutations for settings-UI flows:
registry.Reset("openai"); // re-arm without waiting for the probe interval
registry.Remove("openai"); // drop entirely; next Get allocates fresh
```

#### Pipeline ordering

`AuthFailureDelegatingHandler` MUST sit **outermost** in the pipeline (added
first to `AddHttpMessageHandler`), so it short-circuits *before* the rate
limiter / OAuth refresh handlers run. The OAuth refresh client (the one that
exchanges credentials for new tokens) should be **exempt** from the gate —
gating it would create a deadlock where the only path that can clear the gate
is itself gated.

#### Observability

Metrics counters update inside the gate's lock. Read-coherent snapshots via
`gate.Metrics`. Structured logs emit at LATCH (`LogWarning`) and RECOVERY
(`LogInformation`) transitions, with elapsed latch duration on recovery. The
registry exposes `Count` for per-process gate inventory.

### Auth: recommended adapter wiring

```csharp
services.AddSingleton(sp => new AuthFailureGate(
    sp.GetRequiredService<IAuthFailureHandler>(),
    TimeProvider.System,
    TimeSpan.FromSeconds(60),
    sp.GetService<ILogger<AuthFailureGate>>()));

// In the indexer/import adapter:
if (_authGate is not null && !_authGate.IsHealthy && !_authGate.TryAcquireProbeSlot())
{
    return Array.Empty<StreamingAlbum>(); // short-circuit; no network call
}

try { /* ... call upstream ... */ }
catch (Exception ex) when (LooksLikeAuthFailure(ex))
{
    await _authGate.Handler.HandleFailureAsync(new AuthFailure { Message = ex.Message });
    throw;
}
```

### Auth: known consumers

- `applemusicarr` — wired into `AppleMusicIndexerAdapter` (cycle 39, prior arc) AND `HttpClientFactory` (wave 6, 50-wave plan) — every catalog + playback HttpClient is gated.
- `qobuzarr` — wired into `QobuzIndexerAdapter` (cycle 40, closes the original IP-ban incident) AND the bridge `HttpClient` pipeline via `AuthFailureDelegatingHandler` (wave 3).
- `tidalarr` — wired into `TidalIndexer` (cycle 41) AND 3 of 4 bridge `HttpClient` pipelines (`TidalApiClient`, `TidalOrchestrator`, `TidalChunkDownloader`); `TidalOAuthService` intentionally exempt (wave 4).
- `brainarr` — wired via `IAuthFailureGateRegistry` per-provider (wave 5). Each LLM provider gets its own gate so a bad OpenAI key doesn't block Anthropic / Ollama / local providers.

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

## IDownloadStatusReporter

### Download: triggers

| Method | Trigger |
|--------|---------|
| `ReportProgressAsync(progress)` | During active download — reports track-level progress |
| `ReportCompletedAsync(albumId)` | Album download finished successfully |
| `ReportFailedAsync(albumId, error)` | Album download failed with an exception |

### Download: reliability

**Best-effort.** Missing reports only affect observability, not correctness.

### Download: recoverable errors

`DownloadStatus.Failed` is recoverable — subsequent downloads attempt normally. `LastError` is cleared on any non-error transition (`ReportProgressAsync` or `ReportCompletedAsync`).

### Download: host assumptions

The host reads `Status` to display download state in the UI. Progress reports should be reasonably frequent during active downloads to provide meaningful UI updates.

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
