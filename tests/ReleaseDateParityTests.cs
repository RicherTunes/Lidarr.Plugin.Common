using System;
using System.Collections.Generic;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.TestKit.Builders;
using Lidarr.Plugin.Common.TestKit.Validation;
using Xunit;
using Xunit.Abstractions;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// Tests for ReleaseDate parity utilities and characterization helpers.
/// Tier-2 tests: log observations, non-failing unless core contract violated.
/// </summary>
public class ReleaseDateParityTests
{
    private readonly ITestOutputHelper _output;

    public ReleaseDateParityTests(ITestOutputHelper output)
    {
        _output = output;
    }

    [Fact]
    public void NormalizeToDateOnly_WithTimeComponent_StripsTime()
    {
        // Arrange
        var dateWithTime = new DateTime(2024, 1, 15, 14, 30, 45, 123);

        // Act
        var normalized = TestKit.Validation.ReleaseDateParityTests.NormalizeToDateOnly(dateWithTime);

        // Assert
        Assert.NotNull(normalized);
        Assert.Equal(new DateTime(2024, 1, 15), normalized.Value);
        Assert.Equal(TimeSpan.Zero, normalized.Value.TimeOfDay);
    }

    [Fact]
    public void NormalizeToDateOnly_WithNull_ReturnsNull()
    {
        // Act
        var normalized = TestKit.Validation.ReleaseDateParityTests.NormalizeToDateOnly(null);

        // Assert
        Assert.Null(normalized);
    }

    [Fact]
    public void NormalizeToDateOnly_AlreadyDateOnly_PreservesDate()
    {
        // Arrange
        var dateOnly = new DateTime(2024, 1, 15);

        // Act
        var normalized = TestKit.Validation.ReleaseDateParityTests.NormalizeToDateOnly(dateOnly);

        // Assert
        Assert.Equal(dateOnly, normalized);
    }

    [Fact]
    public void AreDatesEqual_SameDateDifferentTime_ReturnsTrue()
    {
        // Arrange
        var date1 = new DateTime(2024, 1, 15, 10, 0, 0);
        var date2 = new DateTime(2024, 1, 15, 23, 59, 59);

        // Act
        var areEqual = TestKit.Validation.ReleaseDateParityTests.AreDatesEqual(date1, date2);

        // Assert
        Assert.True(areEqual);
    }

    [Fact]
    public void AreDatesEqual_DifferentDates_ReturnsFalse()
    {
        // Arrange
        var date1 = new DateTime(2024, 1, 15);
        var date2 = new DateTime(2024, 1, 16);

        // Act
        var areEqual = TestKit.Validation.ReleaseDateParityTests.AreDatesEqual(date1, date2);

        // Assert
        Assert.False(areEqual);
    }

    [Fact]
    public void AreDatesEqual_BothNull_ReturnsTrue()
    {
        // Act
        var areEqual = TestKit.Validation.ReleaseDateParityTests.AreDatesEqual(null, null);

        // Assert
        Assert.True(areEqual);
    }

    [Fact]
    public void AreDatesEqual_OneNull_ReturnsFalse()
    {
        // Act & Assert
        Assert.False(TestKit.Validation.ReleaseDateParityTests.AreDatesEqual(DateTime.Now, null));
        Assert.False(TestKit.Validation.ReleaseDateParityTests.AreDatesEqual(null, DateTime.Now));
    }

    [Fact]
    public void CharacterizeReleaseDate_WithTimeComponent_DetectsTimeComponent()
    {
        // Arrange
        var dateWithTime = new DateTime(2024, 1, 15, 14, 30, 45);

        // Act
        var result = TestKit.Validation.ReleaseDateParityTests.CharacterizeReleaseDate(dateWithTime, "TestPlugin");

        // Assert
        Assert.True(result.HasTimeComponent);
        Assert.False(result.IsNullOrMissing);
        Assert.Equal(new DateTime(2024, 1, 15), result.NormalizedValue);
        Assert.Contains("Has time component", result.Notes);

        // Tier-2: Log for characterization
        _output.WriteLine($"Characterization: {result}");
        _output.WriteLine($"Notes: {result.Notes}");
    }

    [Fact]
    public void CharacterizeReleaseDate_DateOnly_NoTimeComponent()
    {
        // Arrange
        var dateOnly = new DateTime(2024, 1, 15);

        // Act
        var result = TestKit.Validation.ReleaseDateParityTests.CharacterizeReleaseDate(dateOnly, "TestPlugin");

        // Assert
        Assert.False(result.HasTimeComponent);
        Assert.False(result.IsNullOrMissing);
        Assert.Contains("Clean date-only", result.Notes);
    }

    [Fact]
    public void CharacterizeReleaseDate_Null_IdentifiesAsNullOrMissing()
    {
        // Act
        var result = TestKit.Validation.ReleaseDateParityTests.CharacterizeReleaseDate(null, "TestPlugin");

        // Assert
        Assert.True(result.IsNullOrMissing);
        Assert.Null(result.NormalizedValue);
    }

