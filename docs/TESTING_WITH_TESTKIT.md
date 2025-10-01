# Testing with the TestKit

`Lidarr.Plugin.Common.TestKit` (ships with the repository today; NuGet publishing planned for 1.2) provides fixtures, HTTP handlers, and manifest helpers so plugins can verify their implementations without copy/paste harnesses.

## AssemblyLoadContext harness

Use the isolation host sample to load plugins into collectible contexts, assert metadata, and unload cleanly. See [PLUGIN_ISOLATION.md](PLUGIN_ISOLATION.md) for the loader snippet.

## HTTP handlers & gzip safety net

```csharp file=../tests/HttpClientExtensionsTests.cs#guard-stream
```

```csharp file=../tests/HttpClientExtensionsTests.cs#sniffer-passthrough
```

Guard streams confirm that `ContentDecodingSnifferHandler` only peeks the gzip header and preserves `Content-Length` for pass-through payloads.

## Resilience and cancellation tests

```csharp file=../tests/HttpClientExtensionsTests.cs#resilience-cancel
```

```csharp file=../tests/GenericResilienceExecutorTests.cs#generic-cancel
```

These tests assert that per-request timeouts raise `TimeoutException` while caller cancellations propagate as `TaskCanceledException`/`OperationCanceledException` without retrying.

## Using the TestKit

1. Reference the project (or future NuGet) in your plugin test project.
2. Use the HTTP handlers (gzip mislabel, flaky 429, partial content, slow stream) to exercise resilience paths.
3. Instantiate the ALC fixture to load your plugin directly from disk for end-to-end integration tests.
4. Run `dotnet test` locally; CI executes the same suite.

## Contract

- Every plugin test project should validate gzip sniffing, resilience retries, manifest gating, and ALC unload behaviour.
- Use the shared handlers/fixtures instead of bespoke mocks to reduce drift.
- Keep tests deterministic (no real network); use the provided handlers or handcrafted `DelegatingHandler`s.
- When adding new handlers or fixtures, update this page and tag snippets so the verifier keeps docs honest.
