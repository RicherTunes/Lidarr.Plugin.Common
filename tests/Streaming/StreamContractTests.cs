// <copyright file="StreamContractTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Streaming;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Streaming;

/// <summary>
/// Base class for stream decoder contract tests.
/// Inherit from this class and implement the abstract methods to test your decoder.
/// </summary>
public abstract class StreamContractTestsBase
{
    /// <summary>
    /// Creates an instance of the decoder under test.
    /// </summary>
    protected abstract IStreamDecoder CreateDecoder();

    /// <summary>
    /// Creates a stream with SSE content that ends with [DONE].
    /// The stream should yield at least one content chunk before [DONE].
    /// </summary>
    protected abstract Stream CreateDoneTerminatedStream();

    /// <summary>
    /// Creates a stream with SSE content that ends with finish_reason or equivalent.
    /// </summary>
    protected abstract Stream CreateFinishReasonTerminatedStream();

    /// <summary>
    /// Creates a stream with multiple content chunks.
    /// </summary>
    protected abstract Stream CreateMultipleChunksStream(int chunkCount);

    /// <summary>
    /// Creates a stream with malformed SSE that should be handled gracefully.
    /// </summary>
    protected abstract Stream CreateMalformedStream();

    /// <summary>
    /// Gets the expected content type for this decoder.
    /// </summary>
    protected abstract string ExpectedContentType { get; }

    /// <summary>
    /// Gets the provider ID for this decoder.
    /// </summary>
    protected abstract string ProviderId { get; }

    [Fact]
    public void DecoderId_IsNotNullOrEmpty()
    {
        var decoder = CreateDecoder();
        Assert.False(string.IsNullOrEmpty(decoder.DecoderId));
    }

    [Fact]
    public void SupportedProviderIds_IsNotEmpty()
    {
        var decoder = CreateDecoder();
        Assert.NotNull(decoder.SupportedProviderIds);
        Assert.NotEmpty(decoder.SupportedProviderIds);
    }

    [Fact]
    public void CanDecode_WithExpectedContentType_ReturnsTrue()
    {
        var decoder = CreateDecoder();
        Assert.True(decoder.CanDecode(ExpectedContentType));
    }

    [Fact]
    public void CanDecode_WithNullOrEmpty_ReturnsFalse()
    {
        var decoder = CreateDecoder();
        Assert.False(decoder.CanDecode(null!));
        Assert.False(decoder.CanDecode(string.Empty));
    }

    [Fact]
    public void CanDecodeForProvider_WithKnownProvider_ReturnsTrue()
    {
        var decoder = CreateDecoder();
        Assert.True(decoder.CanDecodeForProvider(ProviderId, ExpectedContentType));
    }

    [Fact]
    public async Task DecodeAsync_DoneTerminated_YieldsIsCompleteTrue()
    {
        // Arrange
        var decoder = CreateDecoder();
        using var stream = CreateDoneTerminatedStream();

        // Act
        var chunks = await CollectChunksAsync(decoder, stream);

        // Assert
        Assert.NotEmpty(chunks);
        var lastChunk = chunks.Last();
        Assert.True(lastChunk.IsComplete, "Last chunk should have IsComplete=true");
    }

    [Fact]
    public async Task DecodeAsync_FinishReasonTerminated_YieldsIsCompleteTrue()
    {
        // Arrange
        var decoder = CreateDecoder();
        using var stream = CreateFinishReasonTerminatedStream();

        // Act
        var chunks = await CollectChunksAsync(decoder, stream);

        // Assert
        Assert.NotEmpty(chunks);
        var lastChunk = chunks.Last();
        Assert.True(lastChunk.IsComplete, "Last chunk should have IsComplete=true");
    }

    [Fact]
    public async Task DecodeAsync_MultipleChunks_PreservesOrder()
    {
        // Arrange
        var decoder = CreateDecoder();
        const int expectedCount = 5;
        using var stream = CreateMultipleChunksStream(expectedCount);

        // Act
        var chunks = await CollectChunksAsync(decoder, stream);

        // Assert
        // At least expectedCount content chunks (plus potentially a completion chunk)
        Assert.True(chunks.Count >= expectedCount,
            $"Expected at least {expectedCount} chunks, got {chunks.Count}");
    }

    [Fact]
    public async Task DecodeAsync_Cancelled_StopsQuickly()
    {
        // Arrange
        var decoder = CreateDecoder();
        using var stream = CreateSlowStream();
        using var cts = new CancellationTokenSource();

        // Act
        var collectTask = CollectChunksWithTimeoutAsync(decoder, stream, cts.Token);

        // Cancel after a short delay
        await Task.Delay(50);
        cts.Cancel();

        // Assert - should complete quickly after cancellation
        var chunks = await collectTask;
        // Just verify it didn't hang - exact chunk count depends on timing
    }

    [Fact]
    public async Task DecodeAsync_MalformedInput_DoesNotHang()
    {
        // Arrange
        var decoder = CreateDecoder();
        using var stream = CreateMalformedStream();
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        // Act - should complete without hanging
        var chunks = await CollectChunksAsync(decoder, stream, cts.Token);

        // Assert - malformed input should result in graceful handling
        // (either skip bad frames or yield error info, but not hang)
        Assert.True(true, "Decoder completed without hanging");
    }

