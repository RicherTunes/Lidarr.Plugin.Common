using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Manifest;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Entry point implemented by every Lidarr streaming plugin.
    /// </summary>
    public interface IPlugin : IAsyncDisposable
    {
        /// <summary>
        /// Manifest metadata describing the plugin.
        /// </summary>
        PluginManifest Manifest { get; }

        /// <summary>
        /// Initializes plugin-wide resources. Invoked once per AssemblyLoadContext instance.
        /// </summary>
        ValueTask InitializeAsync(IPluginContext context, CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates an indexer instance if supported, otherwise returns <c>null</c>.
        /// </summary>
        ValueTask<IIndexer?> CreateIndexerAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Creates a download client instance if supported, otherwise returns <c>null</c>.
        /// </summary>
        ValueTask<IDownloadClient?> CreateDownloadClientAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Provides access to settings operations. Must never return <c>null</c>.
        /// </summary>
        ISettingsProvider SettingsProvider { get; }
    }
}
