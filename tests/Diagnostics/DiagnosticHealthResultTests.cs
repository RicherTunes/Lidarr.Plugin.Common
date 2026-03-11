// <copyright file="DiagnosticHealthResultTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.Text.Json;
using Xunit;

using Lidarr.Plugin.Common.Abstractions.Diagnostics;
using Lidarr.Plugin.Common.Abstractions.Llm;

namespace Lidarr.Plugin.Common.Tests.Diagnostics;

/// <summary>
/// Unit tests for <see cref="DiagnosticHealthResult"/>.
/// Verifies the new non-LLM diagnostics type per issue #317 (DIAG-02).
/// </summary>
public class DiagnosticHealthResultTests
{
    #region Constructor tests

    [Fact]
    public void Constructor_WithAllFields_SetsPropertiesCorrectly()
    {
        var result = new DiagnosticHealthResult
        {
            IsHealthy = false,
            StatusMessage = "OAuth token expired",
            ResponseTime = TimeSpan.FromSeconds(2),
            Provider = "tidal",
            AuthMethod = "oauth",
            DiagnosticType = "auth_validate",
            Capability = "hi_res_streaming",
            ErrorCode = "AUTH_FAILED",
        };

        Assert.False(result.IsHealthy);
        Assert.Equal("OAuth token expired", result.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(2), result.ResponseTime);
        Assert.Equal("tidal", result.Provider);
        Assert.Equal("oauth", result.AuthMethod);
        Assert.Equal("auth_validate", result.DiagnosticType);
        Assert.Equal("hi_res_streaming", result.Capability);
        Assert.Equal("AUTH_FAILED", result.ErrorCode);
    }

    [Fact]
    public void Constructor_WithMinimalFields_HasNullDefaults()
    {
        var result = new DiagnosticHealthResult
        {
            IsHealthy = true,
            Provider = "qobuz",
        };

        Assert.True(result.IsHealthy);
        Assert.Null(result.StatusMessage);
        Assert.Null(result.ResponseTime);
        Assert.Equal("qobuz", result.Provider);
        Assert.Null(result.AuthMethod);
        Assert.Null(result.DiagnosticType);
        Assert.Null(result.Capability);
        Assert.Null(result.ErrorCode);
    }

    #endregion

    #region Healthy() factory method

