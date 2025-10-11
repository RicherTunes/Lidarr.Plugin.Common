# Cache GETs and enable 304 revalidation

This library can cache successful GET responses and transparently handle 304 Not Modified revalidation while keeping bodies available to callers.

Key pieces
- Builder stamps request intent in HttpRequestMessage.Options (endpoint, canonical parameters, optional auth scope).
- HttpClientResilienceExtensions.ExecuteWithResilienceAndCachingAsync applies resilience + caching.
- StreamingResponseCache stores a compact DTO (status, content-type, body, ETag, Last-Modified, StoredAt).

Conditional revalidation (304)
- If validators (ETag/Last-Modified) are available, the executor adds If-None-Match/If-Modified-Since.
- On 304, it returns a synthetic 200 with the cached body and refreshes TTL.
- A short stale grace window is used to avoid races when TTL expires just as a 304 arrives.
- A Revalidated marker header is added for observability:
  - XArrCache: revalidated (preferred)
  - X-Arr-Cache: revalidated (legacy)

How to enable
- Provide an IConditionalRequestState to persist validators per cache key; or
- Use a CachePolicy with EnableConditionalRevalidation when validators can be read from cached entries.

Cache key design
- Derived from endpoint + canonical parameters (and optional scope when policy.VaryByScope is true).
- Builder canonicalization sorts keys, collapses multi-values, and percent-encodes consistently.

Tips
- Do not cache HttpResponseMessage objects; the cache only stores DTOs.
- Tune SlidingExpiration and AbsoluteExpiration; use SlidingRefreshWindow to coalesce TTL bumps.
- For per-user data, set VaryByScope and include a non-PII scope.

Related docs
- docs/Flow.md (Builder → Options → Executor → Cache)
- docs/HTTPDefaults.md (standard headers)
- docs/Telemetry.md (cache.revalidate metric)

