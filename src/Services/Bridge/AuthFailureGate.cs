using System;
using System.Threading;
using System.Threading.Tasks;

using Lidarr.Plugin.Abstractions.Contracts;

using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Bridge;

/// <summary>
/// Fail-fast gate built on top of <see cref="IAuthFailureHandler"/> that prevents
/// plugins from hammering an upstream service when authentication is known bad.
///
/// Background: a user got IP-banned by Qobuz after the auth/session expired and
/// Lidarr's search loop kept driving the plugin at full rate. Each call returned
/// 401, the plugin propagated the error to Lidarr unchanged, and Lidarr retried.
/// The plugin had no mechanism to say "auth is bad — fail fast, do not even
/// touch the network until the user re-credentials."
///
/// Usage on the request side:
/// <code>
/// gate.EnsureCanProceed(); // throws AuthGatedException if auth latched bad
/// var response = await httpClient.SendAsync(req, ct);
/// </code>
///
/// Usage on the response side — prefer the pass-through methods over direct
/// <see cref="Handler"/> access:
/// <code>
/// if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
/// {
///     await gate.HandleFailureAsync(new AuthFailure { ErrorCode = "401", Message = "..." });
/// }
/// </code>
///
/// When you genuinely need to probe the upstream once in a while (to detect
/// that the user re-credentialed by some out-of-band path), call
/// <see cref="TryAcquireProbeSlot"/> first — it returns true at most once per
/// configured probe interval while auth is latched bad.
/// </summary>
/// <remarks>
/// <para>
/// <strong>Cross-ALC constraint:</strong> <see cref="AuthFailureGate"/> and its
/// <see cref="Handler"/> are scoped to a single plugin's <c>AssemblyLoadContext</c>.
/// Because <c>Lidarr.Plugin.Abstractions</c> is internalised by ILRepack per plugin,
/// the <see cref="IAuthFailureHandler"/> type identity diverges across plugin
/// boundaries — passing a gate (or its handler) from one plugin to another and
/// then calling methods on it will produce <c>InvalidCastException</c> on the
/// internalised types.
/// </para>
/// <para>
/// In practice each plugin instantiates its own gate via DI and never shares
/// references across plugin boundaries, so this is a constraint to keep in mind
/// rather than an active risk. The pass-through methods
/// (<see cref="HandleFailureAsync"/>, <see cref="HandleSuccessAsync"/>) intentionally
/// take the bridged contract types so call sites need not touch <see cref="Handler"/>
/// directly for the common path.
/// </para>
/// </remarks>
public sealed class AuthFailureGate
{
    private readonly TimeProvider _clock;
    private readonly TimeSpan _probeInterval;
    private readonly ILogger<AuthFailureGate> _logger;
    private readonly object _lock = new();
    private DateTimeOffset? _lastProbeAt;
    private AuthStatus _lastObservedStatus = AuthStatus.Unknown;

    // Observability counters. All updates happen inside _lock so reads
    // through Metrics return a coherent snapshot.
    private long _latchTransitions;
    private long _recoveryTransitions;
    private long _probeAcquired;
    private long _probeRejected;
    private long _probeRefunded;
    private DateTimeOffset? _lastLatchAt;
    private DateTimeOffset? _lastRecoveryAt;

    public AuthFailureGate(
        IAuthFailureHandler handler,
        TimeProvider? clock = null,
        TimeSpan? probeInterval = null,
        ILogger<AuthFailureGate>? logger = null)
    {
        Handler = handler ?? throw new ArgumentNullException(nameof(handler));
        _clock = clock ?? TimeProvider.System;
        _probeInterval = probeInterval ?? TimeSpan.FromSeconds(60);
        _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<AuthFailureGate>.Instance;
    }

    /// <summary>
    /// The underlying handler. Prefer the gate's own
    /// <see cref="HandleFailureAsync"/> / <see cref="HandleSuccessAsync"/>
    /// pass-throughs for everyday wiring — direct handler access is intended
    /// for advanced scenarios (custom status read, mocking in tests). Never
    /// share this reference across plugin <c>AssemblyLoadContext</c> boundaries;
    /// see the class-level cross-ALC remarks.
    /// </summary>
    public IAuthFailureHandler Handler { get; }

    /// <summary>
    /// Record an authentication failure. Pass-through to <see cref="Handler"/>
    /// to keep call sites independent of <see cref="IAuthFailureHandler"/>'s
    /// type identity (see cross-ALC remarks). Equivalent to
    /// <c>Handler.HandleFailureAsync(failure, ct)</c>.
    /// </summary>
    public ValueTask HandleFailureAsync(AuthFailure failure, CancellationToken cancellationToken = default)
        => Handler.HandleFailureAsync(failure, cancellationToken);

