// <copyright file="DecoderFuzzTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Streaming;
using Lidarr.Plugin.Common.Streaming.Decoders;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Streaming;

/// <summary>
/// Fuzz tests to ensure SSE framing and decoding handle arbitrary chunk boundaries.
/// These tests verify that the implementation doesn't assume line-buffered input.
/// </summary>
public class DecoderFuzzTests
{
    private const int FuzzIterations = 10;
    private readonly Random _random = new(42); // Deterministic seed for reproducibility

    [Fact]
    public async Task SseFramingReader_RandomChunkBoundaries_ParsesCorrectly()
    {
        // Arrange - a valid SSE stream
        var sseContent = @"data: first line
data: second line

data: third line

";

        for (int i = 0; i < FuzzIterations; i++)
        {
            using var stream = CreateChunkedStream(sseContent, minChunkSize: 1, maxChunkSize: 10);
            var reader = new SseFramingReader(stream);

            // Act
            var frames = new List<SseFrame>();
            await foreach (var frame in reader.ReadFramesAsync())
            {
                frames.Add(frame);
            }

            // Assert
            Assert.Equal(2, frames.Count);
            Assert.Equal("first line\nsecond line", frames[0].Data);
            Assert.Equal("third line", frames[1].Data);
        }
    }

    [Fact]
    public async Task SseFramingReader_SingleByteChunks_ParsesCorrectly()
    {
        // Arrange - worst case: every byte is a separate chunk
        var sseContent = "data: hello\n\n";
        using var stream = CreateChunkedStream(sseContent, minChunkSize: 1, maxChunkSize: 1);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = new List<SseFrame>();
        await foreach (var frame in reader.ReadFramesAsync())
        {
            frames.Add(frame);
        }

        // Assert
        Assert.Single(frames);
        Assert.Equal("hello", frames[0].Data);
    }

    [Fact]
    public async Task OpenAiDecoder_RandomChunkBoundaries_DecodesCorrectly()
    {
        // Arrange - a valid OpenAI SSE stream
        var sseContent = @"data: {""choices"":[{""delta"":{""content"":""Hello""}}]}

data: {""choices"":[{""delta"":{""content"":"" world""}}]}

data: [DONE]

";

        for (int i = 0; i < FuzzIterations; i++)
        {
            using var stream = CreateChunkedStream(sseContent, minChunkSize: 1, maxChunkSize: 15);
            var decoder = new OpenAiStreamDecoder();

            // Act
            var chunks = new List<LlmStreamChunk>();
            await foreach (var chunk in decoder.DecodeAsync(stream))
            {
                chunks.Add(chunk);
            }

            // Assert
            Assert.Equal(3, chunks.Count);
            Assert.Equal("Hello", chunks[0].ContentDelta);
            Assert.Equal(" world", chunks[1].ContentDelta);
            Assert.True(chunks[2].IsComplete);
        }
    }

    [Fact]
    public async Task OpenAiDecoder_ChunkBoundaryInJson_DecodesCorrectly()
    {
        // Arrange - ensure JSON isn't broken by chunk boundaries
        var sseContent = @"data: {""id"":""chatcmpl-123"",""choices"":[{""delta"":{""content"":""Test""}}]}

data: [DONE]

";

        // Try many different chunk sizes to hit different boundary positions
        for (int chunkSize = 1; chunkSize <= 50; chunkSize++)
        {
            using var stream = CreateChunkedStream(sseContent, minChunkSize: chunkSize, maxChunkSize: chunkSize);
            var decoder = new OpenAiStreamDecoder();

            // Act
            var chunks = new List<LlmStreamChunk>();
            await foreach (var chunk in decoder.DecodeAsync(stream))
            {
                chunks.Add(chunk);
            }

            // Assert
            Assert.Equal(2, chunks.Count);
            Assert.Equal("Test", chunks[0].ContentDelta);
            Assert.True(chunks[1].IsComplete);
        }
    }

    [Fact]
    public async Task SseFramingReader_MultilineData_ChunkedArbitrarily()
    {
        // Arrange - multiple data fields that span chunks
        var sseContent = @"data: line1
data: line2
data: line3

";

        for (int i = 0; i < FuzzIterations; i++)
        {
            using var stream = CreateChunkedStream(sseContent, minChunkSize: 2, maxChunkSize: 8);
            var reader = new SseFramingReader(stream);

            // Act
            var frames = new List<SseFrame>();
            await foreach (var frame in reader.ReadFramesAsync())
            {
                frames.Add(frame);
            }

            // Assert
            Assert.Single(frames);
            Assert.Equal("line1\nline2\nline3", frames[0].Data);
        }
    }

    [Fact]
    public async Task SseFramingReader_CommentsChunkedArbitrarily()
    {
        // Arrange - comments should be ignored regardless of chunk boundaries
        var sseContent = @": this is a comment
: another comment
data: actual data

";

        for (int i = 0; i < FuzzIterations; i++)
        {
            using var stream = CreateChunkedStream(sseContent, minChunkSize: 1, maxChunkSize: 12);
            var reader = new SseFramingReader(stream);

            // Act
            var frames = new List<SseFrame>();
            await foreach (var frame in reader.ReadFramesAsync())
            {
                frames.Add(frame);
            }

            // Assert
            Assert.Single(frames);
            Assert.Equal("actual data", frames[0].Data);
        }
    }

