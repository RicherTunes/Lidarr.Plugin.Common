using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Metadata;
using Microsoft.Extensions.Logging;
using TagLib;
using Xunit;

namespace Lidarr.Plugin.Common.Tests;

/// <summary>
/// Round-trip tests for <see cref="TagLibAudioArtworkEmbedder"/>. Cover art must be embedded
/// into the audio file itself (FLAC PICTURE / ID3 APIC) so it survives Lidarr import regardless
/// of the host's <c>importExtraFiles</c> setting — a plain sidecar image is dropped at import,
/// which is why downloaded albums arrive art-less.
/// </summary>
public class TagLibAudioArtworkEmbedderTests : IDisposable
{
    private readonly string _tempDir;

    public TagLibAudioArtworkEmbedderTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"ArtworkTests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); }
        catch { /* best effort */ }
    }

    // A tiny but structurally-valid JPEG byte stream (SOI ... EOI). TagLib stores the bytes verbatim.
    private static byte[] FakeJpeg() => new byte[]
    {
        0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01,
        0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xD9
    };

    [Fact]
    public async Task EmbedAsync_Flac_EmbedsFrontCover_RoundTrip()
    {
        var filePath = CreateMinimalFlac();
        var embedder = new TagLibAudioArtworkEmbedder();
        var image = FakeJpeg();

        await embedder.EmbedAsync(filePath, image, "image/jpeg");

        using var file = TagLib.File.Create(filePath);
        Assert.Single(file.Tag.Pictures);
        Assert.Equal(PictureType.FrontCover, file.Tag.Pictures[0].Type);
        Assert.Equal("image/jpeg", file.Tag.Pictures[0].MimeType);
        Assert.Equal(image, file.Tag.Pictures[0].Data.Data);
    }

    [Fact]
    public async Task EmbedAsync_Mp3_EmbedsFrontCover_RoundTrip()
    {
        var filePath = CreateMinimalMp3();
        var embedder = new TagLibAudioArtworkEmbedder();
        var image = FakeJpeg();

        await embedder.EmbedAsync(filePath, image, "image/jpeg");

        using var file = TagLib.File.Create(filePath);
        Assert.Single(file.Tag.Pictures);
        Assert.Equal(PictureType.FrontCover, file.Tag.Pictures[0].Type);
    }

    [Fact]
    public async Task EmbedAsync_EmptyBytes_IsNoOp()
    {
        var filePath = CreateMinimalFlac();
        var embedder = new TagLibAudioArtworkEmbedder();

        await embedder.EmbedAsync(filePath, Array.Empty<byte>(), "image/jpeg");

        using var file = TagLib.File.Create(filePath);
        Assert.Empty(file.Tag.Pictures);
    }

    [Fact]
    public async Task EmbedAsync_NullBytes_DoesNotThrow()
    {
        var filePath = CreateMinimalFlac();
        var embedder = new TagLibAudioArtworkEmbedder();

        var ex = await Record.ExceptionAsync(() => embedder.EmbedAsync(filePath, null!, "image/jpeg"));

        Assert.Null(ex);
    }

    [Fact]
    public async Task EmbedAsync_InvalidFile_EmitsWarningAndDoesNotThrow()
    {
        var invalidFilePath = Path.Combine(_tempDir, "invalid.flac");
        System.IO.File.WriteAllBytes(invalidFilePath, new byte[] { 0x00, 0x01, 0x02 });
        var logger = new TestLogger();
        var embedder = new TagLibAudioArtworkEmbedder(logger);

        var ex = await Record.ExceptionAsync(() => embedder.EmbedAsync(invalidFilePath, FakeJpeg(), "image/jpeg"));

        Assert.Null(ex);
        Assert.Contains(logger.Entries, e => e.Level == LogLevel.Warning);
    }

    [Fact]
    public async Task EmbedAsync_MissingMimeType_DefaultsToJpeg()
    {
        var filePath = CreateMinimalFlac();
        var embedder = new TagLibAudioArtworkEmbedder();

        await embedder.EmbedAsync(filePath, FakeJpeg(), mimeType: "");

        using var file = TagLib.File.Create(filePath);
        Assert.Equal("image/jpeg", file.Tag.Pictures[0].MimeType);
    }

    private sealed class TestLogger : ILogger
    {
        public ConcurrentBag<LogEntry> Entries { get; } = new();
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
            => Entries.Add(new LogEntry(logLevel, formatter(state, exception)));
        public record LogEntry(LogLevel Level, string Message);
        private sealed class NullScope : IDisposable { public static readonly NullScope Instance = new(); public void Dispose() { } }
    }

    private string CreateMinimalMp3()
    {
        var path = Path.Combine(_tempDir, $"test_{Guid.NewGuid():N}.mp3");
        var mp3Bytes = new byte[]
        {
            0x49, 0x44, 0x33, 0x03, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xFF, 0xFB, 0x90, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
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
        var flacBytes = new byte[]
        {
            0x66, 0x4C, 0x61, 0x43,
            0x80, 0x00, 0x00, 0x22,
            0x00, 0x10, 0x00, 0x10,
            0x00, 0x00, 0x01, 0x00, 0x00, 0x01,
            0x0A, 0xC4, 0x40, 0xF0, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
            0xFF, 0xF8, 0x09, 0x18, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00
        };
        System.IO.File.WriteAllBytes(path, flacBytes);
        return path;
    }
}
