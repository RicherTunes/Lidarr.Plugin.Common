using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Contracts;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Bridge;

/// <summary>
/// <see cref="IAuthFailureHandler"/> with sliding-window failure threshold semantics.
/// </summary>
/// <remarks>
/// <para>
/// Latches the auth status to <see cref="AuthStatus.Failed"/> after
/// <see cref="_failureThreshold"/> consecutive failures occur within a sliding
/// <see cref="_failureWindow"/>. Failures older than the window are forgotten —
/// a stale run of 401s against a now-rotated credential won't pre-latch the new
/// one. <see cref="HandleSuccessAsync"/> resets the counter + status to Closed.
/// </para>
/// <para>
/// This sibling of <see cref="DefaultAuthFailureHandler"/> exists for plugins
/// whose auth-failure recovery patterns are minutes-to-hours (e.g. brainarr's
/// LLM-key rotation cycle) rather than the seconds-to-minutes streaming-session
/// model that the default handler targets. The "open duration" — how often
/// the gate allows a probe while latched — is configured at the GATE layer
/// (<see cref="AuthFailureGate"/> ctor's <c>probeInterval</c>), so this handler
/// only owns the sliding-window threshold.
/// </para>
/// <para>
/// Pairing this handler with <c>AuthFailureGateRegistry</c> + per-key
/// <c>probeInterval = openDuration</c> produces the brainarr-style
/// "K-of-N-in-W → open for D → probe → close-or-reopen" circuit-breaker
/// behaviour. The same shape as the prior in-brainarr <c>LlmAuthCircuit</c>
/// but built on the shared Common API surface so the ecosystem stays
/// uniform.
/// </para>
/// </remarks>
public sealed class SlidingWindowAuthFailureHandler : IAuthFailureHandler
{
    private readonly ILogger<SlidingWindowAuthFailureHandler> _logger;
    private readonly int _failureThreshold;
    private readonly TimeSpan _failureWindow;
    private readonly TimeProvider _clock;
    private readonly object _lock = new();

    private AuthStatus _status = AuthStatus.Unknown;
    private AuthFailure? _lastFailure;
    private int _consecutiveFailureCount;
    private DateTimeOffset? _firstFailureInWindow;

