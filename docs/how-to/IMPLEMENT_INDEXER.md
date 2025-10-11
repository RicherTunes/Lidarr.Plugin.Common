# How-to: Implement an Indexer

Use the shared library to minimize boilerplate when building a streaming indexer.

## 1. Start from `IIndexer`
Create a concrete class implementing `IIndexer` (from Abstractions). Inject required helpers via the constructor.

```csharp

public sealed class MyIndexer : IIndexer
{
    private readonly ILogger<MyIndexer> _logger;
    private readonly StreamingIndexerMixin _helper = new("MyPlugin");

    public MyIndexer(ILogger<MyIndexer> logger) => _logger = logger;

    public ValueTask<PluginValidationResult> InitializeAsync(CancellationToken token = default)
        => ValueTask.FromResult(PluginValidationResult.Success());

    // Implement search methods below...
}

```

## 2. Use the mixins and utilities

- `StreamingIndexerMixin` provides request/response helpers.
- `StreamingApiRequestBuilder` builds resilient HTTP requests.
- `StreamingResponseCache` avoids excessive API calls.

See [Developer guide → HTTP resilience](../dev-guide/DEVELOPER_GUIDE.md#http-resilience) for recommended handlers.

## 3. Implement required members
`IIndexer` defines synchronous batch and streaming methods:

- `SearchAlbumsAsync`
- `SearchAlbumsStreamAsync`
- `SearchTracksAsync`
- `SearchTracksStreamAsync`
- `GetAlbumAsync`

Return Abstractions DTOs (`StreamingAlbum`, `StreamingTrack`). Do not expose plugin-private types across the interface.

## 4. Handle paging and throttling

- Use `FetchPagedAsync<T>` from Common when the service exposes offset/next-page cursors.
- Wrap HTTP calls with `ExecuteWithResilienceAsync` to respect retry budgets and `Retry-After` headers.
- If the service enforces rate limits, use `IUniversalAdaptiveRateLimiter`.

## 5. Map metadata carefully
Populate:

- `StreamingAlbum.ExternalIds` with service-specific IDs.
- `StreamingTrack.ExternalIds` and `MusicBrainzId` when available for better matching.
- Quality tiers via `QualityMapper` if the indexer enumerates track formats.

## 6. Test

- Unit test your translation logic.
- Use `MockFactories` (Common) to get realistic fixtures.
- Integration-test against the live service behind feature flags.

## Related docs

- [Create a plugin project](CREATE_PLUGIN.md)
- [Architecture](../concepts/ARCHITECTURE.md)
- [Developer guide: Models & matching](../dev-guide/DEVELOPER_GUIDE.md#models--matching)