    /// <summary>
    /// Record an authentication success. Pass-through to <see cref="Handler"/>.
    /// Implementations MUST be idempotent (see <see cref="IAuthFailureHandler.HandleSuccessAsync"/>).
    /// </summary>
    public ValueTask HandleSuccessAsync(CancellationToken cancellationToken = default)
        => Handler.HandleSuccessAsync(cancellationToken);

    /// <summary>
    /// Consumer-side convenience for bridge entry points (indexer / download-client
    /// <c>Search</c>, <c>Download</c>, <c>Test</c>). Returns true when auth is latched
    /// bad AND no probe slot is currently available — i.e. the caller should
    /// short-circuit (return empty / fail fast) without touching the network. This is
    /// the qobuzarr-incident amplification fix: when Lidarr's search loop drives the
    /// plugin at full rate, refuse to forward every call upstream until the user
    /// re-credentials.
    /// <para>
    /// Equivalent to <c>!IsHealthy &amp;&amp; !TryAcquireProbeSlot()</c>. NOTE: like
    /// <see cref="TryAcquireProbeSlot"/>, this CONSUMES the per-interval probe slot as a
    /// side effect when latched, so call it at most once per entry-point invocation.
    /// </para>
    /// Lifted from the byte-identical <c>IsAuthShortCircuited</c> helpers that
    /// applemusicarr / tidalarr / qobuzarr each re-implemented at their bridge entry
    /// points.
    /// </summary>
    public bool ShouldShortCircuit()
    {
        if (IsHealthy) return false;
        return !TryAcquireProbeSlot();
    }

    /// <summary>
    /// Consumer-side convenience: classify <paramref name="ex"/> via the supplied
    /// <paramref name="classify"/> delegate and, when it maps to an auth failure,
    /// record it on the gate so the gate latches. <paramref name="classify"/> returns
    /// <c>null</c> when the exception is NOT an auth failure (no latch) — this is the
    /// only service-specific part each plugin supplies (401/403, plus e.g. apple's
    /// "user token" <see cref="InvalidOperationException"/>).
    /// <para>
    /// <strong>Why this lives in Common:</strong> every bridge entry point was
    /// duplicating the <c>Task.Run(() =&gt; Handler.HandleFailureAsync(failure).AsTask())
    /// .GetAwaiter().GetResult()</c> hop. That sync-over-async pattern (Category A) is a
    /// deadlock trap — a direct <c>.GetAwaiter().GetResult()</c> on the handler's
    /// <see cref="ValueTask"/> can deadlock under Lidarr's single-threaded host
    /// <see cref="SynchronizationContext"/> because the async continuation is posted
    /// back to the blocked context. Encapsulating it once means the error-prone line
    /// has a single tested home.
    /// </para>
    /// </summary>
    /// <param name="ex">The exception observed at the bridge entry point.</param>
    /// <param name="classify">
    /// Maps an exception to an <see cref="AuthFailure"/> (latch) or <c>null</c> (ignore).
    /// </param>
    public void RecordExceptionOutcome(Exception ex, Func<Exception, AuthFailure?> classify)
    {
        if (ex is null) throw new ArgumentNullException(nameof(ex));
        if (classify is null) throw new ArgumentNullException(nameof(classify));

        AuthFailure? failure = classify(ex);
        if (failure is null) return;

        // SYNC-OVER-ASYNC (Category A): hop to the thread-pool before blocking on the
        // ValueTask so we don't deadlock the host's single-threaded SynchronizationContext.
        Task.Run(() => Handler.HandleFailureAsync(failure).AsTask()).GetAwaiter().GetResult();
    }

    /// <summary>
    /// True when the underlying handler is in a state that allows requests to
    /// proceed without restriction (Authenticated or Unknown AND no in-flight
    /// failure streak). With <c>failureThreshold &gt; 1</c>, the K-1 failures
    /// before latching also flip the gate into rate-limit mode — otherwise
    /// the sub-threshold window would re-enable the original hammering scenario.
    /// </summary>
    public bool IsHealthy =>
        Handler.Status is AuthStatus.Authenticated or AuthStatus.Unknown
        && (Handler as DefaultAuthFailureHandler)?.ConsecutiveFailureCount is null or 0;

