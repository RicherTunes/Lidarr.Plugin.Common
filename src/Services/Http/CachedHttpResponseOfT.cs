using System;
using System.Net;
using Lidarr.Plugin.Common.Services.Caching;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Generic envelope returned by <c>CachingHttpExecutor.SendAsync</c> — a parsed payload
    /// alongside the raw HTTP cache fields and a <see cref="CacheHitKind"/> describing how the response was
    /// produced (cache hit, soft-revalidate, 304 fold, stale-if-error, miss, etc.).
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is a generic sibling of <see cref="CachedHttpResponse"/>. The non-generic type continues to be the
    /// on-disk/in-memory cache record used by <see cref="Lidarr.Plugin.Common.Interfaces.IStreamingResponseCache"/>;
    /// the generic version exists so callers can receive a parsed DTO without re-deserializing the body.
    /// </para>
    /// <para>
    /// <see cref="Body"/> always holds the underlying response bytes (from the origin or, for 304/stale-if-error,
    /// from the cached entry). <see cref="Payload"/> is the result of the optional parse hook and may be
    /// <see langword="null"/> if no parse hook was provided or if parsing was skipped.
    /// </para>
    /// </remarks>
    /// <typeparam name="TPayload">The parsed payload type (typically a DTO).</typeparam>
    public sealed class CachedHttpResponse<TPayload>
    {
        /// <summary>HTTP status code observed (or synthesized — e.g., 200 for a 304 fold or stale-if-error response).</summary>
        public HttpStatusCode StatusCode { get; init; } = HttpStatusCode.OK;

        /// <summary>The parsed payload, if a parse hook was supplied; otherwise <see langword="null"/>.</summary>
        public TPayload? Payload { get; init; }

        /// <summary>Raw response body bytes. Empty for responses with no body.</summary>
        public byte[] Body { get; init; } = Array.Empty<byte>();

        /// <summary>Content-Type, when known.</summary>
        public string? ContentType { get; init; }

        /// <summary>ETag value (without surrounding quotes if produced by HttpClient).</summary>
        public string? ETag { get; init; }

        /// <summary>Last-Modified timestamp, when present.</summary>
        public DateTimeOffset? LastModified { get; init; }

        /// <summary>UTC time the cached entry (if any) was originally stored — preserved across 304 folds and stale-if-error.</summary>
        public DateTimeOffset StoredAt { get; init; }

        /// <summary>How the response was produced — for telemetry / metrics.</summary>
        public CacheHitKind HitKind { get; init; } = CacheHitKind.Miss;
    }
}
