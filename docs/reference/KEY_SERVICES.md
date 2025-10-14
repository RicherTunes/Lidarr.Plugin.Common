# Key Services and Utilities

This page is a practical reference for the most-used classes in Lidarr.Plugin.Common. It shows what each one is for, when to use it, and a minimal example. Pair this with Flow.md (builder → options → executor → cache) for the big picture.

## HTTP Request Flow

- `StreamingApiRequestBuilder` — compose requests and stamp standardized `HttpRequestMessage.Options` used by caching/dedup.
- `HttpClientExtensions.ExecuteWithResilienceAsync` — unified retries, deadlines, and per-host concurrency caps.
- `HttpClientResilienceExtensions.ExecuteWithResilienceAndCachingAsync` — add GET caching + 304 revalidation.
- `RequestDeduplicator` — single-flight identical GETs to avoid cache stampedes.

Example: GET with defaults, resiliency, dedup, and cache

```csharp
var builder = new StreamingApiRequestBuilder("https://api.example.com")
    .WithStreamingDefaults(userAgent: "MyPlugin/1.0")
    .WithPolicy(ResiliencePolicy.Search)
    .WithAuthScope("user:1234")
    .Endpoint("v1/search")
    .Query("q", term)
    .Get();

using var request = builder.Build();
using var response = await httpClient.ExecuteWithResilienceAndCachingAsync(
    request,
    resilience: myResilienceProvider,
    cache: myCache,
    deduplicator: myDeduplicator,
    conditionalState: myValidators,
    cancellationToken: token);

response.EnsureSuccessStatusCode();
```

Notes

- Builder stamps standardized options (`EndpointKey`, `ParametersKey`, `ProfileKey`, `AuthScopeKey`) used for cache keys and dedup keys.
- 301/302 auto-follow only for safe methods; 307/308 preserve method/body. Non-idempotent calls are surfaced to the caller for control.

## Resilience

- `ResiliencePolicy` — immutable profile (retries, jittered backoff, retry budget, per-host concurrency caps, per-request timeout). Built-ins: `Default`, `Search`, `Lookup`, `Streaming`, `Authentication`, `Metadata`.

Customize

```csharp
// Tighter search profile
var quickSearch = ResiliencePolicy.Search.With(
    maxRetries: 3,
    retryBudget: TimeSpan.FromSeconds(12),
    perRequestTimeout: TimeSpan.FromSeconds(8));
```

Gotchas

- Pass a `perRequestTimeout` only when you need a hard per-call deadline. Cancellation tokens are always respected.
- The executor applies per-host gates and an optional aggregate host cap to avoid starving the host when multiple profiles target the same origin.

## Caching

- `StreamingResponseCache` — in-memory DTO cache (never stores `HttpResponseMessage`). Supports endpoint policies, sliding expiration with a refresh window, absolute caps, and a short stale grace to enable race-free 304 flows.

Key behaviors

- Cache keys: `service|/endpoint|canonical-params[|scope:abcdef]`. Canonical parameters come from the builder; sensitive keys are filtered by default.
- 304 path returns a synthetic 200 from cached bytes and refreshes TTL. A `XArrCache: revalidated` header is added for observability.

Conditional validators

```csharp
// Implement IConditionalRequestState to persist ETag/Last-Modified per cache key
public sealed class FileConditionalState : IConditionalRequestState { /* ... */ }
```

## Deduplication

- `RequestDeduplicator` — coalesces identical GETs (single-flight) and shares a buffered record among awaiters.

```csharp
var key = HttpClientExtensions.BuildRequestDedupKey(request);
var response = await deduplicator.GetOrCreateAsync(key, () => http.SendAsync(request, token), token);
```

## Authentication

- `OAuthDelegatingHandler` — injects bearer tokens; single-flight refresh on 401 using an `IStreamingTokenProvider`.
- `TokenDelegatingHandler` — simple bearer injection when refresh is managed elsewhere.
- `StreamingTokenManager<TSession, TCredentials>` — refresh/persist session models, timer-assisted expiry checks, and events.

Example: token manager + OAuth handler

```csharp
// Build a token provider backed by your settings + token manager
var tokenProvider = new MyTokenProvider(settings, tokenManager);
var http = HttpClientFactory.Create(new OAuthDelegatingHandler(tokenProvider, logger));
```

See also: how-to/AUTHENTICATE_OAUTH.md and how-to/TOKEN_MANAGER.md.

## Downloads

- `SimpleDownloadOrchestrator` — resumable partial downloads (Range + If-Range with ETag/Last-Modified), atomic file moves, progress, and simple resume checkpoints.

```csharp
var orchestrator = new SimpleDownloadOrchestrator(
    "MyService", http,
    getAlbumAsync: id => svc.GetAlbumAsync(id),
    getTrackAsync: id => svc.GetTrackAsync(id),
    getAlbumTrackIdsAsync: id => svc.GetAlbumTrackIdsAsync(id),
    getStreamAsync: (id, q) => svc.GetStreamAsync(id, q));
```

## Rate Limiting & Performance

- `UniversalAdaptiveRateLimiter` — per-service, per-endpoint adaptive RPM with success-based increases and error/429 backoff. Use with indexers or custom clients.
- `PerformanceMonitor`/`MemoryHealthMonitor` — coarse-grained timing/health helpers for long-running operations.

## Diagnostics

- `DiagnosticTapHandler` — trace request/response (with masking) for deep debugging. Feature-flag via `LPC_HTTP_TAP=1`.
- `RateLimitTelemetryHandler` — emits counters and basic headers; useful during service tuning.
- Observability: see Telemetry.md for Activities and Counters emitted by the HTTP pipeline and cache.

## Safety & Utilities

- `SafeOperationExecutor` — defensive wrappers: timeouts, try-execute patterns, and IO retry for transient sharing violations.
- `FileNameSanitizer`/`Sanitize` — filesystem-safe names and URL-safe components.
- `RequestSigning`/`IRequestSigner` — MD5/HMAC helpers for signature schemes.
