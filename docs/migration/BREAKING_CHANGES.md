# Breaking Changes

Track ABI-breaking updates here so plugin authors can plan migrations. Each entry links to the relevant release notes and documentation updates.

| Version | Component | Breaking? | Description | Migration steps | Docs |
|---------|-----------|-----------|-------------|-----------------|------|
| 1.0.0 | E2E Sanitization | Yes | Centralized `e2e-sanitize.psm1` module replaces duplicate pattern definitions | Import new module; deprecated patterns in `e2e-diagnostics.psm1` will be removed in next major version | [e2e-sanitize.psm1](../../scripts/lib/e2e-sanitize.psm1) |
| 1.0.0 | E2E Manifest | No | Added `fullSha` field to `sources.*` block for reproducibility | Optional additive field; existing consumers unaffected (schema allows additionalProperties) | [e2e-json-output.psm1](../../scripts/lib/e2e-json-output.psm1) |
| 1.1.7 | Redirect Semantics | Yes | 301/302 auto-follow only for safe methods (GET/HEAD); 307/308 preserve method and body. Previous behaviour followed all redirects uniformly. | If your plugin issues POST/PUT to endpoints that return 301/302, you must handle the redirect explicitly or switch to a safe method. | [CHANGELOG v1.1.7](../../CHANGELOG.md) |
| 1.8.0 | ALC Isolation | Yes | Each plugin loads Common in a private `AssemblyLoadContext`; host shares only `Lidarr.Plugin.Abstractions` and two `Microsoft.Extensions` abstractions. | Plugins must not assume they can cast Common types to host-side types across the ALC boundary. Use the abstraction interfaces instead. | [Plugin Isolation](../PLUGIN_ISOLATION.md) |
| 1.8.0 | Abstractions Merge | Yes | `Lidarr.Plugin.Abstractions` merged into the plugin DLL (cross-ALC fix). | No action required if consuming via NuGet; if referencing Abstractions directly, switch to the Common package. | [CHANGELOG v1.8.0](../../CHANGELOG.md) |
| 1.9.0 | Sync-over-async | Yes | Removed `Task.Delay(...).Wait()` inside lock pattern; replaced with `SemaphoreSlim + await Task.Delay`. | If you called `ApplyRateLimitAsync` synchronously via `.Wait()` or `.Result`, switch to `await`. | [CHANGELOG v1.9.0](../../CHANGELOG.md) |
| 1.12.0 | Builder Seal | Yes | `StreamingApiRequestBuilder` seals after `Build()` to prevent query bleed. Calling `Build()` twice or mutating after build now throws. | Create a new builder instance for each request instead of reusing a built one. | [CHANGELOG v1.12.0](../../CHANGELOG.md) |
| 1.13.0 | Scrub.Url | Yes | `Scrub.Url` delegates recognition to `LogRedactor`. Custom URL-scrubbing logic that relied on the old standalone behaviour may produce different output. | Verify that your logged URLs still redact correctly; update any custom scrubbers to align with `LogRedactor`. | [CHANGELOG v1.13.0](../../CHANGELOG.md) |
| 1.14.0 | AdaptiveRateLimiter | Yes | Removed the deprecated `AdaptiveRateLimiter` and `IAdaptiveRateLimiter` types. (`UniversalAdaptiveRateLimiter` / `IUniversalAdaptiveRateLimiter` are **not** removed and remain the current API.) | Switch from `AdaptiveRateLimiter` to `UniversalAdaptiveRateLimiter`. | [CHANGELOG v1.14.0](../../CHANGELOG.md) |
| 1.14.0 | WithExecutor | Yes | Removed `WithExecutor` method (Wave 17K). | Use `HttpClientExtensions.ExecuteWithResilienceAsync` or the builder → executor pipeline instead. | [CHANGELOG v1.14.0](../../CHANGELOG.md) |
| Unreleased | IAuthFailureGateRegistry | Deprecation | `IAuthFailureGateRegistry` / `AuthFailureGateRegistry` deprecated in favour of a direct `ConcurrentDictionary<string, AuthFailureGate>` per-plugin. Marked `[Obsolete(error: false)]`; will be removed in v2.0.0. | Replace registry usage with a local `ConcurrentDictionary<string, AuthFailureGate>` and pair each gate with a custom `IAuthFailureHandler`. | [CHANGELOG Unreleased](../../CHANGELOG.md) |

Guidelines:

- Update this table whenever `PublicAPI.Shipped.txt` removes or changes APIs.
- Cross-link to the specific release entry in `CHANGELOG.md`.
- Reference detailed instructions in [`migration/FROM_LEGACY.md`](FROM_LEGACY.md) or other how-to guides.
