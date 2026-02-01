// <copyright file="GeminiStreamDecoderContractTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Llm;
using Lidarr.Plugin.Common.Streaming;
using Lidarr.Plugin.Common.Streaming.Decoders;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Streaming;

/// <summary>
/// Contract tests for Gemini stream decoder.
/// </summary>
/// <remarks>
/// Gemini uses SSE with JSON payloads in a different format than OpenAI:
/// - Content-Type: text/event-stream
/// - Data format: {"candidates":[{"content":{"parts":[{"text":"..."}]}}]}
/// - Finish signal: finishReason in candidates
/// - Usage: usageMetadata at message end
/// </remarks>
public class GeminiStreamDecoderContractTests : StreamContractTestsBase
{
    /// <inheritdoc />
    protected override string ExpectedContentType => "text/event-stream";

    /// <inheritdoc />
    protected override string ProviderId => "gemini";

    /// <inheritdoc />
    protected override IStreamDecoder CreateDecoder() => new GeminiStreamDecoder();

    /// <inheritdoc />
    protected override Stream CreateDoneTerminatedStream()
    {
        // Gemini uses finishReason instead of [DONE]
        return CreateFinishReasonTerminatedStream();
    }

    /// <inheritdoc />
    protected override Stream CreateFinishReasonTerminatedStream()
    {
        var content = @"data: {""candidates"":[{""content"":{""parts"":[{""text"":""Hello""}]},""index"":0}]}

data: {""candidates"":[{""content"":{""parts"":[{""text"":"" world""}]},""finishReason"":""STOP""}]}

data: {""usageMetadata"":{""promptTokenCount"":10,""candidatesTokenCount"":5,""totalTokenCount"":15}}

";
        return CreateStream(content);
    }

    /// <inheritdoc />
    protected override Stream CreateMultipleChunksStream(int chunkCount)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < chunkCount - 1; i++)
        {
            sb.AppendLine($@"data: {{""candidates"":[{{""content"":{{""parts"":[{{""text"":""chunk{i}""}}]}},""index"":0}}]}}");
            sb.AppendLine();
        }

        // Final chunk with finish reason
        sb.AppendLine($@"data: {{""candidates"":[{{""content"":{{""parts"":[{{""text"":""final""}}]}},""finishReason"":""STOP""}}]}}");
        sb.AppendLine();

        return CreateStream(sb.ToString());
    }

    /// <inheritdoc />
    protected override Stream CreateMalformedStream()
    {
        var content = @"data: not valid json

data: {""candidates"":[]}

data: {""partial"": true

data: {""candidates"":[{""content"":{""parts"":[{""text"":""valid""}]},""finishReason"":""STOP""}]}

";
        return CreateStream(content);
    }

    [Fact]
    public void Decoder_Exists()
    {
        var decoder = new GeminiStreamDecoder();
        Assert.Equal("gemini", decoder.DecoderId);
    }

    [Fact]
    public void Decoder_SupportedProviders_IncludesGemini()
    {
        var decoder = new GeminiStreamDecoder();
        Assert.Contains("gemini", decoder.SupportedProviderIds);
        Assert.Contains("google", decoder.SupportedProviderIds);
    }

    [Fact]
    public async Task Decoder_ParsesGeminiFormat()
    {
        // Arrange
        var content = @"data: {""candidates"":[{""content"":{""parts"":[{""text"":""Hello""}]},""index"":0}]}

data: {""candidates"":[{""content"":{""parts"":[{""text"":"" world""}]},""finishReason"":""STOP""}]}

data: {""usageMetadata"":{""promptTokenCount"":10,""candidatesTokenCount"":5,""totalTokenCount"":15}}

";
        using var stream = CreateStream(content);
        var decoder = CreateDecoder();

        // Act
        var chunks = await CollectChunksAsync(decoder, stream);

        // Assert
        Assert.True(chunks.Count >= 2, $"Expected at least 2 chunks, got {chunks.Count}");
        Assert.Equal("Hello", chunks[0].ContentDelta);
        Assert.Equal(" world", chunks[1].ContentDelta);
        Assert.True(chunks.Last().IsComplete);
    }

    [Fact]
    public async Task Decoder_ExtractsUsageMetadata()
    {
        // Arrange
        var content = @"data: {""candidates"":[{""content"":{""parts"":[{""text"":""Test""}]},""finishReason"":""STOP""}],""usageMetadata"":{""promptTokenCount"":10,""candidatesTokenCount"":5,""totalTokenCount"":15}}

";
        using var stream = CreateStream(content);
        var decoder = CreateDecoder();

        // Act
        var chunks = await CollectChunksAsync(decoder, stream);

        // Assert
        var lastChunk = chunks.Last();
        Assert.True(lastChunk.IsComplete);
        Assert.NotNull(lastChunk.FinalUsage);
        Assert.Equal(10, lastChunk.FinalUsage.InputTokens);
        Assert.Equal(5, lastChunk.FinalUsage.OutputTokens);
    }

    [Fact]
    public async Task Decoder_HandlesMultipleParts()
    {
        // Arrange - Gemini can send multiple parts in a single chunk
        var content = @"data: {""candidates"":[{""content"":{""parts"":[{""text"":""Hello""},{""text"":"" world""}]},""finishReason"":""STOP""}]}

";
        using var stream = CreateStream(content);
        var decoder = CreateDecoder();

        // Act
        var chunks = await CollectChunksAsync(decoder, stream);

        // Assert
        Assert.True(chunks.Count >= 1);
        Assert.Equal("Hello world", chunks[0].ContentDelta);
    }

    [Fact]
    public async Task Decoder_HandlesSeparateUsageChunk()
    {
        // Arrange - Usage may come in a separate chunk after finish
        var content = @"data: {""candidates"":[{""content"":{""parts"":[{""text"":""Done""}]},""finishReason"":""STOP""}]}

data: {""usageMetadata"":{""promptTokenCount"":20,""candidatesTokenCount"":10,""totalTokenCount"":30}}

";
        using var stream = CreateStream(content);
        var decoder = CreateDecoder();

        // Act
        var chunks = await CollectChunksAsync(decoder, stream);

        // Assert - completion should happen at finishReason, usage from that point
        Assert.True(chunks.Last().IsComplete);
    }

    [Fact]
    public void CanDecodeForProvider_Gemini_ReturnsTrue()
    {
        var decoder = new GeminiStreamDecoder();
        Assert.True(decoder.CanDecodeForProvider("gemini", "text/event-stream"));
        Assert.True(decoder.CanDecodeForProvider("google", "text/event-stream"));
    }

    [Fact]
    public void CanDecodeForProvider_NonGemini_ReturnsFalse()
    {
        var decoder = new GeminiStreamDecoder();
        Assert.False(decoder.CanDecodeForProvider("openai", "text/event-stream"));
        Assert.False(decoder.CanDecodeForProvider("zai", "text/event-stream"));
    }
}
