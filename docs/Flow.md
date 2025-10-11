# Builder → Options → Executor → Cache

This SDK standardizes how HTTP requests are built, executed with resilience, and cached.

Flow

- Builder
  - Use `StreamingApiRequestBuilder` to compose requests.
  - Call `WithStreamingDefaults(userAgent?)` to apply standard headers (see HTTPDefaults.md).
  - Set `Endpoint(path)` and add query with `Query(k, v)`; the builder canonicalizes the query:
    - Ordinal key sort; multivalue sort; lowercase percent-encoding; spaces as `%20`; preserves empty pairs.
  - Optional: `WithPolicy(ResiliencePolicy)` to select profile and `WithAuthScope(raw)` to stamp a hashed non‑PII scope token.

- Options stamping
  - Builder stamps the request with well-known `HttpRequestMessage.Options` keys:
    - `PluginHttpOptions.EndpointKey` — normalized endpoint path ("/detail").
    - `PluginHttpOptions.ProfileKey` — resilience profile name.
    - `PluginHttpOptions.ParametersKey` — canonical query string used for dedup/cache keys.
    - `PluginHttpOptions.AuthScopeKey` — hashed scope token when provided.

- Executor
  - For raw HttpClient usage, call `HttpClientExtensions.ExecuteWithResilienceAsync(request, policy)`.
  - Features: 429 Retry‑After (date preferred), retry budget clamping, per-host|profile gates + aggregate host cap, safe request cloning.
  - Redirects:
    - 307/308 preserve method/body and are auto‑followed.
  - 301/302 are auto‑followed only when the method is safe (GET/HEAD). Unsafe methods (e.g., POST) return the 30x so callers can decide.
  - This mirrors common client behavior while preserving plugin control for non‑idempotent requests.
  - Observability: spans (`http.send`, `host.gate.wait`) and counters (retry.count).


- Cache
  - Use `HttpClientResilienceExtensions.ExecuteWithResilienceAndCachingAsync()` to layer caching:
    - Keys derive from endpoint + canonical parameters (plus scope when policy.VaryByScope).
    - Persists DTOs including `ETag` and `Last-Modified`.
    - On 304, returns a synthetic 200 from cached body and refreshes TTL.
    - A short stale grace prevents races when TTL expires near 304; callers still receive the cached body.
    - Sliding TTL extensions are coalesced (`SlidingRefreshWindow`) and capped by absolute expiration.
  - Observability: `cache.hit`, `cache.miss`, `cache.revalidate` counters.

Conditional revalidation

- If `IConditionalRequestState` is provided, validators are stored per cache key and attached on the next request.
- Alternatively, if the policy enables `EnableConditionalRevalidation`, validators are read from cached entries.
- Revalidation adds headers:
  - `XArrCache: revalidated` (preferred)
  - `X-Arr-Cache: revalidated` (legacy)

Notes

- Do not cache `HttpResponseMessage` instances; the cache stores DTOs only.
- Prefer the builder → options → executor path for consistency and dedup/caching invariants.
- For batch/non-HTTP workflows, use `NetworkResilienceService` for checkpoints & circuit-breaker, and call the HTTP executor for outbound HTTP.
