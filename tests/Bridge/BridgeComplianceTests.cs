using System;
using System.Linq;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.TestKit.Fixtures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// Fixture-backed compliance tests for bridge runtime contracts.
/// Uses real DI container and real implementations — not mocks.
/// </summary>
public class BridgeComplianceTests : IDisposable
{
    private readonly BridgeComplianceFixture _fixture = new();

    // ── Auth Failure Handler ─────────────────────────────────────────

    [Fact]
    public void AuthHandler_Initial_Status_Is_Unknown()
    {
        Assert.Equal(AuthStatus.Unknown, _fixture.AuthHandler.Status);
    }

    [Fact]
    public async Task AuthHandler_Failure_Transitions_To_Failed()
    {
        await _fixture.AuthHandler.HandleFailureAsync(
            new AuthFailure { ErrorCode = "AUTH001", Message = "Token expired" });

        Assert.Equal(AuthStatus.Failed, _fixture.AuthHandler.Status);
    }

    [Fact]
    public async Task AuthHandler_Success_After_Failure_Transitions_To_Authenticated()
    {
        await _fixture.AuthHandler.HandleFailureAsync(
            new AuthFailure { Message = "fail" });
        await _fixture.AuthHandler.HandleSuccessAsync();

        Assert.Equal(AuthStatus.Authenticated, _fixture.AuthHandler.Status);
    }

    [Fact]
    public async Task AuthHandler_Reauth_Transitions_To_Expired()
    {
        await _fixture.AuthHandler.RequestReauthenticationAsync("Token revoked");

        Assert.Equal(AuthStatus.Expired, _fixture.AuthHandler.Status);
    }

    [Fact]
    public async Task AuthHandler_Failure_Produces_Warning_Log()
    {
        await _fixture.AuthHandler.HandleFailureAsync(
            new AuthFailure { ErrorCode = "E001", Message = "test failure" });

        var logs = _fixture.Context.LogEntries.Snapshot();
        Assert.Contains(logs, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("E001"));
    }

    [Fact]
    public async Task AuthHandler_Success_Clears_LastFailure()
    {
        await _fixture.AuthHandler.HandleFailureAsync(
            new AuthFailure { Message = "fail" });
        await _fixture.AuthHandler.HandleSuccessAsync();

        var handler = (Lidarr.Plugin.Common.Services.Bridge.DefaultAuthFailureHandler)_fixture.AuthHandler;
        Assert.Null(handler.LastFailure);
    }

    // ── Indexer Status Reporter ──────────────────────────────────────

    [Fact]
    public void StatusReporter_Initial_Status_Is_Idle()
    {
        Assert.Equal(IndexerStatus.Idle, _fixture.StatusReporter.CurrentStatus);
    }

    [Fact]
    public async Task StatusReporter_Tracks_Transitions()
    {
        await _fixture.StatusReporter.ReportStatusAsync(IndexerStatus.Searching);
        Assert.Equal(IndexerStatus.Searching, _fixture.StatusReporter.CurrentStatus);

        await _fixture.StatusReporter.ReportStatusAsync(IndexerStatus.Idle);
        Assert.Equal(IndexerStatus.Idle, _fixture.StatusReporter.CurrentStatus);
    }

    [Fact]
    public async Task StatusReporter_Error_Sets_Error_Status()
    {
        await _fixture.StatusReporter.ReportErrorAsync(new InvalidOperationException("API down"));

        Assert.Equal(IndexerStatus.Error, _fixture.StatusReporter.CurrentStatus);
    }

    [Fact]
    public async Task StatusReporter_NonError_Status_Clears_LastError()
    {
        await _fixture.StatusReporter.ReportErrorAsync(new Exception("err"));
        await _fixture.StatusReporter.ReportStatusAsync(IndexerStatus.Idle);

        Assert.Equal(IndexerStatus.Idle, _fixture.StatusReporter.CurrentStatus);
        var reporter = (Lidarr.Plugin.Common.Services.Bridge.DefaultIndexerStatusReporter)_fixture.StatusReporter;
        Assert.Null(reporter.LastError);
    }

