using System;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Services.Bridge;
using Lidarr.Plugin.Common.TestKit.Fixtures;
using Microsoft.Extensions.Logging;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Bridge;

/// <summary>
/// Tests for <see cref="DefaultDownloadStatusReporter"/> — DI activation, lifecycle, and error reporting.
/// </summary>
public class DefaultDownloadStatusReporterTests : IDisposable
{
    private readonly BridgeComplianceFixture _fixture = new();

    // ── DI Activation ───────────────────────────────────────────────

    [Fact]
    public void DI_Resolves_IDownloadStatusReporter()
    {
        Assert.NotNull(_fixture.DownloadStatusReporter);
    }

    [Fact]
    public void DI_Resolves_As_DefaultDownloadStatusReporter()
    {
        Assert.IsType<DefaultDownloadStatusReporter>(_fixture.DownloadStatusReporter);
    }

    // ── Lifecycle (status transitions) ──────────────────────────────

    [Fact]
    public void Initial_Status_Is_Idle()
    {
        Assert.Equal(DownloadStatus.Idle, _fixture.DownloadStatusReporter.Status);
    }

    [Fact]
    public async Task ReportProgress_Transitions_To_Downloading()
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
    public async Task ReportCompleted_Transitions_To_Completed()
    {
        await _fixture.DownloadStatusReporter.ReportCompletedAsync("album-123");

        Assert.Equal(DownloadStatus.Completed, _fixture.DownloadStatusReporter.Status);
    }

    [Fact]
    public async Task ReportFailed_Transitions_To_Failed()
    {
        await _fixture.DownloadStatusReporter.ReportFailedAsync(
            "album-456", new InvalidOperationException("network error"));

        Assert.Equal(DownloadStatus.Failed, _fixture.DownloadStatusReporter.Status);
    }

    [Fact]
    public async Task Progress_After_Failure_Transitions_Back_To_Downloading()
    {
        await _fixture.DownloadStatusReporter.ReportFailedAsync(
            "album-789", new Exception("transient"));

        var progress = new AlbumDownloadProgress
        {
            CompletedTracks = 0,
            TotalTracks = 5,
            CurrentTrack = "Retry Track 1"
        };

        await _fixture.DownloadStatusReporter.ReportProgressAsync(progress);

        Assert.Equal(DownloadStatus.Downloading, _fixture.DownloadStatusReporter.Status);
    }

    [Fact]
    public async Task Completed_After_Downloading_Transitions_Correctly()
    {
        var progress = new AlbumDownloadProgress
        {
            CompletedTracks = 5,
            TotalTracks = 5,
            CurrentTrack = "Track 5"
        };

        await _fixture.DownloadStatusReporter.ReportProgressAsync(progress);
        await _fixture.DownloadStatusReporter.ReportCompletedAsync("album-done");

        Assert.Equal(DownloadStatus.Completed, _fixture.DownloadStatusReporter.Status);
    }

    // ── Error reporting ─────────────────────────────────────────────

    [Fact]
    public async Task ReportFailed_Sets_LastError()
    {
        var error = new InvalidOperationException("API error");
        await _fixture.DownloadStatusReporter.ReportFailedAsync("album-err", error);

        var reporter = (DefaultDownloadStatusReporter)_fixture.DownloadStatusReporter;
        Assert.Same(error, reporter.LastError);
    }

    [Fact]
    public async Task ReportProgress_Clears_LastError()
    {
        await _fixture.DownloadStatusReporter.ReportFailedAsync(
            "album-err", new Exception("fail"));

        var progress = new AlbumDownloadProgress
        {
            CompletedTracks = 1,
            TotalTracks = 3,
            CurrentTrack = "Track 1"
        };
        await _fixture.DownloadStatusReporter.ReportProgressAsync(progress);

        var reporter = (DefaultDownloadStatusReporter)_fixture.DownloadStatusReporter;
        Assert.Null(reporter.LastError);
    }

    [Fact]
    public async Task ReportCompleted_Clears_LastError()
    {
        await _fixture.DownloadStatusReporter.ReportFailedAsync(
            "album-err", new Exception("fail"));
        await _fixture.DownloadStatusReporter.ReportCompletedAsync("album-ok");

        var reporter = (DefaultDownloadStatusReporter)_fixture.DownloadStatusReporter;
        Assert.Null(reporter.LastError);
    }

    [Fact]
    public async Task ReportFailed_Produces_Error_Log()
    {
        await _fixture.DownloadStatusReporter.ReportFailedAsync(
            "album-log", new InvalidOperationException("test error"));

        var logs = _fixture.Context.LogEntries.Snapshot();
        Assert.Contains(logs, e => e.Level == LogLevel.Error);
    }

    [Fact]
    public async Task ReportProgress_Produces_Debug_Log()
    {
        var progress = new AlbumDownloadProgress
        {
            CompletedTracks = 2,
            TotalTracks = 8,
            CurrentTrack = "My Track"
        };

        await _fixture.DownloadStatusReporter.ReportProgressAsync(progress);

        var logs = _fixture.Context.LogEntries.Snapshot();
        Assert.Contains(logs, e =>
            e.Level == LogLevel.Debug &&
            e.Message.Contains("My Track"));
    }

    [Fact]
    public async Task ReportCompleted_Produces_Information_Log()
    {
        await _fixture.DownloadStatusReporter.ReportCompletedAsync("album-info");

        var logs = _fixture.Context.LogEntries.Snapshot();
        Assert.Contains(logs, e =>
            e.Level == LogLevel.Information &&
            e.Message.Contains("album-info"));
    }

    [Fact]
    public async Task ReportFailed_With_Null_Error_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _fixture.DownloadStatusReporter.ReportFailedAsync("album-x", null!).AsTask());
    }

    [Fact]
    public async Task ReportProgress_With_Null_Progress_Throws()
    {
        await Assert.ThrowsAsync<ArgumentNullException>(
            () => _fixture.DownloadStatusReporter.ReportProgressAsync(null!).AsTask());
    }

    public void Dispose() => _fixture.Dispose();
}
