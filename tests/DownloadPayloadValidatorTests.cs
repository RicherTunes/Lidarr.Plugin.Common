using System;
using System.IO;
using System.Text;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

public class DownloadPayloadValidatorTests
{
    // Magic byte constants for test clarity
    private static readonly byte[] FlacMagic = Encoding.ASCII.GetBytes("fLaC");
    private static readonly byte[] OggMagic = Encoding.ASCII.GetBytes("OggS");
    private static readonly byte[] Id3Magic = Encoding.ASCII.GetBytes("ID3");
    private static readonly byte[] RiffMagic = Encoding.ASCII.GetBytes("RIFF");

    // ftyp appears at offset 4 in MP4/M4A: [4-byte size][ftyp][brand...]
    private static byte[] CreateFtypHeader(int totalLength = 12)
    {
        if (totalLength <= 0) return Array.Empty<byte>();
        var buffer = new byte[totalLength];
        // Size field (first 4 bytes)
        if (totalLength > 0) buffer[0] = 0x00;
        if (totalLength > 1) buffer[1] = 0x00;
        if (totalLength > 2) buffer[2] = 0x00;
        if (totalLength > 3) buffer[3] = 0x14; // size = 20
        // "ftyp" magic (bytes 4-7)
        if (totalLength > 4) buffer[4] = (byte)'f';
        if (totalLength > 5) buffer[5] = (byte)'t';
        if (totalLength > 6) buffer[6] = (byte)'y';
        if (totalLength > 7) buffer[7] = (byte)'p';
        // Brand "M4A " (bytes 8-11)
        if (totalLength > 8) buffer[8] = (byte)'M';
        if (totalLength > 9) buffer[9] = (byte)'4';
        if (totalLength > 10) buffer[10] = (byte)'A';
        if (totalLength > 11) buffer[11] = (byte)' ';
        return buffer;
    }

