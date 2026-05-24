using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// TDD pins for <see cref="PlaceholderSearchUri"/>. The placeholder URI pattern is how
/// apple and tidal smuggle a search query through Lidarr's HttpIndexerBase pipeline
/// (which expects an HTTP request shape) when the real search call needs to happen
/// inside <c>FetchReleases</c> directly. Each plugin scheme is per-service; the
/// roundtrip (build → extract) is identical algorithm.
/// </summary>
public class PlaceholderSearchUriTests
{
    [Fact]
    public void Build_ProducesValidUri()
    {
        var url = PlaceholderSearchUri.Build("applemusic", "Pink Floyd Dark Side");
        Assert.StartsWith("applemusic://search?query=", url);
        // Query value is URL-encoded
        Assert.Contains("Pink%20Floyd%20Dark%20Side", url);
    }

    [Fact]
    public void Build_EmptyQuery_StillProducesUri()
    {
        // Caller is responsible for not building with empty queries; the builder is
        // defensive and produces a valid-shape URI either way.
        var url = PlaceholderSearchUri.Build("tidal", "");
        Assert.Equal("tidal://search?query=", url);
    }

    [Theory]
    [InlineData("applemusic://search?query=Pink+Floyd", "applemusic", "Pink Floyd")]
    [InlineData("tidal://search?query=Sigur%20R%C3%B3s", "tidal", "Sigur Rós")]
    [InlineData("qobuz://search?query=AC%2FDC", "qobuz", "AC/DC")]
    public void TryExtractQuery_RoundtripsBuild(string url, string scheme, string expected)
    {
        Assert.True(PlaceholderSearchUri.TryExtractQuery(url, scheme, out var query));
        Assert.Equal(expected, query);
    }

    [Theory]
    [InlineData("APPLEMUSIC://search?query=test", "applemusic")] // case-insensitive scheme
    public void TryExtractQuery_CaseInsensitiveScheme(string url, string scheme)
    {
        Assert.True(PlaceholderSearchUri.TryExtractQuery(url, scheme, out _));
    }

    [Theory]
    [InlineData("https://example.com/search?query=x", "applemusic")] // wrong scheme
    [InlineData("applemusic://recent", "applemusic")] // wrong path-equivalent (no search?query=)
    [InlineData("applemusic://search", "applemusic")] // no query param
    [InlineData("applemusic://search?other=x", "applemusic")] // wrong query param
    [InlineData("applemusic://search?query=", "applemusic")] // empty query
    [InlineData("not a uri", "applemusic")]
    [InlineData("", "applemusic")]
    [InlineData(null, "applemusic")]
    public void TryExtractQuery_UnrecognizedReturnsFalse(string? url, string scheme)
    {
        Assert.False(PlaceholderSearchUri.TryExtractQuery(url!, scheme, out var query));
        Assert.Equal(string.Empty, query);
    }

    [Fact]
    public void Build_RoundtripsTryExtract()
    {
        var query = "Sigur Rós — \"Hopelandic\" / Track #1";
        var url = PlaceholderSearchUri.Build("applemusic", query);
        Assert.True(PlaceholderSearchUri.TryExtractQuery(url, "applemusic", out var extracted));
        Assert.Equal(query, extracted);
    }
}
