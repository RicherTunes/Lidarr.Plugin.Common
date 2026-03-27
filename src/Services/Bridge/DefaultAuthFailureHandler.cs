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
    private readonly object _lock = new();
    private AuthStatus _status = AuthStatus.Unknown;
    private AuthFailure? _lastFailure;

    public DefaultAuthFailureHandler(ILogger<DefaultAuthFailureHandler> logger)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public AuthStatus Status { get { lock (_lock) return _status; } }

    /// <summary>Most recent failure details (null after successful auth).</summary>
    public AuthFailure? LastFailure { get { lock (_lock) return _lastFailure; } }

    public ValueTask HandleFailureAsync(AuthFailure failure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);
        lock (_lock)
        {
            _lastFailure = failure;
            _status = AuthStatus.Failed;
        }

        _logger.LogWarning("Authentication failure: {ErrorCode} - {Message}", failure.ErrorCode, failure.Message);
        return ValueTask.CompletedTask;
    }

    public ValueTask HandleSuccessAsync(CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _status = AuthStatus.Authenticated;
            _lastFailure = null;
        }

        _logger.LogInformation("Authentication succeeded");
        return ValueTask.CompletedTask;
    }

    public ValueTask RequestReauthenticationAsync(string reason, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _status = AuthStatus.Expired;
        }

        _logger.LogWarning("Re-authentication requested: {Reason}", reason);
        return ValueTask.CompletedTask;
    }
}
