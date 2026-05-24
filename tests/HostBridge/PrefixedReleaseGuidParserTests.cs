using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// TDD pins for <see cref="PrefixedReleaseGuidParser"/>. This is wave-A item 3 from the
/// May 2026 bridge-unification plan — apple and tidal each had ~60 LOC of identical
/// GUID/URL extraction logic with only the scheme prefix differing. The parser shape is
/// fixed here BEFORE the impl so the contract is operator-readable.
///
/// Expected GUID shapes:
///   {scheme}:album:{id}
///   {scheme}:album:{id}:{extra}            (tidal encodes quality in segment 4)
///   {indexerId}_{scheme}:album:{id}        (Lidarr prefixes search results by indexer ID)
///   {indexerId}_{scheme}:album:{id}:{extra}
///
/// Expected InfoUrl fallback: any URL whose path contains "/album/{id}".
/// </summary>
public class PrefixedReleaseGuidParserTests
{
    [Theory]
    [InlineData("applemusic:album:1234567890", "applemusic", "1234567890")]
    [InlineData("tidal:album:99999", "tidal", "99999")]
    [InlineData("APPLEMUSIC:album:abc", "applemusic", "abc")] // case-insensitive scheme
    [InlineData("tidal:ALBUM:99999", "tidal", "99999")]      // case-insensitive segment 2
    [InlineData("tidal:album:99999:Lossless", "tidal", "99999")] // segment 4 is per-scheme metadata
    public void ExtractAlbumIdFromGuid_RecognizedShapes(string guid, string prefix, string expected)
    {
        Assert.Equal(expected, PrefixedReleaseGuidParser.ExtractAlbumIdFromGuid(guid, prefix));
    }

    [Theory]
    [InlineData("2_applemusic:album:111", "applemusic", "111")] // indexer-ID prefix
    [InlineData("17_tidal:album:9999:Lossless", "tidal", "9999")]
    public void ExtractAlbumIdFromGuid_IndexerIdPrefixed(string guid, string prefix, string expected)
    {
        Assert.Equal(expected, PrefixedReleaseGuidParser.ExtractAlbumIdFromGuid(guid, prefix));
    }

    [Theory]
    [InlineData("", "applemusic")]
    [InlineData(null, "applemusic")]
    [InlineData("   ", "applemusic")]
    [InlineData("notmusic:album:1234", "applemusic")] // wrong scheme
    [InlineData("applemusic:", "applemusic")]         // truncated
    [InlineData("applemusic:track:1234", "applemusic")] // wrong type (album-only)
    [InlineData("applemusic:album:", "applemusic")]   // empty ID segment
    public void ExtractAlbumIdFromGuid_UnrecognizedReturnsNull(string? guid, string prefix)
    {
        Assert.Null(PrefixedReleaseGuidParser.ExtractAlbumIdFromGuid(guid, prefix));
    }

    [Theory]
    [InlineData("https://music.apple.com/album/111", "111")]
    [InlineData("https://tidal.com/browse/album/22222", "22222")]
    [InlineData("https://open.qobuz.com/album/abc-def", "abc-def")]
    [InlineData("https://example.com/foo/album/77/bar", "77")] // arbitrary host
    public void ExtractAlbumIdFromUrlPath_RecognizedUrls(string url, string expected)
    {
        Assert.Equal(expected, PrefixedReleaseGuidParser.ExtractAlbumIdFromUrlPath(url));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    [InlineData("not a url")]
    [InlineData("https://example.com/no-album-segment")]
    [InlineData("https://example.com/album")] // album segment but no id after
    public void ExtractAlbumIdFromUrlPath_UnrecognizedReturnsNull(string? url)
    {
        Assert.Null(PrefixedReleaseGuidParser.ExtractAlbumIdFromUrlPath(url));
    }

    [Fact]
    public void ExtractAlbumId_PrefersGuidOverInfoUrl()
    {
        // Plugin passes release.Guid and release.InfoUrl as strings — Common stays
        // free of NzbDrone.Core dependency.
        Assert.Equal("GUID_ID", PrefixedReleaseGuidParser.ExtractAlbumId(
            guid: "applemusic:album:GUID_ID",
            infoUrl: "https://music.apple.com/album/URL_ID",
            prefix: "applemusic"));
    }

    [Fact]
    public void ExtractAlbumId_FallsBackToInfoUrl()
    {
        Assert.Equal("333", PrefixedReleaseGuidParser.ExtractAlbumId(
            guid: "",
            infoUrl: "https://music.apple.com/album/333",
            prefix: "applemusic"));
    }

    [Fact]
    public void ExtractAlbumId_BothNull_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PrefixedReleaseGuidParser.ExtractAlbumId(null, null, "applemusic"));
    }

    [Fact]
    public void ExtractAlbumId_NeitherYieldsId_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, PrefixedReleaseGuidParser.ExtractAlbumId("garbage", "garbage", "applemusic"));
    }
}