    [Fact]
    public void Healthy_WithNoParameters_ReturnsHealthyResult()
    {
        var result = DiagnosticHealthResult.Healthy();

        Assert.True(result.IsHealthy);
        Assert.Null(result.StatusMessage);
        Assert.Null(result.ResponseTime);
        Assert.Null(result.Provider);
        Assert.Null(result.AuthMethod);
        Assert.Null(result.DiagnosticType);
        Assert.Null(result.Capability);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Healthy_WithResponseTime_SetsResponseTime()
    {
        var responseTime = TimeSpan.FromMilliseconds(150);
        var result = DiagnosticHealthResult.Healthy(responseTime);

        Assert.True(result.IsHealthy);
        Assert.Equal(responseTime, result.ResponseTime);
    }

    [Fact]
    public void Healthy_WithAllDiagnosticFields_SetsAllFields()
    {
        var result = DiagnosticHealthResult.Healthy(
            responseTime: TimeSpan.FromSeconds(1),
            provider: "tidal",
            authMethod: "oauth",
            diagnosticType: "quality_detect",
            capability: "hi_res_streaming");

        Assert.True(result.IsHealthy);
        Assert.Equal(TimeSpan.FromSeconds(1), result.ResponseTime);
        Assert.Equal("tidal", result.Provider);
        Assert.Equal("oauth", result.AuthMethod);
        Assert.Equal("quality_detect", result.DiagnosticType);
        Assert.Equal("hi_res_streaming", result.Capability);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Healthy_WithDiagnosticFieldsOnly_SetsNullableFields()
    {
        var result = DiagnosticHealthResult.Healthy(
            provider: "qobuz",
            authMethod: "apiKey",
            diagnosticType: "connectivity");

        Assert.True(result.IsHealthy);
        Assert.Null(result.ResponseTime);
        Assert.Equal("qobuz", result.Provider);
        Assert.Equal("apiKey", result.AuthMethod);
        Assert.Equal("connectivity", result.DiagnosticType);
        Assert.Null(result.Capability);
    }

    #endregion

    #region Unhealthy() factory method

    [Fact]
    public void Unhealthy_WithReason_SetsUnhealthyState()
    {
        var result = DiagnosticHealthResult.Unhealthy("Service unavailable");

        Assert.False(result.IsHealthy);
        Assert.Equal("Service unavailable", result.StatusMessage);
        Assert.Null(result.Provider);
        Assert.Null(result.DiagnosticType);
        Assert.Null(result.Capability);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Unhealthy_WithAllDiagnosticFields_SetsAllFields()
    {
        var result = DiagnosticHealthResult.Unhealthy(
            reason: "Token refresh failed",
            responseTime: TimeSpan.FromSeconds(3),
            provider: "tidal",
            authMethod: "oauth",
            diagnosticType: "auth_validate",
            capability: "lossless_download",
            errorCode: "AUTH_FAILED");

        Assert.False(result.IsHealthy);
        Assert.Equal("Token refresh failed", result.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(3), result.ResponseTime);
        Assert.Equal("tidal", result.Provider);
        Assert.Equal("oauth", result.AuthMethod);
        Assert.Equal("auth_validate", result.DiagnosticType);
        Assert.Equal("lossless_download", result.Capability);
        Assert.Equal("AUTH_FAILED", result.ErrorCode);
    }

    [Fact]
    public void Unhealthy_WithErrorCode_SetsErrorCode()
    {
        var result = DiagnosticHealthResult.Unhealthy(
            reason: "Region not supported",
            errorCode: "REGION_BLOCKED");

        Assert.False(result.IsHealthy);
        Assert.Equal("REGION_BLOCKED", result.ErrorCode);
    }

    [Fact]
    public void Unhealthy_WithProvider_SetsProvider()
    {
        var result = DiagnosticHealthResult.Unhealthy(
            reason: "API endpoint down",
            provider: "apple-music");

        Assert.False(result.IsHealthy);
        Assert.Equal("apple-music", result.Provider);
    }

    [Fact]
    public void Unhealthy_WithDiagnosticType_SetsDiagnosticType()
    {
        var result = DiagnosticHealthResult.Unhealthy(
            reason: "Quality probe failed",
            diagnosticType: "quality_detect");

        Assert.False(result.IsHealthy);
        Assert.Equal("quality_detect", result.DiagnosticType);
    }

    [Fact]
    public void Unhealthy_WithCapability_SetsCapability()
    {
        var result = DiagnosticHealthResult.Unhealthy(
            reason: "Catalog access denied",
            capability: "catalog_access");

        Assert.False(result.IsHealthy);
        Assert.Equal("catalog_access", result.Capability);
    }

    #endregion

    #region Degraded() factory method

    [Fact]
    public void Degraded_WithReason_SetsDegradedState()
    {
        var result = DiagnosticHealthResult.Degraded("High latency to API");

        Assert.True(result.IsHealthy);
        Assert.Equal("[Degraded] High latency to API", result.StatusMessage);
        Assert.Null(result.Provider);
        Assert.Null(result.DiagnosticType);
        Assert.Null(result.Capability);
    }

    [Fact]
    public void Degraded_WithAllDiagnosticFields_SetsAllFields()
    {
        var result = DiagnosticHealthResult.Degraded(
            reason: "Reduced quality available",
            responseTime: TimeSpan.FromSeconds(5),
            provider: "qobuz",
            authMethod: "apiKey",
            diagnosticType: "quality_detect",
            capability: "hi_res_streaming");

        Assert.True(result.IsHealthy);
        Assert.StartsWith("[Degraded]", result.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(5), result.ResponseTime);
        Assert.Equal("qobuz", result.Provider);
        Assert.Equal("apiKey", result.AuthMethod);
        Assert.Equal("quality_detect", result.DiagnosticType);
        Assert.Equal("hi_res_streaming", result.Capability);
    }

    [Fact]
    public void Degraded_WithProvider_SetsProvider()
    {
        var result = DiagnosticHealthResult.Degraded(
            reason: "Slow response",
            provider: "tidal");

        Assert.Equal("tidal", result.Provider);
    }

    #endregion

    #region Serialization/Deserialization

    [Fact]
    public void SerializeToJson_WithAllFields_RoundTrips()
    {
        var original = new DiagnosticHealthResult
        {
            IsHealthy = false,
            StatusMessage = "Token expired",
            ResponseTime = TimeSpan.FromSeconds(2),
            Provider = "tidal",
            AuthMethod = "oauth",
            DiagnosticType = "auth_validate",
            Capability = "hi_res_streaming",
            ErrorCode = "AUTH_FAILED",
        };

        var json = JsonSerializer.Serialize(original, new JsonSerializerOptions { WriteIndented = true });
        var deserialized = JsonSerializer.Deserialize<DiagnosticHealthResult>(json);

        Assert.NotNull(deserialized);
        Assert.False(deserialized!.IsHealthy);
        Assert.Equal("Token expired", deserialized.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(2), deserialized.ResponseTime);
        Assert.Equal("tidal", deserialized.Provider);
        Assert.Equal("oauth", deserialized.AuthMethod);
        Assert.Equal("auth_validate", deserialized.DiagnosticType);
        Assert.Equal("hi_res_streaming", deserialized.Capability);
        Assert.Equal("AUTH_FAILED", deserialized.ErrorCode);
    }

    [Fact]
    public void SerializeToJson_WithNullFields_RoundTrips()
    {
        var original = DiagnosticHealthResult.Healthy(responseTime: TimeSpan.FromMilliseconds(100));

        var json = JsonSerializer.Serialize(original);
        var deserialized = JsonSerializer.Deserialize<DiagnosticHealthResult>(json);

        Assert.NotNull(deserialized);
        Assert.True(deserialized!.IsHealthy);
        Assert.Equal(TimeSpan.FromMilliseconds(100), deserialized.ResponseTime);
        Assert.Null(deserialized.Provider);
        Assert.Null(deserialized.AuthMethod);
        Assert.Null(deserialized.DiagnosticType);
        Assert.Null(deserialized.Capability);
        Assert.Null(deserialized.ErrorCode);
    }

    [Fact]
    public void Deserialize_EmptyJson_ReturnsDefaultInstance()
    {
        var json = "{}";

        var result = JsonSerializer.Deserialize<DiagnosticHealthResult>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        }) ?? new DiagnosticHealthResult();

        Assert.Null(result.StatusMessage);
        Assert.Null(result.ResponseTime);
        Assert.Null(result.Provider);
        Assert.Null(result.AuthMethod);
        Assert.Null(result.DiagnosticType);
        Assert.Null(result.Capability);
        Assert.Null(result.ErrorCode);
    }

    [Fact]
    public void Deserialize_OldLlmJsonWithModel_IgnoresUnknownField()
    {
        // JSON from a ProviderHealthResult with "Model" field — DiagnosticHealthResult has no Model,
        // so it should be silently ignored (backward compat for schema transition).
        var json = """{"IsHealthy":true,"Provider":"openai","Model":"gpt-4"}""";

        var result = JsonSerializer.Deserialize<DiagnosticHealthResult>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
        });

