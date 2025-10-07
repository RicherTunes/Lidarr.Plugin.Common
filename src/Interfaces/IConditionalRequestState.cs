using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Optional conditional request validators (e.g., ETag/Last-Modified) storage.
    /// Implemented by plugins that want to enable 304 revalidation for GETs.
    /// </summary>
    public interface IConditionalRequestState
    {
        /// <summary>Try get validators for a cache key (may return null if none).</summary>
        ValueTask<(string? ETag, DateTimeOffset? LastModified)?> TryGetValidatorsAsync(string cacheKey, CancellationToken cancellationToken = default);

        /// <summary>Persist validators for a cache key.</summary>
        ValueTask SetValidatorsAsync(string cacheKey, string? eTag, DateTimeOffset? lastModified, CancellationToken cancellationToken = default);
    }
}