    /// <summary>
    /// Operator-side observability snapshot. Counters are monotonic across
    /// the lifetime of the gate; timestamps are the most recent occurrence
    /// of each transition. Read-only.
    /// </summary>
    public AuthFailureGateMetrics Metrics
    {
        get
        {
            lock (_lock)
            {
                return new AuthFailureGateMetrics(
                    LatchTransitions: _latchTransitions,
                    RecoveryTransitions: _recoveryTransitions,
                    ProbeAcquired: _probeAcquired,
                    ProbeRejected: _probeRejected,
                    ProbeRefunded: _probeRefunded,
                    LastLatchAt: _lastLatchAt,
                    LastRecoveryAt: _lastRecoveryAt);
            }
        }
    }

    /// <summary>
    /// Throws <see cref="AuthGatedException"/> when authentication is known bad.
    /// Returns silently when auth is healthy or in the indeterminate Unknown state.
    /// Reads <see cref="IAuthFailureHandler.LastFailure"/> via the interface so
    /// custom handlers carry through the actionable failure detail.
    /// </summary>
    public void EnsureCanProceed()
    {
        ResetIfRecovered();
        if (IsHealthy) return;

        var retryAfter = ComputeRetryAfter();
        var failure = Handler.LastFailure;
        var msg = failure?.Message is { Length: > 0 } m ? m : $"Auth status is {Handler.Status}";
        throw new AuthGatedException(Handler.Status, msg, failure?.ErrorCode, retryAfter);
    }

