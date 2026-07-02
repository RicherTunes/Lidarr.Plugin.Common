using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Lyrics;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Lyrics;

/// <summary>
/// LRCLIB's exact <c>/api/get</c> requires artist+track+album+duration to match its
/// record precisely. Real-world streaming metadata frequently differs: a service's
/// album-edition name ("Watch It Burn") or a 1s duration-rounding difference makes
/// <c>/api/get</c> 404 even when LRCLIB HAS the track (found live: Katy Perry "bandaids"
/// — get 404 on album "Watch It Burn"/188s, but search returns it with synced lyrics at
/// 189s). So on a get-miss the client now falls back to the fuzzy <c>/api/search</c>
/// (artist+track) and takes the closest-duration synced match within a tolerance,
/// materially improving lyrics coverage without risking a grossly-wrong version.
/// </summary>
public sealed class LrclibClientSearchFallbackTests
{
    [Fact]
    public async Task GetMiss_SearchFindsSyncedNearDuration_ReturnsSearchLyrics()
    {
        var handler = new RoutingStubHandler
        {
            Get = (HttpStatusCode.NotFound, null),
            Search = (HttpStatusCode.OK, "[{\"id\":1,\"artistName\":\"Katy Perry\",\"trackName\":\"bandaids\",\"albumName\":\"bandaids\",\"duration\":189.0,\"syncedLyrics\":\"[00:00.00] la la\"}]"),
        };
        using var client = new LrclibClient(new HttpClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("Katy Perry", "bandaids", "Watch It Burn", 188);

        Assert.Equal("[00:00.00] la la", lrc);
        Assert.Contains("/api/search", handler.PathsSeen);
    }

    [Fact]
    public async Task GetSucceeds_DoesNotCallSearch()
    {
        var handler = new RoutingStubHandler
        {
            Get = (HttpStatusCode.OK, "{\"id\":1,\"syncedLyrics\":\"[00:00.00] exact\"}"),
            Search = (HttpStatusCode.OK, "[{\"syncedLyrics\":\"[00:00.00] fuzzy\",\"duration\":100.0}]"),
        };
        using var client = new LrclibClient(new HttpClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("A", "B", "C", 100);

        Assert.Equal("[00:00.00] exact", lrc);
        Assert.DoesNotContain("/api/search", handler.PathsSeen);
    }

    [Fact]
    public async Task GetMiss_SearchHasOnlyPlainLyrics_ReturnsNull()
    {
        var handler = new RoutingStubHandler
        {
            Get = (HttpStatusCode.NotFound, null),
            Search = (HttpStatusCode.OK, "[{\"trackName\":\"B\",\"syncedLyrics\":null,\"plainLyrics\":\"words\",\"duration\":188.0}]"),
        };
        using var client = new LrclibClient(new HttpClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("A", "B", "C", 188);

        Assert.Null(lrc);
    }

    [Fact]
    public async Task GetMiss_SearchOnlyGrosslyWrongDuration_ReturnsNull()
    {
        // A synced result whose duration is far from the requested one is likely a
        // different version (live/extended) — its timings would be misaligned, so skip it.
        var handler = new RoutingStubHandler
        {
            Get = (HttpStatusCode.NotFound, null),
            Search = (HttpStatusCode.OK, "[{\"trackName\":\"B\",\"syncedLyrics\":\"[00:00.00] wrong\",\"duration\":320.0}]"),
        };
        using var client = new LrclibClient(new HttpClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("A", "B", "C", 188);

        Assert.Null(lrc);
    }

    [Fact]
    public async Task GetMiss_SearchSameDurationWrongTrack_ReturnsNull()
    {
        // LRCLIB search is fuzzy. Duration alone is not enough: two different tracks can
        // share a runtime, and returning the wrong LRC would be worse than no lyrics.
        var handler = new RoutingStubHandler
        {
            Get = (HttpStatusCode.NotFound, null),
            Search = (HttpStatusCode.OK, "[{\"trackName\":\"Wrong Song\",\"syncedLyrics\":\"[00:00.00] wrong\",\"duration\":188.0}]"),
        };
        using var client = new LrclibClient(new HttpClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("A", "Right Song", "C", 188);

        Assert.Null(lrc);
    }

    [Fact]
    public async Task GetMiss_SearchPicksClosestDurationSyncedMatch()
    {
        var handler = new RoutingStubHandler
        {
            Get = (HttpStatusCode.NotFound, null),
            Search = (HttpStatusCode.OK, "[{\"trackName\":\"B\",\"syncedLyrics\":\"[00:00.00] far\",\"duration\":196.0},{\"trackName\":\"B\",\"syncedLyrics\":\"[00:00.00] close\",\"duration\":189.0}]"),
        };
        using var client = new LrclibClient(new HttpClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("A", "B", "C", 188);

        Assert.Equal("[00:00.00] close", lrc);
    }

    [Fact]
    public async Task GetMiss_SearchNetworkError_ReturnsNull()
    {
        var handler = new RoutingStubHandler
        {
            Get = (HttpStatusCode.NotFound, null),
            SearchThrows = new HttpRequestException("dns down"),
        };
        using var client = new LrclibClient(new HttpClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("A", "B", "C", 188);

        Assert.Null(lrc);
    }

    [Fact]
    public async Task GetMiss_UnknownDuration_TakesFirstSyncedResult()
    {
        // durationSeconds == 0 means we don't know the duration (can't compare); take the
        // top-ranked synced result LRCLIB returns.
        var handler = new RoutingStubHandler
        {
            Get = (HttpStatusCode.NotFound, null),
            Search = (HttpStatusCode.OK, "[{\"trackName\":\"B\",\"syncedLyrics\":\"[00:00.00] first\",\"duration\":260.0}]"),
        };
        using var client = new LrclibClient(new HttpClient(handler));

        var lrc = await client.TryFetchSyncedLyricsAsync("A", "B", "C", 0);

        Assert.Equal("[00:00.00] first", lrc);
    }

    private sealed class RoutingStubHandler : HttpMessageHandler
    {
        public (HttpStatusCode status, string? json) Get { get; set; } = (HttpStatusCode.NotFound, null);
        public (HttpStatusCode status, string? json) Search { get; set; } = (HttpStatusCode.NotFound, null);
        public Exception? SearchThrows { get; set; }
        public List<string> PathsSeen { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var path = request.RequestUri!.AbsolutePath;
            PathsSeen.Add(path);

            (HttpStatusCode status, string? json) chosen;
            if (path.Contains("/api/search", StringComparison.Ordinal))
            {
                if (SearchThrows is not null) throw SearchThrows;
                chosen = Search;
            }
            else
            {
                chosen = Get;
            }

            var resp = new HttpResponseMessage(chosen.status)
            {
                Content = chosen.json is not null
                    ? new StringContent(chosen.json, Encoding.UTF8, "application/json")
                    : new StringContent(string.Empty),
            };
            return Task.FromResult(resp);
        }
    }
}
