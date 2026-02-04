// <copyright file="ProviderHealthResultTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Text.Json;
using Xunit;

using Lidarr.Plugin.Common.Abstractions.Llm;

namespace Lidarr.Plugin.Common.Tests.Providers.Llm;

/// <summary>
/// Unit tests for <see cref="ProviderHealthResult"/>.
/// Tests the new DIAG-02 fields: provider, authMethod, model, errorCode.
/// </summary>
public class ProviderHealthResultTests
{
    #region Constructor with DIAG-02 fields

    [Fact]
    public void Constructor_WithAllFields_SetsPropertiesCorrectly()
    {
        // Arrange & Act
        var result = new ProviderHealthResult
        {
            IsHealthy = false,
            StatusMessage = "Connection failed",
            ResponseTime = TimeSpan.FromSeconds(1),
            Provider = "openai",
            AuthMethod = "apiKey",
            Model = "gpt-4",
            ErrorCode = "AUTH_FAILED"
        };

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Equal("Connection failed", result.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(1), result.ResponseTime);
        Assert.Equal("openai", result.Provider);
        Assert.Equal("apiKey", result.AuthMethod);
        Assert.Equal("gpt-4", result.Model);
        Assert.Equal("AUTH_FAILED", result.ErrorCode);
    }

    [Fact]
    public void Constructor_WithOnlyNewFields_HasNullValuesForOldFields()
    {
        // Arrange & Act
        var result = new ProviderHealthResult
        {
            IsHealthy = true,
            Provider = "claude-code",
            AuthMethod = "cli"
        };

        // Assert
        Assert.True(result.IsHealthy);
        Assert.Null(result.StatusMessage);
        Assert.Null(result.ResponseTime);
        Assert.Equal("claude-code", result.Provider);
        Assert.Equal("cli", result.AuthMethod);
        Assert.Null(result.Model);
        Assert.Null(result.ErrorCode);
    }

    #endregion

    #region Healthy() static factory method

    [Fact]
    public void Healthy_WithNoParameters_ReturnsHealthyResult()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Healthy();

        // Assert
        Assert.NotNull(result);
        Assert.True(result!.IsHealthy);
        Assert.Null(result.StatusMessage);
        Assert.Null(result.ResponseTime);
        Assert.Null(result.Provider);
        Assert.Null(result.AuthMethod);
        Assert.Null(result.Model);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Healthy_WithResponseTime_SetsResponseTime()
    {
        // Arrange & Act
        var responseTime = TimeSpan.FromMilliseconds(250);
        var result = ProviderHealthResult.Healthy(responseTime);

        // Assert
        Assert.True(result.IsHealthy);
        Assert.Equal(responseTime, result.ResponseTime);
    }

    [Fact]
    public void Healthy_WithDiagnosticFields_SetsAllFields()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Healthy(
            responseTime: TimeSpan.FromSeconds(2),
            provider: "glm",
            authMethod: "apiKey",
            model: "glm-4-flash");

        // Assert
        Assert.True(result.IsHealthy);
        Assert.Equal(TimeSpan.FromSeconds(2), result.ResponseTime);
        Assert.Equal("glm", result.Provider);
        Assert.Equal("apiKey", result.AuthMethod);
        Assert.Equal("glm-4-flash", result.Model);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Healthy_WithDiagnosticFieldsOnly_SetsNullableFields()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Healthy(
            provider: "openai",
            authMethod: "cli",
            model: "gpt-3.5-turbo");

