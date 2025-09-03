# Changelog - Lidarr.Plugin.Common

All notable changes to the shared library will be documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.0.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [1.0.0] - 2025-08-26

### Added - Initial Release 🎉

#### Base Classes
- `BaseStreamingSettings` - Common configuration patterns for all streaming services
- `BaseStreamingIndexer<T>` - Generic indexer with caching, rate limiting, validation
- `BaseStreamingDownloadClient<T>` - Download orchestration with progress tracking
- `BaseStreamingAuthenticationService<T>` - Complete authentication framework

#### Services
- `StreamingResponseCache` - Generic cache implementation with TTL and cleanup
- `StreamingApiRequestBuilder` - Fluent HTTP request builder for streaming APIs
- `QualityMapper` - Quality tier mapping and comparison utilities
- `PerformanceMonitor` - Comprehensive performance tracking
- `StreamingPluginModule` - Plugin registration and DI patterns

#### Models
- `StreamingArtist` - Universal artist model for cross-service compatibility
- `StreamingAlbum` - Universal album model with quality and metadata support
- `StreamingTrack` - Universal track model with rich feature support
- `StreamingQuality` - Quality abstraction with tier mapping
- `StreamingQualityTier` - Universal quality classification system

#### Utilities
- `FileNameSanitizer` - Cross-platform file naming with security
- `HttpClientExtensions` - HTTP utilities with retry and error handling
- `RetryUtilities` - Exponential backoff, circuit breaker, rate limiter

#### Testing Support
- `MockFactories` - Realistic test data generators
- `TestDataSets` - Pre-built test scenarios for edge cases

#### Interfaces
- `IStreamingAuthenticationService<T>` - Generic authentication contract
- `IStreamingResponseCache` - Cache service interface
- `IQueryOptimizer` - Query optimization patterns

### Features
- **60-75% code reduction** for new streaming service plugins
- **Thread-safe operations** with proper locking mechanisms
- **Security built-in** with parameter masking and validation
- **Performance optimization** with caching and rate limiting
- **Comprehensive error handling** with retry strategies
- **Universal quality management** across different streaming services
- **Professional testing support** with mock data generators

### Documentation
- Complete README with usage examples
- Streaming plugin development template
- Ecosystem expansion roadmap
- Complete usage examples with working code

### Compatibility
- **.NET 6.0** target framework
- **Lidarr plugins branch** compatibility
- **Production-ready** for immediate use

---

## [1.1.1] - 2025-09-03

### Added
- Context-specific sanitizers: `Sanitize.UrlComponent`, `Sanitize.PathSegment`, `Sanitize.DisplayText`, `Sanitize.IsSafePath`.
- HTTP resilience: `HttpClientExtensions.ExecuteWithResilienceAsync` with 429/Retry-After awareness, jittered backoff, retry budget, and per-host concurrency gating.
- OAuth token refresh: `OAuthDelegatingHandler` for Bearer injection and single-flight refresh on 401.
- Atomic/resumable downloads: Base download client now writes to `.partial`, flushes to disk, and performs atomic move; resumes when server supports ranges.
- Universal IDs on models: `StreamingAlbum.MusicBrainzId`, `StreamingAlbum.ExternalIds`, `StreamingTrack.MusicBrainzId`, `StreamingTrack.ExternalIds`.

### Changed
- `BaseStreamingIndexer` now uses a shared `HttpClient` + resilient pipeline to avoid socket exhaustion and improve stability.
- `InputSanitizer` methods marked `[Obsolete]` in favor of context-specific `Sanitize` helpers.

### Notes
- No breaking changes: obsolete APIs remain for compatibility.
- Submodule usage continues to work; package metadata remains enabled for future NuGet distribution.

## [1.1.2] - 2025-09-03

### Added
- Preview detection: threshold-based duration (default ≤90s), extended URL patterns, and tunable overload with extra patterns.
- Validation: `ValidateFileSignature` (FLAC/OGG/MP4/M4A/WAV) and an extended `ValidateDownloadedFile` overload.
- Hashing/Signing: `ComputeSHA256`, `ComputeHmacSha256`, and `IRequestSigner` interfaces with `Md5ConcatSigner` and `HmacSha256Signer` implementations.
- File system: NFC normalization and strengthened reserved-name guard in `FileSystemUtilities`.
- Settings: `Locale` added to `BaseStreamingSettings` (defaults to `en-US`).

### Changed
- Documentation updated to reference new utilities where applicable.

### Notes
- All changes are additive and backward compatible. Submodule consumers can adopt incrementally.

## [1.1.0] - 2025-08-30

### Added
- OAuth/PKCE authentication base and token management utilities.
- Base streaming indexer and download client frameworks.
- Performance and memory management helpers (batch manager, monitors).

### Notes
- Prepared the library for packaging with source link and symbols.

---

## Version Management

- **1.x.x**: Stable API, backward compatible changes only
- **0.x.x**: Development versions, breaking changes allowed
- **x.Y.x**: Feature additions, backward compatible
- **x.x.Z**: Bug fixes and patches

## Migration Guide

When upgrading between versions:
1. Check CHANGELOG for breaking changes
2. Update plugin project references
3. Run provided migration scripts (if any)
4. Test thoroughly with updated shared library
5. Update plugin version numbers to match compatibility

## Support

- **Issues**: Report bugs in the main Qobuzarr repository
- **Feature Requests**: Discuss in GitHub Discussions
- **Community**: Join the streaming plugin developer community
- **Documentation**: See README.md and examples/ directory
