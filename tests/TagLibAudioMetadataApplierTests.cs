using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Services.Metadata;
using Microsoft.Extensions.Logging;
using TagLib;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// Round-trip tests for TagLibAudioMetadataApplier.
/// Verifies that ISRC and MusicBrainz IDs are correctly written and can be read back.
/// See docs/TRACK_IDENTITY_PARITY.md for field mapping documentation.
/// </summary>
public class TagLibAudioMetadataApplierTests : IDisposable
{
    private readonly string _tempDir;

    public TagLibAudioMetadataApplierTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"TagLibTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Best effort cleanup
        }
    }

    #region MP3 Round-Trip Tests

    [Fact]
    public async Task ApplyAsync_Mp3_WritesIsrc_RoundTrip()
    {
        // Arrange
        var filePath = CreateMinimalMp3();
        var applier = new TagLibAudioMetadataApplier();
        var expectedIsrc = "USRC12345678";
        var track = CreateTrackWithIsrc(expectedIsrc);

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert - read back and verify
        using var file = TagLib.File.Create(filePath);
        var actualIsrc = ReadIsrc(file);
        Assert.Equal(expectedIsrc, actualIsrc);
    }

    [Fact]
    public async Task ApplyAsync_Mp3_WritesMusicBrainzTrackId_RoundTrip()
    {
        // Arrange
        var filePath = CreateMinimalMp3();
        var applier = new TagLibAudioMetadataApplier();
        var expectedMbid = "f27ec8db-af05-4f36-916e-3d57f91ecf5e";
        var track = CreateTrackWithMusicBrainzId(expectedMbid);

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert
        using var file = TagLib.File.Create(filePath);
        Assert.Equal(expectedMbid, file.Tag.MusicBrainzTrackId);
    }

    [Fact]
    public async Task ApplyAsync_Mp3_WritesMusicBrainzReleaseId_RoundTrip()
    {
        // Arrange
        var filePath = CreateMinimalMp3();
        var applier = new TagLibAudioMetadataApplier();
        var expectedReleaseId = "a1b2c3d4-e5f6-7890-abcd-ef1234567890";
        var track = CreateTrackWithAlbumMusicBrainzId(expectedReleaseId);

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert
        using var file = TagLib.File.Create(filePath);
        Assert.Equal(expectedReleaseId, file.Tag.MusicBrainzReleaseId);
    }

    [Fact]
    public async Task ApplyAsync_Mp3_WritesAllIdentifiers_RoundTrip()
    {
        // Arrange
        var filePath = CreateMinimalMp3();
        var applier = new TagLibAudioMetadataApplier();
        var expectedIsrc = "GBAYE0000001";
        var expectedTrackMbid = "12345678-1234-1234-1234-123456789012";
        var expectedAlbumMbid = "abcdefab-cdef-abcd-efab-cdefabcdefab";

        var track = new StreamingTrack
        {
            Title = "Test Track",
            Isrc = expectedIsrc,
            MusicBrainzId = expectedTrackMbid,
            Album = new StreamingAlbum
            {
                Title = "Test Album",
                MusicBrainzId = expectedAlbumMbid
            }
        };

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert
        using var file = TagLib.File.Create(filePath);
        Assert.Equal(expectedIsrc, ReadIsrc(file));
        Assert.Equal(expectedTrackMbid, file.Tag.MusicBrainzTrackId);
        Assert.Equal(expectedAlbumMbid, file.Tag.MusicBrainzReleaseId);
    }

    #endregion

    #region FLAC Round-Trip Tests

    [Fact]
    public async Task ApplyAsync_Flac_WritesIsrc_RoundTrip()
    {
        // Arrange
        var filePath = CreateMinimalFlac();
        var applier = new TagLibAudioMetadataApplier();
        var expectedIsrc = "USRC98765432";
        var track = CreateTrackWithIsrc(expectedIsrc);

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert
        using var file = TagLib.File.Create(filePath);
        var actualIsrc = ReadIsrc(file);
        Assert.Equal(expectedIsrc, actualIsrc);
    }

    [Fact]
    public async Task ApplyAsync_Flac_WritesMusicBrainzTrackId_RoundTrip()
    {
        // Arrange
        var filePath = CreateMinimalFlac();
        var applier = new TagLibAudioMetadataApplier();
        var expectedMbid = "98765432-dcba-4321-fedc-ba9876543210";
        var track = CreateTrackWithMusicBrainzId(expectedMbid);

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert
        using var file = TagLib.File.Create(filePath);
        Assert.Equal(expectedMbid, file.Tag.MusicBrainzTrackId);
    }

    [Fact]
    public async Task ApplyAsync_Flac_WritesAllIdentifiers_RoundTrip()
    {
        // Arrange
        var filePath = CreateMinimalFlac();
        var applier = new TagLibAudioMetadataApplier();
        var expectedIsrc = "FRXXX0000002";
        var expectedTrackMbid = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
        var expectedAlbumMbid = "11111111-2222-3333-4444-555555555555";

        var track = new StreamingTrack
        {
            Title = "FLAC Test Track",
            Isrc = expectedIsrc,
            MusicBrainzId = expectedTrackMbid,
            Album = new StreamingAlbum
            {
                Title = "FLAC Test Album",
                MusicBrainzId = expectedAlbumMbid
            }
        };

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert
        using var file = TagLib.File.Create(filePath);
        Assert.Equal(expectedIsrc, ReadIsrc(file));
        Assert.Equal(expectedTrackMbid, file.Tag.MusicBrainzTrackId);
        Assert.Equal(expectedAlbumMbid, file.Tag.MusicBrainzReleaseId);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public async Task ApplyAsync_EmptyIsrc_DoesNotWriteTag()
    {
        // Arrange
        var filePath = CreateMinimalMp3();
        var applier = new TagLibAudioMetadataApplier();
        var track = new StreamingTrack
        {
            Title = "Test",
            Isrc = string.Empty
        };

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert - ISRC should be null/empty, not crash
        using var file = TagLib.File.Create(filePath);
        var isrc = ReadIsrc(file);
        Assert.True(string.IsNullOrEmpty(isrc));
    }

    [Fact]
    public async Task ApplyAsync_NullMusicBrainzId_DoesNotWriteTag()
    {
        // Arrange
        var filePath = CreateMinimalMp3();
        var applier = new TagLibAudioMetadataApplier();
        var track = new StreamingTrack
        {
            Title = "Test",
            MusicBrainzId = null!
        };

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert
        using var file = TagLib.File.Create(filePath);
        Assert.Null(file.Tag.MusicBrainzTrackId);
    }

    [Fact]
    public async Task ApplyAsync_IsrcWithWhitespaceAndLowercase_NormalizesToUppercase()
    {
        // Arrange
        var filePath = CreateMinimalMp3();
        var applier = new TagLibAudioMetadataApplier();
        var inputIsrc = " usrc17607839 "; // lowercase with whitespace
        var expectedIsrc = "USRC17607839"; // normalized: trimmed + uppercase
        var track = CreateTrackWithIsrc(inputIsrc);

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert - should be normalized to uppercase, trimmed
        using var file = TagLib.File.Create(filePath);
        var actualIsrc = ReadIsrc(file);
        Assert.Equal(expectedIsrc, actualIsrc);
    }

    [Fact]
    public async Task ApplyAsync_MusicBrainzIdWithUppercase_NormalizesToLowercase()
    {
        // Arrange
        var filePath = CreateMinimalMp3();
        var applier = new TagLibAudioMetadataApplier();
        var inputMbid = " F27EC8DB-AF05-4F36-916E-3D57F91ECF5E "; // uppercase with whitespace
        var expectedMbid = "f27ec8db-af05-4f36-916e-3d57f91ecf5e"; // normalized: trimmed + lowercase
        var track = CreateTrackWithMusicBrainzId(inputMbid);

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert - MusicBrainz IDs should be canonical lowercase UUIDs
        using var file = TagLib.File.Create(filePath);
        Assert.Equal(expectedMbid, file.Tag.MusicBrainzTrackId);
    }

    [Fact]
    public async Task ApplyAsync_WhitespaceOnlyIsrc_DoesNotWriteTag()
    {
        // Arrange
        var filePath = CreateMinimalMp3();
        var applier = new TagLibAudioMetadataApplier();
        var track = new StreamingTrack
        {
            Title = "Test",
            Isrc = "   " // whitespace only
        };

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert - whitespace-only ISRC should not be written
        using var file = TagLib.File.Create(filePath);
        var isrc = ReadIsrc(file);
        Assert.True(string.IsNullOrEmpty(isrc));
    }

    [Fact]
    public async Task ApplyAsync_InvalidMusicBrainzId_DoesNotWriteTag()
    {
        // Arrange
        var filePath = CreateMinimalMp3();
        var applier = new TagLibAudioMetadataApplier();
        var track = new StreamingTrack
        {
            Title = "Test",
            MusicBrainzId = "not-a-guid" // invalid UUID format
        };

        // Act
        await applier.ApplyAsync(filePath, track);

        // Assert - invalid MBID should not be written (garbage preservation is not normalization)
        using var file = TagLib.File.Create(filePath);
        Assert.Null(file.Tag.MusicBrainzTrackId);
    }

    [Fact]
    public async Task ApplyAsync_InvalidFile_EmitsWarningAndReturnsSuccess()
    {
        // Arrange
        var invalidFilePath = Path.Combine(_tempDir, "invalid.mp3");
        System.IO.File.WriteAllBytes(invalidFilePath, new byte[] { 0x00, 0x01, 0x02 }); // Invalid MP3

        var logger = new TestLogger();
        var applier = new TagLibAudioMetadataApplier(logger);
        var track = new StreamingTrack { Title = "Test Track" };

        // Act - should complete without throwing
        await applier.ApplyAsync(invalidFilePath, track);

        // Assert - warning should be logged (graceful degradation)
        var entries = logger.Entries.ToArray();
        Assert.Single(entries);
        Assert.Equal(LogLevel.Warning, entries[0].Level);
        Assert.Contains("Test Track", entries[0].Message);
    }

    #endregion

    #region Test Infrastructure

    private sealed class TestLogger : ILogger
    {
        public ConcurrentBag<LogEntry> Entries { get; } = new();

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        }

        public record LogEntry(LogLevel Level, string Message);

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();
            public void Dispose() { }
        }
    }

    #endregion

    #region Helper Methods

    private string CreateMinimalMp3()
    {
        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.mp3");

        // Minimal valid MP3: ID3v2 header + one silent MP3 frame
        // ID3v2.3 header (10 bytes) + minimal frame
        var mp3Bytes = new byte[]
        {
            // ID3v2.3 header
            0x49, 0x44, 0x33, // "ID3"
            0x03, 0x00,       // Version 2.3
            0x00,             // Flags
            0x00, 0x00, 0x00, 0x00, // Size (syncsafe, 0 bytes of tags)

            // MP3 frame header (MPEG Audio Layer 3)
            0xFF, 0xFB,       // Frame sync + MPEG1 Layer3
            0x90,             // 128kbps, 44100Hz
            0x00,             // Padding, private, channel mode, etc.

            // Frame data (silent, minimal)
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            // More padding to make it a valid frame
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        System.IO.File.WriteAllBytes(path, mp3Bytes);
        return path;
    }

    private string CreateMinimalFlac()
    {
        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.flac");

        // Minimal valid FLAC file structure
        // FLAC signature + STREAMINFO block + minimal frame
        var flacBytes = new byte[]
        {
            // FLAC signature
            0x66, 0x4C, 0x61, 0x43, // "fLaC"

            // STREAMINFO metadata block header (last block, type 0, length 34)
            0x80, 0x00, 0x00, 0x22,

            // STREAMINFO block (34 bytes)
            0x00, 0x10, // min block size = 16
            0x00, 0x10, // max block size = 16
            0x00, 0x00, 0x01, // min frame size
            0x00, 0x00, 0x01, // max frame size
            0x0A, 0xC4, 0x40, // sample rate (44100Hz) + channels (1) + bits (16)
            0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, // total samples = 1
            // MD5 signature (16 bytes, zeros for silence)
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,

            // Minimal FLAC frame header + data
            0xFF, 0xF8, // sync code
            0x09, 0x18, // blocking strategy, block size, sample rate
            0x00,       // channel assignment, sample size
            0x00,       // frame number
            0x00,       // CRC-8
            // Subframe (constant, silence)
            0x00, 0x00,
            // Frame footer CRC-16
            0x00, 0x00
        };

        System.IO.File.WriteAllBytes(path, flacBytes);
        return path;
    }

    private static StreamingTrack CreateTrackWithIsrc(string isrc)
    {
        return new StreamingTrack
        {
            Title = "Test Track",
            Isrc = isrc
        };
    }

    private static StreamingTrack CreateTrackWithMusicBrainzId(string mbid)
    {
        return new StreamingTrack
        {
            Title = "Test Track",
            MusicBrainzId = mbid
        };
    }

    private static StreamingTrack CreateTrackWithAlbumMusicBrainzId(string albumMbid)
    {
        return new StreamingTrack
        {
            Title = "Test Track",
            Album = new StreamingAlbum
            {
                Title = "Test Album",
                MusicBrainzId = albumMbid
            }
        };
    }

    /// <summary>
    /// Reads ISRC from a TagLib file using format-specific access.
    /// </summary>
    private static string? ReadIsrc(TagLib.File file)
    {
        // Try ID3v2 (MP3)
        if (file.GetTag(TagTypes.Id3v2) is TagLib.Id3v2.Tag id3v2Tag)
        {
            var tsrcFrame = TagLib.Id3v2.TextInformationFrame.Get(
                id3v2Tag,
                TagLib.ByteVector.FromString("TSRC", TagLib.StringType.Latin1),
                false);
            if (tsrcFrame?.Text?.Length > 0)
            {
                return tsrcFrame.Text[0];
            }
        }

        // Try Xiph/Vorbis comment (FLAC, Ogg)
        if (file.GetTag(TagTypes.Xiph) is TagLib.Ogg.XiphComment xiphComment)
        {
            var isrcValues = xiphComment.GetField("ISRC");
            if (isrcValues?.Length > 0)
            {
                return isrcValues[0];
            }
        }

        // Try Apple tag (M4A)
        if (file.GetTag(TagTypes.Apple) is TagLib.Mpeg4.AppleTag appleTag)
        {
            var isrcItem = appleTag.GetDashBox("com.apple.iTunes", "ISRC");
            if (isrcItem != null)
            {
                return isrcItem.ToString();
            }
        }

        return null;
    }

    #endregion
}
