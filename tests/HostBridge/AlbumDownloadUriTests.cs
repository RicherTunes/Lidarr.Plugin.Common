using System;
using Lidarr.Plugin.Common.HostBridge;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.HostBridge;

/// <summary>
/// Tests for <see cref="AlbumDownloadUri"/>. Sibling helper of
/// <see cref="PlaceholderSearchUri"/> covering the album-download URL round-trip.
/// </summary>
public class AlbumDownloadUriTests
{
    // -------------------------------------------------- //
    // Build
    // -------------------------------------------------- //

    [Fact]
    public void Build_WithoutQuality_ReturnsBareUri()
    {
        Assert.Equal("qobuz://album/12345", AlbumDownloadUri.Build("qobuz", "12345"));
    }

    [Fact]
    public void Build_WithQuality_AppendsQueryParameter()
    {
        Assert.Equal("qobuz://album/12345?quality=27", AlbumDownloadUri.Build("qobuz", "12345", "27"));
    }

    [Fact]
    public void Build_EmptyOrWhitespaceQuality_TreatedAsAbsent()
    {
        Assert.Equal("qobuz://album/12345", AlbumDownloadUri.Build("qobuz", "12345", ""));
        Assert.Equal("qobuz://album/12345", AlbumDownloadUri.Build("qobuz", "12345", "   "));
        Assert.Equal("qobuz://album/12345", AlbumDownloadUri.Build("qobuz", "12345", null));
    }

    [Fact]
    public void Build_UrlEncodesAlbumIdAndQuality()
    {
        // Album IDs are typically opaque tokens but apple uses dotted ids and tidal
        // sometimes uses prefixed forms. Encoding makes the round-trip stable.
        var built = AlbumDownloadUri.Build("apple", "ab/cd?special&value", "lossless quality");
        Assert.StartsWith("apple://album/", built);
        Assert.Contains("ab%2Fcd", built);              // forward-slash encoded
        Assert.Contains("?quality=", built);
        Assert.Contains("lossless%20quality", built);   // space encoded
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_EmptyScheme_Throws(string? scheme)
    {
        Assert.Throws<ArgumentException>(() => AlbumDownloadUri.Build(scheme!, "12345"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_EmptyAlbumId_Throws(string? albumId)
    {
        Assert.Throws<ArgumentException>(() => AlbumDownloadUri.Build("qobuz", albumId!));
    }

    // -------------------------------------------------- //
    // TryExtractAlbumId — new format
    // -------------------------------------------------- //

    [Fact]
    public void TryExtractAlbumId_BareNewFormat_ReturnsId()
    {
        Assert.True(AlbumDownloadUri.TryExtractAlbumId("qobuz://album/12345", "qobuz", out var id));
        Assert.Equal("12345", id);
    }

    [Fact]
    public void TryExtractAlbumId_NewFormatWithQuality_ReturnsId()
    {
        Assert.True(AlbumDownloadUri.TryExtractAlbumId("qobuz://album/12345?quality=27", "qobuz", out var id));
        Assert.Equal("12345", id);
    }

    [Fact]
    public void TryExtractAlbumId_NewFormatWithMultipleQueryParams_ReturnsId()
    {
        // Quality may be followed by other params (e.g. &edition=...). Strip all of them.
        Assert.True(AlbumDownloadUri.TryExtractAlbumId("qobuz://album/12345?quality=27&edition=deluxe", "qobuz", out var id));
        Assert.Equal("12345", id);
    }

    [Fact]
    public void TryExtractAlbumId_UrlEncodedId_Decoded()
    {
        Assert.True(AlbumDownloadUri.TryExtractAlbumId("apple://album/ab%2Fcd", "apple", out var id));
        Assert.Equal("ab/cd", id);
    }

    // -------------------------------------------------- //
    // TryExtractAlbumId — legacy format
    // -------------------------------------------------- //

    [Fact]
    public void TryExtractAlbumId_LegacyPathSegmentQuality_ReturnsId()
    {
        // qobuz://album/{id}/{quality} — the path-segment quality is the format used by
        // some in-flight downloads queued before the migration to ?quality=.
        Assert.True(AlbumDownloadUri.TryExtractAlbumId("qobuz://album/12345/27", "qobuz", out var id));
        Assert.Equal("12345", id);
    }

    // -------------------------------------------------- //
    // TryExtractAlbumId — rejections
    // -------------------------------------------------- //

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryExtractAlbumId_EmptyUrl_ReturnsFalse(string? url)
    {
        Assert.False(AlbumDownloadUri.TryExtractAlbumId(url, "qobuz", out var id));
        Assert.Equal(string.Empty, id);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void TryExtractAlbumId_EmptyScheme_ReturnsFalse(string? scheme)
    {
        Assert.False(AlbumDownloadUri.TryExtractAlbumId("qobuz://album/12345", scheme!, out _));
    }

    [Fact]
    public void TryExtractAlbumId_WrongScheme_ReturnsFalse()
    {
        Assert.False(AlbumDownloadUri.TryExtractAlbumId("tidal://album/12345", "qobuz", out _));
    }

    [Fact]
    public void TryExtractAlbumId_HttpsUrl_ReturnsFalse()
    {
        Assert.False(AlbumDownloadUri.TryExtractAlbumId("https://api.qobuz.com/album/12345", "qobuz", out _));
    }

    [Fact]
    public void TryExtractAlbumId_NoAlbumSegment_ReturnsFalse()
    {
        Assert.False(AlbumDownloadUri.TryExtractAlbumId("qobuz://track/12345", "qobuz", out _));
    }

    [Fact]
    public void TryExtractAlbumId_EmptyIdAfterPrefix_ReturnsFalse()
    {
        Assert.False(AlbumDownloadUri.TryExtractAlbumId("qobuz://album/", "qobuz", out _));
        Assert.False(AlbumDownloadUri.TryExtractAlbumId("qobuz://album/?quality=27", "qobuz", out _));
    }

    // -------------------------------------------------- //
    // Round-trip
    // -------------------------------------------------- //

    [Theory]
    [InlineData("qobuz", "123456789", null)]
    [InlineData("qobuz", "123456789", "27")]
    [InlineData("apple", "1234567890.abc", "lossless")]
    [InlineData("tidal", "123456789", "HI_RES_LOSSLESS")]
    public void RoundTrip_BuildThenExtract_RecoversId(string scheme, string id, string? quality)
    {
        var built = AlbumDownloadUri.Build(scheme, id, quality);
        Assert.True(AlbumDownloadUri.TryExtractAlbumId(built, scheme, out var recovered));
        Assert.Equal(id, recovered);
    }
}
