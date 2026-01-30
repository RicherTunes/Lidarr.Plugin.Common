// <copyright file="SseFramingReaderTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.IO;
using System.Text;
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

    private static MemoryStream CreateStream(string content)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    private static async Task<SseFrame[]> CollectFramesAsync(SseFramingReader reader)
    {
        var frames = new System.Collections.Generic.List<SseFrame>();
        await foreach (var frame in reader.ReadFramesAsync())
        {
            frames.Add(frame);
        }

        return frames.ToArray();
    }
}
