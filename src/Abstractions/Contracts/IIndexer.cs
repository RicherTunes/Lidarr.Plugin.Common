using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Contract for exposing search/indexing capabilities to the host.
    /// All models must come from Lidarr.Plugin.Abstractions to guarantee cross-ALC compatibility.
    /// </summary>
    public interface IIndexer : IAsyncDisposable
    {
        /// <summary>
        /// Performs any network or authentication initialization required before handling requests.
        /// </summary>
        ValueTask<PluginValidationResult> InitializeAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes a query returning albums. Plugins may return an empty collection when no matches are found.
        /// </summary>
        ValueTask<IReadOnlyList<StreamingAlbum>> SearchAlbumsAsync(string query, CancellationToken cancellationToken = default);

        /// <summary>
        /// Executes a query returning tracks.
        /// </summary>
        ValueTask<IReadOnlyList<StreamingTrack>> SearchTracksAsync(string query, CancellationToken cancellationToken = default);

        /// <summary>
        /// Retrieves a single album by its provider identifier.
        /// </summary>
        ValueTask<StreamingAlbum?> GetAlbumAsync(string albumId, CancellationToken cancellationToken = default);

        /// <summary>
        /// Streams album results lazily. Hosts may fall back to <see cref="SearchAlbumsAsync"/> when not implemented.
        /// </summary>
        IAsyncEnumerable<StreamingAlbum> SearchAlbumsStreamAsync(string query, CancellationToken cancellationToken = default);

        /// <summary>
        /// Streams track results lazily. Hosts may fall back to <see cref="SearchTracksAsync"/> when not implemented.
        /// </summary>
        IAsyncEnumerable<StreamingTrack> SearchTracksStreamAsync(string query, CancellationToken cancellationToken = default);
    }
}
