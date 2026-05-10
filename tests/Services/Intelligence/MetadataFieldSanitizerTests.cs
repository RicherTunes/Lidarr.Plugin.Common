using Lidarr.Plugin.Common.Services.Intelligence;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Intelligence;

[Trait("Category", "Unit")]
public class MetadataFieldSanitizerTests
{
    [Theory]
    [InlineData(null, "")]
    [InlineData("", "")]
    [InlineData("   ", "")]
    public void SanitizeVersion_ReturnsEmpty_ForNullOrWhitespace(string? input, string expected)
    {
        Assert.Equal(expected, MetadataFieldSanitizer.SanitizeVersion(input));
    }

    [Theory]
    [InlineData("Deluxe Edition", "Deluxe Edition")]
    [InlineData("Remastered 2009", "Remastered 2009")]
    [InlineData("(Anniversary Edition)", "(Anniversary Edition)")]
    public void SanitizeVersion_PassesThroughLegitimateVersions(string input, string expected)
    {
        Assert.Equal(expected, MetadataFieldSanitizer.SanitizeVersion(input));
    }

    [Fact]
    public void SanitizeVersion_StripsControlCharacters()
    {
        var input = "DeluxeEdition";
        var result = MetadataFieldSanitizer.SanitizeVersion(input);
        Assert.Equal("DeluxeEdition", result);
    }

    [Fact]
    public void SanitizeVersion_StripsZeroWidthCharacters()
    {
        var input = "Deluxe​Edition﻿";
        var result = MetadataFieldSanitizer.SanitizeVersion(input);
        Assert.Equal("DeluxeEdition", result);
    }

    [Fact]
    public void SanitizeVersion_StripsScriptTags()
    {
        var input = "Deluxe<script>alert(1)</script>Edition";
        var result = MetadataFieldSanitizer.SanitizeVersion(input);
        Assert.DoesNotContain("script", result);
        Assert.DoesNotContain("alert", result);
    }

    [Fact]
    public void SanitizeVersion_ReplacesPathSeparators()
    {
        var input = "Edition/2020\\Remaster";
        var result = MetadataFieldSanitizer.SanitizeVersion(input);
        Assert.DoesNotContain("/", result);
        Assert.DoesNotContain("\\", result);
    }

    [Fact]
    public void SanitizeVersion_ReplacesPathTraversal()
    {
        var input = "..Edition";
        var result = MetadataFieldSanitizer.SanitizeVersion(input);
        Assert.DoesNotContain("..", result);
    }

    [Fact]
    public void SanitizeVersion_TruncatesToMaxLength()
    {
        var longInput = new string('a', MetadataFieldSanitizer.DefaultMaxVersionLength + 50);
        var result = MetadataFieldSanitizer.SanitizeVersion(longInput);
        Assert.True(result.Length <= MetadataFieldSanitizer.DefaultMaxVersionLength);
    }

    [Theory]
    [InlineData(null, "Unknown Artist")]
    [InlineData("", "Unknown Artist")]
    [InlineData("   ", "Unknown Artist")]
    public void SanitizeArtistName_DefaultsToUnknownArtist(string? input, string expected)
    {
        Assert.Equal(expected, MetadataFieldSanitizer.SanitizeArtistName(input));
    }

    [Fact]
    public void SanitizeArtistName_PreservesUnicodeCharacters()
    {
        var input = "Björk";
        Assert.Equal("Björk", MetadataFieldSanitizer.SanitizeArtistName(input));
    }

    [Fact]
    public void SanitizeArtistName_StripsHtmlTags()
    {
        var input = "Some <b>Artist</b>";
        var result = MetadataFieldSanitizer.SanitizeArtistName(input);
        Assert.Equal("Some Artist", result);
    }

    [Theory]
    [InlineData(null, "Unknown Album")]
    [InlineData("", "Unknown Album")]
    public void SanitizeAlbumTitle_DefaultsToUnknownAlbum(string? input, string expected)
    {
        Assert.Equal(expected, MetadataFieldSanitizer.SanitizeAlbumTitle(input));
    }

    [Fact]
    public void SanitizeAlbumTitle_NormalizesMultipleWhitespace()
    {
        var input = "Some   Album    Title";
        var result = MetadataFieldSanitizer.SanitizeAlbumTitle(input);
        Assert.Equal("Some Album Title", result);
    }

    [Fact]
    public void SanitizeAlbumTitle_NormalizesNewlinesToSpaces()
    {
        var input = "Album\r\nName";
        var result = MetadataFieldSanitizer.SanitizeAlbumTitle(input);
        Assert.Equal("Album Name", result);
    }

    [Fact]
    public void HtmlEncode_EscapesAllCriticalCharacters()
    {
        var input = "<script>'\"&";
        var result = MetadataFieldSanitizer.HtmlEncode(input);
        Assert.Equal("&lt;script&gt;&#39;&quot;&amp;", result);
    }

    [Theory]
    [InlineData("normal text", false)]
    [InlineData("../etc/passwd", true)]
    [InlineData("..\\windows\\system32", true)]
    [InlineData("Deluxe Edition", false)]
    public void ContainsPathTraversal_DetectsTraversal(string input, bool expected)
    {
        Assert.Equal(expected, MetadataFieldSanitizer.ContainsPathTraversal(input));
    }

    [Fact]
    public void SanitizeMetadataField_RespectsCustomMaxLength()
    {
        var input = "1234567890";
        var result = MetadataFieldSanitizer.SanitizeMetadataField(input, defaultValue: "default", maxLength: 5);
        Assert.Equal("12345", result);
    }

    [Fact]
    public void SanitizeMetadataField_FallsBackToDefault_WhenAllStripped()
    {
        // Input that becomes empty after sanitization
        var input = "<script></script>";
        var result = MetadataFieldSanitizer.SanitizeMetadataField(input, defaultValue: "fallback");
        Assert.Equal("fallback", result);
    }

    [Fact]
    public void SanitizeTrackTitle_PreservesOrdinaryTitle()
    {
        Assert.Equal("Some Track", MetadataFieldSanitizer.SanitizeTrackTitle("Some Track"));
    }
}
