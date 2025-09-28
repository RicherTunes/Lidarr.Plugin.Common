# Changelog - Lidarr.Plugin.Common

All notable changes to the shared library are documented here. The format follows [Keep a Changelog](https://keepachangelog.com/en/1.0.0/) and the project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased] - 2025-09-28

### Added
- `Directory.Build.props` pins `<AssemblyVersion>`/`<FileVersion>` to `10.0.0.35686` (Lidarr 2.14.2.4786 host) so every downstream plugin consumes matching binaries.
- `scripts/verify-assemblies.ps1` copies host assemblies, validates `FileVersion` <-> `AssemblyVersion`, and fails fast when the Lidarr output folder is missing.
- `.github/workflows/pr-validation.yml` enforces the verification script, `dotnet build -c Release -warnaserror:NU1903`, and `dotnet test -c Release --no-build` on every pull request.
- `docs/UNIFIED_PLUGIN_PIPELINE.md` describes the shared platform repo, version-gated CI, ILRepack guardrails, release orchestration, packaging, and monitoring expectations for plugins.
- `TokenDelegatingHandler` and `ContentDecodingSnifferHandler` provide reusable bearer-token injection and mislabelled gzip recovery across all plugins.

### Changed
- README gains a Maintainer Checklist and Plugin Version Governance section referencing the sync script and unified pipeline playbook.
- NuGet dependencies (System.Text.Json, Microsoft.Extensions.* , Newtonsoft.Json) updated to the latest 6.0.x/13.0.x patches to clear NU1903 advisories during Release builds.
- `HttpClientExtensions.GetJsonAsync<T>` now verifies Content-Type and includes payload previews when responses are not JSON.
- Removed unused Polly packages and disabled `AllowUnsafeBlocks` in the library project to avoid accidental unsafe usage.
- Multi-targeted the library for net6.0 and net8.0 with conditional Microsoft.Extensions dependency versions.
- BaseStreamingIndexer now accepts an optional HttpClient factory for DI scenarios.

### Deprecated
- Marked `IAdaptiveRateLimiter` / `AdaptiveRateLimiter` as obsolete; migrate to `IUniversalAdaptiveRateLimiter` and `UniversalAdaptiveRateLimiter`.

### Reminder
- Maintainers must keep host assemblies in sync with Lidarr 2.14.2.4786 before shipping plugin updates; see `docs/UNIFIED_PLUGIN_PIPELINE.md` for the complete process.

## [1.1.3] - 2025-09-03

### Added
- `BaseStreamingIndexer`: streaming search helpers (`SearchAlbumsStreamAsync`, `SearchTracksStreamAsync`, `FetchPagedAsync<T>`) plus deduplication on title/artist/year.
- `BaseStreamingDownloadClient`: overridable retry hook with `Retry-After` handling and jittered backoff; configurable retry counts per service.

### Notes
- All additions are non-breaking; existing list-based APIs remain fully supported.

## [1.1.2] - 2025-09-03

### Added
- Preview detection improvements: duration threshold (default ~90s), extended URL markers, additional heuristics.
- Validation enhancements: `ValidateFileSignature` for FLAC/OGG/MP4/M4A/WAV and richer overloads of `ValidateDownloadedFile`.
- Hashing/signing utilities: `ComputeSHA256`, `ComputeHmacSha256`, and `IRequestSigner` implementations (`Md5ConcatSigner`, `HmacSha256Signer`).
- File system hardening: NFC normalization, expanded reserved-name guard.
- Settings: `Locale` property on `BaseStreamingSettings` (default `en-US`).

### Changed
- Documentation refreshed to reference the new utilities and configuration options.

### Notes
- Changes are additive and backward compatible; submodule consumers can adopt incrementally.

## [1.1.1] - 2025-09-03

### Added
- Context-specific sanitizers: `Sanitize.UrlComponent`, `Sanitize.PathSegment`, `Sanitize.DisplayText`, `Sanitize.IsSafePath`.
- HTTP resilience: `HttpClientExtensions.ExecuteWithResilienceAsync` with 429/Retry-After awareness, jittered backoff, retry budgets, and per-host concurrency gating.
- OAuth token refresh: `OAuthDelegatingHandler` for bearer injection and single-flight refresh on 401.
- Atomic/resumable downloads: `.partial` staging, atomic moves, resume on 206.
- Model metadata: `StreamingAlbum.ExternalIds`, `StreamingTrack.ExternalIds`, `MusicBrainzId` support.

### Changed
- `BaseStreamingIndexer` now shares a resilient `HttpClient` pipeline to reduce socket exhaustion.
- Legacy `InputSanitizer` methods marked `[Obsolete]` in favor of the context-specific helpers.

### Notes
- No breaking changes; obsolete APIs remain available for compatibility.

## [1.1.0] - 2025-08-30

### Added
- OAuth/PKCE authentication base classes and token lifecycle helpers.
- Core streaming indexer/download client frameworks.
- Performance and memory management helpers (batch manager, monitors).

### Notes
- Prepared the library for packaging with Source Link and symbols.

## [1.0.0] - 2025-08-26

### Added
- Base classes: `BaseStreamingSettings`, `BaseStreamingIndexer<T>`, `BaseStreamingDownloadClient<T>`, `BaseStreamingAuthenticationService<T>`.
- Services: `StreamingResponseCache`, `StreamingApiRequestBuilder`, `QualityMapper`, `PerformanceMonitor`, `StreamingPluginModule`.
- Models: `StreamingArtist`, `StreamingAlbum`, `StreamingTrack`, `StreamingQuality`, `StreamingQualityTier`.
- Utilities: `FileNameSanitizer`, `HttpClientExtensions`, `RetryUtilities`.
- Testing support: `MockFactories`, `TestDataSets`.
- Interfaces: `IStreamingAuthenticationService<T>`, `IStreamingResponseCache`, `IQueryOptimizer`.

### Highlights
- 60–75% code reduction for new streaming plugins, thread-safe operations, built-in security, performance optimizations, comprehensive error handling, and rich documentation/examples.

---

## Version Management

- **1.x.x**: Backward-compatible API evolution.
- **0.x.x**: Development versions where breaking changes are allowed.
- **x.Y.x**: Feature additions (minor).
- **x.x.Z**: Bug fixes and patches (patch).

## Migration Guide

1. Check this changelog for breaking changes.
2. Update plugin project references.
3. Run provided migration scripts (if any).
4. Test thoroughly with the updated shared library.
5. Update plugin version numbers to match compatibility.

## Support

- **Issues**: Report bugs in the main Qobuzarr repository.
- **Feature Requests**: Discuss in GitHub Discussions.
- **Community**: Join the streaming plugin developer community.
- **Documentation**: See `README.md` and the `docs/` folder.
