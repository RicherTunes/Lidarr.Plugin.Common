using System;
using System.IO;
using Lidarr.Plugin.Common.Utilities;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Utilities;

public class AudioMagicBytesValidatorTests
{
    // IsValidAudioMagicBytes - format detection

    [Fact]
    public void IsValid_FlacSignature_True()
    {
        // "fLaC"
        ReadOnlySpan<byte> bytes = stackalloc byte[] { 0x66, 0x4C, 0x61, 0x43 };
        Assert.True(AudioMagicBytesValidator.IsValidAudioMagicBytes(bytes));
    }

    [Fact]
    public void IsValid_OggSignature_True()
    {
        // "OggS"
        ReadOnlySpan<byte> bytes = stackalloc byte[] { 0x4F, 0x67, 0x67, 0x53 };
        Assert.True(AudioMagicBytesValidator.IsValidAudioMagicBytes(bytes));
    }

    [Fact]
    public void IsValid_RiffWaveSignature_True()
    {
        // "RIFF" — note: validator only checks first 4 bytes, so any RIFF (WAV/AVI/etc.) passes.
        // Documented limitation; downstream codec-specific checks are out of scope.
        ReadOnlySpan<byte> bytes = stackalloc byte[] { 0x52, 0x49, 0x46, 0x46 };
        Assert.True(AudioMagicBytesValidator.IsValidAudioMagicBytes(bytes));
    }

    [Fact]
    public void IsValid_Mp3WithId3Tag_True()
    {
        // "ID3" — only 3 bytes need to match.
        ReadOnlySpan<byte> bytes = stackalloc byte[] { 0x49, 0x44, 0x33, 0x04 };
        Assert.True(AudioMagicBytesValidator.IsValidAudioMagicBytes(bytes));
    }

    [Fact]
    public void IsValid_Mp3FrameSync_True()
    {
        // 0xFF + (0xE0 in top 3 bits) — MPEG audio frame sync.
        ReadOnlySpan<byte> bytes = stackalloc byte[] { 0xFF, 0xFB, 0x00, 0x00 };
        Assert.True(AudioMagicBytesValidator.IsValidAudioMagicBytes(bytes));
    }

    [Theory]
    [InlineData(0xFF, 0xE0)]   // exactly the boundary
    [InlineData(0xFF, 0xF0)]
    [InlineData(0xFF, 0xFF)]
    public void IsValid_Mp3FrameSyncBoundaryBytes_True(byte b0, byte b1)
    {
        var arr = new byte[] { b0, b1 };
        Assert.True(AudioMagicBytesValidator.IsValidAudioMagicBytes(arr));
    }

    [Theory]
    [InlineData(0xFF, 0xC0)]   // top 3 bits 110 — not MPEG sync
    [InlineData(0xFE, 0xE0)]   // first byte not 0xFF
    public void IsValid_NonMp3FrameSyncBytes_False(byte b0, byte b1)
    {
        var arr = new byte[] { b0, b1, 0x00, 0x00 };
        Assert.False(AudioMagicBytesValidator.IsValidAudioMagicBytes(arr));
    }

    [Fact]
    public void IsValid_RandomBytes_False()
    {
        ReadOnlySpan<byte> bytes = stackalloc byte[] { 0x00, 0x01, 0x02, 0x03 };
        Assert.False(AudioMagicBytesValidator.IsValidAudioMagicBytes(bytes));
    }

    [Fact]
    public void IsValid_OneByteSpan_False()
    {
        // Validator requires at least 2 bytes.
        ReadOnlySpan<byte> bytes = stackalloc byte[] { 0xFF };
        Assert.False(AudioMagicBytesValidator.IsValidAudioMagicBytes(bytes));
    }

    [Fact]
    public void IsValid_EmptySpan_False()
    {
        Assert.False(AudioMagicBytesValidator.IsValidAudioMagicBytes(ReadOnlySpan<byte>.Empty));
    }

    [Fact]
    public void IsValid_TextFilePrefix_False()
    {
        // ASCII "Hell" — common false-positive shape we want to reject.
        ReadOnlySpan<byte> bytes = stackalloc byte[] { 0x48, 0x65, 0x6C, 0x6C };
        Assert.False(AudioMagicBytesValidator.IsValidAudioMagicBytes(bytes));
    }

    // ValidateAudioMagicBytes - file-path entry point

    [Fact]
    public void Validate_ValidFlacFile_DoesNotThrow()
    {
        var path = WriteTempFile(new byte[] { 0x66, 0x4C, 0x61, 0x43, 0x00, 0x00 });
        try
        {
            AudioMagicBytesValidator.ValidateAudioMagicBytes(path);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_InvalidBytes_ThrowsInvalidOperation()
    {
        var path = WriteTempFile(new byte[] { 0x00, 0x01, 0x02, 0x03 });
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => AudioMagicBytesValidator.ValidateAudioMagicBytes(path));
            Assert.Contains("Invalid audio magic bytes", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void Validate_FileTooSmall_ThrowsInvalidOperation()
    {
        var path = WriteTempFile(new byte[] { 0x66, 0x4C });   // 2 bytes — below 4-byte minimum
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => AudioMagicBytesValidator.ValidateAudioMagicBytes(path));
            Assert.Contains("too small", ex.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Validate_NullOrEmptyPath_ThrowsArgumentException(string? path)
    {
        Assert.Throws<ArgumentException>(() => AudioMagicBytesValidator.ValidateAudioMagicBytes(path!));
    }

    [Fact]
    public void Validate_ExceptionMessageIncludesAsciiPreview()
    {
        // ASCII "Hell" should appear in the error message preview for diagnosability.
        var path = WriteTempFile(new byte[] { 0x48, 0x65, 0x6C, 0x6C });
        try
        {
            var ex = Assert.Throws<InvalidOperationException>(() => AudioMagicBytesValidator.ValidateAudioMagicBytes(path));
            Assert.Contains("Hell", ex.Message);
        }
        finally
        {
            File.Delete(path);
        }
    }

    private static string WriteTempFile(byte[] contents)
    {
        var path = Path.Combine(Path.GetTempPath(), $"audio-magic-test-{Guid.NewGuid():N}.bin");
        File.WriteAllBytes(path, contents);
        return path;
    }
}
