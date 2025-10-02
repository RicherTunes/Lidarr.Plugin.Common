using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;\nusing System.Runtime.CompilerServices;\nusing Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Hosting;
using Lidarr.Plugin.Common.Services.Registration;
using Microsoft.Extensions.DependencyInjection;

namespace StreamingPluginSample;

public interface IServiceProviderAccessor
{
    IServiceProvider Services { get; }
}

// snippet:streaming-plugin-entry
public sealed class SampleStreamingPlugin : StreamingPlugin<SampleModule, SampleSettings>, IServiceProviderAccessor
{
    protected override void ConfigureServices(IServiceCollection services, IPluginContext context, SampleSettings settings)
    {
        services.AddSingleton<SampleIndexer>();
    }

    protected override ValueTask<IIndexer?> CreateIndexerAsync(SampleSettings settings, IServiceProvider services, CancellationToken cancellationToken)
    {
        return ValueTask.FromResult<IIndexer?>(services.GetRequiredService<SampleIndexer>());
    }

    public IServiceProvider Services => base.Services;
}
// end-snippet

// snippet:streaming-plugin-module
public sealed class SampleModule : StreamingPluginModule
{
    public override string ServiceName => "Sample Stream";
    public override string Description => "Minimal streaming plugin example.";
    public override string Author => "Lidarr Plugin Team";
}
// end-snippet

// snippet:streaming-plugin-settings
public sealed class SampleSettings
{
    public string ApiKey { get; set; } = string.Empty;
    public string Region { get; set; } = "US";
}
// end-snippet

// snippet:streaming-plugin-indexer
public sealed class SampleIndexer : IIndexer
{
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;

    public ValueTask<PluginValidationResult> InitializeAsync(CancellationToken cancellationToken = default)
        => ValueTask.FromResult(PluginValidationResult.Success());

    public ValueTask<IReadOnlyList<StreamingAlbum>> SearchAlbumsAsync(string query, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StreamingAlbum>>(Array.Empty<StreamingAlbum>());

    public ValueTask<IReadOnlyList<StreamingTrack>> SearchTracksAsync(string query, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<IReadOnlyList<StreamingTrack>>(Array.Empty<StreamingTrack>());

    public ValueTask<StreamingAlbum?> GetAlbumAsync(string albumId, CancellationToken cancellationToken = default)
        => ValueTask.FromResult<StreamingAlbum?>(null);

    public async IAsyncEnumerable<StreamingAlbum> SearchAlbumsStreamAsync(string query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }

    public async IAsyncEnumerable<StreamingTrack> SearchTracksStreamAsync(string query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await Task.CompletedTask;
        yield break;
    }
}
// end-snippet

