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
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask HandleSuccessAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Requests re-authentication from the user.
        /// </summary>
        /// <param name="reason">Reason re-auth is needed</param>
        /// <param name="cancellationToken">Cancellation token</param>
        ValueTask RequestReauthenticationAsync(string reason, CancellationToken cancellationToken = default);
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