    #region Test 1: Short-buffer boundary cases (ftyp offset=4)

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    [InlineData(7)]
    public void ValidateOrThrow_M4A_ShortBuffer_ThrowsWithSpecificMessage(int bufferLength)
    {
        // Arrange: Create a buffer that is too short to contain ftyp at offset 4
        var buffer = bufferLength > 0 ? CreateFtypHeader(bufferLength) : Array.Empty<byte>();

        // Act & Assert
        var ex = Assert.Throws<InvalidDataException>(() =>
            DownloadPayloadValidator.ValidateOrThrow(buffer, ".m4a", "audio/mp4"));

        // Should mention insufficient bytes or similar - not just generic message
        Assert.True(
            ex.Message.Contains("insufficient", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("too small", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("header", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("MP4", StringComparison.OrdinalIgnoreCase) ||
            ex.Message.Contains("M4A", StringComparison.OrdinalIgnoreCase),
            $"Expected specific error about insufficient header bytes for M4A, got: {ex.Message}");
    }

    [Fact]
    public void ValidateOrThrow_M4A_ExactlyEightBytes_ValidFtyp_Passes()
    {
        // Arrange: Minimum valid ftyp header (8 bytes: 4-byte size + "ftyp")
        var buffer = CreateFtypHeader(8);

        // Act & Assert: Should not throw
        DownloadPayloadValidator.ValidateOrThrow(buffer, ".m4a", "audio/mp4");
    }

    [Fact]
    public void ValidateOrThrow_M4A_FullHeader_Passes()
    {
        // Arrange: Full valid ftyp header
        var buffer = CreateFtypHeader(12);

        // Act & Assert: Should not throw
        DownloadPayloadValidator.ValidateOrThrow(buffer, ".m4a", "audio/mp4");
    }

    #endregion

    #region Test 3: Extension vs magic mismatch contract

    [Fact]
    public void ValidateOrThrow_FtypHeader_FlacExtension_Throws()
    {
        // Arrange: ftyp header but .flac extension
        var buffer = CreateFtypHeader(12);

        // Act & Assert: Should throw - mismatch
        Assert.Throws<InvalidDataException>(() =>
            DownloadPayloadValidator.ValidateOrThrow(buffer, ".flac", "audio/flac"));
    }

    [Fact]
    public void ValidateOrThrow_FlacHeader_M4aExtension_Throws()
    {
        // Arrange: FLAC header but .m4a extension
        var buffer = new byte[64];
        FlacMagic.CopyTo(buffer, 0);

        // Act & Assert: Should throw - mismatch
        Assert.Throws<InvalidDataException>(() =>
            DownloadPayloadValidator.ValidateOrThrow(buffer, ".m4a", "audio/mp4"));
    }

    [Fact]
    public void ValidateOrThrow_FlacHeader_UnknownExtension_Passes()
    {
        // Arrange: Valid FLAC header with unknown extension
        var buffer = new byte[64];
        FlacMagic.CopyTo(buffer, 0);

        // Act & Assert: Should pass - unknown ext accepts any valid audio signature
        DownloadPayloadValidator.ValidateOrThrow(buffer, ".xyz", null);
    }

    [Fact]
    public void ValidateOrThrow_FlacHeader_NoExtension_Passes()
    {
        // Arrange: Valid FLAC header with no extension
        var buffer = new byte[64];
        FlacMagic.CopyTo(buffer, 0);

        // Act & Assert: Should pass
        DownloadPayloadValidator.ValidateOrThrow(buffer, null, null);
    }

    [Theory]
    [InlineData(".flac")]
    [InlineData("flac")]
    [InlineData(".FLAC")]
    [InlineData("FLAC")]
    public void ValidateOrThrow_FlacHeader_VariousFlacExtensions_Passes(string extension)
    {
        // Arrange: Valid FLAC header with various extension formats
        var buffer = new byte[64];
        FlacMagic.CopyTo(buffer, 0);

        // Act & Assert: Should pass - extension normalization handles dot and case
        DownloadPayloadValidator.ValidateOrThrow(buffer, extension, "audio/flac");
    }

    [Theory]
    [InlineData(".m4a")]
    [InlineData("m4a")]
    [InlineData(".mp4")]
    [InlineData("mp4")]
    [InlineData(".M4A")]
    public void ValidateOrThrow_FtypHeader_VariousM4aExtensions_Passes(string extension)
    {
        // Arrange: Valid ftyp header with various extension formats
        var buffer = CreateFtypHeader(12);

        // Act & Assert: Should pass
        DownloadPayloadValidator.ValidateOrThrow(buffer, extension, "audio/mp4");
    }

    #endregion

    #region Test 4: ValidateFileOrThrow on partial/empty files

    [Fact]
    public void ValidateFileOrThrow_EmptyFile_Throws()
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, Array.Empty<byte>());

            // Act & Assert
            var ex = Assert.Throws<InvalidOperationException>(() =>
                DownloadPayloadValidator.ValidateFileOrThrow(tempFile));
            Assert.Contains("too small", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void ValidateFileOrThrow_TinyJunkFile_Throws(int size)
    {
        // Arrange
        var tempFile = Path.GetTempFileName();
        try
        {
            var junk = new byte[size];
            new Random(42).NextBytes(junk);
            File.WriteAllBytes(tempFile, junk);

            // Act & Assert
            Assert.Throws<InvalidOperationException>(() =>
                DownloadPayloadValidator.ValidateFileOrThrow(tempFile));
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ValidateFileOrThrow_ValidFlacHeaderOnly_Passes()
    {
        // Arrange: Just the FLAC magic bytes (structural sanity check, not completeness)
        var tempFile = Path.GetTempFileName();
        try
        {
            File.WriteAllBytes(tempFile, FlacMagic);

            // Act & Assert: Should pass - header matches known container
            DownloadPayloadValidator.ValidateFileOrThrow(tempFile);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ValidateFileOrThrow_ValidId3HeaderOnly_Passes()
    {
        // Arrange: Just the ID3 magic bytes
        var tempFile = Path.GetTempFileName();
        try
        {
            var header = new byte[4];
            Id3Magic.CopyTo(header, 0);
            File.WriteAllBytes(tempFile, header);

            // Act & Assert: Should pass
            DownloadPayloadValidator.ValidateFileOrThrow(tempFile);
        }
        finally
        {
            if (File.Exists(tempFile)) File.Delete(tempFile);
        }
    }

    [Fact]
    public void ValidateFileOrThrow_NonexistentFile_Throws()
    {
        // Act & Assert
        Assert.Throws<FileNotFoundException>(() =>
            DownloadPayloadValidator.ValidateFileOrThrow("/nonexistent/path/file.flac"));
    }

    #endregion

    #region Valid audio signature detection

    [Fact]
    public void LooksLikeAudioPayload_ValidFlac_ReturnsTrue()
    {
        var buffer = new byte[64];
        FlacMagic.CopyTo(buffer, 0);
        Assert.True(DownloadPayloadValidator.LooksLikeAudioPayload(buffer));
    }

    [Fact]
    public void LooksLikeAudioPayload_ValidOgg_ReturnsTrue()
    {
        var buffer = new byte[64];
        OggMagic.CopyTo(buffer, 0);
        Assert.True(DownloadPayloadValidator.LooksLikeAudioPayload(buffer));
    }

    [Fact]
    public void LooksLikeAudioPayload_ValidId3_ReturnsTrue()
    {
        var buffer = new byte[64];
        Id3Magic.CopyTo(buffer, 0);
        Assert.True(DownloadPayloadValidator.LooksLikeAudioPayload(buffer));
    }

    [Fact]
    public void LooksLikeAudioPayload_ValidMpegSync_ReturnsTrue()
    {
        var buffer = new byte[] { 0xFF, 0xFB, 0x90, 0x00 }; // MP3 frame sync
        Assert.True(DownloadPayloadValidator.LooksLikeAudioPayload(buffer));
    }

    [Fact]
    public void LooksLikeAudioPayload_ValidRiff_ReturnsTrue()
    {
        var buffer = new byte[64];
        RiffMagic.CopyTo(buffer, 0);
        Assert.True(DownloadPayloadValidator.LooksLikeAudioPayload(buffer));
    }

    [Fact]
    public void LooksLikeAudioPayload_ValidFtyp_ReturnsTrue()
    {
        var buffer = CreateFtypHeader(12);
        Assert.True(DownloadPayloadValidator.LooksLikeAudioPayload(buffer));
    }

    [Fact]
    public void LooksLikeAudioPayload_RandomJunk_ReturnsFalse()
    {
        var buffer = new byte[] { 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08 };
        Assert.False(DownloadPayloadValidator.LooksLikeAudioPayload(buffer));
    }

    #endregion

    #region Test 2: False-positive text detection

    [Fact]
    public void LooksLikeTextPayload_ValidHtml_ReturnsTrue()
    {
        var html = Encoding.UTF8.GetBytes("<!DOCTYPE html><html><body>Error</body></html>");
        Assert.True(DownloadPayloadValidator.LooksLikeTextPayload(html));
    }

    [Fact]
    public void LooksLikeTextPayload_ValidJson_ReturnsTrue()
    {
        var json = Encoding.UTF8.GetBytes("{\"error\": \"unauthorized\", \"code\": 401}");
        Assert.True(DownloadPayloadValidator.LooksLikeTextPayload(json));
    }

    [Fact]
    public void LooksLikeTextPayload_ValidJsonArray_ReturnsTrue()
    {
        var json = Encoding.UTF8.GetBytes("[{\"id\": 1}, {\"id\": 2}]");
        Assert.True(DownloadPayloadValidator.LooksLikeTextPayload(json));
    }

    [Fact]
    public void LooksLikeTextPayload_BinaryStartingWithOpenBrace_ShouldNotFalsePositive()
    {
        // Binary data that happens to start with '{' but is not JSON
        // This is a key test - the eager check should NOT trigger here
        var binary = new byte[] { (byte)'{', 0x00, 0x01, 0xFF, 0xFE, 0x89, 0x50, 0x4E };

        // Current implementation is too eager - this test documents expected behavior
        // After fix: should return false (binary, not real JSON)
        // For now, this test will likely fail, showing the bug
        Assert.False(DownloadPayloadValidator.LooksLikeTextPayload(binary));
    }

    [Fact]
    public void LooksLikeTextPayload_BinaryStartingWithLessThan_ShouldNotFalsePositive()
    {
        // Binary data that happens to start with '<' but is not HTML
        var binary = new byte[] { (byte)'<', 0x00, 0xFF, 0xFE, 0x89, 0x50, 0x4E, 0x47 };

        // After fix: should return false (binary, not real HTML)
        Assert.False(DownloadPayloadValidator.LooksLikeTextPayload(binary));
    }

    [Fact]
    public void LooksLikeTextPayload_BinaryStartingWithBracket_ShouldNotFalsePositive()
    {
        // Binary data that happens to start with '[' but is not JSON array
        var binary = new byte[] { (byte)'[', 0x00, 0xFF, 0xFE, 0x89, 0x50, 0x4E, 0x47 };

        // After fix: should return false
        Assert.False(DownloadPayloadValidator.LooksLikeTextPayload(binary));
    }

    [Fact]
    public void LooksLikeTextPayload_ValidFlacMagic_ReturnsFalse()
    {
        // FLAC magic bytes should not be detected as text
        var buffer = new byte[64];
        FlacMagic.CopyTo(buffer, 0);
        Assert.False(DownloadPayloadValidator.LooksLikeTextPayload(buffer));
    }

    [Fact]
    public void LooksLikeTextPayload_ValidFtypMagic_ReturnsFalse()
    {
        // ftyp magic bytes should not be detected as text
        var buffer = CreateFtypHeader(12);
        Assert.False(DownloadPayloadValidator.LooksLikeTextPayload(buffer));
    }

    [Fact]
    public void LooksLikeTextPayload_EmptyBuffer_ReturnsFalse()
    {
        Assert.False(DownloadPayloadValidator.LooksLikeTextPayload(Array.Empty<byte>()));
    }

    [Fact]
    public void LooksLikeTextPayload_WhitespaceOnly_ReturnsFalse()
    {
        var whitespace = Encoding.UTF8.GetBytes("   \t\r\n   ");
        Assert.False(DownloadPayloadValidator.LooksLikeTextPayload(whitespace));
    }

    #endregion

    #region Test 5: BOM/whitespace prefix for text detection

    [Fact]
    public void LooksLikeTextPayload_HtmlWithUtf8Bom_ReturnsTrue()
    {
        // UTF-8 BOM: EF BB BF
        var bom = new byte[] { 0xEF, 0xBB, 0xBF };
        var html = Encoding.UTF8.GetBytes("<!DOCTYPE html>");
        var buffer = new byte[bom.Length + html.Length];
        bom.CopyTo(buffer, 0);
        html.CopyTo(buffer, bom.Length);

        Assert.True(DownloadPayloadValidator.LooksLikeTextPayload(buffer));
    }

    [Fact]
    public void LooksLikeTextPayload_JsonWithLeadingWhitespace_ReturnsTrue()
    {
        var json = Encoding.UTF8.GetBytes("  \n\t  {\"error\": \"test\"}");
        Assert.True(DownloadPayloadValidator.LooksLikeTextPayload(json));
    }

    [Fact]
    public void LooksLikeTextPayload_HtmlWithLeadingNewlines_ReturnsTrue()
    {
        var html = Encoding.UTF8.GetBytes("\r\n\r\n<!DOCTYPE html><html>");
        Assert.True(DownloadPayloadValidator.LooksLikeTextPayload(html));
    }

    [Fact]
    public void LooksLikeTextPayload_HtmlLowercase_ReturnsTrue()
    {
        var html = Encoding.UTF8.GetBytes("<html><head></head><body>Error page</body></html>");
        Assert.True(DownloadPayloadValidator.LooksLikeTextPayload(html));
    }

    [Fact]
    public void LooksLikeTextPayload_ScriptTag_ReturnsTrue()
    {
        var html = Encoding.UTF8.GetBytes("<script>alert('error')</script>");
        Assert.True(DownloadPayloadValidator.LooksLikeTextPayload(html));
    }

    #endregion
}
