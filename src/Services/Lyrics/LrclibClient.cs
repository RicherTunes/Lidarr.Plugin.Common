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
        private const string SearchUrl = "https://lrclib.net/api/search";
        private const string DefaultUserAgent = "Lidarr.Plugin.Common.LrclibClient";

        // On a fuzzy-search fallback, a synced result whose duration is further than this
        // from the requested track is treated as a different version (live/extended/remix)
        // whose LRC timings would be misaligned, so it is skipped. Small edition/rounding
        // differences (LRCLIB is crowd-sourced; ±1-2s is common) still match.
        private const double SearchDurationToleranceSeconds = 15.0;

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

            // 1) Exact match: LRCLIB's /api/get keys on artist+track+album+duration. When the
            //    streaming metadata matches its record precisely this is the best result.
            var exact = await TryExactGetAsync(artistName, trackName, albumName ?? string.Empty, durationSeconds, cancellationToken).ConfigureAwait(false);
            if (exact is not null) return exact;

            // 2) Fallback: /api/get 404s whenever the album-edition name or duration differs even
            //    slightly from LRCLIB's crowd-sourced record (common), so fuzzy-search by
            //    artist+track and take the closest-duration synced match within tolerance.
            return await TrySearchSyncedLyricsAsync(artistName, trackName, durationSeconds, cancellationToken).ConfigureAwait(false);
        }

        private async Task<string?> TryExactGetAsync(string artistName, string trackName, string albumName, int durationSeconds, CancellationToken cancellationToken)
        {
            var uri = BuildUri(artistName, trackName, albumName, durationSeconds);

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

        private async Task<string?> TrySearchSyncedLyricsAsync(string artistName, string trackName, int durationSeconds, CancellationToken cancellationToken)
        {
            var uri = BuildSearchUri(artistName, trackName);

            try
            {
                using var response = await _httpClient.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                if (!response.IsSuccessStatusCode) return null;

                var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                LrclibSearchResult[]? results;
                try
                {
                    results = JsonSerializer.Deserialize<LrclibSearchResult[]>(body, JsonOptions);
                }
                catch (JsonException)
                {
                    return null;
                }

                if (results is null || results.Length == 0) return null;

                LrclibSearchResult? best = null;
                var bestDiff = double.MaxValue;
                foreach (var r in results)
                {
                    if (string.IsNullOrWhiteSpace(r?.SyncedLyrics)) continue;
                    if (!IsSameTrack(trackName, r.TrackName)) continue;

                    if (durationSeconds <= 0)
                    {
                        // Unknown duration — take the top-ranked synced result LRCLIB returned.
                        best = r;
                        break;
                    }

                    var diff = Math.Abs(r!.Duration - durationSeconds);
                    if (diff < bestDiff)
                    {
                        bestDiff = diff;
                        best = r;
                    }
                }

                if (best is null) return null;
                if (durationSeconds > 0 && bestDiff > SearchDurationToleranceSeconds) return null;

                return string.IsNullOrWhiteSpace(best.SyncedLyrics) ? null : best.SyncedLyrics;
            }
            catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
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

        private static Uri BuildSearchUri(string artist, string track)
        {
            var query = HttpUtility.ParseQueryString(string.Empty);
            query["artist_name"] = artist;
            query["track_name"] = track;

            var builder = new UriBuilder(SearchUrl) { Query = query.ToString() ?? string.Empty };
            return builder.Uri;
        }

        private static bool IsSameTrack(string requested, string? candidate)
        {
            var normalizedRequested = NormalizeTrackName(requested);
            var normalizedCandidate = NormalizeTrackName(candidate);
            return normalizedRequested.Length > 0
                && normalizedCandidate.Length > 0
                && string.Equals(normalizedRequested, normalizedCandidate, StringComparison.Ordinal);
        }

        private static string NormalizeTrackName(string? value)
        {
            if (string.IsNullOrWhiteSpace(value)) return string.Empty;

            var buffer = new char[value.Length];
            var length = 0;
            foreach (var c in value)
            {
                if (char.IsLetterOrDigit(c))
                {
                    buffer[length++] = char.ToLowerInvariant(c);
                }
            }

            return length == 0 ? string.Empty : new string(buffer, 0, length);
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

        private sealed class LrclibSearchResult
        {
            [JsonPropertyName("trackName")]
            public string? TrackName { get; init; }

            [JsonPropertyName("syncedLyrics")]
            public string? SyncedLyrics { get; init; }

            [JsonPropertyName("duration")]
            public double Duration { get; init; }

            // The API also returns id/artistName/trackName/albumName/plainLyrics — not needed here.
        }
    }
}