        // Assert
        Assert.True(result.IsHealthy);
        Assert.Equal("openai", result.Provider);
        Assert.Equal("cli", result.AuthMethod);
        Assert.Equal("gpt-3.5-turbo", result.Model);
    }

    #endregion

    #region Unhealthy() static factory method

    [Fact]
    public void Unhealthy_WithReason_SetsUnhealthyState()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Unhealthy("API key is invalid");

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Equal("API key is invalid", result.StatusMessage);
        Assert.Null(result.Provider);
        Assert.Null(result.AuthMethod);
        Assert.Null(result.Model);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Unhealthy_WithDiagnosticFields_SetsAllFields()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Unhealthy(
            reason: "Rate limit exceeded",
            responseTime: TimeSpan.FromSeconds(1),
            provider: "anthropic",
            authMethod: "apiKey",
            model: "claude-3-opus",
            errorCode: "RATE_LIMITED");

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Equal("Rate limit exceeded", result.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(1), result.ResponseTime);
        Assert.Equal("anthropic", result.Provider);
        Assert.Equal("apiKey", result.AuthMethod);
        Assert.Equal("claude-3-opus", result.Model);
        Assert.Equal("RATE_LIMITED", result.ErrorCode);
    }

    [Fact]
    public void Unhealthy_WithErrorResponseCode_SetsErrorCode()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Unhealthy(
            reason: "Connection timeout",
            errorCode: "TIMEOUT");

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Equal("TIMEOUT", result.ErrorCode);
    }

    [Fact]
    public void Unhealthy_WithAuthMethod_SetsAuthMethod()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Unhealthy(
            reason: "Authentication failed",
            authMethod: "deviceCode");

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Equal("deviceCode", result.AuthMethod);
    }

    [Fact]
    public void Unhealthy_WithProvider_SetsProvider()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Unhealthy(
            reason: "Provider not available",
            provider: "ollama");

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Equal("ollama", result.Provider);
    }

    [Fact]
    public void Unhealthy_WithModel_SetsModel()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Unhealthy(
            reason: "Model not found",
            model: "unknown-model");

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Equal("unknown-model", result.Model);
    }

    #endregion

    #region Degraded() static factory method

    [Fact]
    public void Degraded_WithReason_SetsDegradedState()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Degraded("High latency detected");

        // Assert
        Assert.True(result.IsHealthy);
        Assert.Equal("[Degraded] High latency detected", result.StatusMessage);
        Assert.Null(result.Provider);
        Assert.Null(result.AuthMethod);
        Assert.Null(result.Model);
    }

    [Fact]
    public void Degraded_WithDiagnosticFields_SetsAllFields()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Degraded(
            reason: "Response time exceeding threshold",
            responseTime: TimeSpan.FromSeconds(5),
            provider: "gemini",
            authMethod: "oauth",
            model: "gemini-pro");

        // Assert
        Assert.True(result.IsHealthy);
        Assert.StartsWith("[Degraded]", result.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(5), result.ResponseTime);
        Assert.Equal("gemini", result.Provider);
        Assert.Equal("oauth", result.AuthMethod);
        Assert.Equal("gemini-pro", result.Model);
    }

    [Fact]
    public void Degraded_WithProvider_SetsProvider()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Degraded("Provider responding slowly", provider: "zai-glm");

        // Assert
        Assert.Equal("zai-glm", result.Provider);
    }

    #endregion

    #region Serialization/Deserialization

    [Fact]
    public void SerializeToJson_WithAllFields_IncludesAllProperties()
    {
        // Arrange
        var result = new ProviderHealthResult
        {
            IsHealthy = false,
            StatusMessage = "Connection failed",
            ResponseTime = TimeSpan.FromSeconds(1),
            Provider = "openai",
            AuthMethod = "apiKey",
            Model = "gpt-4",
            ErrorCode = "AUTH_FAILED"
        };

        // Act
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<ProviderHealthResult>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.False(deserialized!.IsHealthy);
        Assert.Equal("Connection failed", deserialized.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(1), deserialized.ResponseTime);
        Assert.Equal("openai", deserialized.Provider);
        Assert.Equal("apiKey", deserialized.AuthMethod);
        Assert.Equal("gpt-4", deserialized.Model);
        Assert.Equal("AUTH_FAILED", deserialized.ErrorCode);
    }

    [Fact]
    public void SerializeToJson_WithNullDiagnosticFields_IncludesNullValues()
    {
        // Arrange
        var result = ProviderHealthResult.Healthy(responseTime: TimeSpan.FromMilliseconds(100));

        // Act
        var json = JsonSerializer.Serialize(result);
        var deserialized = JsonSerializer.Deserialize<ProviderHealthResult>(json);

        // Assert
        Assert.NotNull(deserialized);
        Assert.True(deserialized!.IsHealthy);
        Assert.Equal(TimeSpan.FromMilliseconds(100), deserialized.ResponseTime);
        Assert.Null(deserialized.Provider);
        Assert.Null(deserialized.AuthMethod);
        Assert.Null(deserialized.Model);
        Assert.Null(deserialized.ErrorCode);
    }

    [Fact]
    public void Deserialize_EmptyJson_ReturnsDefaultInstance()
    {
        // Arrange
        var json = "{}";

        // Act
        var result = JsonSerializer.Deserialize<ProviderHealthResult>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? new ProviderHealthResult();

        // Assert - when deserializing from empty JSON, nullable fields remain null
        Assert.Null(result.StatusMessage);
        Assert.Null(result.ResponseTime);
        Assert.Null(result.Provider);
        Assert.Null(result.AuthMethod);
        Assert.Null(result.Model);
        Assert.Null(result.ErrorCode);
    }

    #endregion

    #region Backward compatibility

    [Fact]
    public void Constructor_WithOldConstructor_SetsOldProperties()
    {
        // Arrange & Act - old constructor
        var result = new ProviderHealthResult
        {
            IsHealthy = true,
            StatusMessage = "Healthy",
            ResponseTime = TimeSpan.FromSeconds(1)
        };

        // Assert - old properties should still be set
        Assert.True(result.IsHealthy);
        Assert.Equal("Healthy", result.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(1), result.ResponseTime);
        // New fields should be null
        Assert.Null(result.Provider);
        Assert.Null(result.AuthMethod);
        Assert.Null(result.Model);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Healthy_WithOldSignature_ReturnsHealthyResult()
    {
        // Act
        var result = ProviderHealthResult.Healthy(responseTime: TimeSpan.FromSeconds(1));

        // Assert
        Assert.True(result.IsHealthy);
        Assert.Equal(TimeSpan.FromSeconds(1), result.ResponseTime);
        Assert.Null(result.Provider);
        Assert.Null(result.AuthMethod);
    }

    [Fact]
    public void Unhealthy_WithOldSignature_ReturnsUnhealthyResult()
    {
        // Act
        var result = ProviderHealthResult.Unhealthy("Error", responseTime: TimeSpan.FromSeconds(2));

        // Assert
        Assert.False(result.IsHealthy);
        Assert.Equal("Error", result.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(2), result.ResponseTime);
        Assert.Null(result.Provider);
        Assert.Null(result.AuthMethod);
    }

    [Fact]
    public void Degraded_WithOldSignature_ReturnsDegradedResult()
    {
        // Act
        var result = ProviderHealthResult.Degraded("Warning", responseTime: TimeSpan.FromMilliseconds(500));

        // Assert
        Assert.True(result.IsHealthy);
        Assert.StartsWith("[Degraded]", result.StatusMessage);
        Assert.Equal(TimeSpan.FromMilliseconds(500), result.ResponseTime);
        Assert.Null(result.Provider);
        Assert.Null(result.AuthMethod);
    }

    #endregion
}
