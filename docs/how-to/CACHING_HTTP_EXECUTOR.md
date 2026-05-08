# How to: use `CachingHttpExecutor`

`CachingHttpExecutor` is a single integrated executor for cache-aware, conditional, resilient HTTP GETs.
It collapses what each Arr streaming plugin used to inline (~370 LOC inside `SendAsync`) into one composable
class. This guide migrates the canonical applemusicarr `SendAsync` pattern to the executor.

## What it does

For one HTTP GET, the executor composes (in order):

1. **Soft-revalidate window** — if the cached body is younger than `CachePolicy.SoftRevalidateWindow`,
   return it without contacting the origin (`CacheHitKind.SoftRevalidate`).
2. **Conditional headers** — attach `If-None-Match` / `If-Modified-Since` from the configured
   `IConditionalRequestState` *or*, when `CachePolicy.EnableConditionalRevalidation` is set, from the
   cached entry itself.
3. **Resilience** — pass the request through `GenericResilienceExecutor`: 429/Retry-After aware,
   exponential backoff with jitter, retry budget, and per-host concurrency gate.
4. **304 Not Modified fold** — synthesize a 200 OK from the cached body, refresh the cache TTL
   (`CacheHitKind.NotModifiedFold`).
5. **2xx caching** — buffer once, write the body to the cache, persist validators
   (`CacheHitKind.Miss`).
6. **Stale-if-error** — on 5xx, return a cached body within `CachePolicy.StaleIfErrorTtl`
   (`CacheHitKind.StaleIfError`).
7. **Terminal eviction** — on 404/410, evict the cache entry and validators when
   `CachePolicy.EvictOnTerminalStatus` is true (`CacheHitKind.EvictOnTerminal`).
8. **Passthrough** — anything else (401/403/etc.) is returned as-is (`CacheHitKind.Passthrough`).

## Construction

```csharp
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Services.Caching;
using Lidarr.Plugin.Common.Utilities;

var policyProvider = new MyCachePolicyProvider(); // or your CachePolicyRegistry
var cache = new MyResponseCache(policyProvider);  // any IStreamingResponseCache
var conditional = new FileConditionalRequestState(); // optional

var executor = new CachingHttpExecutor(
    invoker: httpClient,             // HttpClient is an HttpMessageInvoker
    cache: cache,
    resiliencePolicy: ResiliencePolicy.Default,
    policyProvider: policyProvider,  // optional — enables the SendAsync overload that omits CachePolicy
    conditionalState: conditional,   // optional — falls back to in-cache validators
    timeProvider: TimeProvider.System,
    logger: loggerFactory.CreateLogger<CachingHttpExecutor>());
```

## Sending a request

```csharp
var builder = new StreamingApiRequestBuilder("https://api.music.apple.com")
    .Endpoint("v1/catalog/us/albums/12345")
    .BearerToken(developerToken)
    .Header("Accept-Language", "en-US")
    .Get();

var key = new CacheKey("/v1/catalog/us/albums/12345",
    new Dictionary<string, string> { ["lang"] = "en-US" });

var policy = CachePolicy.LongLived
    .WithExecutor(
        softRevalidateWindow: TimeSpan.FromHours(2),
        staleIfErrorTtl: TimeSpan.FromDays(7),
        evictOnTerminalStatus: true);

var hooks = new CachingHttpHooks<AlbumDto>(
    ParseAsync: async (resp, ct) =>
        await JsonSerializer.DeserializeAsync<AlbumDto>(
            await resp.Content.ReadAsStreamAsync(ct), cancellationToken: ct),
    OnHit: (kind, k) => metrics.Counter("am.cache").WithTag("kind", kind.ToString()).Add(1));

var result = await executor.SendAsync(builder, key, policy, hooks, ct);
// result.Payload     - parsed AlbumDto (or null if no parse hook)
// result.Body        - raw bytes
// result.HitKind     - Hit / SoftRevalidate / NotModifiedFold / StaleIfError / EvictOnTerminal / Miss
// result.StatusCode  - synthesized 200 for fold/stale/soft, original for Miss/Passthrough
```

When you don't need a parsed payload, use the non-generic overload:

```csharp
var raw = await executor.SendAsync(builder, key, policy, ct);
// raw is CachedHttpResponse<object?> with Body populated and Payload = null
```

## Migrating from the applemusicarr pattern