    [Fact]
    public async Task StatusReporter_Error_Produces_Error_Log()
    {
        await _fixture.StatusReporter.ReportErrorAsync(new InvalidOperationException("test error"));

        var logs = _fixture.Context.LogEntries.Snapshot();
        Assert.Contains(logs, e => e.Level == LogLevel.Error);
    }

    // ── Download Status Reporter ────────────────────────────────────

    [Fact]
    public void DownloadReporter_Initial_Status_Is_Idle()
    {
        Assert.Equal(DownloadStatus.Idle, _fixture.DownloadStatusReporter.Status);
    }

    [Fact]
    public async Task DownloadReporter_Progress_Transitions_To_Downloading()
    {
        var progress = new AlbumDownloadProgress
        {
            CompletedTracks = 1,
            TotalTracks = 10,
            CurrentTrack = "Track 1"
        };

        await _fixture.DownloadStatusReporter.ReportProgressAsync(progress);

        Assert.Equal(DownloadStatus.Downloading, _fixture.DownloadStatusReporter.Status);
    }

    [Fact]
    public async Task DownloadReporter_Completed_Sets_Status()
    {
        await _fixture.DownloadStatusReporter.ReportCompletedAsync("album-1");

        Assert.Equal(DownloadStatus.Completed, _fixture.DownloadStatusReporter.Status);
    }

    [Fact]
    public async Task DownloadReporter_Failed_Sets_Status_And_Logs_Error()
    {
        await _fixture.DownloadStatusReporter.ReportFailedAsync(
            "album-2", new InvalidOperationException("download error"));

        Assert.Equal(DownloadStatus.Failed, _fixture.DownloadStatusReporter.Status);

        var logs = _fixture.Context.LogEntries.Snapshot();
        Assert.Contains(logs, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task DownloadReporter_NonError_Status_Clears_LastError()
    {
        await _fixture.DownloadStatusReporter.ReportFailedAsync(
            "album-3", new Exception("err"));
        await _fixture.DownloadStatusReporter.ReportCompletedAsync("album-3");

        Assert.Equal(DownloadStatus.Completed, _fixture.DownloadStatusReporter.Status);
        var reporter = (Lidarr.Plugin.Common.Services.Bridge.DefaultDownloadStatusReporter)_fixture.DownloadStatusReporter;
        Assert.Null(reporter.LastError);
    }

    // ── Rate Limit Reporter ──────────────────────────────────────────

    [Fact]
    public void RateLimitReporter_Initial_Status_Is_Not_Limited()
    {
        Assert.False(_fixture.RateLimitReporter.Status.IsRateLimited);
    }

    [Fact]
    public async Task RateLimitReporter_Report_Sets_Limited_With_ResetAt()
    {
        var before = DateTimeOffset.UtcNow;
        await _fixture.RateLimitReporter.ReportRateLimitAsync(TimeSpan.FromSeconds(30));

        Assert.True(_fixture.RateLimitReporter.Status.IsRateLimited);
        Assert.NotNull(_fixture.RateLimitReporter.Status.ResetAt);
        Assert.True(_fixture.RateLimitReporter.Status.ResetAt >= before.AddSeconds(29));
    }

    [Fact]
    public async Task RateLimitReporter_Cleared_Resets_Status()
    {
        await _fixture.RateLimitReporter.ReportRateLimitAsync(TimeSpan.FromSeconds(60));
        await _fixture.RateLimitReporter.ReportRateLimitClearedAsync();

        Assert.False(_fixture.RateLimitReporter.Status.IsRateLimited);
    }

    [Fact]
    public async Task RateLimitReporter_Backoff_Logs_Warning()
    {
        await _fixture.RateLimitReporter.ReportBackoffAsync(
            TimeSpan.FromSeconds(5), "exponential");

        var logs = _fixture.Context.LogEntries.Snapshot();
        Assert.Contains(logs, e =>
            e.Level == LogLevel.Warning &&
            e.Message.Contains("exponential"));
    }

    public void Dispose() => _fixture.Dispose();
}
