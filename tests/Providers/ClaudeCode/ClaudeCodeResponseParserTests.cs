// <copyright file="ClaudeCodeResponseParserTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Providers.ClaudeCode;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Providers.ClaudeCode;

/// <summary>
/// Unit tests for <see cref="ClaudeCodeResponseParser"/>.
/// Tests JSON parsing of Claude Code CLI output including valid responses,
/// error responses, and malformed input.
/// </summary>
public class ClaudeCodeResponseParserTests
{
    #region Valid JSON Parsing

    [Fact]
    public void ParseJsonResponse_ValidFullResponse_ReturnsLlmResponse()
    {
        // Arrange
        var json = """
        {
            "type": "result",
            "result": "Hello, world!",
            "session_id": "abc123",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 500,
            "total_cost_usd": 0.001,
            "usage": {
                "input_tokens": 10,
                "output_tokens": 5
            }
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.Equal("Hello, world!", response.Content);
        Assert.Equal("stop", response.FinishReason);
        Assert.NotNull(response.Usage);
        Assert.Equal(10, response.Usage.InputTokens);
        Assert.Equal(5, response.Usage.OutputTokens);
        Assert.Equal(0.001m, response.Usage.EstimatedCostUsd);
        Assert.NotNull(response.Metadata);
        Assert.Equal("abc123", response.Metadata["session_id"]);
        Assert.Equal(1, response.Metadata["num_turns"]);
        Assert.Equal(500, response.Metadata["duration_ms"]);
    }

    [Fact]
    public void ParseJsonResponse_MinimalValidResponse_ReturnsLlmResponse()
    {
        // Arrange - only required fields, no usage
        var json = """
        {
            "type": "result",
            "result": "Minimal response",
            "session_id": "",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 100
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.Equal("Minimal response", response.Content);
        Assert.Null(response.Usage);
        Assert.Equal("stop", response.FinishReason);
    }

    [Fact]
    public void ParseJsonResponse_NullResult_ReturnsEmptyContent()
    {
        // Arrange
        var json = """
        {
            "type": "result",
            "result": null,
            "session_id": "test",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 50
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.Equal("", response.Content);
    }

    [Fact]
    public void ParseJsonResponse_WithNullUsage_ReturnsNullUsage()
    {
        // Arrange
        var json = """
        {
            "type": "result",
            "result": "Response text",
            "session_id": "test123",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 200,
            "usage": null
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.Equal("Response text", response.Content);
        Assert.Null(response.Usage);
    }

    [Fact]
    public void ParseJsonResponse_WithZeroCost_ReturnsCostAsZero()
    {
        // Arrange
        var json = """
        {
            "type": "result",
            "result": "Free response",
            "session_id": "test",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 100,
            "total_cost_usd": 0.0,
            "usage": {
                "input_tokens": 5,
                "output_tokens": 3
            }
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.NotNull(response.Usage);
        Assert.Equal(0.0m, response.Usage.EstimatedCostUsd);
    }

    #endregion

    #region Error Response Handling

    [Fact]
    public void ParseJsonResponse_ErrorResponse_ThrowsProviderException()
    {
        // Arrange
        var json = """{"is_error": true, "result": "Rate limit exceeded", "session_id": "", "num_turns": 0, "duration_ms": 0}""";

        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(json));

        Assert.Contains("Rate limit exceeded", ex.Message);
        Assert.Equal("claude-code", ex.ProviderId);
        Assert.Equal(LlmErrorCode.InvalidRequest, ex.ErrorCode);
    }

    [Fact]
    public void ParseJsonResponse_ErrorResponseWithNullResult_ThrowsProviderExceptionWithDefaultMessage()
    {
        // Arrange
        var json = """{"is_error": true, "result": null, "session_id": "", "num_turns": 0, "duration_ms": 0}""";

        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(json));

        Assert.Contains("Unknown CLI error", ex.Message);
    }

    [Fact]
    public void ParseJsonResponse_ErrorResponseWithEmptyResult_ThrowsProviderException()
    {
        // Arrange
        var json = """{"is_error": true, "result": "", "session_id": "", "num_turns": 0, "duration_ms": 0}""";

        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(json));

        // Empty result should still be considered an error (is_error=true)
        Assert.IsType<ProviderException>(ex);
    }

    #endregion

    #region Malformed Input

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("\t\n")]
    public void ParseJsonResponse_EmptyOrWhitespace_ThrowsProviderException(string input)
    {
        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(input));

        Assert.Contains("empty response", ex.Message);
        Assert.Equal(LlmErrorCode.InvalidRequest, ex.ErrorCode);
    }

    [Theory]
    [InlineData("not json at all")]
    [InlineData("{ invalid json }")]
    [InlineData("{ \"key\": }")]
    [InlineData("[1, 2, 3]")] // Valid JSON but wrong type
    public void ParseJsonResponse_InvalidJson_ThrowsProviderException(string input)
    {
        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(input));

        Assert.Contains("parse", ex.Message.ToLowerInvariant());
        Assert.Equal(LlmErrorCode.InvalidRequest, ex.ErrorCode);
    }

    [Fact]
    public void ParseJsonResponse_JsonNull_ThrowsProviderException()
    {
        // Arrange - JSON "null" literal deserializes to null
        var json = "null";

        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(json));

        Assert.Contains("null response", ex.Message.ToLowerInvariant());
    }

    [Fact]
    public void ParseJsonResponse_EmptyObject_ReturnsEmptyContent()
    {
        // Arrange - Empty object has default values (is_error=false)
        var json = "{}";

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert - Should work with defaults (result=null becomes "")
        Assert.Equal("", response.Content);
    }

    #endregion

    #region Edge Cases

    [Fact]
    public void ParseJsonResponse_LargeTokenCounts_ParsesCorrectly()
    {
        // Arrange
        var json = """
        {
            "type": "result",
            "result": "Large context response",
            "session_id": "large-test",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 30000,
            "total_cost_usd": 0.50,
            "usage": {
                "input_tokens": 150000,
                "output_tokens": 8000
            }
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.NotNull(response.Usage);
        Assert.Equal(150000, response.Usage.InputTokens);
        Assert.Equal(8000, response.Usage.OutputTokens);
        Assert.Equal(0.50m, response.Usage.EstimatedCostUsd);
    }

    [Fact]
    public void ParseJsonResponse_UnicodeContent_ParsesCorrectly()
    {
        // Arrange
        var json = """
        {
            "type": "result",
            "result": "Unicode test: \u4e2d\u6587 \u65e5\u672c\u8a9e \ud83c\udfb5",
            "session_id": "unicode",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 100
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.Contains("\u4e2d\u6587", response.Content); // Chinese
        Assert.Contains("\u65e5\u672c\u8a9e", response.Content); // Japanese
    }

    [Fact]
    public void ParseJsonResponse_MultilineContent_PreservesNewlines()
    {
        // Arrange
        var json = """
        {
            "type": "result",
            "result": "Line 1\nLine 2\nLine 3",
            "session_id": "multiline",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 100
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.Contains("\n", response.Content);
        Assert.Equal("Line 1\nLine 2\nLine 3", response.Content);
    }

    [Fact]
    public void ParseJsonResponse_CaseInsensitivePropertyNames_ParsesCorrectly()
    {
        // Arrange - Use different case for property names
        var json = """
        {
            "TYPE": "result",
            "RESULT": "Case test",
            "SESSION_ID": "case-test",
            "IS_ERROR": false,
            "NUM_TURNS": 1,
            "DURATION_MS": 100
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert - Parser should handle case-insensitive property names
        Assert.Equal("Case test", response.Content);
    }

    #endregion
}
