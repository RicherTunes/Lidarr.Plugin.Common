# Developer Guide

Best practices for library and plugin contributors. For quick tasks, jump to the [how-to guides](../how-to/).

## Assembly load isolation
Summary only. The canonical reference lives in [Plugin isolation](../PLUGIN_ISOLATION.md).

- Reference `Lidarr.Plugin.Abstractions` as compile-time only.
- Ship `Lidarr.Plugin.Common` next to your plugin DLL.
- Keep cross-boundary types limited to Abstractions.

## HTTP resilience

- `HttpClientExtensions.ExecuteWithResilienceAsync` handles retries, `Retry-After`, and concurrency limits.
- Pass `perRequestTimeout` when a hard deadline is required; the helper raises `TimeoutException` while honouring caller cancellations.
- Keep `ContentDecodingSnifferHandler` near the end of your pipeline to rescue mislabelled gzip payloads without buffering large bodies.
- Compose handlers: authentication first (OAuth), then resilience, then content sniffing.
- Centralize retry budgets so you do not fight host-level throttling.

## Authentication

- Use `OAuthStreamingAuthenticationService` + `OAuthDelegatingHandler` for bearer flows.
- Implement `IStreamingTokenProvider` backed by the plugin settings.
- For API keys or query token schemes, wrap refresh logic behind a semaphore to avoid thundering herds.

## Sanitisation

- `Sanitize.UrlComponent` for query parameters.
- `Sanitize.PathSegment` with `FileNameSanitizer` for filesystem-safe names.
- Never HTML encode search terms.

## Signing & hashing

- Implement `IRequestSigner` when the service requires MD5/HMAC tokens.
- Helpers: `Md5ConcatSigner`, `HmacSha256Signer`, plus `HashUtilities` for generic hashes.

## Downloads

- Use `SimpleDownloadOrchestrator` for resumable downloads.
- Keep track metadata in `DownloadProgress` events.
- Validate files with `ValidateDownloadedFile` and optional container signature checks.

## Models & matching

- Populate `StreamingAlbum.ExternalIds` and `StreamingTrack.ExternalIds` for cross-service matching.
- Set `MusicBrainzId` when known.
- Use `QualityMapper` to translate service-specific quality tiers.

## Indexer streaming & pagination

- Override `Search*StreamAsync` when the API streams results.
- Use `FetchPagedAsync<T>` for offset/next links.

## Settings

- Define settings via `ISettingsProvider` as described in the [Settings reference](../reference/SETTINGS.md).
- Provide defaults for optional keys.

## Preview detection

- `PreviewDetectionUtility.IsLikelyPreview` uses duration + URL heuristics to filter 30–90 second samples.

## Further reading

- [Architecture](../concepts/ARCHITECTURE.md)
- [Create a plugin project](../how-to/CREATE_PLUGIN.md)
- [Public API baselines](../reference/PUBLIC_API_BASELINES.md)

