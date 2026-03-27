using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Bridge;

/// <summary>
/// Default bridge implementation that tracks rate limit state and logs events.
/// </summary>
public sealed class DefaultRateLimitReporter : IRateLimitReporter
{
    private readonly ILogger<DefaultRateLimitReporter> _logger;
    private volatile RateLimitStatus _status = new() { IsRateLimited = false };

    public DefaultRateLimitReporter(ILogger<DefaultRateLimitReporter> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public RateLimitStatus Status => _status;

    public ValueTask ReportRateLimitAsync(TimeSpan retryAfter, CancellationToken cancellationToken = default)
    {
        _status = new RateLimitStatus
        {
            IsRateLimited = true,
            ResetAt = DateTimeOffset.UtcNow + retryAfter
        };
        _logger.LogWarning("Rate limited. Retry after: {RetryAfter}", retryAfter);
        return ValueTask.CompletedTask;
    }

    public ValueTask ReportRateLimitClearedAsync(CancellationToken cancellationToken = default)
    {
        _status = new RateLimitStatus { IsRateLimited = false };
        _logger.LogInformation("Rate limit cleared");
        return ValueTask.CompletedTask;
    }

    public ValueTask ReportBackoffAsync(TimeSpan delay, string reason,
        CancellationToken cancellationToken = default)
    {
        _logger.LogWarning("Backoff applied: {Delay} - {Reason}", delay, reason);
        return ValueTask.CompletedTask;
    }
}