    [Fact]
    public async Task GeminiDecoder_RandomChunkBoundaries_DecodesCorrectly()
    {
        // Arrange - a valid Gemini SSE stream
        var sseContent = @"data: {""candidates"":[{""content"":{""parts"":[{""text"":""Hello""}]},""index"":0}]}

data: {""candidates"":[{""content"":{""parts"":[{""text"":"" world""}]},""finishReason"":""STOP""}]}

";

        for (int i = 0; i < FuzzIterations; i++)
        {
            using var stream = CreateChunkedStream(sseContent, minChunkSize: 1, maxChunkSize: 15);
            var decoder = new GeminiStreamDecoder();

            // Act
            var chunks = new List<LlmStreamChunk>();
            await foreach (var chunk in decoder.DecodeAsync(stream))
            {
                chunks.Add(chunk);
            }

            // Assert
            Assert.Equal(2, chunks.Count);
            Assert.Equal("Hello", chunks[0].ContentDelta);
            Assert.Equal(" world", chunks[1].ContentDelta);
            Assert.True(chunks[1].IsComplete);
        }
    }

    [Fact]
    public async Task GeminiDecoder_ChunkBoundaryInJson_DecodesCorrectly()
    {
        // Arrange - ensure JSON isn't broken by chunk boundaries
        var sseContent = @"data: {""candidates"":[{""content"":{""parts"":[{""text"":""Test""}]},""finishReason"":""STOP""}]}

";

        // Try many different chunk sizes to hit different boundary positions
        for (int chunkSize = 1; chunkSize <= 50; chunkSize++)
        {
            using var stream = CreateChunkedStream(sseContent, minChunkSize: chunkSize, maxChunkSize: chunkSize);
            var decoder = new GeminiStreamDecoder();

            // Act
            var chunks = new List<LlmStreamChunk>();
            await foreach (var chunk in decoder.DecodeAsync(stream))
            {
                chunks.Add(chunk);
            }

            // Assert
            Assert.Single(chunks);
            Assert.Equal("Test", chunks[0].ContentDelta);
            Assert.True(chunks[0].IsComplete);
        }
    }

    [Fact]
    public async Task ZaiDecoder_RandomChunkBoundaries_DecodesCorrectly()
    {
        // Arrange - a valid Z.AI GLM SSE stream
        var sseContent = @"data: {""id"":""abc123"",""choices"":[{""index"":0,""delta"":{""content"":""Hello""}}]}

data: {""id"":""abc123"",""choices"":[{""index"":0,""delta"":{""content"":"" world""}}]}

data: [DONE]

";

        for (int i = 0; i < FuzzIterations; i++)
        {
            using var stream = CreateChunkedStream(sseContent, minChunkSize: 1, maxChunkSize: 15);
            var decoder = new ZaiStreamDecoder();

            // Act
            var chunks = new List<LlmStreamChunk>();
            await foreach (var chunk in decoder.DecodeAsync(stream))
            {
                chunks.Add(chunk);
            }

            // Assert
            Assert.Equal(3, chunks.Count);
            Assert.Equal("Hello", chunks[0].ContentDelta);
            Assert.Equal(" world", chunks[1].ContentDelta);
            Assert.True(chunks[2].IsComplete);
        }
    }

    [Fact]
    public async Task ZaiDecoder_ChunkBoundaryInJson_DecodesCorrectly()
    {
        // Arrange - ensure JSON isn't broken by chunk boundaries
        var sseContent = @"data: {""id"":""abc123"",""model"":""glm-4"",""choices"":[{""index"":0,""delta"":{""content"":""Test""},""finish_reason"":""stop""}]}

";

        // Try many different chunk sizes to hit different boundary positions
        for (int chunkSize = 1; chunkSize <= 50; chunkSize++)
        {
            using var stream = CreateChunkedStream(sseContent, minChunkSize: chunkSize, maxChunkSize: chunkSize);
            var decoder = new ZaiStreamDecoder();

            // Act
            var chunks = new List<LlmStreamChunk>();
            await foreach (var chunk in decoder.DecodeAsync(stream))
            {
                chunks.Add(chunk);
            }

            // Assert
            Assert.Single(chunks);
            Assert.Equal("Test", chunks[0].ContentDelta);
            Assert.True(chunks[0].IsComplete);
        }
    }

    /// <summary>
    /// Creates a stream that delivers content in random-sized chunks.
    /// This simulates network delivery where data arrives in arbitrary fragments.
    /// </summary>
    private ChunkedMemoryStream CreateChunkedStream(string content, int minChunkSize, int maxChunkSize)
    {
        return new ChunkedMemoryStream(content, minChunkSize, maxChunkSize, _random);
    }

    /// <summary>
    /// A memory stream that delivers data in random-sized chunks to simulate network behavior.
    /// </summary>
    private sealed class ChunkedMemoryStream : Stream
    {
        private readonly byte[] _data;
        private readonly int _minChunkSize;
        private readonly int _maxChunkSize;
        private readonly Random _random;
        private int _position;

        public ChunkedMemoryStream(string content, int minChunkSize, int maxChunkSize, Random random)
        {
            _data = Encoding.UTF8.GetBytes(content);
            _minChunkSize = minChunkSize;
            _maxChunkSize = maxChunkSize;
            _random = random;
        }

        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _data.Length)
            {
                return 0;
            }

            // Deliver a random-sized chunk
            var chunkSize = _random.Next(_minChunkSize, _maxChunkSize + 1);
            var bytesToRead = Math.Min(chunkSize, Math.Min(count, _data.Length - _position));

            Array.Copy(_data, _position, buffer, offset, bytesToRead);
            _position += bytesToRead;

            return bytesToRead;
        }

        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
