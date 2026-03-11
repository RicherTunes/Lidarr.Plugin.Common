using System;
using System.Collections.Generic;
using System.Linq;
using Lidarr.Plugin.Common.Services.Validation;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Validation
{
    [Trait("Category", "Unit")]
    public class DataValidationServiceTests
    {
        private readonly DataValidationService _service;

        public DataValidationServiceTests()
        {
            _service = new DataValidationService(NullLogger<DataValidationService>.Instance);
        }

        #region Track Validation Tests

        [Fact]
        public void ValidateTrackData_NullTrack_ReturnsFailure()
        {
            // Arrange
            TestTrack? track = null;

            // Act
            var result = _service.ValidateTrackData(track!, t => t.Title!, t => t.Artist!);

            // Assert
            Assert.False(result.IsValid);
            Assert.Equal("Track is null", result.ErrorMessage);
        }

        [Fact]
        public void ValidateTrackData_EmptyTitle_ReturnsFailure()
        {
            // Arrange
            var track = new TestTrack { Title = "", Artist = "Artist" };

            // Act
            var result = _service.ValidateTrackData(track, t => t.Title!, t => t.Artist!);

            // Assert
            Assert.False(result.IsValid);
            Assert.Equal("Track title is null or empty", result.ErrorMessage);
        }

        [Fact]
        public void ValidateTrackData_WhitespaceTitle_ReturnsFailure()
        {
            // Arrange
            var track = new TestTrack { Title = "   ", Artist = "Artist" };

            // Act
            var result = _service.ValidateTrackData(track, t => t.Title!, t => t.Artist!);

            // Assert
            Assert.False(result.IsValid);
            Assert.Equal("Track title is null or empty", result.ErrorMessage);
        }

        [Fact]
        public void ValidateTrackData_NullTitle_ReturnsFailure()
        {
            // Arrange
            var track = new TestTrack { Title = null!, Artist = "Artist" };

            // Act
            var result = _service.ValidateTrackData(track, t => t.Title!, t => t.Artist!);

            // Assert
            Assert.False(result.IsValid);
            Assert.Equal("Track title is null or empty", result.ErrorMessage);
        }

        [Fact]
        public void ValidateTrackData_EmptyArtist_ReturnsFailure()
        {
            // Arrange
            var track = new TestTrack { Title = "Title", Artist = "" };

            // Act
            var result = _service.ValidateTrackData(track, t => t.Title!, t => t.Artist!);

            // Assert
            Assert.False(result.IsValid);
            Assert.Equal("Track artist is null or empty", result.ErrorMessage);
        }

        [Fact]
        public void ValidateTrackData_NullArtist_ReturnsFailure()
        {
            // Arrange
            var track = new TestTrack { Title = "Title", Artist = null! };

            // Act
            var result = _service.ValidateTrackData(track, t => t.Title!, t => t.Artist!);

            // Assert
            Assert.False(result.IsValid);
            Assert.Equal("Track artist is null or empty", result.ErrorMessage);
        }

        [Fact]
        public void ValidateTrackData_ValidTrack_ReturnsSuccess()
        {
            // Arrange
            var track = new TestTrack { Title = "Song Title", Artist = "Artist Name" };

            // Act
            var result = _service.ValidateTrackData(track, t => t.Title!, t => t.Artist!);

            // Assert
            Assert.True(result.IsValid);
            Assert.Equal(track, result.Data);
        }

        [Fact]
        public void ValidateTrackData_VeryLongTitle_LogsWarning_ReturnsSuccess()
        {
            // Arrange
            var track = new TestTrack
            {
                Title = new string('A', 501),
                Artist = "Artist"
            };

            // Act
            var result = _service.ValidateTrackData(track, t => t.Title!, t => t.Artist!);

            // Assert
            Assert.True(result.IsValid);
            Assert.Equal(track, result.Data);
        }

        [Fact]
        public void ValidateTrackData_VeryLongArtist_LogsWarning_ReturnsSuccess()
        {
            // Arrange
            var track = new TestTrack
            {
                Title = "Title",
                Artist = new string('B', 201)
            };

            // Act
            var result = _service.ValidateTrackData(track, t => t.Title!, t => t.Artist!);

            // Assert
            Assert.True(result.IsValid);
            Assert.Equal(track, result.Data);
        }

        #endregion

        #region Filename Sanitization Tests

        [Fact]
        public void SanitizeFileName_ValidName_ReturnsUnchanged()
        {
            // Arrange
            var fileName = "normal_file.mp3";

            // Act
            var result = _service.SanitizeFileName(fileName);

            // Assert
            Assert.Equal("normal_file.mp3", result);
        }

        [Fact]
        public void SanitizeFileName_WithInvalidChars_RemovesThem()
        {
            // Arrange
            var fileName = "file<>:\"|?*.mp3";

            // Act
            var result = _service.SanitizeFileName(fileName);

            // Assert
            Assert.DoesNotContain("<", result);
            Assert.DoesNotContain(">", result);
            Assert.DoesNotContain(":", result);
            Assert.DoesNotContain("\"", result);
            Assert.DoesNotContain("|", result);
            Assert.DoesNotContain("?", result);
            Assert.DoesNotContain("*", result);
        }

        [Fact]
        public void SanitizeFileName_TooLong_TruncatesWithEllipsis()
        {
            // Arrange
            var fileName = new string('A', 300) + ".mp3";

            // Act
            var result = _service.SanitizeFileName(fileName);

            // Assert
            Assert.True(result.Length <= 255);
            Assert.EndsWith("...", result);
        }

        [Fact]
        public void SanitizeFileName_Null_ReturnsUnknown()
        {
            // Arrange
            string? fileName = null;

            // Act
            var result = _service.SanitizeFileName(fileName!);

            // Assert
            Assert.NotNull(result);
            Assert.Equal("Unknown", result);
        }

        [Fact]
        public void SanitizeFileName_EmptyString_HandlesGracefully()
        {
            // Arrange
            var fileName = "";

            // Act
            var result = _service.SanitizeFileName(fileName);

            // Assert
            Assert.NotNull(result);
        }

        #endregion

        #region Path Validation Tests

        [Fact]
        public void ValidateFilePath_ValidInput_ReturnsValidResult()
        {
            // Arrange
            var basePath = @"C:\Music";
            var fileName = "song.mp3";

            // Act
            var result = _service.ValidateFilePath(basePath, fileName);

            // Assert
            Assert.True(result.IsValid);
            Assert.NotNull(result.SanitizedPath);
            Assert.Contains("song.mp3", result.SanitizedPath);
        }

        [Fact]
        public void ValidateFilePath_LongPath_TruncatesFilename()
        {
            // Arrange
            var basePath = new string('A', 250); // Long base path
            var fileName = new string('B', 200) + ".mp3";

            // Act
            var result = _service.ValidateFilePath(basePath, fileName);

            // Assert
            Assert.True(result.IsValid);
            Assert.NotNull(result.SanitizedPath);
        }

        [Fact]
        public void ValidateFilePath_InvalidCharacters_Sanitizes()
        {
            // Arrange
            var basePath = @"C:\Music";
            var fileName = "song<>file.mp3";

            // Act
            var result = _service.ValidateFilePath(basePath, fileName);

            // Assert
            Assert.True(result.IsValid);
            Assert.DoesNotContain("<>", result.SanitizedPath);
        }

        [Fact]
        public void ValidateFilePath_ExceptionThrown_ReturnsInvalidResult()
        {
            // Arrange
            var basePath = (string?)null;
            var fileName = "test.mp3";

            // Act
            var result = _service.ValidateFilePath(basePath!, fileName);

            // Assert
            Assert.False(result.IsValid);
            Assert.NotNull(result.ErrorMessage);
            Assert.Equal("fallback_file.mp3", result.SanitizedFileName);
        }

        #endregion

        #region Duplicate Detection Tests

        [Fact]
        public void DetectDuplicates_EmptyArray_ReturnsNoDuplicates()
        {
            // Arrange
            var tracks = Array.Empty<TestTrack>();

            // Act
            var result = _service.DetectDuplicates(tracks, t => t.Title!, t => t.Artist!, t => t.Duration);

            // Assert
            Assert.False(result.HasDuplicates);
            Assert.Equal(0, result.DuplicateCount);
            Assert.Empty(result.RecommendedTracks);
        }

        [Fact]
        public void DetectDuplicates_SingleTrack_ReturnsNoDuplicates()
        {
            // Arrange
            var tracks = new[] { new TestTrack { Title = "Song", Artist = "Artist" } };

            // Act
            var result = _service.DetectDuplicates(tracks, t => t.Title!, t => t.Artist!, t => t.Duration);

            // Assert
            Assert.False(result.HasDuplicates);
            Assert.Equal(0, result.DuplicateCount);
            Assert.Single(result.RecommendedTracks);
        }

        [Fact]
        public void DetectDuplicates_UniqueTracks_ReturnsNoDuplicates()
        {
            // Arrange
            var tracks = new[]
            {
                new TestTrack { Title = "Song1", Artist = "Artist1" },
                new TestTrack { Title = "Song2", Artist = "Artist2" },
                new TestTrack { Title = "Song3", Artist = "Artist3" }
            };

            // Act
            var result = _service.DetectDuplicates(tracks, t => t.Title!, t => t.Artist!, t => t.Duration);

            // Assert
            Assert.False(result.HasDuplicates);
            Assert.Equal(0, result.DuplicateCount);
            Assert.Equal(3, result.RecommendedTracks.Count);
        }

        [Fact]
        public void DetectDuplicates_ExactDuplicates_DetectsCorrectly()
        {
            // Arrange
            var tracks = new[]
            {
                new TestTrack { Title = "Song", Artist = "Artist", Duration = TimeSpan.FromMinutes(3) },
                new TestTrack { Title = "Song", Artist = "Artist", Duration = TimeSpan.FromMinutes(3) },
                new TestTrack { Title = "Other", Artist = "Artist", Duration = TimeSpan.FromMinutes(4) }
            };

            // Act
            var result = _service.DetectDuplicates(tracks, t => t.Title!, t => t.Artist!, t => t.Duration);

            // Assert
            Assert.True(result.HasDuplicates);
            Assert.Equal(1, result.DuplicateCount);
            Assert.Equal(2, result.RecommendedTracks.Count); // First of duplicates + unique track
        }

        [Fact]
        public void DetectDuplicates_CaseInsensitive_DetectsDuplicates()
        {
            // Arrange
            var tracks = new[]
            {
                new TestTrack { Title = "Song", Artist = "Artist" },
                new TestTrack { Title = "song", Artist = "artist" },
                new TestTrack { Title = "SONG", Artist = "ARTIST" }
            };

            // Act
            var result = _service.DetectDuplicates(tracks, t => t.Title!, t => t.Artist!, t => t.Duration);

            // Assert
            Assert.True(result.HasDuplicates);
            Assert.Equal(2, result.DuplicateCount);
            Assert.Single(result.RecommendedTracks);
        }

        [Fact]
        public void DetectDuplicates_IgnoresSpacesAndSpecialChars_DetectsDuplicates()
        {
            // Arrange
            var tracks = new[]
            {
                new TestTrack { Title = "Song Title", Artist = "Artist Name" },
                new TestTrack { Title = "SongTitle", Artist = "ArtistName" },
                new TestTrack { Title = "Song-Title", Artist = "Artist_Name" },
                new TestTrack { Title = "Song_Title", Artist = "Artist_Name" }
            };

            // Act
            var result = _service.DetectDuplicates(tracks, t => t.Title!, t => t.Artist!, t => t.Duration);

            // Assert
            Assert.True(result.HasDuplicates);
            Assert.Equal(3, result.DuplicateCount);
            Assert.Single(result.RecommendedTracks);
        }

        [Fact]
        public void DetectDuplicates_KeepsFirstOfEachDuplicateGroup()
        {
            // Arrange
            var track1 = new TestTrack { Title = "Song", Artist = "Artist", Id = 1 };
            var track2 = new TestTrack { Title = "Song", Artist = "Artist", Id = 2 };
            var track3 = new TestTrack { Title = "Song", Artist = "Artist", Id = 3 };
            var tracks = new[] { track1, track2, track3 };

            // Act
            var result = _service.DetectDuplicates(tracks, t => t.Title!, t => t.Artist!, t => t.Duration);

            // Assert
            Assert.Equal(2, result.DuplicateCount);
            Assert.Single(result.RecommendedTracks);
            Assert.Equal(1, result.RecommendedTracks[0].Id); // First track kept
        }

        [Fact]
        public void DetectDuplicates_MultipleGroups_DetectsAll()
        {
            // Arrange
            var tracks = new[]
            {
                new TestTrack { Title = "Song1", Artist = "Artist1", Id = 1 },
                new TestTrack { Title = "Song1", Artist = "Artist1", Id = 2 },
                new TestTrack { Title = "Song2", Artist = "Artist2", Id = 3 },
                new TestTrack { Title = "Song2", Artist = "Artist2", Id = 4 },
                new TestTrack { Title = "Song2", Artist = "Artist2", Id = 5 },
                new TestTrack { Title = "Song3", Artist = "Artist3", Id = 6 }
            };

            // Act
            var result = _service.DetectDuplicates(tracks, t => t.Title!, t => t.Artist!, t => t.Duration);

            // Assert
            Assert.True(result.HasDuplicates);
            Assert.Equal(3, result.DuplicateCount); // 1 + 2 duplicates
            Assert.Equal(3, result.RecommendedTracks.Count); // One from each group
        }

        #endregion

        #region Track Sequence Validation Tests

        [Fact]
        public void ValidateTrackSequence_EmptyArray_ReturnsInvalid()
        {
            // Arrange
            var tracks = Array.Empty<TestTrack>();

            // Act
            var result = _service.ValidateTrackSequence(tracks, t => t.TrackNumber);

            // Assert
            Assert.False(result.IsValid);
            Assert.Equal("No valid track numbers found", result.Issue);
        }

        [Fact]
        public void ValidateTrackSequence_AllZeroOrNegative_ReturnsInvalid()
        {
            // Arrange
            var tracks = new[]
            {
                new TestTrack { TrackNumber = 0 },
                new TestTrack { TrackNumber = -1 },
                new TestTrack { TrackNumber = -5 }
            };

            // Act
            var result = _service.ValidateTrackSequence(tracks, t => t.TrackNumber);

            // Assert
            Assert.False(result.IsValid);
            Assert.Equal("No valid track numbers found", result.Issue);
        }

        [Fact]
        public void ValidateTrackSequence_SequentialNumbers_ReturnsValid()
        {
            // Arrange
            var tracks = new[]
            {
                new TestTrack { TrackNumber = 1 },
                new TestTrack { TrackNumber = 2 },
                new TestTrack { TrackNumber = 3 },
                new TestTrack { TrackNumber = 4 }
            };

            // Act
            var result = _service.ValidateTrackSequence(tracks, t => t.TrackNumber);

            // Assert
            Assert.True(result.IsValid);
            Assert.Empty(result.MissingTrackNumbers);
        }

        [Fact]
        public void ValidateTrackSequence_WithGaps_ReturnsInvalid()
        {
            // Arrange
            var tracks = new[]
            {
                new TestTrack { TrackNumber = 1 },
                new TestTrack { TrackNumber = 2 },
                new TestTrack { TrackNumber = 5 }, // Gap: 3, 4 missing
                new TestTrack { TrackNumber = 6 }
            };

            // Act
            var result = _service.ValidateTrackSequence(tracks, t => t.TrackNumber);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(3, result.MissingTrackNumbers);
            Assert.Contains(4, result.MissingTrackNumbers);
        }

        [Fact]
        public void ValidateTrackSequence_Unordered_ReturnsValid()
        {
            // Arrange
            var tracks = new[]
            {
                new TestTrack { TrackNumber = 3 },
                new TestTrack { TrackNumber = 1 },
                new TestTrack { TrackNumber = 2 }
            };

            // Act
            var result = _service.ValidateTrackSequence(tracks, t => t.TrackNumber);

            // Assert
            Assert.True(result.IsValid);
            Assert.Empty(result.MissingTrackNumbers);
        }

        [Fact]
        public void ValidateTrackSequence_IgnoresZeroAndNegative_DetectsGaps()
        {
            // Arrange
            var tracks = new[]
            {
                new TestTrack { TrackNumber = 0 },
                new TestTrack { TrackNumber = 1 },
                new TestTrack { TrackNumber = 2 },
                new TestTrack { TrackNumber = -1 },
                new TestTrack { TrackNumber = 5 }, // Gap: 3, 4 missing
                new TestTrack { TrackNumber = 6 }
            };

            // Act
            var result = _service.ValidateTrackSequence(tracks, t => t.TrackNumber);

            // Assert
            Assert.False(result.IsValid);
            Assert.Contains(3, result.MissingTrackNumbers);
            Assert.Contains(4, result.MissingTrackNumbers);
        }

        [Fact]
        public void ValidateTrackSequence_SingleTrack_ReturnsValid()
        {
            // Arrange
            var tracks = new[] { new TestTrack { TrackNumber = 1 } };

            // Act
            var result = _service.ValidateTrackSequence(tracks, t => t.TrackNumber);

            // Assert
            Assert.True(result.IsValid);
        }

        [Fact]
        public void ValidateTrackSequence_LargeGap_DetectsAllMissing()
        {
            // Arrange
            var tracks = new[]
            {
                new TestTrack { TrackNumber = 1 },
                new TestTrack { TrackNumber = 10 } // Gap: 2-9 missing
            };

            // Act
            var result = _service.ValidateTrackSequence(tracks, t => t.TrackNumber);

            // Assert
            Assert.False(result.IsValid);
            Assert.Equal(8, result.MissingTrackNumbers.Count);
            Assert.Contains(2, result.MissingTrackNumbers);
            Assert.Contains(9, result.MissingTrackNumbers);
        }

        #endregion

        #region Test Helper Class

        private class TestTrack
        {
            public string? Title { get; set; }
            public string? Artist { get; set; }
            public TimeSpan? Duration { get; set; }
            public int TrackNumber { get; set; }
            public int Id { get; set; }
        }

        #endregion
    }
}
