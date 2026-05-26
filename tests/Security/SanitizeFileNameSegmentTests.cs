using Lidarr.Plugin.Common.Security;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Security;

/// <summary>
/// Contract tests for <see cref="Sanitize.FileNameSegment"/> — the security-correct
/// successor to <c>FileNameSanitizer.SanitizeFileName</c> for the 11 internal callers in
/// <c>BaseStreamingDownloadClient</c> and <c>StreamingPluginMixins</c>. Pins the behavior
/// these callers actually need (non-null fallback, readable separator-replacement, dot
/// preservation) so Phase 2 deprecation can proceed without altering the resulting paths
/// for any of the four streaming plugins.
/// </summary>
public class SanitizeFileNameSegmentTests
{
    // --- Real bug from FileNameSanitizer Phase 1: dots in the middle of names ---

    [Theory]
    [InlineData("re..master.flac", "re..master.flac")]
    [InlineData("track..remix.mp3", "track..remix.mp3")]
    [InlineData("Symphony No.5..2024.flac", "Symphony No.5..2024.flac")]
    public void FileNameSegment_PreservesDotsEmbeddedInNames(string input, string expected)
    {
        Assert.Equal(expected, Sanitize.FileNameSegment(input));
    }

    [Theory]
    [InlineData("..")]
    [InlineData("...")]
    [InlineData("....")]
    public void FileNameSegment_PureDotSequences_AreNeutralized(string input)
    {
        var result = Sanitize.FileNameSegment(input);

        Assert.NotEqual(input, result);
        Assert.False(string.IsNullOrWhiteSpace(result));
    }

    // --- Caller-side contract: 11 sites all pass `name ?? "Unknown ..."` defaults ---

    [Fact]
    public void FileNameSegment_NullInput_ReturnsDefaultFallback()
    {
        Assert.Equal("Unknown", Sanitize.FileNameSegment(null));
    }

    [Fact]
    public void FileNameSegment_EmptyInput_ReturnsDefaultFallback()
    {
        Assert.Equal("Unknown", Sanitize.FileNameSegment(""));
    }

    [Fact]
    public void FileNameSegment_WhitespaceInput_ReturnsDefaultFallback()
    {
        Assert.Equal("Unknown", Sanitize.FileNameSegment("   "));
    }

    [Fact]
    public void FileNameSegment_RespectsCustomFallback()
    {
        Assert.Equal("Unknown Artist", Sanitize.FileNameSegment(null, fallback: "Unknown Artist"));
        Assert.Equal("Unknown Album", Sanitize.FileNameSegment("  ", fallback: "Unknown Album"));
    }

    // --- Readability: replace separators with space (vs delete) ---

    [Fact]
    public void FileNameSegment_PathSeparators_ReplacedWithSpace()
    {
        // "AC/DC" → "AC DC" not "ACDC" — readability matters when the segment is shown
        // in a UI / disk listing. The replace-with-space contract is inherited from
        // FileNameSanitizer's historical behavior.
        Assert.Equal("AC DC", Sanitize.FileNameSegment("AC/DC"));
        Assert.Equal("AC DC", Sanitize.FileNameSegment("AC\\DC"));
    }

    [Fact]
    public void FileNameSegment_WhitespaceChars_ReplacedWithSpace_ThenCollapsed()
    {
        Assert.Equal("Track Name", Sanitize.FileNameSegment("Track\tName"));
        Assert.Equal("Track Name", Sanitize.FileNameSegment("Track\nName"));
        Assert.Equal("Track Name", Sanitize.FileNameSegment("Track\rName"));
        Assert.Equal("Track Name", Sanitize.FileNameSegment("Track    Name")); // collapse multi-spaces
    }

    // --- Defense-in-depth carryovers ---

    [Fact]
    public void FileNameSegment_ZeroWidthChars_AreStripped()
    {
        // U+200B ZERO WIDTH SPACE; U+FEFF BYTE ORDER MARK. These can appear in
        // streaming-service-supplied metadata and would create filenames that look
        // identical to a benign one but compare unequal — strip (NOT space-replace) so
        // the resulting filename has no invisible content. Matches the historical
        // FileNameSanitizer behavior.
        Assert.Equal("UnknownTrack", Sanitize.FileNameSegment("Unknown​Track"));
        Assert.Equal("Track", Sanitize.FileNameSegment("﻿Track"));
    }

    [Theory]
    [InlineData("CON", "_CON")]
    [InlineData("NUL.txt", "_NUL.txt")]
    [InlineData("LPT1", "_LPT1")]
    [InlineData("aux", "_aux")]
    public void FileNameSegment_WindowsReservedDeviceNames_AreEscaped(string input, string expected)
    {
        // Case-insensitive match; prefix with `_` so a streaming service emitting a
        // band literally named "CON" doesn't break on Windows even when the deployment
        // is Linux (portable behavior).
        Assert.Equal(expected, Sanitize.FileNameSegment(input));
    }

    // --- Non-corruption: arbitrary metadata should pass through largely unchanged ---

    [Theory]
    [InlineData("Pink Floyd", "Pink Floyd")]
    [InlineData("The Dark Side of the Moon", "The Dark Side of the Moon")]
    [InlineData("90's Music", "90's Music")]
    public void FileNameSegment_OrdinaryNames_PassThroughUnchanged(string input, string expected)
    {
        Assert.Equal(expected, Sanitize.FileNameSegment(input));
    }
}
