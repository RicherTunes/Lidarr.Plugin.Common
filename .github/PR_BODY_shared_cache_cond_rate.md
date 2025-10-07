Title: Add shared streaming GET cache, conditional request state, and rate-limit telemetry handler

Summary
- Introduce shared, dependency-light primitives that all Arr plugins can consume:
  - IStreamingResponseCache + FileStreamingResponseCache (TTL; size/entry caps; LRU; atomic replace; read-through on success only; respects Cache-Control: private/no-store).
  - IConditionalRequestState + FileConditionalRequestState (ETag/Last-Modified validators; configurable vary set, e.g., Accept-Language for Apple catalog).
  - RateLimitTelemetryHandler + IRateLimitObserver (records Retry-After delta or http-date; exposes last delay/timestamp for status UIs).
- Keep existing ExecuteWithResilienceAsync unchanged; these are additive. The resilient GET composition remains opt-in and lives in plugin code paths.

Public API additions
- namespace Lidarr.Plugin.Common.Interfaces:
  - public interface IStreamingResponseCache
  - public interface IConditionalRequestState
  - public interface IRateLimitObserver
- namespace Lidarr.Plugin.Common.Services.Caching:
  - public sealed class FileStreamingResponseCache
  - public static class ArrCachingHeaders (RevalidatedHeader, LegacyRevalidatedHeader, RevalidatedValue)
- namespace Lidarr.Plugin.Common.Services.Http:
  - public sealed class FileConditionalRequestState
  - public sealed class RateLimitTelemetryHandler

Design notes
- Cache: GET-only; keys built from endpoint + canonical params. Callers may add header variance (e.g., Accept-Language) into params before keygen.
- Conditional: Adds If-None-Match/If-Modified-Since when validators exist. On 304 Not Modified, callers can synthesize 200 OK with cached body and add ArrCachingHeaders.RevalidatedHeader.
- RateLimit: lightweight DelegatingHandler that observes Retry-After and notifies IRateLimitObserver; no policy engine baked in.

Tests
- 304 path → synthetic 200 with XArrCache header, including Accept-Language variance path.
- private/no-store: body not cached; validators persisted; subsequent 304 surfaces (no synthesis).
- Retry-After: both delta and http-date forms recorded, with non-negative delay.

Rationale
- Consolidates common, boring-but-critical behaviors so Arr plugins don’t reimplement them. Improves consistency across Apple/Qobuz/Tidal/Brain and simplifies testing.

Migration plan
1) Merge these additions; publish a prerelease package.
2) AppleMusicarr switches to direct types (patch prepared on its repo) and deletes local duplicates.
3) Qobuz/Tidal/Brain follow the same pattern; use PluginHttpOptions.* keys for policy/cache metadata.

No breaking changes
- This is additive; existing consumers are unaffected until they opt-in.