        Assert.NotNull(result);
        Assert.True(result!.IsHealthy);
        Assert.Equal("openai", result.Provider);
        Assert.Null(result.DiagnosticType); // Model is NOT mapped automatically
    }

    #endregion

    #region Adapter: FromLlmResult

    [Fact]
    public void FromLlmResult_HealthyResult_MapsAllFields()
    {
        var llm = ProviderHealthResult.Healthy(
            responseTime: TimeSpan.FromSeconds(1),
            provider: "claude-code",
            authMethod: "cli",
            model: "claude-3-opus");

        var diag = DiagnosticHealthResult.FromLlmResult(llm);

        Assert.True(diag.IsHealthy);
        Assert.Null(diag.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(1), diag.ResponseTime);
        Assert.Equal("claude-code", diag.Provider);
        Assert.Equal("cli", diag.AuthMethod);
        Assert.Equal("claude-3-opus", diag.DiagnosticType); // Model → DiagnosticType
        Assert.Null(diag.Capability); // No equivalent in LLM result
        Assert.Null(diag.ErrorCode);
    }

    [Fact]
    public void FromLlmResult_UnhealthyResult_MapsAllFields()
    {
        var llm = ProviderHealthResult.Unhealthy(
            reason: "Rate limit exceeded",
            responseTime: TimeSpan.FromSeconds(3),
            provider: "openai",
            authMethod: "apiKey",
            model: "gpt-4",
            errorCode: "RATE_LIMITED");

        var diag = DiagnosticHealthResult.FromLlmResult(llm);

        Assert.False(diag.IsHealthy);
        Assert.Equal("Rate limit exceeded", diag.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(3), diag.ResponseTime);
        Assert.Equal("openai", diag.Provider);
        Assert.Equal("apiKey", diag.AuthMethod);
        Assert.Equal("gpt-4", diag.DiagnosticType);
        Assert.Equal("RATE_LIMITED", diag.ErrorCode);
    }

    [Fact]
    public void FromLlmResult_DegradedResult_PreservesStatusPrefix()
    {
        var llm = ProviderHealthResult.Degraded(
            reason: "High latency",
            provider: "gemini");

        var diag = DiagnosticHealthResult.FromLlmResult(llm);

        Assert.True(diag.IsHealthy);
        Assert.StartsWith("[Degraded]", diag.StatusMessage);
        Assert.Equal("gemini", diag.Provider);
    }

    [Fact]
    public void FromLlmResult_NullFields_MapsToNulls()
    {
        var llm = ProviderHealthResult.Healthy();

        var diag = DiagnosticHealthResult.FromLlmResult(llm);

        Assert.True(diag.IsHealthy);
        Assert.Null(diag.Provider);
        Assert.Null(diag.AuthMethod);
        Assert.Null(diag.DiagnosticType);
        Assert.Null(diag.Capability);
        Assert.Null(diag.ErrorCode);
    }

    #endregion

    #region Adapter: ToLlmResult

    [Fact]
    public void ToLlmResult_HealthyResult_MapsAllFields()
    {
        var diag = DiagnosticHealthResult.Healthy(
            responseTime: TimeSpan.FromSeconds(1),
            provider: "tidal",
            authMethod: "oauth",
            diagnosticType: "quality_detect",
            capability: "hi_res_streaming");

        var llm = diag.ToLlmResult();

        Assert.True(llm.IsHealthy);
        Assert.Null(llm.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(1), llm.ResponseTime);
        Assert.Equal("tidal", llm.Provider);
        Assert.Equal("oauth", llm.AuthMethod);
        Assert.Equal("quality_detect", llm.Model); // DiagnosticType → Model
        Assert.Null(llm.ErrorCode);
    }

    [Fact]
    public void ToLlmResult_UnhealthyResult_MapsAllFields()
    {
        var diag = DiagnosticHealthResult.Unhealthy(
            reason: "Service down",
            responseTime: TimeSpan.FromSeconds(5),
            provider: "qobuz",
            authMethod: "apiKey",
            diagnosticType: "connectivity",
            errorCode: "CONNECTION_FAILED");

        var llm = diag.ToLlmResult();

        Assert.False(llm.IsHealthy);
        Assert.Equal("Service down", llm.StatusMessage);
        Assert.Equal(TimeSpan.FromSeconds(5), llm.ResponseTime);
        Assert.Equal("qobuz", llm.Provider);
        Assert.Equal("apiKey", llm.AuthMethod);
        Assert.Equal("connectivity", llm.Model);
        Assert.Equal("CONNECTION_FAILED", llm.ErrorCode);
    }

    [Fact]
    public void ToLlmResult_CapabilityIsDropped()
    {
        // Capability has no equivalent in ProviderHealthResult — verify it's not lost silently
        var diag = DiagnosticHealthResult.Healthy(
            capability: "hi_res_streaming");

        var llm = diag.ToLlmResult();

        Assert.True(llm.IsHealthy);
        Assert.Null(llm.Model); // Capability is NOT mapped to Model
    }

    #endregion

    #region Round-trip: FromLlmResult → ToLlmResult

    [Fact]
    public void RoundTrip_LlmToDiagAndBack_PreservesFields()
    {
        var original = ProviderHealthResult.Unhealthy(
            reason: "Timeout",
            responseTime: TimeSpan.FromSeconds(10),
            provider: "glm",
            authMethod: "apiKey",
            model: "glm-4-flash",
            errorCode: "TIMEOUT");

        var diag = DiagnosticHealthResult.FromLlmResult(original);
        var roundTripped = diag.ToLlmResult();

        Assert.Equal(original.IsHealthy, roundTripped.IsHealthy);
        Assert.Equal(original.StatusMessage, roundTripped.StatusMessage);
        Assert.Equal(original.ResponseTime, roundTripped.ResponseTime);
        Assert.Equal(original.Provider, roundTripped.Provider);
        Assert.Equal(original.AuthMethod, roundTripped.AuthMethod);
        Assert.Equal(original.Model, roundTripped.Model);
        Assert.Equal(original.ErrorCode, roundTripped.ErrorCode);
    }

    #endregion
}
