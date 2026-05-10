# Lidarr.Plugin.Common — Benchmarks

BenchmarkDotNet harness for the unification's hot paths. **Standalone** — not part of
`lidarr.plugin.common.sln` and not run by `dotnet test`.

## What's covered

| Benchmark class                       | Hot path                                               |
| ------------------------------------- | ------------------------------------------------------ |
| `CachingHttpExecutorBenchmarks`       | `CachingHttpExecutor.SendAsync` (Hit / Miss / 304 fold)|
| `ChunkedHttpAssemblerBenchmarks`      | `ChunkedHttpAssembler.AssembleAsync` (8 vs 64 chunks)  |
| `JsonFileStoreBenchmarks`             | `JsonFileStore<TKey,TValue>` Set + Get with TTL/LRU    |
| `AnthropicStreamDecoderBenchmarks`    | `AnthropicStreamDecoder.DecodeAsync` (small / typical) |
| `FileTokenStoreBenchmarks`            | `FileTokenStore<T>` Save+Load round-trip               |
| `LogRedactorBenchmarks`               | `LogRedactor.Redact` (plain, bearer, JSON, query)      |

## Running

```bash
cd bench/Lidarr.Plugin.Common.Benchmarks
dotnet run -c Release -- --filter "*"
```

Filter by class:

```bash
dotnet run -c Release -- --filter "*Caching*"
dotnet run -c Release -- --filter "*Anthropic*"
```

Smoke run (fast, less precise — good for sanity-checking changes):

```bash
dotnet run -c Release -- --filter "*Caching*" --job short --warmupCount 2 --iterationCount 3
```

## Baselines

Initial baseline numbers live under `bench/baselines/` (one file per snapshot date).
**Do not optimize against these without comparing on the same hardware** — BDN already
reports machine info. When you suspect a regression:

1. Run the affected benchmark on a clean checkout of the suspected baseline commit.
2. Run the same benchmark on the change branch.
3. Compare ns/op and B/op deltas.

## Conventions

- All benchmarks use `[MemoryDiagnoser]` and target `net8.0`.
- Tests use deterministic in-memory fakes (`ScriptedHandler`, `InMemoryByteHandler`,
  `FakeTimeProvider`) so numbers reflect library work, not network/clock noise.
- Don't add benchmarks to `lidarr.plugin.common.sln` — they need their own `Release`
  invocation and shouldn't slow down `dotnet test`.
