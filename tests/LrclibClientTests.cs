using System;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Lyrics;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// LrclibClient calls https://lrclib.net/api/get with track metadata and returns
/// the synced-lyrics body when available. Plugins can use it as a fallback when
/// their primary streaming-service lyrics endpoint returns nothing (Tidal,
/// Apple Music). The client is intentionally minimal — no caching, no retries
/// beyond the underlying HttpClient's, no rate-limit gating — because callers
/// already have those in their own pipelines.
///
/// "Not found" is a normal case (LRCLIB doesn't cover every track), so it
/// surfaces as <c>null</c> rather than an exception. Other failure modes
/// (5xx, network, malformed JSON) also collapse to <c>null</c> because lyrics
/// are a nice-to-have — a fall-through failure must never break a download.
/// </summary>
public sealed class LrclibClientTests
{
    private static HttpClient StubClient(StubHandler handler) => new(handler);

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_FoundTrack_ReturnsLrcBody()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            json: "{\"id\":1,\"trackName\":\"Test\",\"syncedLyrics\":\"[00:00.00] Hello\\n[00:01.00] World\"}");
        using var client = new LrclibClient(StubClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync(
            artistName: "Test Artist",
            trackName: "Test Track",
            albumName: "Test Album",
            durationSeconds: 180);

        Assert.Equal("[00:00.00] Hello\n[00:01.00] World", lrc);
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_404_ReturnsNull()
    {
        var handler = new StubHandler(HttpStatusCode.NotFound);
        using var client = new LrclibClient(StubClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("Artist", "Track", "Album", 120);

        Assert.Null(lrc);
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_ServerError_ReturnsNull()
    {
        // 5xx from LRCLIB shouldn't escape — lyrics are best-effort.
        var handler = new StubHandler(HttpStatusCode.InternalServerError);
        using var client = new LrclibClient(StubClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("Artist", "Track", "Album", 120);

        Assert.Null(lrc);
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_NetworkError_ReturnsNull()
    {
        // Underlying SendAsync throws (DNS failure, connection refused, etc.).
        var handler = new StubHandler(throwOnSend: new HttpRequestException("DNS down"));
        using var client = new LrclibClient(StubClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("Artist", "Track", "Album", 120);

        Assert.Null(lrc);
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_MalformedJson_ReturnsNull()
    {
        var handler = new StubHandler(HttpStatusCode.OK, json: "not valid json");
        using var client = new LrclibClient(StubClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("Artist", "Track", "Album", 120);

        Assert.Null(lrc);
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_EmptySyncedLyrics_ReturnsNull()
    {
        // LRCLIB can return a record with only plain lyrics (no syncedLyrics).
        // Empty syncedLyrics is treated the same as missing — null.
        var handler = new StubHandler(HttpStatusCode.OK,
            json: "{\"id\":1,\"trackName\":\"Test\",\"syncedLyrics\":\"\"}");
        using var client = new LrclibClient(StubClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("Artist", "Track", "Album", 120);

        Assert.Null(lrc);
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_QueryParameters_FormattedCorrectly()
    {
        var handler = new StubHandler(HttpStatusCode.OK,
            json: "{\"id\":1,\"trackName\":\"x\",\"syncedLyrics\":\"[00:00.00] y\"}");
        using var client = new LrclibClient(StubClient(handler));

        await client.TryFetchSyncedLyricsAsync(
            artistName: "Pink Floyd",
            trackName: "Money",
            albumName: "The Dark Side of the Moon",
            durationSeconds: 382);

        Assert.NotNull(handler.LastRequestUri);
        var uri = handler.LastRequestUri!;
        Assert.Equal("lrclib.net", uri.Host);
        Assert.Equal("/api/get", uri.AbsolutePath);
        Assert.Contains("artist_name=Pink+Floyd", uri.Query);
        Assert.Contains("track_name=Money", uri.Query);
        Assert.Contains("album_name=The+Dark+Side+of+the+Moon", uri.Query);
        Assert.Contains("duration=382", uri.Query);
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_AmbiguousCharsInMetadata_EscapedNotInjected()
    {
        // Defensive against weird track names. The client must not let user-controlled
        // metadata break the URL or produce duplicate query params.
        var handler = new StubHandler(HttpStatusCode.NotFound);
        using var client = new LrclibClient(StubClient(handler));

        await client.TryFetchSyncedLyricsAsync(
            artistName: "AC/DC",
            trackName: "T.N.T.",
            albumName: "High Voltage & Beyond",
            durationSeconds: 200);

        // A 404 now triggers the /api/search fallback (a second request), so assert on the
        // FIRST request — the /api/get whose album_name+escaping this test exercises.
        var uri = handler.FirstRequestUri!;
        // & in album must be encoded, NOT bleed into a separate query parameter.
        Assert.Contains("album_name=High+Voltage+%26+Beyond", uri.Query, StringComparison.OrdinalIgnoreCase);
        // / in artist must be encoded. (%2F / %2f vary by URI implementation; case-insensitive.)
        Assert.Contains("artist_name=AC%2FDC", uri.Query, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_NullArtist_Throws()
    {
        using var client = new LrclibClient(StubClient(new StubHandler(HttpStatusCode.OK)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.TryFetchSyncedLyricsAsync(null!, "track", "album", 100));
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_NullTrack_Throws()
    {
        using var client = new LrclibClient(StubClient(new StubHandler(HttpStatusCode.OK)));
        await Assert.ThrowsAsync<ArgumentException>(() =>
            client.TryFetchSyncedLyricsAsync("artist", null!, "album", 100));
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_EmptyAlbum_AllowedAsOptionalField()
    {
        // LRCLIB tolerates a missing album_name (the field is helpful but not required).
        // The client should pass through empty string without throwing.
        var handler = new StubHandler(HttpStatusCode.NotFound);
        using var client = new LrclibClient(StubClient(handler));

        await client.TryFetchSyncedLyricsAsync("artist", "track", albumName: "", durationSeconds: 100);

        Assert.NotNull(handler.LastRequestUri);
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_NegativeDuration_Throws()
    {
        using var client = new LrclibClient(StubClient(new StubHandler(HttpStatusCode.OK)));
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            client.TryFetchSyncedLyricsAsync("artist", "track", "album", durationSeconds: -1));
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_CancellationRequested_PropagatesOperationCanceled()
    {
        // The handler waits indefinitely; cancellation must abort the call.
        var handler = new StubHandler(HttpStatusCode.OK, blockUntilCancelled: true);
        using var client = new LrclibClient(StubClient(handler));
        using var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromMilliseconds(50));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            client.TryFetchSyncedLyricsAsync("artist", "track", "album", 100, cts.Token));
    }

    [Fact]
    public async Task TryFetchSyncedLyricsAsync_UserAgent_IdentifiesPluginEcosystem()
    {
        // Public services like LRCLIB appreciate a recognisable User-Agent so they
        // can rate-limit or contact the owning project if something goes wrong.
        // The client must SET a User-Agent; the consumer plugin can override it
        // later if it wants a per-plugin identity.
        var handler = new StubHandler(HttpStatusCode.NotFound);
        using var client = new LrclibClient(StubClient(handler));

        await client.TryFetchSyncedLyricsAsync("artist", "track", "album", 100);

        Assert.NotNull(handler.LastRequestHeaders);
        var userAgents = handler.LastRequestHeaders!.UserAgent;
        Assert.NotEmpty(userAgents);
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string? _json;
        private readonly Exception? _throwOnSend;
        private readonly bool _blockUntilCancelled;

        public Uri? LastRequestUri { get; private set; }
        public Uri? FirstRequestUri { get; private set; }
        public System.Net.Http.Headers.HttpRequestHeaders? LastRequestHeaders { get; private set; }

        public StubHandler(
            HttpStatusCode status = HttpStatusCode.OK,
            string? json = null,
            Exception? throwOnSend = null,
            bool blockUntilCancelled = false)
        {
            _status = status;
            _json = json;
            _throwOnSend = throwOnSend;
            _blockUntilCancelled = blockUntilCancelled;
        }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri;
            FirstRequestUri ??= request.RequestUri;
            LastRequestHeaders = request.Headers;

            if (_throwOnSend is not null) throw _throwOnSend;

            if (_blockUntilCancelled)
            {
                await Task.Delay(TimeSpan.FromSeconds(60), cancellationToken).ConfigureAwait(false);
            }

            return new HttpResponseMessage(_status)
            {
                Content = _json is not null
                    ? new StringContent(_json, Encoding.UTF8, "application/json")
                    : new StringContent(string.Empty)
            };
        }
    }
}
