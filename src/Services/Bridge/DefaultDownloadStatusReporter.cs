using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Lidarr.Plugin.Abstractions.Models;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Bridge;

/// <summary>
/// Default bridge implementation that tracks download status transitions and logs events.
/// Plugins can override by registering their own IDownloadStatusReporter before calling AddBridgeDefaults().
/// </summary>
public sealed class DefaultDownloadStatusReporter : IDownloadStatusReporter
{
    private readonly ILogger<DefaultDownloadStatusReporter> _logger;
    private DownloadStatus _status = DownloadStatus.Idle;
    private Exception? _lastError;

    public DefaultDownloadStatusReporter(ILogger<DefaultDownloadStatusReporter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public DownloadStatus Status => _status;

    /// <summary>Last reported error (cleared on non-error status transition).</summary>
    public Exception? LastError => _lastError;

    public ValueTask ReportProgressAsync(AlbumDownloadProgress progress, CancellationToken cancellationToken = default)
    {
        if (progress is null)
        {
            throw new ArgumentNullException(nameof(progress));
        }

        DownloadStatus previous = _status;
        _status = DownloadStatus.Downloading;
        _lastError = null;

        _logger.LogDebug("Download progress: {Previous} -> Downloading — {CurrentTrack} ({Completed}/{Total}, {Percentage:F1}%)",
            previous, progress.CurrentTrack, progress.CompletedTracks, progress.TotalTracks, progress.OverallPercentage);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReportCompletedAsync(string albumId, CancellationToken cancellationToken = default)
    {
        DownloadStatus previous = _status;
        _status = DownloadStatus.Completed;
        _lastError = null;

        _logger.LogInformation("Download completed: {Previous} -> Completed (album {AlbumId})",
            previous, albumId);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReportFailedAsync(string albumId, Exception error, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(error);
        _lastError = error;
        DownloadStatus previous = _status;
        _status = DownloadStatus.Failed;

        _logger.LogError(error, "Download failed: {Previous} -> Failed (album {AlbumId})",
            previous, albumId);
        return ValueTask.CompletedTask;
    }
}