    [Fact]
    public void CharacterizeAlbum_WithLegacyMetadata_DetectsLegacyField()
    {
        // Arrange
        var album = new StreamingAlbumBuilder()
            .WithTitle("Test Album")
            .WithReleaseDate(new DateTime(2024, 1, 15))
            .WithMetadata("release_date", "2024-01-15")
            .Build();

        // Act
        var result = TestKit.Validation.ReleaseDateParityTests.CharacterizeAlbum(album, "TestPlugin");

        // Assert
        Assert.True(result.HasLegacyMetadataField);
        Assert.Equal("2024-01-15", result.LegacyMetadataValue);
        Assert.Contains("Legacy Metadata['release_date']", result.Notes);

        // Tier-2: Log for characterization
        _output.WriteLine($"Album characterization: {result}");
        _output.WriteLine($"Notes: {result.Notes}");
    }

    [Fact]
    public void CharacterizeAlbum_NullAlbum_HandlesGracefully()
    {
        // Act
        var result = TestKit.Validation.ReleaseDateParityTests.CharacterizeAlbum(null, "TestPlugin");

        // Assert
        Assert.True(result.IsNullOrMissing);
        Assert.Contains("Album is null", result.Notes);
    }

    [Fact]
    public void CreateParityReport_AllPluginsAgree_NoParityIssue()
    {
        // Arrange
        var pluginResults = new[]
        {
            ("Qobuzarr", (DateTime?)new DateTime(2024, 1, 15)),
            ("Tidalarr", (DateTime?)new DateTime(2024, 1, 15)),
        };

        // Act
        var report = TestKit.Validation.ReleaseDateParityTests.CreateParityReport(
            "album-123",
            "Test Album",
            pluginResults);

        // Assert
        Assert.False(report.HasParityIssue);
        Assert.Equal(new DateTime(2024, 1, 15), report.NormalizedConsensus);

        // Tier-2: Log full report
        _output.WriteLine(report.ToLogSummary());
    }

    [Fact]
    public void CreateParityReport_PluginsDisagree_ReportsParityIssue()
    {
        // Arrange
        var pluginResults = new[]
        {
            ("Qobuzarr", (DateTime?)new DateTime(2024, 1, 15)),
            ("Tidalarr", (DateTime?)new DateTime(2024, 1, 16)), // Different date
        };

        // Act
        var report = TestKit.Validation.ReleaseDateParityTests.CreateParityReport(
            "album-123",
            "Test Album",
            pluginResults);

        // Assert
        Assert.True(report.HasParityIssue);

        // Tier-2: Log mismatch for investigation
        _output.WriteLine("PARITY MISMATCH DETECTED:");
        _output.WriteLine(report.ToLogSummary());
    }

    [Fact]
    public void CreateParityReport_TimeComponentDifferences_StillMatches()
    {
        // Arrange - Same date, different times should normalize to match
        var pluginResults = new[]
        {
            ("Qobuzarr", (DateTime?)new DateTime(2024, 1, 15, 0, 0, 0)),
            ("Tidalarr", (DateTime?)new DateTime(2024, 1, 15, 23, 59, 59)),
        };

        // Act
        var report = TestKit.Validation.ReleaseDateParityTests.CreateParityReport(
            "album-123",
            "Test Album",
            pluginResults);

        // Assert - Should NOT be a parity issue after normalization
        Assert.False(report.HasParityIssue);
    }

    [Theory]
    [MemberData(nameof(GetKnownAlbums))]
    public void KnownAlbumReleaseDates_AreValid(string albumName, string artist, DateTime expectedDate)
    {
        // Tier-2: Characterization test - document known release dates
        _output.WriteLine($"Known Album: {artist} - {albumName}");
        _output.WriteLine($"  Expected Release Date: {expectedDate:yyyy-MM-dd}");

        // Basic sanity: date should be in the past and reasonable
        Assert.True(expectedDate < DateTime.Now, $"{albumName} release date should be in the past");
        Assert.True(expectedDate.Year >= 1950, $"{albumName} release date year should be >= 1950");
    }

    public static IEnumerable<object[]> GetKnownAlbums()
    {
        foreach (var (albumName, artist, releaseDate) in KnownAlbumReleaseDates.GetAll())
        {
            yield return new object[] { albumName, artist, releaseDate };
        }
    }

    [Fact]
    public void ParityReport_ToLogSummary_FormatsCorrectly()
    {
        // Arrange
        var pluginResults = new[]
        {
            ("Qobuzarr", (DateTime?)new DateTime(2024, 1, 15, 10, 30, 0)),
            ("Tidalarr", (DateTime?)new DateTime(2024, 1, 15)),
        };

        var report = TestKit.Validation.ReleaseDateParityTests.CreateParityReport(
            "album-123",
            "Random Access Memories",
            pluginResults);

        // Act
        var summary = report.ToLogSummary();

        // Assert
        Assert.Contains("ReleaseDate Parity Report", summary);
        Assert.Contains("Random Access Memories", summary);
        Assert.Contains("Qobuzarr", summary);
        Assert.Contains("Tidalarr", summary);

        // Output for manual review
        _output.WriteLine("=== Full Report Output ===");
        _output.WriteLine(summary);
    }
}
