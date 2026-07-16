using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Abstractions.Diagnostics;
using Lidarr.Plugin.Common.Diagnostics;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Diagnostics;

/// <summary>
/// Behavior-pinning tests for <see cref="HealthCheckHelper.CheckAuthAsync"/> — the shared
/// timing/try-catch scaffold behind the Tidalarr/Qobuzarr/AppleMusicarr auth health checks.
/// </summary>
[Trait("Category", "Unit")]
public class HealthCheckHelperTests
{
    [Fact]
    public async Task ProbeReturnsTrue_HealthyResult_ThreadsAllTags()
    {
        var result = await HealthCheckHelper.CheckAuthAsync(
            _ => Task.FromResult(true),
            provider: "tidal",
            authMethod: "oauth",
            diagnosticType: "auth_validate",
            capability: "hi_res_streaming");

        Assert.True(result.IsHealthy);
        Assert.Null(result.StatusMessage);
        Assert.Null(result.ErrorCode);
        Assert.Equal("tidal", result.Provider);
        Assert.Equal("oauth", result.AuthMethod);
        Assert.Equal("auth_validate", result.DiagnosticType);
        Assert.Equal("hi_res_streaming", result.Capability);
        Assert.NotNull(result.ResponseTime);
        Assert.True(result.ResponseTime >= TimeSpan.Zero);
    }

    [Fact]
    public async Task ProbeReturnsFalse_UnhealthyWithDefaults()
    {
        var result = await HealthCheckHelper.CheckAuthAsync(
            _ => Task.FromResult(false),
            provider: "qobuz",
            authMethod: "app-secret");

        Assert.False(result.IsHealthy);
        Assert.Equal("Authentication failed", result.StatusMessage);
        Assert.Equal(DiagnosticErrorCodes.AuthFailed, result.ErrorCode);
        Assert.Equal("qobuz", result.Provider);
        Assert.Equal("app-secret", result.AuthMethod);
        Assert.NotNull(result.ResponseTime);
    }

    [Fact]
    public async Task ProbeReturnsFalse_CustomMessageAndErrorCode_AreUsed()
    {
        var result = await HealthCheckHelper.CheckAuthAsync(
            _ => Task.FromResult(false),
            provider: "qobuz",
            authMethod: "app-secret",
            capability: "lossless_download",
            unhealthyMessage: "Token expired",
            authFailedErrorCode: "TOKEN_EXPIRED");

        Assert.False(result.IsHealthy);
        Assert.Equal("Token expired", result.StatusMessage);
        Assert.Equal("TOKEN_EXPIRED", result.ErrorCode);
        Assert.Equal("lossless_download", result.Capability);
    }

    [Fact]
    public async Task ProbeThrows_UnhealthyWithExceptionMessage_AndConnectionFailedCode()
    {
        var result = await HealthCheckHelper.CheckAuthAsync(
            _ => throw new InvalidOperationException("socket closed"),
            provider: "apple-music",
            authMethod: "developer-token");

        Assert.False(result.IsHealthy);
        Assert.Equal("socket closed", result.StatusMessage);
        Assert.Equal(DiagnosticErrorCodes.ConnectionFailed, result.ErrorCode);
        Assert.Equal("apple-music", result.Provider);
        Assert.Equal("developer-token", result.AuthMethod);
        Assert.NotNull(result.ResponseTime);
    }

    [Fact]
    public async Task ProbeThrows_ExceptionMessageTransform_IsApplied()
    {
        var result = await HealthCheckHelper.CheckAuthAsync(
            _ => throw new InvalidOperationException("401"),
            provider: "apple-music",
            authMethod: "developer-token",
            connectionFailedErrorCode: "APPLE_DOWN",
            exceptionMessageTransform: ex => $"Check your Developer Token ({ex.Message})");

        Assert.False(result.IsHealthy);
        Assert.Equal("Check your Developer Token (401)", result.StatusMessage);
        Assert.Equal("APPLE_DOWN", result.ErrorCode);
    }

    [Fact]
    public async Task ProbeThrows_DiagnosticTypeIsThreaded_ButCapabilityIsNot()
    {
        // Characterization: the exception path forwards diagnosticType but (unlike the
        // probe-returned-false path) does NOT forward capability. Pinned so any future
        // change to that asymmetry is a conscious one.
        var result = await HealthCheckHelper.CheckAuthAsync(
            _ => throw new InvalidOperationException("boom"),
            provider: "tidal",
            authMethod: "oauth",
            diagnosticType: "connectivity",
            capability: "search");

        Assert.False(result.IsHealthy);
        Assert.Equal("connectivity", result.DiagnosticType);
        Assert.Null(result.Capability);
    }

    [Fact]
    public async Task ProbeThrowsOperationCanceled_IsRethrown_NotConverted()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            HealthCheckHelper.CheckAuthAsync(
                ct =>
                {
                    ct.ThrowIfCancellationRequested();
                    return Task.FromResult(true);
                },
                provider: "tidal",
                authMethod: "oauth",
                cancellationToken: cts.Token));
    }

    [Fact]
    public async Task CancellationToken_IsForwardedToProbe()
    {
        using var cts = new CancellationTokenSource();
        CancellationToken observed = default;

        await HealthCheckHelper.CheckAuthAsync(
            ct =>
            {
                observed = ct;
                return Task.FromResult(true);
            },
            provider: "tidal",
            authMethod: "oauth",
            cancellationToken: cts.Token);

        Assert.Equal(cts.Token, observed);
    }
}
