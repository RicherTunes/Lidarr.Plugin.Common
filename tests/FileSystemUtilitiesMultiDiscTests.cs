using Xunit;
using Lidarr.Plugin.Common.Utilities;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// Parity tests for multi-disc naming and extension normalization.
/// These ensure Tidalarr and Qobuzarr produce identical filenames.
/// </summary>
public class FileSystemUtilitiesMultiDiscTests
{
    [Theory]
    [InlineData(1, 1, 2, "D01T01 - Track Title.flac")]  // Disc 1 of 2-disc album
    [InlineData(1, 2, 2, "D02T01 - Track Title.flac")]  // Disc 2 of 2-disc album
    [InlineData(5, 1, 3, "D01T05 - Track Title.flac")]  // Track 5, disc 1 of 3-disc album
    [InlineData(1, 3, 3, "D03T01 - Track Title.flac")]  // Disc 3 of 3-disc album
    public void MultiDiscAlbum_AllDiscsGetPrefix(int trackNumber, int discNumber, int totalDiscs, string expected)
    {
        // Act
        var result = FileSystemUtilities.CreateTrackFileName(
            title: "Track Title",
            trackNumber: trackNumber,
            extension: "flac",
            discNumber: discNumber,
            totalDiscs: totalDiscs);

        // Assert
        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(1, 1, 1, "01 - Track Title.flac")]  // Single disc album
    [InlineData(5, 1, 1, "05 - Track Title.flac")]  // Track 5 on single disc
    public void SingleDiscAlbum_NoDiscPrefix(int trackNumber, int discNumber, int totalDiscs, string expected)
    {
        var result = FileSystemUtilities.CreateTrackFileName(
            title: "Track Title",
            trackNumber: trackNumber,
            extension: "flac",
            discNumber: discNumber,
            totalDiscs: totalDiscs);

        Assert.Equal(expected, result);
    }

    [Theory]
    [InlineData(".flac", "01 - Title.flac")]      // Leading dot stripped
    [InlineData("..flac", "01 - Title.flac")]     // Multiple leading dots stripped
    [InlineData(" flac ", "01 - Title.flac")]     // Whitespace trimmed
    [InlineData(" .flac ", "01 - Title.flac")]    // Both whitespace and dot
    [InlineData("", "01 - Title.flac")]           // Empty defaults to flac
    [InlineData(null, "01 - Title.flac")]         // Null defaults to flac
    [InlineData("m4a", "01 - Title.m4a")]         // Normal extension unchanged
    public void ExtensionNormalization_HandlesEdgeCases(string? extension, string expected)
    {
        var result = FileSystemUtilities.CreateTrackFileName(
            title: "Title",
            trackNumber: 1,
            extension: extension!,
            discNumber: 1,
            totalDiscs: 1);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void OriginalOverload_StillWorks_BackwardCompatibility()
    {
        // The original 3-parameter overload should still work
        var result = FileSystemUtilities.CreateTrackFileName("My Track", 3, "mp3");
        Assert.Equal("03 - My Track.mp3", result);
    }

    [Fact]
    public void OriginalOverload_DefaultsToFlac()
    {
        // Default extension is flac
        var result = FileSystemUtilities.CreateTrackFileName("My Track", 1);
        Assert.Equal("01 - My Track.flac", result);
    }
}
