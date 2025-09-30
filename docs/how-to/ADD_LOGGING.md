# How-to: Add Structured Logging

Use the shared logging abstractions so plugin logs integrate with the host.

## Guidelines

- Always request an `ILogger<T>` via dependency injection from the host-provided `ILoggerFactory` (available inside `IPluginContext`).
- Include plugin id/version in log scopes to simplify support tickets.
- Prefer structured logging (named properties) over string concatenation.

## Example

```csharp

public sealed class MyIndexer : IIndexer
{
    private readonly ILogger<MyIndexer> _logger;

    public MyIndexer(ILoggerFactory loggerFactory)
        => _logger = loggerFactory.CreateLogger<MyIndexer>();

    public async ValueTask<IReadOnlyList<StreamingAlbum>> SearchAlbumsAsync(string query, CancellationToken token = default)
    {
        using (_logger.BeginScope(new Dictionary<string, object>
        {
            ["Plugin"] = "myplugin",
            ["Query"] = query
        }))
        {
            _logger.LogInformation("Searching albums");
            var albums = await FetchAlbumsAsync(query, token);
            _logger.LogInformation("Found {Count} albums", albums.Count);
            return albums;
        }
    }
}

```

## Log levels

- **Trace/Debug**: detailed API responses (guard with feature flags).
- **Information**: business milestones (search start/end, download complete).
- **Warning**: transient issues that are retried (e.g., HTTP 429 handled by resilience).
- **Error**: unrecoverable failures that propagate to the host.

## Forwarding to host sinks

- Hosts typically configure logging providers (console, file, telemetry) through the shared `Microsoft.Extensions.Logging` stack.
- Plugins should never create their own logging sinks or static loggers.

## Related docs

- [Developer guide → Logging](../dev-guide/DEVELOPER_GUIDE.md#logging)
- [Architecture](../concepts/ARCHITECTURE.md)