    /// <summary>
    /// Acquire a probe slot AND return the timestamp it was committed at, so
    /// a caller that fails before reaching the upstream (e.g. cancelled HTTP
    /// request, DNS failure) can refund the slot via <see cref="RefundProbeSlot"/>.
    /// Returns null when no slot was acquired (gate healthy, or probe rate-limited).
    /// </summary>
    internal DateTimeOffset? AcquireProbeSlotWithTimestamp()
    {
        ResetIfRecovered();
        if (IsHealthy)
        {
            // Healthy callers don't need a probe slot — they just proceed.
            return null;
        }

        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            if (_lastProbeAt is null || now - _lastProbeAt.Value >= _probeInterval)
            {
                _lastProbeAt = now;
                _probeAcquired++;
                _logger.LogDebug("AuthFailureGate: probe slot acquired at {Now}; next eligible at {Next}",
                    now, now + _probeInterval);
                return now;
            }
            _probeRejected++;
            return null;
        }
    }

    /// <summary>
    /// Refund a probe slot previously acquired via <see cref="AcquireProbeSlotWithTimestamp"/>,
    /// but only if no other caller has grabbed a slot in the meantime (CAS).
    /// Use when the network call never executed (cancellation, pre-send error)
    /// so the probe budget isn't burned for nothing — the operator's recovery
    /// window stays tight.
    /// </summary>
    /// <remarks>
    /// Known transient anomaly: if caller B is
    /// rejected based on the slot held by caller A, then caller A's request
    /// fails pre-network and refunds the slot, caller C can immediately
    /// acquire — leaving B with an unnecessary rejection while C succeeds.
    /// This is a one-shot ordering quirk (B's caller can retry on the next
    /// call and will succeed), not a probe-budget violation. Documented
    /// rather than guarded because deterministic reproduction requires
    /// ms-precise coordination not worth the test infrastructure cost.
    /// </remarks>
    internal void RefundProbeSlot(DateTimeOffset slotTimestamp)
    {
        lock (_lock)
        {
            if (_lastProbeAt == slotTimestamp)
            {
                _lastProbeAt = null;
                _probeRefunded++;
                _logger.LogDebug("AuthFailureGate: probe slot refunded (network call did not execute)");
            }
        }
    }

    /// <summary>
    /// When auth is healthy, always returns true (no rate-limit on probes).
    /// When auth is latched bad, returns true at most once per <c>probeInterval</c>,
    /// so the plugin can deliberately attempt one network call to detect that
    /// the user re-credentialed without spamming the upstream.
    /// </summary>
    public bool TryAcquireProbeSlot()
    {
        ResetIfRecovered();
        if (IsHealthy) return true;

        lock (_lock)
        {
            var now = _clock.GetUtcNow();
            if (_lastProbeAt is null || now - _lastProbeAt.Value >= _probeInterval)
            {
                _lastProbeAt = now;
                _probeAcquired++;
                _logger.LogDebug("AuthFailureGate: probe slot acquired at {Now}; next eligible at {Next}",
                    now, now + _probeInterval);
                return true;
            }
            _probeRejected++;
            return false;
        }
    }

    private TimeSpan? ComputeRetryAfter()
    {
        lock (_lock)
        {
            if (_lastProbeAt is null)
            {
                // No probe has been issued yet. The full probe interval is the
                // most conservative hint we can give callers — null would let
                // them retry immediately, defeating the gate.
                return _probeInterval;
            }
            var elapsed = _clock.GetUtcNow() - _lastProbeAt.Value;
            var remaining = _probeInterval - elapsed;
            return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
        }
    }

    /// <summary>
    /// Forcibly reset the gate to fully open: status back to Unknown,
    /// last failure cleared, probe-slot budget cleared. Used by the
    /// <see cref="IAuthFailureGateRegistry"/> Reset(key) flow so a
    /// settings-UI "Test Connection" can retry immediately after the user
    /// supplies fresh credentials. No-ops on non-default handlers.
    /// </summary>
    public void ForceReset()
    {
        if (Handler is DefaultAuthFailureHandler defaultHandler)
        {
            defaultHandler.ResetToUnknown();
        }
        lock (_lock)
        {
            _lastProbeAt = null;
            _lastObservedStatus = AuthStatus.Unknown;
        }
        // Emit an observable signal so an operator who triggered "Test
        // Connection" can see in logs that the reset took effect, even if
        // the subsequent connectivity test fails.
        _logger.LogInformation("AuthFailureGate: forcibly RESET to Unknown");
    }

    private void ResetIfRecovered()
    {
        lock (_lock)
        {
            var status = Handler.Status;
            if (status != _lastObservedStatus)
            {
                // Track transitions for observability.
                if (status is AuthStatus.Failed or AuthStatus.Expired &&
                    _lastObservedStatus is AuthStatus.Authenticated or AuthStatus.Unknown)
                {
                    _latchTransitions++;
                    _lastLatchAt = _clock.GetUtcNow();
                    _logger.LogWarning("AuthFailureGate: LATCH at {Now} (status {Status})",
                        _lastLatchAt, status);
                }
                else if (status == AuthStatus.Authenticated &&
                         _lastObservedStatus is AuthStatus.Failed or AuthStatus.Expired)
                {
                    _recoveryTransitions++;
                    _lastRecoveryAt = _clock.GetUtcNow();
                    var latchDuration = _lastLatchAt is { } latch ? _lastRecoveryAt - latch : null;
                    _logger.LogInformation(
                        "AuthFailureGate: RECOVERY at {Now} (latched for {Duration})",
                        _lastRecoveryAt, latchDuration);
                }

                // Status changed — track it but PRESERVE _lastProbeAt across
                // the transition.
                //
                // The original implementation zeroed _lastProbeAt on
                // recovery → next re-latch granted a fresh probe slot. That
                // created a "1-out-of-N hammer" pattern when the recovery
                // signal was wrong (e.g. a 200 from a cached/public endpoint
                // that doesn't actually validate the user's credential).
                //
                // The new contract: probe slots are time-budgeted, not
                // status-budgeted. The clock is the only thing that frees
                // a slot. When healthy, the probe slot is irrelevant (the
                // gate short-circuits to "always allow"). When latched bad
                // again, the OLD timestamp is still valid and the probe is
                // correctly rate-limited if we're within the original window.
                _lastObservedStatus = status;
            }
        }
    }
}

/// <summary>
/// Operator-side snapshot of an <see cref="AuthFailureGate"/>'s lifecycle.
/// All counters are monotonic. Timestamps are the most recent occurrence.
/// </summary>
public readonly record struct AuthFailureGateMetrics(
    long LatchTransitions,
    long RecoveryTransitions,
    long ProbeAcquired,
    long ProbeRejected,
    long ProbeRefunded,
    DateTimeOffset? LastLatchAt,
    DateTimeOffset? LastRecoveryAt);

/// <summary>
/// Thrown by <see cref="AuthFailureGate.EnsureCanProceed"/> when an outbound
/// request is short-circuited because authentication is latched bad. Carries
/// <see cref="RetryAfter"/> so callers can surface a delay to the host
/// (instead of every call going to the network and getting 401).
/// </summary>
public sealed class AuthGatedException : Exception
{
    public AuthStatus Status { get; }
    public string? ErrorCode { get; }
    public TimeSpan? RetryAfter { get; }

    public AuthGatedException(AuthStatus status, string message, string? errorCode = null, TimeSpan? retryAfter = null)
        : base(message)
    {
        Status = status;
        ErrorCode = errorCode;
        RetryAfter = retryAfter;
    }
}
