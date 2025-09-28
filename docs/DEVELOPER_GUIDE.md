# Developer Guide

This guide summarizes best practices and key building blocks when implementing a streaming plugin with Lidarr.Plugin.Common v1.1.3.

## HTTP Resilience
- Use `HttpClientExtensions.ExecuteWithResilienceAsync` for requests.
- Honors `Retry-After` (delta/date) and adds per-host concurrency gates.
- Keep retries budgeted; avoid stacking custom throttles on top of adaptive limiters.
- Register `ContentDecodingSnifferHandler` after your auth handler to auto-inflate mislabelled gzip payloads.
- For non-OAuth services, add `TokenDelegatingHandler` to automatically attach bearer tokens from your `IStreamingTokenProvider`.
- Prefer `IUniversalAdaptiveRateLimiter` for throttling; the legacy `AdaptiveRateLimiter` is retained only for compatibility.

## Authentication
- OAuth flows: use `OAuthStreamingAuthenticationService` and `OAuthDelegatingHandler` for 401 single-flight refresh.
- Token-in-query services: continue using pre-request handlers, but centralize refresh logic behind a semaphore.

## Sanitization
- Use `Sanitize.UrlComponent` for URL parts, `Sanitize.PathSegment` for filenames, `Sanitize.DisplayText` for rendering.
- Do not HTML-encode search terms.

## Signing
- Implement `IRequestSigner` when a service requires request signing.
- Provided: `Md5ConcatSigner` (legacy styles) and `HmacSha256Signer`.

## Downloads
- Stream to `*.partial`, flush, then atomic move to final.
- Resume from partial when the server returns 206.
- Use `ValidateDownloadedFile(..., validateSignature:true)` to detect truncation/corruption.
- Override `GetMaxDownloadRetries`, `ShouldRetryDownload`, and `GetDownloadRetryDelay` to tailor resilience.

## Models & Matching
- Populate `ExternalIds[service]` and `MusicBrainzId` to stabilize matching.
- Normalize filenames using `FileSystemUtilities` (NFC + reserved-name guard).

## Indexer Streaming & Pagination
- Optionally override `Search*StreamAsync` for large result sets.
- Use `FetchPagedAsync<T>` to iterate offset-based APIs.

## Settings
- Use `CountryCode` + `Locale` for region-aware APIs and caches.

## Preview Detection
- Use `PreviewDetectionUtility.IsLikelyPreview` to filter 30–90s previews based on duration/URL/flags.

