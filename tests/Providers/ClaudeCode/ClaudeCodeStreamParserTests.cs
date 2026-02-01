// <copyright file="ClaudeCodeStreamParserTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Providers.ClaudeCode;
using Lidarr.Plugin.Common.Subprocess;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Providers.ClaudeCode;

public class ClaudeCodeStreamParserTests
{
    private readonly ClaudeCodeStreamParser _parser = new();

    [Fact]
    public async Task ParseAsync_TextDelta_ReturnsContentChunk()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":""Hello""}}"),
            new CliStreamEvent.Exited(0, TimeSpan.FromMilliseconds(100)));

        // Act
        var chunks = await CollectChunksAsync(events);

        // Assert
        Assert.Equal(2, chunks.Length);
        Assert.Equal("Hello", chunks[0].ContentDelta);
        Assert.False(chunks[0].IsComplete);
        Assert.True(chunks[1].IsComplete);
    }

    [Fact]
    public async Task ParseAsync_ThinkingDelta_ReturnsReasoningChunk()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""thinking_delta"",""thinking"":""Let me think...""}}"),
            new CliStreamEvent.Exited(0, TimeSpan.FromMilliseconds(100)));

        // Act
        var chunks = await CollectChunksAsync(events);

        // Assert
        Assert.Equal(2, chunks.Length);
        Assert.Equal("Let me think...", chunks[0].ReasoningDelta);
        Assert.Null(chunks[0].ContentDelta);
    }

    [Fact]
    public async Task ParseAsync_MultipleDeltas_ReturnsAllChunks()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":""Hello""}}"),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":"" world""}}"),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":""!""}}"),
            new CliStreamEvent.Exited(0, TimeSpan.FromMilliseconds(100)));

        // Act
        var chunks = await CollectChunksAsync(events);

        // Assert
        Assert.Equal(4, chunks.Length);
        Assert.Equal("Hello", chunks[0].ContentDelta);
        Assert.Equal(" world", chunks[1].ContentDelta);
        Assert.Equal("!", chunks[2].ContentDelta);
        Assert.True(chunks[3].IsComplete);
    }

    [Fact]
    public async Task ParseAsync_MessageStop_ReturnsCompletionChunk()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":""text""}}"),
            new CliStreamEvent.StandardOutput(@"{""type"":""message_stop""}"),
            new CliStreamEvent.Exited(0, TimeSpan.FromMilliseconds(100)));

        // Act
        var chunks = await CollectChunksAsync(events);

        // Assert
        Assert.Equal(2, chunks.Length);
        Assert.False(chunks[0].IsComplete);
        Assert.True(chunks[1].IsComplete);
    }

    [Fact]
    public async Task ParseAsync_MessageDeltaWithUsage_CapturesUsage()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":""text""}}"),
            new CliStreamEvent.StandardOutput(@"{""type"":""message_delta"",""usage"":{""input_tokens"":10,""output_tokens"":5}}"),
            new CliStreamEvent.StandardOutput(@"{""type"":""message_stop""}"),
            new CliStreamEvent.Exited(0, TimeSpan.FromMilliseconds(100)));

        // Act
        var chunks = await CollectChunksAsync(events);

        // Assert
        var completionChunk = chunks[^1];
        Assert.True(completionChunk.IsComplete);
        Assert.NotNull(completionChunk.FinalUsage);
        Assert.Equal(10, completionChunk.FinalUsage!.InputTokens);
        Assert.Equal(5, completionChunk.FinalUsage.OutputTokens);
    }

    [Fact]
    public async Task ParseAsync_ResultType_ExtractsUsage()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":""text""}}"),
            new CliStreamEvent.StandardOutput(@"{""type"":""result"",""usage"":{""input_tokens"":15,""output_tokens"":8}}"),
            new CliStreamEvent.Exited(0, TimeSpan.FromMilliseconds(100)));

        // Act
        var chunks = await CollectChunksAsync(events);

        // Assert
        var completionChunk = chunks[^1];
        Assert.True(completionChunk.IsComplete);
        Assert.NotNull(completionChunk.FinalUsage);
        Assert.Equal(15, completionChunk.FinalUsage!.InputTokens);
        Assert.Equal(8, completionChunk.FinalUsage.OutputTokens);
    }

    [Fact]
    public async Task ParseAsync_ErrorEvent_ThrowsException()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.StandardOutput(@"{""type"":""error"",""error"":{""type"":""api_error"",""message"":""Something went wrong""}}"));

        // Act & Assert
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await CollectChunksAsync(events));
        Assert.Contains("Something went wrong", ex.Message);
    }

    [Fact]
    public async Task ParseAsync_MalformedJson_SkipsLine()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.StandardOutput("not valid json"),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":""valid""}}"),
            new CliStreamEvent.Exited(0, TimeSpan.FromMilliseconds(100)));

        // Act
        var chunks = await CollectChunksAsync(events);

        // Assert
        Assert.Equal(2, chunks.Length);
        Assert.Equal("valid", chunks[0].ContentDelta);
    }

    [Fact]
    public async Task ParseAsync_EmptyLines_AreSkipped()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.StandardOutput(""),
            new CliStreamEvent.StandardOutput("   "),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":""text""}}"),
            new CliStreamEvent.Exited(0, TimeSpan.FromMilliseconds(100)));

        // Act
        var chunks = await CollectChunksAsync(events);

        // Assert
        Assert.Equal(2, chunks.Length);
        Assert.Equal("text", chunks[0].ContentDelta);
    }

    [Fact]
    public async Task ParseAsync_StderrEvents_AreIgnored()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.StandardError("Warning: some warning"),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":""text""}}"),
            new CliStreamEvent.Exited(0, TimeSpan.FromMilliseconds(100)));

        // Act
        var chunks = await CollectChunksAsync(events);

        // Assert
        Assert.Equal(2, chunks.Length);
        Assert.Equal("text", chunks[0].ContentDelta);
    }

    [Fact]
    public async Task ParseAsync_ProcessExitWithoutMessageStop_YieldsCompletion()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":""text""}}"),
            new CliStreamEvent.Exited(0, TimeSpan.FromMilliseconds(100)));

        // Act
        var chunks = await CollectChunksAsync(events);

        // Assert
        Assert.Equal(2, chunks.Length);
        Assert.True(chunks[1].IsComplete);
    }

    [Fact]
    public async Task ParseAsync_NoOutputEvents_YieldsCompletion()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.Exited(0, TimeSpan.FromMilliseconds(100)));

        // Act
        var chunks = await CollectChunksAsync(events);

        // Assert
        Assert.Single(chunks);
        Assert.True(chunks[0].IsComplete);
    }

    [Fact]
    public async Task ParseAsync_UnknownEventType_IsIgnored()
    {
        // Arrange
        var events = CreateEvents(
            new CliStreamEvent.Started(1234),
            new CliStreamEvent.StandardOutput(@"{""type"":""message_start""}"),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_start""}"),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_delta"",""delta"":{""type"":""text_delta"",""text"":""text""}}"),
            new CliStreamEvent.StandardOutput(@"{""type"":""content_block_stop""}"),
            new CliStreamEvent.Exited(0, TimeSpan.FromMilliseconds(100)));

        // Act
        var chunks = await CollectChunksAsync(events);

        // Assert
        Assert.Equal(2, chunks.Length);
        Assert.Equal("text", chunks[0].ContentDelta);
    }

    private static async IAsyncEnumerable<CliStreamEvent> CreateEvents(params CliStreamEvent[] events)
    {
        foreach (var evt in events)
        {
            await Task.Yield();
            yield return evt;
        }
    }

    private async Task<LlmStreamChunk[]> CollectChunksAsync(IAsyncEnumerable<CliStreamEvent> events)
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in _parser.ParseAsync(events, CancellationToken.None))
        {
            chunks.Add(chunk);
        }

        return chunks.ToArray();
    }
}
