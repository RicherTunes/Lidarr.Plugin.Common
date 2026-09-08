// <copyright file="SseFramingReaderTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Streaming;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Streaming;

public class SseFramingReaderTests
{
    [Fact]
    public async Task ReadFramesAsync_SingleDataField_ReturnsOneFrame()
    {
        // Arrange
        var sseContent = "data: hello world\n\n";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Single(frames);
        Assert.Equal("hello world", frames[0].Data);
        Assert.Null(frames[0].EventType);
    }

    [Fact]
    public async Task ReadFramesAsync_MultipleDataFields_ConcatenatesWithNewlines()
    {
        // Arrange
        var sseContent = "data: line1\ndata: line2\ndata: line3\n\n";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Single(frames);
        Assert.Equal("line1\nline2\nline3", frames[0].Data);
    }

    [Fact]
    public async Task ReadFramesAsync_EventTypeField_ParsesCorrectly()
    {
        // Arrange
        var sseContent = "event: update\ndata: content\n\n";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Single(frames);
        Assert.Equal("update", frames[0].EventType);
        Assert.Equal("content", frames[0].Data);
    }

    [Fact]
    public async Task ReadFramesAsync_IdField_ParsesCorrectly()
    {
        // Arrange
        var sseContent = "id: 123\ndata: content\n\n";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Single(frames);
        Assert.Equal("123", frames[0].Id);
    }

    [Fact]
    public async Task ReadFramesAsync_RetryField_ParsesCorrectly()
    {
        // Arrange
        var sseContent = "retry: 5000\ndata: content\n\n";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Single(frames);
        Assert.Equal(5000, frames[0].RetryMilliseconds);
    }

    [Fact]
    public async Task ReadFramesAsync_MultipleFrames_ReturnsAll()
    {
        // Arrange
        var sseContent = "data: first\n\ndata: second\n\ndata: third\n\n";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Equal(3, frames.Length);
        Assert.Equal("first", frames[0].Data);
        Assert.Equal("second", frames[1].Data);
        Assert.Equal("third", frames[2].Data);
    }

    [Fact]
    public async Task ReadFramesAsync_Comments_AreIgnored()
    {
        // Arrange
        var sseContent = ": this is a comment\ndata: content\n\n";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Single(frames);
        Assert.Equal("content", frames[0].Data);
    }

    [Fact]
    public async Task ReadFramesAsync_SpaceAfterColon_IsStripped()
    {
        // Arrange
        var sseContent = "data: with space\n\n";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Single(frames);
        Assert.Equal("with space", frames[0].Data);
    }

    [Fact]
    public async Task ReadFramesAsync_NoSpaceAfterColon_WorksCorrectly()
    {
        // Arrange
        var sseContent = "data:no space\n\n";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Single(frames);
        Assert.Equal("no space", frames[0].Data);
    }

    [Fact]
    public async Task ReadFramesAsync_DoneMarker_IsDoneIsTrue()
    {
        // Arrange
        var sseContent = "data: [DONE]\n\n";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Single(frames);
        Assert.True(frames[0].IsDone);
    }

    [Fact]
    public async Task ReadFramesAsync_OpenAiStyleStream_ParsesCorrectly()
    {
        // Arrange - simulates OpenAI streaming response
        var sseContent = @"data: {""choices"":[{""delta"":{""content"":""Hello""}}]}

data: {""choices"":[{""delta"":{""content"":"" world""}}]}

data: [DONE]

";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Equal(3, frames.Length);
        Assert.Contains("Hello", frames[0].Data);
        Assert.Contains("world", frames[1].Data);
        Assert.True(frames[2].IsDone);
    }

    [Fact]
    public async Task ReadFramesAsync_EmptyStream_ReturnsEmpty()
    {
        // Arrange
        using var stream = CreateStream(string.Empty);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Empty(frames);
    }

    [Fact]
    public async Task ReadFramesAsync_StreamEndsWithoutBlankLine_ReturnsFrame()
    {
        // Arrange - stream ends without final blank line
        var sseContent = "data: content";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Single(frames);
        Assert.Equal("content", frames[0].Data);
    }

