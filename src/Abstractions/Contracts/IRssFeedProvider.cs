using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Provides RSS feed capability for indexers.
    /// IMPORTANT: This interface is OPTIONAL. Bridge plugins that don't support RSS
    /// should return false from SupportsRss and throw NotSupportedException from methods.
    /// </summary>
    /// <remarks>
    /// RSS support may not be feasible for all bridge plugins due to architectural constraints.
    /// The native RSS path via HttpIndexerBase remains available for plugins that require it.
    /// </remarks>
    public interface IRssFeedProvider
    {
        /// <summary>
        /// Whether this provider supports RSS feeds.
        /// Bridge plugins should return false if RSS is not supported.
        /// </summary>
        bool SupportsRss { get; }

        /// <summary>
        /// Gets RSS feed items.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token</param>
        /// <returns>RSS feed items, or empty collection if not supported</returns>
        /// <exception cref="NotSupportedException">Thrown when SupportsRss is false</exception>
        IAsyncEnumerable<RssFeedItem> GetFeedItemsAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Gets the last time the RSS feed was checked.
        /// </summary>
        DateTimeOffset? LastChecked { get; }

        /// <summary>
        /// Gets the recommended polling interval for the RSS feed.
        /// </summary>
        TimeSpan PollingInterval { get; }
    }

    /// <summary>
    /// Represents an item in an RSS feed.
    /// </summary>
    public class RssFeedItem
    {
        /// <summary>
        /// Unique identifier for the feed item.
        /// </summary>
        public string Id { get; init; } = string.Empty;

        /// <summary>
        /// Title of the feed item.
        /// </summary>
        public string Title { get; init; } = string.Empty;

        /// <summary>
        /// When the item was published.
        /// </summary>
        public DateTimeOffset Published { get; init; }

        /// <summary>
        /// Download URL if available.
        /// </summary>
        public string? DownloadUrl { get; init; }

        /// <summary>
        /// Album information if parsed.
        /// </summary>
        public StreamingAlbum? Album { get; init; }

        /// <summary>
        /// Raw content of the feed item.
        /// </summary>
        public string? Content { get; init; }
    }
}