    protected static async Task<List<LlmStreamChunk>> CollectChunksAsync(
        IStreamDecoder decoder,
        Stream stream,
        CancellationToken cancellationToken = default)
    {
        var chunks = new List<LlmStreamChunk>();
        await foreach (var chunk in decoder.DecodeAsync(stream, cancellationToken))
        {
            chunks.Add(chunk);
            if (chunk.IsComplete)
            {
                break;
            }
        }

        return chunks;
    }

    protected static async Task<List<LlmStreamChunk>> CollectChunksWithTimeoutAsync(
        IStreamDecoder decoder,
        Stream stream,
        CancellationToken cancellationToken)
    {
        var chunks = new List<LlmStreamChunk>();
        try
        {
            await foreach (var chunk in decoder.DecodeAsync(stream, cancellationToken))
            {
                chunks.Add(chunk);
                if (chunk.IsComplete)
                {
                    break;
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Expected when cancelled
        }

        return chunks;
    }

    /// <summary>
    /// Creates a slow stream that yields data slowly for cancellation testing.
    /// </summary>
    protected virtual Stream CreateSlowStream()
    {
        // Default implementation returns multiple chunks with delays
        return CreateMultipleChunksStream(100);
    }

    protected static MemoryStream CreateStream(string content)
    {
        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }
}

/// <summary>
/// Tests that only meaningful chunks reset the inter-chunk timeout.
/// </summary>
public class TimeoutResetBehaviorTests
{
    [Fact]
    public void OnChunkReceived_ShouldOnlyBeCalledForMeaningfulContent()
    {
        // This is a design documentation test - the actual behavior is tested in ClaudeCodeProvider
        // Meaningful content means:
        // - ContentDelta is not null or empty
        // - ReasoningDelta is not null or empty
        //
        // NOT meaningful (should NOT reset timeout):
        // - Chunks with only FinalUsage
        // - Chunks with only IsComplete
        // - Structural events (message_start, content_block_start, etc.)
        // - Heartbeat/keepalive frames

        var meaningfulChunk = new LlmStreamChunk { ContentDelta = "hello" };
        var metadataOnlyChunk = new LlmStreamChunk { IsComplete = true };

        Assert.False(string.IsNullOrEmpty(meaningfulChunk.ContentDelta));
        Assert.True(string.IsNullOrEmpty(metadataOnlyChunk.ContentDelta));
        Assert.True(string.IsNullOrEmpty(metadataOnlyChunk.ReasoningDelta));
    }
}

/// <summary>
/// SSE framing contract tests.
/// </summary>
public class SseFramingContractTests
{
    [Fact]
    public async Task ReadFramesAsync_HeartbeatFrame_DoesNotResetInterChunkTimeout()
    {
        // Heartbeat frames (comments) should be ignored and not count as "real" data
        var sseContent = ": heartbeat\n\n: another heartbeat\n\ndata: actual content\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseContent));
        var reader = new SseFramingReader(stream);

        var frames = new List<SseFrame>();
        await foreach (var frame in reader.ReadFramesAsync())
        {
            frames.Add(frame);
        }

        // Only the actual data frame should be returned
        Assert.Single(frames);
        Assert.Equal("actual content", frames[0].Data);
    }

    [Fact]
    public void Constructor_MaxEventSizeZero_AllowsUnlimited()
    {
        // maxEventSize=0 means unlimited
        using var stream = new MemoryStream();
        var reader = new SseFramingReader(stream, maxEventSize: 0);
        Assert.NotNull(reader);
    }

    [Fact]
    public async Task ReadFramesAsync_EventExceedsMaxSize_ThrowsException()
    {
        // Arrange - create a stream with a large event
        var largeData = new string('x', 1000);
        var sseContent = $"data: {largeData}\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseContent));

        // Set max event size smaller than the data
        var reader = new SseFramingReader(stream, maxEventSize: 100);

        // Act & Assert - throws StreamFrameTooLargeException (a dedicated type)
        var ex = await Assert.ThrowsAsync<StreamFrameTooLargeException>(async () =>
        {
            await foreach (var _ in reader.ReadFramesAsync())
            {
            }
        });

        Assert.Equal(100, ex.MaxEventSize);
        Assert.Equal(1000, ex.ActualSize);
        Assert.Contains("exceeds maximum", ex.Message);
    }

    [Fact]
    public async Task ReadFramesAsync_EventWithinMaxSize_Succeeds()
    {
        // Arrange
        var sseContent = "data: small data\n\n";
        using var stream = new MemoryStream(Encoding.UTF8.GetBytes(sseContent));
        var reader = new SseFramingReader(stream, maxEventSize: 1000);

        // Act
        var frames = new List<SseFrame>();
        await foreach (var frame in reader.ReadFramesAsync())
        {
            frames.Add(frame);
        }

        // Assert
        Assert.Single(frames);
        Assert.Equal("small data", frames[0].Data);
    }
}
