using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Bridge;

/// <summary>
/// Default bridge implementation that tracks authentication status transitions and logs events.
/// Plugins can override by registering their own IAuthFailureHandler before calling AddBridgeDefaults().
/// </summary>
public sealed class DefaultAuthFailureHandler : IAuthFailureHandler
{
    private readonly ILogger<DefaultAuthFailureHandler> _logger;
    private readonly int _failureThreshold;
    private readonly object _lock = new();
    private AuthStatus _status = AuthStatus.Unknown;
    private AuthFailure? _lastFailure;
    private int _consecutiveFailureCount;

    public DefaultAuthFailureHandler(
        ILogger<DefaultAuthFailureHandler> logger,
        int failureThreshold = 1)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (failureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureThreshold),
                "Failure threshold must be at least 1. Use 1 (default) to latch on the first failure; " +
                "higher values require K consecutive failures (broken by any HandleSuccessAsync call).");
        }
        _failureThreshold = failureThreshold;
    }

    public AuthStatus Status { get { lock (_lock) return _status; } }

    /// <summary>Most recent failure details (null after successful auth).</summary>
    public AuthFailure? LastFailure { get { lock (_lock) return _lastFailure; } }

    /// <summary>
    /// Number of consecutive failures observed since the last success (or
    /// since construction / <see cref="ResetToUnknown"/>). Exposed for the
    /// <c>AuthFailureGate</c> so it can apply probe-interval rate-limiting
    /// in the sub-threshold window when <c>failureThreshold &gt; 1</c> —
    /// without this, K-of-N would re-enable the IP-ban hammering for the
    /// first K-1 calls.
    /// </summary>
    public int ConsecutiveFailureCount { get { lock (_lock) return _consecutiveFailureCount; } }

    public ValueTask HandleFailureAsync(AuthFailure failure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        bool latched;
        lock (_lock)
        {
            _lastFailure = failure;
            _consecutiveFailureCount++;
            latched = _consecutiveFailureCount >= _failureThreshold;
            if (latched)
            {
                // Do NOT downgrade Expired → Failed. Expired carries
                // the operator-actionable "user must re-auth" signal; Failed
                // is the generic transient. Keep the stronger signal.
                if (_status != AuthStatus.Expired)
                {
                    _status = AuthStatus.Failed;
                }
            }
        }

        if (latched)
        {
            _logger.LogWarning("Authentication failure: {ErrorCode} - {Message}", failure.ErrorCode, failure.Message);
        }
        else
        {
            _logger.LogDebug(
                "Sub-threshold auth failure {Count}/{Threshold}: {ErrorCode} - {Message}",
                _consecutiveFailureCount, _failureThreshold, failure.ErrorCode, failure.Message);
        }
        return ValueTask.CompletedTask;
    }

    public ValueTask HandleSuccessAsync(CancellationToken cancellationToken = default)
    {
        bool wasAuthenticated;
        lock (_lock)
        {
            wasAuthenticated = _status == AuthStatus.Authenticated;
            _status = AuthStatus.Authenticated;
            _lastFailure = null;
            _consecutiveFailureCount = 0;
        }

        // Idempotency-friendly logging: only log on the transition to
        // Authenticated, not on every subsequent success call. Custom
        // handlers MUST also follow this idempotency contract — see
        // remarks on IAuthFailureHandler.HandleSuccessAsync.
        if (!wasAuthenticated)
        {
            _logger.LogInformation("Authentication succeeded");
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reset the handler back to <see cref="AuthStatus.Unknown"/>, clearing
    /// any latched failure. Used by the registry's <c>Reset(key)</c> API for
    /// settings-UI "Test Connection" flows where the operator has provided
    /// fresh credentials and wants to retry without waiting for the probe
    /// interval.
    /// </summary>
    public void ResetToUnknown()
    {
        lock (_lock)
        {
            _status = AuthStatus.Unknown;
            _lastFailure = null;
            _consecutiveFailureCount = 0;
        }
    }

    public ValueTask RequestReauthenticationAsync(string reason, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _status = AuthStatus.Expired;
            // Synthesize an AuthFailure so callers reading LastFailure get an
            // actionable code + message, not a generic "Auth status is Expired"
            // exception with empty body.
            _lastFailure = new AuthFailure
            {
                ErrorCode = "EXPIRED",
                Message = string.IsNullOrWhiteSpace(reason) ? "Re-authentication requested" : reason,
            };
        }

        _logger.LogWarning("Re-authentication requested: {Reason}", reason);
        return ValueTask.CompletedTask;
    }
}
