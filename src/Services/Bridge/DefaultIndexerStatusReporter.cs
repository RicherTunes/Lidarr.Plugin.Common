using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Bridge;

/// <summary>
/// Default bridge implementation that tracks indexer status transitions and logs events.
/// </summary>
public sealed class DefaultIndexerStatusReporter : IIndexerStatusReporter
{
    private readonly ILogger<DefaultIndexerStatusReporter> _logger;
    private IndexerStatus _currentStatus = IndexerStatus.Idle;
    private Exception? _lastError;

    public DefaultIndexerStatusReporter(ILogger<DefaultIndexerStatusReporter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public IndexerStatus CurrentStatus => _currentStatus;

    /// <summary>Last reported error (cleared on non-error status transition).</summary>
    public Exception? LastError => _lastError;

    public ValueTask ReportStatusAsync(IndexerStatus status, string? message = null,
        CancellationToken cancellationToken = default)
    {
        IndexerStatus previous = _currentStatus;
        _currentStatus = status;
        if (status != IndexerStatus.Error)
        {
            _lastError = null;
        }

        _logger.LogDebug("Indexer status: {Previous} -> {Current}{Message}",
            previous, status, message is null ? "" : $" ({message})");
        return ValueTask.CompletedTask;
    }

    public ValueTask ReportErrorAsync(Exception error, CancellationToken cancellationToken = default)
    {
        _lastError = error ?? throw new ArgumentNullException(nameof(error));
        _currentStatus = IndexerStatus.Error;
        _logger.LogError(error, "Indexer error reported");
        return ValueTask.CompletedTask;
    }
}
