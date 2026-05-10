// <copyright file="ClaudeCodeResponseParserCovTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Text.Json;
using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Providers.ClaudeCode;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Providers.ClaudeCode;

/// <summary>
/// Coverage tests for <see cref="ClaudeCodeResponseParser"/>.
/// Tests uncovered paths including inner exception preservation,
/// exception properties, and edge cases.
/// </summary>
public class ClaudeCodeResponseParserCovTests
{
    #region Inner Exception Preservation

    [Fact]
    public void ParseJsonResponse_JsonException_PreservesInnerException()
    {
        // Arrange - JSON with unclosed string causes JsonException
        // Line 55: throw new ProviderException(..., ex) - inner exception should be preserved
        var json = """{"result": "unclosed string, "session_id": "", "num_turns": 0, "duration_ms": 0}""";

        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(json));

        Assert.NotNull(ex.InnerException);
        Assert.IsType<JsonException>(ex.InnerException);
        Assert.Contains("parse CLI JSON response", ex.Message);
    }

    [Fact]
    public void ParseJsonResponse_JsonExceptionWithTrailingComma_HasInnerException()
    {
        // Arrange - JSON with trailing comma (invalid in strict JSON)
        var json = """{"result": "test", "session_id": "", "num_turns": 0, "duration_ms": 0, }""";

        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(json));

        Assert.NotNull(ex.InnerException);
        Assert.Equal("claude-code", ex.ProviderId);
        Assert.Equal(LlmErrorCode.InvalidRequest, ex.ErrorCode);
    }

    #endregion

    #region Exception Properties Validation

    [Fact]
    public void ParseJsonResponse_EmptyInput_ExceptionHasCorrectProperties()
    {
        // Arrange - Line 38: throw for empty input
        var json = "";

        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(json));

        Assert.Equal("claude-code", ex.ProviderId);
        Assert.Equal(LlmErrorCode.InvalidRequest, ex.ErrorCode);
        Assert.Contains("empty response", ex.Message);
    }

    [Fact]
    public void ParseJsonResponse_WhitespaceInput_ExceptionHasCorrectProperties()
    {
        // Arrange
        var json = "   \t\n   ";

        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(json));

        Assert.Equal("claude-code", ex.ProviderId);
        Assert.Equal(LlmErrorCode.InvalidRequest, ex.ErrorCode);
    }

    [Fact]
    public void ParseJsonResponse_NullDeserialization_ExceptionHasCorrectProperties()
    {
        // Arrange - Line 60: throw for null after deserialization
        var json = "null";

        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(json));

        Assert.Equal("claude-code", ex.ProviderId);
        Assert.Equal(LlmErrorCode.InvalidRequest, ex.ErrorCode);
        Assert.Contains("null response", ex.Message);
    }

    [Fact]
    public void ParseJsonResponse_IsErrorTrue_ExceptionHasCorrectProperties()
    {
        // Arrange - Line 68: throw for is_error=true
        var json = """{"is_error": true, "result": "API error", "session_id": "", "num_turns": 0, "duration_ms": 0}""";

        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(json));

        Assert.Equal("claude-code", ex.ProviderId);
        Assert.Equal(LlmErrorCode.InvalidRequest, ex.ErrorCode);
        Assert.Contains("API error", ex.Message);
    }

    [Fact]
    public void ParseJsonResponse_IsErrorTrueWithNullResult_UsesDefaultErrorMessage()
    {
        // Arrange - is_error with null result should use default message
        var json = """{"is_error": true, "result": null, "session_id": "", "num_turns": 0, "duration_ms": 0}""";

        // Act & Assert
        var ex = Assert.Throws<ProviderException>(() =>
            ClaudeCodeResponseParser.ParseJsonResponse(json));

        Assert.Equal("claude-code", ex.ProviderId);
        Assert.Equal(LlmErrorCode.InvalidRequest, ex.ErrorCode);
        Assert.Equal("Unknown CLI error", ex.Message);
    }

    #endregion

    #region Metadata Construction

    [Fact]
    public void ParseJsonResponse_ValidResponse_MetadataContainsAllKeys()
    {
        // Arrange - BuildMetadata should create dictionary with 3 keys
        var json = """
        {
            "result": "test",
            "session_id": "session-123",
            "num_turns": 5,
            "duration_ms": 1500,
            "is_error": false
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert - Metadata should have exactly 3 keys
        Assert.NotNull(response.Metadata);
        Assert.Equal(3, response.Metadata.Count);
        Assert.True(response.Metadata.ContainsKey("session_id"));
        Assert.True(response.Metadata.ContainsKey("num_turns"));
        Assert.True(response.Metadata.ContainsKey("duration_ms"));
    }

    [Fact]
    public void ParseJsonResponse_ValidResponse_MetadataValuesAreCorrect()
    {
        // Arrange
        var json = """
        {
            "result": "test",
            "session_id": "abc-def",
            "num_turns": 10,
            "duration_ms": 5000,
            "is_error": false
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.NotNull(response.Metadata);
        Assert.Equal("abc-def", response.Metadata["session_id"]);
        Assert.Equal(10, response.Metadata["num_turns"]);
        Assert.Equal(5000, response.Metadata["duration_ms"]);
    }

    [Fact]
    public void ParseJsonResponse_EmptyObjectMetadata_HasDefaultValues()
    {
        // Arrange - Empty object has default values (string empty, int 0)
        var json = "{}";

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.NotNull(response.Metadata);
        Assert.Equal(3, response.Metadata.Count);
        Assert.Equal("", response.Metadata["session_id"]);
        Assert.Equal(0, response.Metadata["num_turns"]);
        Assert.Equal(0, response.Metadata["duration_ms"]);
    }

    #endregion

    #region Usage Mapping Edge Cases

    [Fact]
    public void ParseJsonResponse_UsageWithAllFields_MapsAllFields()
    {
        // Arrange - MapUsage should map all usage fields
        var json = """
        {
            "result": "test",
            "session_id": "",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 100,
            "usage": {
                "input_tokens": 1000,
                "output_tokens": 500
            },
            "total_cost_usd": 0.0125
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert - MapUsage lines 90-95
        Assert.NotNull(response.Usage);
        Assert.Equal(1000, response.Usage.InputTokens);
        Assert.Equal(500, response.Usage.OutputTokens);
        Assert.Equal(0.0125m, response.Usage.EstimatedCostUsd);
    }

    [Fact]
    public void ParseJsonResponse_UsageWithNullTotalCost_HasZeroCost()
    {
        // Arrange - total_cost_usd is null, should default to 0
        var json = """
        {
            "result": "test",
            "session_id": "",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 100,
            "usage": {
                "input_tokens": 100,
                "output_tokens": 50
            }
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.NotNull(response.Usage);
        Assert.Equal(100, response.Usage.InputTokens);
        Assert.Equal(50, response.Usage.OutputTokens);
        Assert.Null(response.Usage.EstimatedCostUsd);
    }

    [Fact]
    public void ParseJsonResponse_UsageWithZeroTokens_MapsCorrectly()
    {
        // Arrange - Edge case: zero tokens
        var json = """
        {
            "result": "cached response",
            "session_id": "",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 10,
            "usage": {
                "input_tokens": 0,
                "output_tokens": 0
            }
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.NotNull(response.Usage);
        Assert.Equal(0, response.Usage.InputTokens);
        Assert.Equal(0, response.Usage.OutputTokens);
    }

    [Fact]
    public void ParseJsonResponse_VeryLargeCost_MapsCorrectly()
    {
        // Arrange - Large cost value
        var json = """
        {
            "result": "expensive result",
            "session_id": "",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 1000,
            "usage": {
                "input_tokens": 1000000,
                "output_tokens": 500000
            },
            "total_cost_usd": 15.50
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.NotNull(response.Usage);
        Assert.Equal(1000000, response.Usage.InputTokens);
        Assert.Equal(500000, response.Usage.OutputTokens);
        Assert.Equal(15.50m, response.Usage.EstimatedCostUsd);
    }

    #endregion

    #region Response Structure Edge Cases

    [Fact]
    public void ParseJsonResponse_ResponseWithExtraFields_ParsesCorrectly()
    {
        // Arrange - JSON with extra unknown fields (should be ignored)
        var json = """
        {
            "result": "test",
            "session_id": "",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 100,
            "unknown_field": "ignored",
            "another_unknown": 12345
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert - Should parse successfully, ignoring extra fields
        Assert.Equal("test", response.Content);
        Assert.Equal("stop", response.FinishReason);
    }

    [Fact]
    public void ParseJsonResponse_ResultWithEscapedCharacters_ParsesCorrectly()
    {
        // Arrange - Result with escaped JSON characters
        var json = """
        {
            "result": "Line 1\nLine 2\tTabbed\"Quote\"",
            "session_id": "",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 100
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.Contains("\n", response.Content);
        Assert.Contains("\t", response.Content);
        Assert.Contains("\"", response.Content);
    }

    [Fact]
    public void ParseJsonResponse_SessionIdWithSpecialCharacters_ParsesCorrectly()
    {
        // Arrange - Session ID with dashes, underscores
        var json = """
        {
            "result": "test",
            "session_id": "sess_123-abc_def",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 100
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.NotNull(response.Metadata);
        Assert.Equal("sess_123-abc_def", response.Metadata["session_id"]);
    }

    #endregion

    #region Finish Reason

    [Fact]
    public void ParseJsonResponse_ValidResponse_AlwaysReturnsStopFinishReason()
    {
        // Arrange - FinishReason is hardcoded to "stop" on line 78
        var json = """
        {
            "result": "any response",
            "session_id": "",
            "is_error": false,
            "num_turns": 1,
            "duration_ms": 100
        }
        """;

        // Act
        var response = ClaudeCodeResponseParser.ParseJsonResponse(json);

        // Assert
        Assert.Equal("stop", response.FinishReason);
    }

    #endregion
}
