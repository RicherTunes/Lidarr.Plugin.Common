// <copyright file="ZaiStreamDecoderContractTests.cs" company="RicherTunes">
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
/// Contract tests for Z.AI GLM stream decoder.
/// </summary>
/// <remarks>
/// Z.AI GLM uses SSE similar to OpenAI but with some differences:
/// - Content-Type: text/event-stream
/// - Data format: similar to OpenAI but with GLM-specific fields
/// - May include web_search results in some responses
/// - Usage in final message
/// </remarks>
public class ZaiStreamDecoderContractTests : StreamContractTestsBase
{
    /// <inheritdoc />
    protected override string ExpectedContentType => "text/event-stream";

    /// <inheritdoc />
    protected override string ProviderId => "zai";

    /// <inheritdoc />
    protected override IStreamDecoder CreateDecoder() => new ZaiStreamDecoder();

    /// <inheritdoc />
    protected override Stream CreateDoneTerminatedStream()
    {
        var content = @"data: {""id"":""abc123"",""created"":1234567890,""model"":""glm-4"",""choices"":[{""index"":0,""delta"":{""role"":""assistant"",""content"":""Hello""}}]}

data: {""id"":""abc123"",""created"":1234567890,""model"":""glm-4"",""choices"":[{""index"":0,""delta"":{""content"":"" world""}}]}

data: [DONE]

";
        return CreateStream(content);
    }

    /// <inheritdoc />
    protected override Stream CreateFinishReasonTerminatedStream()
    {
        var content = @"data: {""id"":""abc123"",""created"":1234567890,""model"":""glm-4"",""choices"":[{""index"":0,""delta"":{""role"":""assistant"",""content"":""Hello""}}]}

data: {""id"":""abc123"",""created"":1234567890,""model"":""glm-4"",""choices"":[{""index"":0,""delta"":{},""finish_reason"":""stop""}],""usage"":{""prompt_tokens"":10,""completion_tokens"":5,""total_tokens"":15}}

";
        return CreateStream(content);
    }

    /// <inheritdoc />
    protected override Stream CreateMultipleChunksStream(int chunkCount)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < chunkCount; i++)
        {
            sb.AppendLine($@"data: {{""id"":""abc123"",""created"":1234567890,""model"":""glm-4"",""choices"":[{{""index"":0,""delta"":{{""content"":""chunk{i}""}}}}]}}");
            sb.AppendLine();
        }

        sb.AppendLine("data: [DONE]");
        sb.AppendLine();

        return CreateStream(sb.ToString());
    }

    /// <inheritdoc />
    protected override Stream CreateMalformedStream()
    {
        var content = @"data: not valid json

data: {""choices"":[]}

data: {""partial"": true

data: {""id"":""abc123"",""choices"":[{""index"":0,""delta"":{""content"":""valid""},""finish_reason"":""stop""}]}

";
        return CreateStream(content);
    }

    [Fact]
    public void Decoder_Exists()
    {
        var decoder = new ZaiStreamDecoder();
        Assert.Equal("zai", decoder.DecoderId);
    }

    [Fact]
    public void Decoder_SupportedProviders_IncludesZai()
    {
        var decoder = new ZaiStreamDecoder();
        Assert.Contains("zai", decoder.SupportedProviderIds);
        Assert.Contains("glm", decoder.SupportedProviderIds);
        Assert.Contains("zhipu", decoder.SupportedProviderIds);
    }

    [Fact]
    public async Task Decoder_ParsesZaiFormat()
    {
        // Arrange
        var content = @"data: {""id"":""abc123"",""created"":1234567890,""model"":""glm-4"",""choices"":[{""index"":0,""delta"":{""role"":""assistant"",""content"":""Hello""}}]}

data: {""id"":""abc123"",""created"":1234567890,""model"":""glm-4"",""choices"":[{""index"":0,""delta"":{""content"":"" world""}}]}

data: {""id"":""abc123"",""created"":1234567890,""model"":""glm-4"",""choices"":[{""index"":0,""delta"":{},""finish_reason"":""stop""}],""usage"":{""prompt_tokens"":10,""completion_tokens"":5,""total_tokens"":15}}

data: [DONE]

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
    public async Task Decoder_ExtractsUsage()
    {
        // Arrange
        var content = @"data: {""id"":""abc123"",""choices"":[{""index"":0,""delta"":{""content"":""Test""},""finish_reason"":""stop""}],""usage"":{""prompt_tokens"":10,""completion_tokens"":5,""total_tokens"":15}}

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
    public async Task Decoder_HandlesWebSearchResults()
    {
        // Arrange - Z.AI GLM can include web_search in the response
        // This should be handled gracefully (not crash)
        var content = @"data: {""id"":""abc123"",""choices"":[{""index"":0,""delta"":{""content"":""Based on search results...""}}],""web_search"":{""search_results"":[{""title"":""Test"",""link"":""https://example.com"",""content"":""Example""}]}}

data: {""id"":""abc123"",""choices"":[{""index"":0,""delta"":{},""finish_reason"":""stop""}]}

";
        using var stream = CreateStream(content);
        var decoder = CreateDecoder();

        // Act
        var chunks = await CollectChunksAsync(decoder, stream);

        // Assert - should parse successfully without crashing
        Assert.NotEmpty(chunks);
        Assert.Equal("Based on search results...", chunks[0].ContentDelta);
        Assert.True(chunks.Last().IsComplete);
    }

    [Fact]
    public void CanDecodeForProvider_Zai_ReturnsTrue()
    {
        var decoder = new ZaiStreamDecoder();
        Assert.True(decoder.CanDecodeForProvider("zai", "text/event-stream"));
        Assert.True(decoder.CanDecodeForProvider("glm", "text/event-stream"));
        Assert.True(decoder.CanDecodeForProvider("zhipu", "text/event-stream"));
    }

    [Fact]
    public void CanDecodeForProvider_NonZai_ReturnsFalse()
    {
        var decoder = new ZaiStreamDecoder();
        Assert.False(decoder.CanDecodeForProvider("openai", "text/event-stream"));
        Assert.False(decoder.CanDecodeForProvider("gemini", "text/event-stream"));
    }

    [Fact]
    public async Task Decoder_HandlesDoneTerminator()
    {
        // Arrange
        var content = @"data: {""id"":""abc123"",""choices"":[{""index"":0,""delta"":{""content"":""Hello""}}]}

data: [DONE]

";
        using var stream = CreateStream(content);
        var decoder = CreateDecoder();

        // Act
        var chunks = await CollectChunksAsync(decoder, stream);

        // Assert
        Assert.Equal(2, chunks.Count);
        Assert.Equal("Hello", chunks[0].ContentDelta);
        Assert.True(chunks[1].IsComplete);
    }
}
