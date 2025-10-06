using System;
using System.Net;

namespace Lidarr.Plugin.Common.Services.Caching
{
    /// <summary>
    /// Compact DTO for caching successful GET responses.
    /// </summary>
    public sealed class CachedHttpResponse
    {
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;
        public string? ContentType { get; init; }
        public byte[] Body { get; init; } = Array.Empty<byte>();
        public string? ETag { get; init; }
        public DateTimeOffset? LastModified { get; init; }
        public DateTimeOffset StoredAt { get; init; }
    }
}