This deletes the ~370 LOC of `SendAsync` in `AppleMusicApiClient.cs` lines 300-672. Concretely:

| Old block | Replacement |
|---|---|
| Manual `_conditional.TryGetValidatorsAsync` + `request.Headers.TryAddWithoutValidation("If-None-Match", ...)` | Pass `IConditionalRequestState` to the executor; hooks attach automatically. |
| `APPLEMUSICARR_SOFT_REVALIDATE_DAYS` env-driven branch synthesizing 200 OK + `XArrCache: soft` | Set `CachePolicy.SoftRevalidateWindow`. The executor synthesizes the 200 with the same header. |
| `_resilience.Get(profile)` + `httpClient.ExecuteWithResilienceAsync(...)` | Construct the executor with a `ResiliencePolicy`; the executor calls `GenericResilienceExecutor` internally. |
| 304 branch building a synthetic 200 from `_cache.Get<CachedHttpResponse>(...)` | Automatic — `CacheHitKind.NotModifiedFold`. |
| 2xx branch buffering bytes + `_cache.Set(...)` + `_conditional.SetValidatorsAsync(...)` | Automatic — `CacheHitKind.Miss`. |
| `APPLEMUSICARR_STALE_IF_ERROR_DAYS` env-driven 5xx fallback to cached body with `Warning: 110` header | Set `CachePolicy.StaleIfErrorTtl`. The executor synthesizes the 200 + Warning header. |
| 404/410 branch calling `_conditional.SetValidatorsAsync(cacheKey, null, null)` + `_cache.ClearEndpoint(endpoint)` | Automatic when `CachePolicy.EvictOnTerminalStatus` is true (default). |
| `Interlocked.Increment(ref _catalog304Hits)` etc. | Use the `OnHit(kind, key)` hook to drive plugin-specific metrics counters. |

## CachePolicy preset for streaming catalog endpoints

The applemusicarr defaults map cleanly:

```csharp
public static readonly CachePolicy AppleMusicCatalog = CachePolicy.LongLived
    .With(enableConditionalRevalidation: true)
    .WithExecutor(
        softRevalidateWindow: TimeSpan.FromDays(double.TryParse(
            Environment.GetEnvironmentVariable("APPLEMUSICARR_SOFT_REVALIDATE_DAYS"), out var d) ? d : 0),
        staleIfErrorTtl: TimeSpan.FromDays(double.TryParse(
            Environment.GetEnvironmentVariable("APPLEMUSICARR_STALE_IF_ERROR_DAYS"), out var s) ? s : 7),
        evictOnTerminalStatus: true);
```

The two env-var overrides remain exactly compatible with the legacy applemusicarr behaviour.

## Composition hooks

`CachingHttpHooks<TPayload>` is a record with four nullable callbacks:

- **`ParseAsync(HttpResponseMessage, CancellationToken) -> Task<TPayload>`** — runs on every code path that
  produces a body (Miss, SoftRevalidate, NotModifiedFold, StaleIfError, Passthrough). Receives a buffered
  copy of the response so the body can be re-read.
- **`MutateRequest(HttpRequestMessage)`** — last-mile mutation after auth and conditional headers are
  attached. Use for correlation IDs, custom telemetry headers, etc.
- **`OnEvict(HttpStatusCode, CacheKey)`** — fires after a terminal-status eviction.
- **`OnHit(CacheHitKind, CacheKey)`** — fires for every outcome (Miss, SoftRevalidate, etc.). Use this for
  metrics counters; the executor itself emits no metrics by default.

Hook callbacks are best-effort; exceptions are logged and swallowed by the executor.

## Testing tips

- Pass `Microsoft.Extensions.Time.Testing.FakeTimeProvider` to the constructor to drive cache windows
  deterministically.
- The executor uses `TimeProvider.System` for resilience retry delays (a deliberate split — windowing is
  caller-controlled, retry timing is wall-clock); test backoffs by configuring a small
  `ResiliencePolicy` (e.g., 1ms initial backoff, 0 jitter).
- Use `DelegatingHandler` subclasses (or the testkit's `ErrorSimulationHandler`/`RateLimitTestHandler`) as
  the inner transport on a `HttpClient` and pass that `HttpClient` as the `invoker`.
- The cache policy's `Duration` is automatically widened to cover `SoftRevalidateWindow` and
  `StaleIfErrorTtl`; the cached `StoredAt` field is preserved across folds so window bounds remain
  correct independent of the storage TTL.
