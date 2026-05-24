using System;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Abstractions.Contracts
{
    /// <summary>
    /// Handles authentication failure events.
    /// Bridge plugins use this to propagate auth failures to the host for UI notification.
    /// </summary>
    public interface IAuthFailureHandler
    {
        /// <summary>
        /// Gets the current authentication status.
        /// </summary>
        AuthStatus Status { get; }

        /// <summary>
        /// Handles an authentication failure event.
        /// </summary>
        /// <param name="failure">Details of the authentication failure</param>
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask HandleFailureAsync(AuthFailure failure, CancellationToken cancellationToken = default);

        /// <summary>
        /// Handles successful authentication.
        /// </summary>
        /// <remarks>
        /// IMPLEMENTATIONS MUST BE IDEMPOTENT. Callers (e.g.
        /// <c>AuthFailureDelegatingHandler</c>) may invoke this method on
        /// every successful response while the handler is in a non-healthy
        /// state, and concurrent probes during recovery may produce overlapping
        /// calls. The handler must not produce externally-visible side effects
        /// beyond the first invocation per recovery (no double-counting
        /// metrics, no duplicate event emission, no repeated DB writes).
        /// </remarks>
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask HandleSuccessAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Requests re-authentication from the user.
        /// </summary>
        /// <param name="reason">Reason re-auth is needed</param>
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask RequestReauthenticationAsync(string reason, CancellationToken cancellationToken = default);

        /// <summary>
        /// Most recent failure details, or null if no failure has been observed
        /// since the last <see cref="HandleSuccessAsync"/>. Implementers SHOULD
        /// expose this so consumers (e.g. <c>AuthFailureGate</c>) can surface
        /// actionable error messages without downcasting to a specific
        /// implementation. Default returns null for backwards compatibility
        /// with handlers that haven't been updated.
        /// </summary>
        AuthFailure? LastFailure => null;
    }

    /// <summary>
    /// Represents authentication status.
    /// </summary>
    public enum AuthStatus
    {
        /// <summary>
        /// Authentication state unknown.
        /// </summary>
        Unknown,

        /// <summary>
        /// Successfully authenticated.
        /// </summary>
        Authenticated,

        /// <summary>
        /// Not authenticated.
        /// </summary>
        Unauthenticated,

        /// <summary>
        /// Authentication expired.
        /// </summary>
        Expired,

        /// <summary>
        /// Authentication failed.
        /// </summary>
        Failed
    }

    /// <summary>
    /// Details of an authentication failure.
    /// </summary>
    public class AuthFailure
    {
        /// <summary>
        /// Error code for the failure.
        /// </summary>
        public string? ErrorCode { get; init; }

        /// <summary>
        /// Human-readable error message.
        /// </summary>
        public string Message { get; init; } = string.Empty;

        /// <summary>
        /// When the failure occurred.
        /// </summary>
        public DateTimeOffset Timestamp { get; init; } = DateTimeOffset.UtcNow;

        /// <summary>
        /// Whether re-authentication is possible.
        /// </summary>
        public bool CanReauthenticate { get; init; } = true;

        /// <summary>
        /// OAuth authorization URL for re-authentication (if applicable).
        /// </summary>
        public string? AuthorizationUrl { get; init; }
    }
}