    [Fact]
    public async Task ReadFramesAsync_FieldNameOnly_TreatedAsEmptyValue()
    {
        // Arrange
        var sseContent = "data\n\n";
        using var stream = CreateStream(sseContent);
        var reader = new SseFramingReader(stream);

        // Act
        var frames = await CollectFramesAsync(reader);

        // Assert
        Assert.Single(frames);
        Assert.Equal(string.Empty, frames[0].Data);
    }

    [Fact]
    public async Task ReadFramesAsync_UsesAsyncReadWithoutCallingSynchronousRead()
    {
        await using var stream = new AsyncOnlyStream("data: async\n\n");
        var frames = await CollectFramesAsync(new SseFramingReader(stream));
        Assert.Single(frames);
        Assert.Equal("async", frames[0].Data);
    }

    [Fact]
    public async Task ReadFramesAsync_PreCancelledToken_ThrowsOperationCanceledException()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await using var stream = new AsyncOnlyStream("data: ignored\n\n");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CollectFramesAsync(new SseFramingReader(stream), cancellation.Token));
    }

    [Fact]
    public async Task ReadFramesAsync_CancellationDuringPendingRead_ThrowsAndDoesNotEmitPartialFrame()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new BlockingAsyncOnlyStream("data: partial\n");
        var reader = new SseFramingReader(stream);
        var frames = new System.Collections.Generic.List<SseFrame>();

        async Task ReadUntilCancelledAsync()
        {
            await foreach (var frame in reader.ReadFramesAsync(cancellation.Token))
            {
                frames.Add(frame);
            }
        }

        var readTask = ReadUntilCancelledAsync();
        await stream.PendingRead.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => readTask);
        Assert.Empty(frames);
    }

    [Fact]
    public async Task ReadFramesAsync_LeavesSuppliedStreamOpen()
    {
        var stream = new AsyncOnlyStream("data: owned\n\n");
        await CollectFramesAsync(new SseFramingReader(stream));
        Assert.True(stream.CanRead);
        await stream.DisposeAsync();
    }

    private static MemoryStream CreateStream(string content)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    private static async Task<SseFrame[]> CollectFramesAsync(SseFramingReader reader)
        => await CollectFramesAsync(reader, CancellationToken.None);

    private static async Task<SseFrame[]> CollectFramesAsync(SseFramingReader reader, CancellationToken cancellationToken)
    {
        var frames = new System.Collections.Generic.List<SseFrame>();
        await foreach (var frame in reader.ReadFramesAsync(cancellationToken))
        {
            frames.Add(frame);
        }

        return frames.ToArray();
    }

    private sealed class AsyncOnlyStream : MemoryStream
    {
        private readonly byte[] _data;
        public AsyncOnlyStream(string text) : base() { _data = Encoding.UTF8.GetBytes(text); }
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("sync read is forbidden");
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = _data.Length - (int)Position;
            var count = Math.Min(remaining, buffer.Length);
            if (count > 0) _data.AsSpan((int)Position, count).CopyTo(buffer.Span);
            Position += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class BlockingAsyncOnlyStream : Stream
    {
        private readonly byte[] _data;
        private readonly TaskCompletionSource<bool> _pendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public BlockingAsyncOnlyStream(string text)
        {
            _data = Encoding.UTF8.GetBytes(text);
        }

        public Task PendingRead => _pendingRead.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("sync read is forbidden");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = _data.Length - _position;
            if (remaining > 0)
            {
                var count = Math.Min(remaining, buffer.Length);
                _data.AsSpan(_position, count).CopyTo(buffer.Span);
                _position += count;
                return ValueTask.FromResult(count);
            }

            _pendingRead.TrySetResult(true);
            return WaitForCancellationAsync(cancellationToken);
        }

        private static async ValueTask<int> WaitForCancellationAsync(CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            return 0;
        }
    }
}
