using System;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Threading;
using System.Threading.Tasks;
using System.Web;

namespace Lidarr.Plugin.Common.Services.Lyrics
{
    /// <summary>
    /// Minimal client for the LRCLIB public lyrics API (https://lrclib.net/api).
    ///
    /// Plugins call <see cref="TryFetchSyncedLyricsAsync"/> as a fallback when
    /// their streaming-service's own lyrics endpoint returns nothing. The client
    /// intentionally has no caching, no retries beyond the underlying HttpClient,
    /// and no rate-limit gating because callers already wire those into their
    /// own pipelines.
    ///
    /// All failure modes (404, 5xx, network, malformed JSON, empty
    /// <c>syncedLyrics</c>) collapse to <c>null</c>. Lyrics are a nice-to-have —
    /// a fall-through failure here must never break a download.
    /// </summary>
    public sealed class LrclibClient : IDisposable
    {
        private const string BaseUrl = "https://lrclib.net/api/get";
        private const string DefaultUserAgent = "Lidarr.Plugin.Common.LrclibClient";

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

        private readonly HttpClient _httpClient;
        private readonly bool _ownsHttpClient;
        private bool _disposed;

        /// <summary>
        /// Construct with an injected HttpClient. The caller retains ownership.
        /// Useful when the consumer wants the client to participate in its
        /// HttpClientFactory / handler-chain plumbing.
        /// </summary>
        public LrclibClient(HttpClient httpClient)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _ownsHttpClient = false;
            EnsureUserAgent(_httpClient);
        }

        /// <summary>
        /// Construct with an internally owned HttpClient. Convenient for one-shot
        /// use; consumers running a long-lived plugin should prefer the injected
        /// overload so connection pooling is shared.
        /// </summary>
        public LrclibClient()
        {
            _httpClient = new HttpClient();
            _ownsHttpClient = true;
            EnsureUserAgent(_httpClient);
        }

        /// <summary>
        /// Look up a track's synced lyrics. Returns the LRC body on success, or
        /// <c>null</c> when LRCLIB doesn't have the track or when any error is
        /// encountered. Cancellation is propagated as
        /// <see cref="OperationCanceledException"/>.
        /// </summary>
        /// <param name="artistName">Primary artist name; required (non-empty).</param>
        /// <param name="trackName">Track title; required (non-empty).</param>
        /// <param name="albumName">Album name; may be empty (LRCLIB tolerates it).</param>
        /// <param name="durationSeconds">Track duration in seconds; must be &gt;= 0.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        public async Task<string?> TryFetchSyncedLyricsAsync(
            string artistName,
            string trackName,
            string albumName,
            int durationSeconds,
            CancellationToken cancellationToken = default)
        {
            if (string.IsNullOrWhiteSpace(artistName)) throw new ArgumentException("Artist name is required.", nameof(artistName));
            if (string.IsNullOrWhiteSpace(trackName)) throw new ArgumentException("Track name is required.", nameof(trackName));
            if (durationSeconds < 0) throw new ArgumentOutOfRangeException(nameof(durationSeconds), durationSeconds, "Duration must be >= 0.");

            var uri = BuildUri(artistName, trackName, albumName ?? string.Empty, durationSeconds);

            try
            {
                using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (response.StatusCode == HttpStatusCode.NotFound) return null;
                if (!response.IsSuccessStatusCode) return null;

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                LrclibResponse? parsed;
                try
                {
                    parsed = JsonSerializer.Deserialize<LrclibResponse>(body, JsonOptions);
                }
                catch (JsonException)
                {
                    return null;
                }

                var lrc = parsed?.SyncedLyrics;
                return string.IsNullOrWhiteSpace(lrc) ? null : lrc;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // SendAsync timeout (not user cancellation). Treat as a miss.
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (HttpRequestException)
            {
                return null;
            }
        }

        private static Uri BuildUri(string artist, string track, string album, int durationSeconds)
        {
            // Build with HttpUtility.ParseQueryString so reserved chars are
            // escaped properly and we don't accidentally inject extra params via
            // a stray '&' or '/' in user metadata.
            var query = HttpUtility.ParseQueryString(string.Empty);
            query["artist_name"] = artist;
            query["track_name"] = track;
            query["album_name"] = album;
            query["duration"] = durationSeconds.ToString(System.Globalization.CultureInfo.InvariantCulture);

            var builder = new UriBuilder(BaseUrl) { Query = query.ToString() ?? string.Empty };
            return builder.Uri;
        }

        private static void EnsureUserAgent(HttpClient client)
        {
            if (client.DefaultRequestHeaders.UserAgent.Count == 0)
            {
                var asmVersion = typeof(LrclibClient).Assembly.GetName().Version?.ToString() ?? "unknown";
                client.DefaultRequestHeaders.UserAgent.Add(
                    new ProductInfoHeaderValue(DefaultUserAgent, asmVersion));
            }
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            if (_ownsHttpClient)
            {
                _httpClient.Dispose();
            }
        }

        private sealed class LrclibResponse
        {
            [JsonPropertyName("syncedLyrics")]
            public string? SyncedLyrics { get; init; }

            // The API also returns plainLyrics, trackName, etc. — not needed here.
        }
    }
}
