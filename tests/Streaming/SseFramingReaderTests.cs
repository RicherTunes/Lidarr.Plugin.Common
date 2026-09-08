// <copyright file="SseFramingReaderTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Collections.Generic;
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

    [Theory]
    [InlineData("\u0800", 3, 2)]
    [InlineData("🎵", 4, 3)]
    public async Task ReadFramesAsync_Utf8Payload_UsesEncodedByteCount(string payload, int exactLimit, int rejectedLimit)
    {
        await using var exactStream = new AsyncOnlyStream($"data: {payload}\n\n");
        var exact = new SseFramingReader(exactStream, Encoding.UTF8, bufferSize: 8, maxEventSize: exactLimit);
        Assert.Equal(payload, Assert.Single(await CollectFramesAsync(exact)).Data);

        await using var oversizedStream = new AsyncOnlyStream($"data: {payload}\n\n");
        var oversized = new SseFramingReader(oversizedStream, Encoding.UTF8, bufferSize: 8, maxEventSize: rejectedLimit);
        var error = await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(oversized));
        Assert.Equal(rejectedLimit, error.MaxEventSize);
        Assert.Equal(exactLimit, error.ActualSize);
    }

    [Fact]
    public async Task ReadFramesAsync_MultilineData_CountsEncodedInsertedNewline()
    {
        const string content = "data: é\ndata: 🎵\n\n";
        await using var exactStream = new AsyncOnlyStream(content);
        var exact = new SseFramingReader(exactStream, Encoding.UTF8, bufferSize: 8, maxEventSize: 7);
        Assert.Equal("é\n🎵", Assert.Single(await CollectFramesAsync(exact)).Data);

        await using var oversizedStream = new AsyncOnlyStream(content);
        var oversized = new SseFramingReader(oversizedStream, Encoding.UTF8, bufferSize: 8, maxEventSize: 6);
        var error = await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(oversized));
        Assert.Equal(7, error.ActualSize);
    }

    [Fact]
    public async Task ReadFramesAsync_UsesSelectedEncodingForDataAndInsertedNewline()
    {
        const string content = "data: é\ndata: é\n\n";
        await using var exactStream = new EncodedAsyncOnlyStream(content, Encoding.Unicode);
        var exact = new SseFramingReader(exactStream, Encoding.Unicode, bufferSize: 8, maxEventSize: 6);
        Assert.Equal("é\né", Assert.Single(await CollectFramesAsync(exact)).Data);

        await using var oversizedStream = new EncodedAsyncOnlyStream(content, Encoding.Unicode);
        var oversized = new SseFramingReader(oversizedStream, Encoding.Unicode, bufferSize: 8, maxEventSize: 5);
        var error = await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(oversized));
        Assert.Equal(6, error.ActualSize);
    }

    [Fact]
    public async Task ReadFramesAsync_MultilineData_CountsRetainedUtf16IncludingInsertedNewlines()
    {
        await using var exactStream = new AsyncOnlyStream("data: ab\ndata: cd\ndata: ef\n\n");
        var exact = new SseFramingReader(exactStream, Encoding.UTF8, bufferSize: 8, maxEventSize: 8);
        Assert.Equal("ab\ncd\nef", Assert.Single(await CollectFramesAsync(exact)).Data);

        await using var oversizedStream = new AsyncOnlyStream("data: ab\ndata: cd\ndata: ef\ndata:\n\n");
        var oversized = new SseFramingReader(oversizedStream, Encoding.UTF8, bufferSize: 8, maxEventSize: 8);
        var error = await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(oversized));
        Assert.Equal(8, error.MaxEventSize);
        Assert.Equal(9, error.ActualSize);
    }

    public static TheoryData<string> HostilePhysicalLines => new()
    {
        "data: 0123456789",
        "id: 012345678901",
        "event: 0123456789",
        "retry: 012345678",
        ": 01234567890123",
        "fieldonly0123456",
        "unknown: 0123456",
    };

    [Theory]
    [MemberData(nameof(HostilePhysicalLines))]
    public async Task ReadFramesAsync_BoundedPhysicalLine_StopsBeforePostLimitSentinel(string content)
    {
        await using var stream = new SentinelAsyncOnlyStream(content, Encoding.UTF8);
        var reader = new SseFramingReader(stream, Encoding.UTF8, bufferSize: 8, maxEventSize: 8);

        await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(reader));
        Assert.False(stream.SentinelReadAttempted);
    }

    [Fact]
    public async Task ReadFramesAsync_PhysicalLineAtExactPrefixAllowance_Completes()
    {
        // UTF-8: maxEventSize 8 plus the longest recognized prefix ("event: ", 7) is 15 bytes.
        await using var stream = new AsyncOnlyStream(": 0123456789012\n\ndata: x\n\n");
        var reader = new SseFramingReader(stream, Encoding.UTF8, bufferSize: 8, maxEventSize: 8);
        Assert.Equal("x", Assert.Single(await CollectFramesAsync(reader)).Data);
    }

    [Fact]
    public async Task ReadFramesAsync_Utf16RetentionFailure_NamesDiagnosticUnit()
    {
        await using var stream = new AsyncOnlyStream("data: ab\ndata: cd\ndata: ef\ndata:\n\n");
        var reader = new SseFramingReader(stream, Encoding.UTF8, bufferSize: 8, maxEventSize: 8);
        var error = await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(reader));
        Assert.Equal(StreamFrameSizeUnit.Utf16CodeUnits, error.SizeUnit);
        Assert.Equal(8, error.MaxEventSize);
        Assert.Equal(9, error.ActualSize);
        Assert.Contains("retained UTF-16 code units", error.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    public async Task ReadFramesAsync_BoundedMode_PreservesLineTerminators(string terminator)
    {
        await using var stream = new AsyncOnlyStream($"data: first{terminator}data: second{terminator}{terminator}");
        var reader = new SseFramingReader(stream, Encoding.UTF8, bufferSize: 8, maxEventSize: 12);
        Assert.Equal("first\nsecond", Assert.Single(await CollectFramesAsync(reader)).Data);
    }

    [Fact]
    public async Task ReadFramesAsync_BoundedMode_CrPushesBackNextLineCharacter()
    {
        await using var stream = new AsyncOnlyStream("data: first\rdata: second\r\r");
        var reader = new SseFramingReader(stream, Encoding.UTF8, bufferSize: 8, maxEventSize: 12);
        Assert.Equal("first\nsecond", Assert.Single(await CollectFramesAsync(reader)).Data);
    }

    [Fact]
    public async Task ReadFramesAsync_ZeroOutputEncoding_BoundsPhysicalLineByRetainedCharacters()
    {
        await using var stream = new SentinelAsyncOnlyStream("unknown012345678", Encoding.ASCII);
        var reader = new SseFramingReader(stream, new ZeroOutputEncoding(), bufferSize: 8, maxEventSize: 8);
        var error = await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(reader));
        Assert.Equal(StreamFrameSizeUnit.Utf16CodeUnits, error.SizeUnit);
        Assert.False(stream.SentinelReadAttempted);
    }

    [Fact]
    public async Task ReadFramesAsync_ZeroOutputEncoding_BoundsAccumulatedEventDataBeforeSentinel()
    {
        await using var stream = new SentinelAsyncOnlyStream("data: a\ndata: a\ndata: a\ndata: a\ndata: a\n", Encoding.ASCII);
        var reader = new SseFramingReader(stream, new ZeroOutputEncoding(), bufferSize: 8, maxEventSize: 8);
        var error = await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(reader));
        Assert.Equal(StreamFrameSizeUnit.Utf16CodeUnits, error.SizeUnit);
        Assert.Equal(9, error.ActualSize);
        Assert.False(stream.SentinelReadAttempted);
    }

    [Fact]
    public async Task ReadFramesAsync_StatefulEncoderFlushBytesCountAtEventBoundary()
    {
        await using var exactStream = new SentinelAsyncOnlyStream("data: a\n\n", Encoding.ASCII, returnEof: true);
        var exact = new SseFramingReader(exactStream, new FlushSuffixEncoding(), bufferSize: 8, maxEventSize: 3);
        Assert.Equal("a", Assert.Single(await CollectFramesAsync(exact)).Data);

        await using var oversizedStream = new SentinelAsyncOnlyStream("data: a\n\n", Encoding.ASCII, returnEof: true);
        var oversized = new SseFramingReader(oversizedStream, new FlushSuffixEncoding(), bufferSize: 8, maxEventSize: 2);
        var error = await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(oversized));
        Assert.Equal(StreamFrameSizeUnit.EncodedBytes, error.SizeUnit);
        Assert.Equal(3, error.ActualSize);
    }

    [Fact]
    public async Task ReadFramesAsync_StatefulEncoderCountsMultilineNewlineAndResetsBetweenEvents()
    {
        const string content = "data: a\ndata: b\n\ndata: a\ndata: b\n\n";
        await using var exactStream = new SentinelAsyncOnlyStream(content, Encoding.ASCII, returnEof: true);
        var exact = new SseFramingReader(exactStream, new FlushSuffixEncoding(), bufferSize: 8, maxEventSize: 7);
        var frames = await CollectFramesAsync(exact);
        Assert.Equal(2, frames.Length);
        Assert.All(frames, frame => Assert.Equal("a\nb", frame.Data));

        await using var oversizedStream = new SentinelAsyncOnlyStream(content, Encoding.ASCII, returnEof: true);
        var oversized = new SseFramingReader(oversizedStream, new FlushSuffixEncoding(), bufferSize: 8, maxEventSize: 6);
        var error = await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(oversized));
        Assert.Equal(7, error.ActualSize);
    }

    [Fact]
    public async Task ReadFramesAsync_StatefulEncoderPreservesStateAcrossEventScratchChunks()
    {
        var payload = new string('a', 40);
        await using var stream = new SentinelAsyncOnlyStream($"data: {payload}\n\n", Encoding.ASCII, returnEof: true);
        var reader = new SseFramingReader(stream, new FlushSuffixEncoding(), bufferSize: 8, maxEventSize: 81);
        Assert.Equal(payload, Assert.Single(await CollectFramesAsync(reader)).Data);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r")]
    [InlineData("")]
    public async Task ReadFramesAsync_StatefulPhysicalLineFlush_ReportsEffectiveAllowance(string terminator)
    {
        await using var stream = new SentinelAsyncOnlyStream($"12345678{terminator}", Encoding.ASCII, returnEof: true);
        var reader = new SseFramingReader(stream, new FlushSuffixEncoding(), bufferSize: 8, maxEventSize: 1);
        var error = await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(reader));
        Assert.Equal(1, error.MaxEventSize);
        Assert.Equal(17, error.ActualSize);
        Assert.Contains("16 encoded bytes", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFramesAsync_NoProgressEncoder_FailsDeterministically()
    {
        await using var stream = new SentinelAsyncOnlyStream("data: x\n\n", Encoding.ASCII, returnEof: true);
        var reader = new SseFramingReader(stream, new NoProgressEncoding(), bufferSize: 8, maxEventSize: 8);
        var error = await Assert.ThrowsAsync<InvalidDataException>(() => CollectFramesAsync(reader));
        Assert.Contains("made no progress", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFramesAsync_EncoderDrainsMoreThanScratchCapacityForOneCharacter()
    {
        await using var exactStream = new SentinelAsyncOnlyStream("data: x\n\n", Encoding.ASCII, returnEof: true);
        var exact = new SseFramingReader(exactStream, new FortyByteEncoding(), bufferSize: 8, maxEventSize: 40);
        Assert.Equal("x", Assert.Single(await CollectFramesAsync(exact)).Data);

        await using var oversizedStream = new SentinelAsyncOnlyStream("data: x\n\n", Encoding.ASCII, returnEof: true);
        var oversized = new SseFramingReader(oversizedStream, new FortyByteEncoding(), bufferSize: 8, maxEventSize: 39);
        var error = await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(oversized));
        Assert.Equal(40, error.ActualSize);
    }

    [Fact]
    public async Task ReadFramesAsync_PhysicalAllowanceUsesLargestEncodedPrefix()
    {
        await using var stream = new SentinelAsyncOnlyStream("id: 12345678\n", Encoding.ASCII, returnEof: true);
        var reader = new SseFramingReader(stream, new SkewedPrefixEncoding(), bufferSize: 8, maxEventSize: 8);
        Assert.Empty(await CollectFramesAsync(reader));
    }

    [Fact]
    public void StreamFrameTooLargeException_LegacyConstructorAndSaturationRemainCompatible()
    {
        var legacy = new StreamFrameTooLargeException(8, 9);
        Assert.Equal(StreamFrameSizeUnit.EncodedBytes, legacy.SizeUnit);
        Assert.Equal(9, legacy.ActualSize);

        var saturated = new StreamFrameTooLargeException(8, (long)int.MaxValue + 42, StreamFrameSizeUnit.Utf16CodeUnits);
        Assert.Equal(int.MaxValue, saturated.ActualSize);
        Assert.Contains(((long)int.MaxValue + 42).ToString("N0"), saturated.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReadFramesAsync_CancellationDuringCrLookahead_RemainsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        await using var stream = new BlockingAsyncOnlyStream("data: partial\r");
        var task = CollectFramesAsync(new SseFramingReader(stream, Encoding.UTF8, bufferSize: 8, maxEventSize: 32), cancellation.Token);
        await stream.PendingRead.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Theory]
    [InlineData("\n")]
    [InlineData("\r\n")]
    [InlineData("\r")]
    [InlineData("")]
    public async Task ReadFramesAsync_SplitUtf8SurrogatePair_FinalizesAtEveryBoundary(string terminator)
    {
        await using var stream = new SentinelAsyncOnlyStream($"data: 🎵{terminator}", Encoding.UTF8, returnEof: true);
        var reader = new SseFramingReader(stream, Encoding.UTF8, bufferSize: 8, maxEventSize: 4);
        Assert.Equal("🎵", Assert.Single(await CollectFramesAsync(reader)).Data);
    }

    [Theory]
    [InlineData(10)]
    [InlineData(13)]
    [InlineData(-1)]
    public async Task ReadFramesAsync_UnpairedHighSurrogate_FlushesAtPhysicalBoundary(int terminator)
    {
        var bytes = new List<byte>(Encoding.ASCII.GetBytes("data: ")) { 0xff };
        if (terminator >= 0) bytes.Add((byte)terminator);
        await using var stream = new SentinelAsyncOnlyStream(bytes.ToArray(), returnEof: true);
        var reader = new SseFramingReader(stream, new UnpairedSurrogateEncoding(), bufferSize: 8, maxEventSize: 3);
        Assert.Equal("\ud800", Assert.Single(await CollectFramesAsync(reader)).Data);
    }

    [Fact]
    public async Task ReadFramesAsync_CancellationDuringSplitUtf8Sequence_RemainsCallerCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var complete = Encoding.UTF8.GetBytes("data: 🎵\n\n");
        await using var stream = new PrefixThenBlockingStream(complete[..^3]);
        var task = CollectFramesAsync(new SseFramingReader(stream, Encoding.UTF8, bufferSize: 8, maxEventSize: 8), cancellation.Token);
        await stream.PendingRead.WaitAsync(TimeSpan.FromSeconds(1));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    [Fact]
    public async Task ReadFramesAsync_BoundFailure_LeavesCallerStreamOpen()
    {
        var stream = new AsyncOnlyStream("data: oversized\n\n");
        var reader = new SseFramingReader(stream, Encoding.UTF8, bufferSize: 8, maxEventSize: 2);
        await Assert.ThrowsAsync<StreamFrameTooLargeException>(() => CollectFramesAsync(reader));
        Assert.True(stream.CanRead);
        await stream.DisposeAsync();
    }

    [Fact]
    public async Task ReadFramesAsync_UnlimitedMode_AllowsFiniteLargeLine()
    {
        var payload = new string('x', 64 * 1024);
        await using var stream = new AsyncOnlyStream($"data: {payload}\n\n");
        var reader = new SseFramingReader(stream, Encoding.UTF8, bufferSize: 8, maxEventSize: 0);
        Assert.Equal(payload, Assert.Single(await CollectFramesAsync(reader)).Data);
    }

    [Fact]
    public async Task ReadFramesAsync_UnlimitedMode_DoesNotConstructEncoder()
    {
        await using var stream = new SentinelAsyncOnlyStream("data: unlimited\n\n", Encoding.ASCII, returnEof: true);
        var reader = new SseFramingReader(stream, new ThrowingEncoderEncoding(), bufferSize: 8, maxEventSize: 0);
        Assert.Equal("unlimited", Assert.Single(await CollectFramesAsync(reader)).Data);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(32)]
    public async Task ReadFramesAsync_PreservesExistingBomHandling(int maximum)
    {
        await using var stream = new AsyncOnlyStream("\uFEFFdata: hidden-by-bom\n\n");
        var reader = new SseFramingReader(stream, Encoding.UTF8, bufferSize: 8, maxEventSize: maximum);
        Assert.Equal("hidden-by-bom", Assert.Single(await CollectFramesAsync(reader)).Data);
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

    private sealed class EncodedAsyncOnlyStream : MemoryStream
    {
        private readonly byte[] _data;

        public EncodedAsyncOnlyStream(string text, Encoding encoding) => _data = encoding.GetBytes(text);

        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("sync read is forbidden");

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = _data.Length - (int)Position;
            var count = Math.Min(remaining, buffer.Length);
            if (count > 0)
            {
                _data.AsSpan((int)Position, count).CopyTo(buffer.Span);
            }

            Position += count;
            return ValueTask.FromResult(count);
        }
    }

    private sealed class SentinelAsyncOnlyStream : Stream
    {
        private readonly byte[] _data;
        private int _position;

        private readonly bool _returnEof;

        public SentinelAsyncOnlyStream(string text, Encoding encoding, bool returnEof = false)
        {
            _data = encoding.GetBytes(text);
            _returnEof = returnEof;
        }

        public SentinelAsyncOnlyStream(byte[] data, bool returnEof = false)
        {
            _data = data;
            _returnEof = returnEof;
        }

        public bool SentinelReadAttempted { get; private set; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _data.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_position >= _data.Length)
            {
                if (_returnEof)
                {
                    return ValueTask.FromResult(0);
                }

                SentinelReadAttempted = true;
                throw new SentinelReadException();
            }

            buffer.Span[0] = _data[_position++];
            return ValueTask.FromResult(1);
        }
    }

    private sealed class SentinelReadException : Exception;

    private class DelegatingAsciiEncoding : Encoding
    {
        public override int GetByteCount(char[] chars, int index, int count) => Encoding.ASCII.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => Encoding.ASCII.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => Encoding.ASCII.GetCharCount(bytes, index, count);
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex) => Encoding.ASCII.GetChars(bytes, byteIndex, byteCount, chars, charIndex);
        public override int GetMaxByteCount(int charCount) => Encoding.ASCII.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => Encoding.ASCII.GetMaxCharCount(byteCount);
        public override Decoder GetDecoder() => Encoding.ASCII.GetDecoder();
    }

    private sealed class UnpairedSurrogateEncoding : Encoding
    {
        public override int GetByteCount(char[] chars, int index, int count) => Encoding.UTF8.GetByteCount(chars, index, count);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => Encoding.UTF8.GetBytes(chars, charIndex, charCount, bytes, byteIndex);
        public override int GetCharCount(byte[] bytes, int index, int count) => count;
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            for (var i = 0; i < byteCount; i++) chars[charIndex + i] = bytes[byteIndex + i] == 0xff ? '\ud800' : (char)bytes[byteIndex + i];
            return byteCount;
        }

        public override int GetMaxByteCount(int charCount) => Encoding.UTF8.GetMaxByteCount(charCount);
        public override int GetMaxCharCount(int byteCount) => byteCount;
        public override Decoder GetDecoder() => new UnpairedSurrogateDecoder();
        public override Encoder GetEncoder() => Encoding.UTF8.GetEncoder();
    }

    private sealed class UnpairedSurrogateDecoder : Decoder
    {
        public override int GetCharCount(byte[] bytes, int index, int count) => count;
        public override int GetChars(byte[] bytes, int byteIndex, int byteCount, char[] chars, int charIndex)
        {
            for (var i = 0; i < byteCount; i++) chars[charIndex + i] = bytes[byteIndex + i] == 0xff ? '\ud800' : (char)bytes[byteIndex + i];
            return byteCount;
        }
    }

    private sealed class ZeroOutputEncoding : DelegatingAsciiEncoding
    {
        public override int GetByteCount(char[] chars, int index, int count) => 0;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex) => 0;
        public override int GetMaxByteCount(int charCount) => 0;
        public override Encoder GetEncoder() => new ZeroOutputEncoder();
    }

    private sealed class ZeroOutputEncoder : Encoder
    {
        public override int GetByteCount(char[] chars, int index, int count, bool flush) => 0;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, bool flush) => 0;
        public override void Convert(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, int byteCount, bool flush, out int charsUsed, out int bytesUsed, out bool completed)
        {
            charsUsed = charCount;
            bytesUsed = 0;
            completed = true;
        }
    }

    private sealed class FlushSuffixEncoding : DelegatingAsciiEncoding
    {
        public override int GetByteCount(char[] chars, int index, int count) => count == 0 ? 0 : (count * 2) + 1;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            for (var i = 0; i < charCount; i++)
            {
                bytes[byteIndex + (i * 2)] = (byte)chars[charIndex + i];
                bytes[byteIndex + (i * 2) + 1] = 0;
            }

            if (charCount == 0) return 0;
            bytes[byteIndex + (charCount * 2)] = 0x7e;
            return (charCount * 2) + 1;
        }

        public override int GetMaxByteCount(int charCount) => (charCount * 2) + 1;
        public override Encoder GetEncoder() => new FlushSuffixEncoder();
    }

    private sealed class FlushSuffixEncoder : Encoder
    {
        private bool _hasInput;
        private int _remainingForCharacter;
        public override int GetByteCount(char[] chars, int index, int count, bool flush) => (count * 2) + (flush && (_hasInput || count > 0) ? 1 : 0);
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, bool flush)
        {
            for (var i = 0; i < charCount; i++)
            {
                bytes[byteIndex + (i * 2)] = (byte)chars[charIndex + i];
                bytes[byteIndex + (i * 2) + 1] = 0;
            }
            _hasInput |= charCount > 0;
            var written = charCount * 2;
            if (flush && _hasInput)
            {
                bytes[byteIndex + written++] = 0x7e;
                _hasInput = false;
            }

            return written;
        }

        public override void Convert(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, int byteCount, bool flush, out int charsUsed, out int bytesUsed, out bool completed)
        {
            charsUsed = 0;
            bytesUsed = 0;
            while (charsUsed < charCount && bytesUsed < byteCount)
            {
                if (_remainingForCharacter == 0) _remainingForCharacter = 2;
                var emitted = Math.Min(_remainingForCharacter, byteCount - bytesUsed);
                Array.Fill(bytes, (byte)'x', byteIndex + bytesUsed, emitted);
                bytesUsed += emitted;
                _remainingForCharacter -= emitted;
                if (_remainingForCharacter == 0)
                {
                    charsUsed++;
                    _hasInput = true;
                }
            }

            if (flush && charsUsed == charCount && _remainingForCharacter == 0 && _hasInput && bytesUsed < byteCount)
            {
                bytes[byteIndex + bytesUsed++] = 0x7e;
                _hasInput = false;
            }

            completed = charsUsed == charCount && _remainingForCharacter == 0 && (!flush || !_hasInput);
        }
    }

    private sealed class NoProgressEncoding : DelegatingAsciiEncoding
    {
        public override Encoder GetEncoder() => new NoProgressEncoder();
    }

    private sealed class FortyByteEncoding : DelegatingAsciiEncoding
    {
        public override int GetByteCount(char[] chars, int index, int count) => count * 40;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            Array.Fill(bytes, (byte)'x', byteIndex, charCount * 40);
            return charCount * 40;
        }
        public override int GetMaxByteCount(int charCount) => charCount * 40;
        public override Encoder GetEncoder() => new FortyByteEncoder();
    }

    private sealed class FortyByteEncoder : Encoder
    {
        private int _remaining;
        public override int GetByteCount(char[] chars, int index, int count, bool flush) => count * 40;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, bool flush) => throw new NotSupportedException();
        public override void Convert(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, int byteCount, bool flush, out int charsUsed, out int bytesUsed, out bool completed)
        {
            if (_remaining == 0 && charCount > 0) _remaining = 40;
            bytesUsed = Math.Min(_remaining, byteCount);
            Array.Fill(bytes, (byte)'x', byteIndex, bytesUsed);
            _remaining -= bytesUsed;
            charsUsed = _remaining == 0 && charCount > 0 ? 1 : 0;
            completed = _remaining == 0 && charsUsed == charCount;
        }
    }

    private sealed class SkewedPrefixEncoding : DelegatingAsciiEncoding
    {
        public override int GetByteCount(char[] chars, int index, int count)
        {
            var expensive = 0;
            for (var i = index; i < index + count; i++) if (chars[i] is 'i' or 'd') expensive++;
            return (expensive * 20) + (count - expensive);
        }

        public override int GetMaxByteCount(int charCount) => charCount * 20;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex)
        {
            var written = 0;
            for (var i = charIndex; i < charIndex + charCount; i++)
            {
                var count = chars[i] is 'i' or 'd' ? 20 : 1;
                Array.Fill(bytes, (byte)chars[i], byteIndex + written, count);
                written += count;
            }

            return written;
        }

        public override Encoder GetEncoder() => new ExpensiveCharacterEncoder();
    }

    private sealed class ExpensiveCharacterEncoder : Encoder
    {
        private int _remaining;
        public override int GetByteCount(char[] chars, int index, int count, bool flush) => count * 20;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, bool flush) => throw new NotSupportedException();
        public override void Convert(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, int byteCount, bool flush, out int charsUsed, out int bytesUsed, out bool completed)
        {
            if (charCount == 0)
            {
                charsUsed = 0;
                bytesUsed = 0;
                completed = true;
                return;
            }

            if (_remaining == 0)
            {
                _remaining = chars[charIndex] is 'i' or 'd' ? 20 : 1;
            }

            bytesUsed = Math.Min(_remaining, byteCount);
            Array.Fill(bytes, (byte)'x', byteIndex, bytesUsed);
            _remaining -= bytesUsed;
            charsUsed = _remaining == 0 ? 1 : 0;
            completed = charsUsed == charCount;
        }
    }

    private sealed class ThrowingEncoderEncoding : DelegatingAsciiEncoding
    {
        public override Encoder GetEncoder() => throw new InvalidOperationException("Unlimited mode must not construct an encoder.");
    }

    private sealed class NoProgressEncoder : Encoder
    {
        public override int GetByteCount(char[] chars, int index, int count, bool flush) => 0;
        public override int GetBytes(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, bool flush) => 0;
        public override void Convert(char[] chars, int charIndex, int charCount, byte[] bytes, int byteIndex, int byteCount, bool flush, out int charsUsed, out int bytesUsed, out bool completed)
        {
            charsUsed = 0;
            bytesUsed = 0;
            completed = false;
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

    private sealed class PrefixThenBlockingStream : Stream
    {
        private readonly byte[] _prefix;
        private readonly TaskCompletionSource<bool> _pendingRead = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private int _position;

        public PrefixThenBlockingStream(byte[] prefix) => _prefix = prefix;
        public Task PendingRead => _pendingRead.Task;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _prefix.Length;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() => throw new NotSupportedException();
        public override int Read(byte[] buffer, int offset, int count) => throw new InvalidOperationException("sync read is forbidden");
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_position < _prefix.Length)
            {
                buffer.Span[0] = _prefix[_position++];
                return ValueTask.FromResult(1);
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