    /// <param name="logger">Required. Diagnostics for latch/recovery transitions and sub-threshold failures.</param>
    /// <param name="failureThreshold">
    /// How many consecutive failures within the window trigger the latch. Default 3 —
    /// matches brainarr's <c>LlmAuthCircuit.DefaultConsecutiveFailureThreshold</c> and tolerates
    /// transient hiccups (token-refresh races, propagation delays).
    /// </param>
    /// <param name="failureWindow">
    /// Sliding window inside which failures count toward the threshold. Default 5 minutes.
    /// A failure older than this is dropped from the counter so a stale 401-run against a
    /// rotated credential doesn't pre-latch the new one.
    /// </param>
    /// <param name="clock">Optional. Pass a fake to make sliding-window tests deterministic.</param>
    public SlidingWindowAuthFailureHandler(
        ILogger<SlidingWindowAuthFailureHandler> logger,
        int failureThreshold = 3,
        TimeSpan? failureWindow = null,
        TimeProvider? clock = null)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        if (failureThreshold < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureThreshold),
                "Failure threshold must be at least 1. Use 1 to latch on the first failure; " +
                "higher values require K consecutive failures within failureWindow.");
        }
        _failureThreshold = failureThreshold;

        var window = failureWindow ?? TimeSpan.FromMinutes(5);
        if (window <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(failureWindow),
                "Failure window must be positive. Pass null to use the default 5 minutes.");
        }
        _failureWindow = window;

        _clock = clock ?? TimeProvider.System;
    }

    /// <inheritdoc />
    public AuthStatus Status { get { lock (_lock) return _status; } }

    /// <summary>Most recent failure details (null after successful auth).</summary>
    public AuthFailure? LastFailure { get { lock (_lock) return _lastFailure; } }

    /// <summary>
    /// Consecutive failures observed within the current sliding window. Exposed for
    /// <see cref="AuthFailureGate"/>'s sub-threshold rate-limit logic.
    /// </summary>
    public int ConsecutiveFailureCount { get { lock (_lock) return _consecutiveFailureCount; } }

    /// <summary>
    /// First failure in the current sliding window, or null if no failures recorded.
    /// Exposed for operator-side observability (how recent is the current run).
    /// </summary>
    public DateTimeOffset? FirstFailureInWindow { get { lock (_lock) return _firstFailureInWindow; } }

    /// <inheritdoc />
    public ValueTask HandleFailureAsync(AuthFailure failure, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(failure);

        var now = _clock.GetUtcNow();
        bool latched;
        int countAfter;
        lock (_lock)
        {
            // Sliding window: if the previous failure run started outside the window,
            // reset the counter. A stale run shouldn't pre-latch the new credential.
            if (_consecutiveFailureCount > 0 && _firstFailureInWindow.HasValue &&
                (now - _firstFailureInWindow.Value) > _failureWindow)
            {
                _consecutiveFailureCount = 0;
                _firstFailureInWindow = null;
            }

            if (_consecutiveFailureCount == 0)
            {
                _firstFailureInWindow = now;
            }

            _consecutiveFailureCount++;
            _lastFailure = failure;
            countAfter = _consecutiveFailureCount;
            latched = countAfter >= _failureThreshold;

            if (latched)
            {
                // Don't downgrade Expired → Failed. Expired carries the operator-actionable
                // "user must re-auth" signal; Failed is the generic transient. Keep the
                // stronger signal. Mirrors DefaultAuthFailureHandler.
                if (_status != AuthStatus.Expired)
                {
                    _status = AuthStatus.Failed;
                }
            }
        }

        if (latched)
        {
            _logger.LogWarning(
                "Authentication failure (sliding-window threshold reached: {Count}/{Threshold} within {Window}): {ErrorCode} - {Message}",
                countAfter, _failureThreshold, _failureWindow, failure.ErrorCode, failure.Message);
        }
        else
        {
            _logger.LogDebug(
                "Sub-threshold auth failure {Count}/{Threshold} (window {Window}): {ErrorCode} - {Message}",
                countAfter, _failureThreshold, _failureWindow, failure.ErrorCode, failure.Message);
        }
        return ValueTask.CompletedTask;
    }

    /// <inheritdoc />
    public ValueTask HandleSuccessAsync(CancellationToken cancellationToken = default)
    {
        bool wasAuthenticated;
        lock (_lock)
        {
            wasAuthenticated = _status == AuthStatus.Authenticated;
            _status = AuthStatus.Authenticated;
            _consecutiveFailureCount = 0;
            _firstFailureInWindow = null;
            _lastFailure = null;
        }

        // Idempotency-friendly logging — only on the transition into Authenticated.
        // Matches the IAuthFailureHandler.HandleSuccessAsync contract.
        if (!wasAuthenticated)
        {
            _logger.LogInformation("Authentication succeeded — sliding-window circuit reset to Authenticated.");
        }
        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Reset the handler back to <see cref="AuthStatus.Unknown"/>, clearing any
    /// latched failure + counter. Used by the registry's <c>Reset(key)</c> API
    /// for settings-UI "Test Connection" flows where the operator has provided
    /// fresh credentials and wants to retry without waiting for the probe interval.
    /// </summary>
    public void ResetToUnknown()
    {
        lock (_lock)
        {
            _status = AuthStatus.Unknown;
            _lastFailure = null;
            _consecutiveFailureCount = 0;
            _firstFailureInWindow = null;
        }
    }

    /// <inheritdoc />
    public ValueTask RequestReauthenticationAsync(string reason, CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            _status = AuthStatus.Expired;
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
