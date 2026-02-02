// <copyright file="ProviderHealthResultTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Text.Json;
using FluentAssertions;
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
        result.IsHealthy.Should().BeFalse();
        result.StatusMessage.Should().Be("Connection failed");
        result.ResponseTime.Should().Be(TimeSpan.FromSeconds(1));
        result.Provider.Should().Be("openai");
        result.AuthMethod.Should().Be("apiKey");
        result.Model.Should().Be("gpt-4");
        result.ErrorCode.Should().Be("AUTH_FAILED");
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
        result.IsHealthy.Should().BeTrue();
        result.StatusMessage.Should().BeNull();
        result.ResponseTime.Should().BeNull();
        result.Provider.Should().Be("claude-code");
        result.AuthMethod.Should().Be("cli");
        result.Model.Should().BeNull();
        result.ErrorCode.Should().BeNull();
    }

    #endregion

    #region Healthy() static factory method

    [Fact]
    public void Healthy_WithNoParameters_ReturnsHealthyResult()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Healthy();

        // Assert
        result.Should().NotBeNull();
        result!.IsHealthy.Should().BeTrue();
        result.StatusMessage.Should().BeNull();
        result.ResponseTime.Should().BeNull();
        result.Provider.Should().BeNull();
        result.AuthMethod.Should().BeNull();
        result.Model.Should().BeNull();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void Healthy_WithResponseTime_SetsResponseTime()
    {
        // Arrange & Act
        var responseTime = TimeSpan.FromMilliseconds(250);
        var result = ProviderHealthResult.Healthy(responseTime);

        // Assert
        result.IsHealthy.Should().BeTrue();
        result.ResponseTime.Should().Be(responseTime);
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
        result.IsHealthy.Should().BeTrue();
        result.ResponseTime.Should().Be(TimeSpan.FromSeconds(2));
        result.Provider.Should().Be("glm");
        result.AuthMethod.Should().Be("apiKey");
        result.Model.Should().Be("glm-4-flash");
        result.ErrorCode.Should().BeNull();
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
        result.IsHealthy.Should().BeTrue();
        result.Provider.Should().Be("openai");
        result.AuthMethod.Should().Be("cli");
        result.Model.Should().Be("gpt-3.5-turbo");
    }

    #endregion

    #region Unhealthy() static factory method

    [Fact]
    public void Unhealthy_WithReason_SetsUnhealthyState()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Unhealthy("API key is invalid");

        // Assert
        result.IsHealthy.Should().BeFalse();
        result.StatusMessage.Should().Be("API key is invalid");
        result.Provider.Should().BeNull();
        result.AuthMethod.Should().BeNull();
        result.Model.Should().BeNull();
        result.ErrorCode.Should().BeNull();
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
        result.IsHealthy.Should().BeFalse();
        result.StatusMessage.Should().Be("Rate limit exceeded");
        result.ResponseTime.Should().Be(TimeSpan.FromSeconds(1));
        result.Provider.Should().Be("anthropic");
        result.AuthMethod.Should().Be("apiKey");
        result.Model.Should().Be("claude-3-opus");
        result.ErrorCode.Should().Be("RATE_LIMITED");
    }

    [Fact]
    public void Unhealthy_WithErrorResponseCode_SetsErrorCode()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Unhealthy(
            reason: "Connection timeout",
            errorCode: "TIMEOUT");

        // Assert
        result.IsHealthy.Should().BeFalse();
        result.ErrorCode.Should().Be("TIMEOUT");
    }

    [Fact]
    public void Unhealthy_WithAuthMethod_SetsAuthMethod()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Unhealthy(
            reason: "Authentication failed",
            authMethod: "deviceCode");

        // Assert
        result.IsHealthy.Should().BeFalse();
        result.AuthMethod.Should().Be("deviceCode");
    }

    [Fact]
    public void Unhealthy_WithProvider_SetsProvider()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Unhealthy(
            reason: "Provider not available",
            provider: "ollama");

        // Assert
        result.IsHealthy.Should().BeFalse();
        result.Provider.Should().Be("ollama");
    }

    [Fact]
    public void Unhealthy_WithModel_SetsModel()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Unhealthy(
            reason: "Model not found",
            model: "unknown-model");

        // Assert
        result.IsHealthy.Should().BeFalse();
        result.Model.Should().Be("unknown-model");
    }

    #endregion

    #region Degraded() static factory method

    [Fact]
    public void Degraded_WithReason_SetsDegradedState()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Degraded("High latency detected");

        // Assert
        result.IsHealthy.Should().BeTrue();
        result.StatusMessage.Should().Be("[Degraded] High latency detected");
        result.Provider.Should().BeNull();
        result.AuthMethod.Should().BeNull();
        result.Model.Should().BeNull();
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
        result.IsHealthy.Should().BeTrue();
        result.StatusMessage.Should().StartWith("[Degraded]");
        result.ResponseTime.Should().Be(TimeSpan.FromSeconds(5));
        result.Provider.Should().Be("gemini");
        result.AuthMethod.Should().Be("oauth");
        result.Model.Should().Be("gemini-pro");
    }

    [Fact]
    public void Degraded_WithProvider_SetsProvider()
    {
        // Arrange & Act
        var result = ProviderHealthResult.Degraded("Provider responding slowly", provider: "zai-glm");

        // Assert
        result.Provider.Should().Be("zai-glm");
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
        deserialized.Should().NotBeNull();
        deserialized!.IsHealthy.Should().BeFalse();
        deserialized.StatusMessage.Should().Be("Connection failed");
        deserialized.ResponseTime.Should().Be(TimeSpan.FromSeconds(1));
        deserialized.Provider.Should().Be("openai");
        deserialized.AuthMethod.Should().Be("apiKey");
        deserialized.Model.Should().Be("gpt-4");
        deserialized.ErrorCode.Should().Be("AUTH_FAILED");
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
        deserialized.Should().NotBeNull();
        deserialized!.IsHealthy.Should().BeTrue();
        deserialized.ResponseTime.Should().Be(TimeSpan.FromMilliseconds(100));
        deserialized.Provider.Should().BeNull();
        deserialized.AuthMethod.Should().BeNull();
        deserialized.Model.Should().BeNull();
        deserialized.ErrorCode.Should().BeNull();
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
        result.StatusMessage.Should().BeNull();
        result.ResponseTime.Should().BeNull();
        result.Provider.Should().BeNull();
        result.AuthMethod.Should().BeNull();
        result.Model.Should().BeNull();
        result.ErrorCode.Should().BeNull();
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
        result.IsHealthy.Should().BeTrue();
        result.StatusMessage.Should().Be("Healthy");
        result.ResponseTime.Should().Be(TimeSpan.FromSeconds(1));
        // New fields should be null
        result.Provider.Should().BeNull();
        result.AuthMethod.Should().BeNull();
        result.Model.Should().BeNull();
        result.ErrorCode.Should().BeNull();
    }

    [Fact]
    public void Healthy_WithOldSignature_ReturnsHealthyResult()
    {
        // Act
        var result = ProviderHealthResult.Healthy(responseTime: TimeSpan.FromSeconds(1));

        // Assert
        result.IsHealthy.Should().BeTrue();
        result.ResponseTime.Should().Be(TimeSpan.FromSeconds(1));
        result.Provider.Should().BeNull();
        result.AuthMethod.Should().BeNull();
    }

    [Fact]
    public void Unhealthy_WithOldSignature_ReturnsUnhealthyResult()
    {
        // Act
        var result = ProviderHealthResult.Unhealthy("Error", responseTime: TimeSpan.FromSeconds(2));

        // Assert
        result.IsHealthy.Should().BeFalse();
        result.StatusMessage.Should().Be("Error");
        result.ResponseTime.Should().Be(TimeSpan.FromSeconds(2));
        result.Provider.Should().BeNull();
        result.AuthMethod.Should().BeNull();
    }

    [Fact]
    public void Degraded_WithOldSignature_ReturnsDegradedResult()
    {
        // Act
        var result = ProviderHealthResult.Degraded("Warning", responseTime: TimeSpan.FromMilliseconds(500));

        // Assert
        result.IsHealthy.Should().BeTrue();
        result.StatusMessage.Should().StartWith("[Degraded]");
        result.ResponseTime.Should().Be(TimeSpan.FromMilliseconds(500));
        result.Provider.Should().BeNull();
        result.AuthMethod.Should().BeNull();
    }

    #endregion
}
