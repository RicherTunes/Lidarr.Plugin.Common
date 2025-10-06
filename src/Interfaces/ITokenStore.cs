using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Interfaces
{
    /// <summary>
    /// Persists authentication sessions for streaming providers.
    /// </summary>
    /// <typeparam name="TSession">Type representing the authenticated session token.</typeparam>
    public interface ITokenStore<TSession>
        where TSession : class
    {
        /// <summary>
        /// Loads a previously persisted session, if available.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        /// <returns>The persisted session envelope or <c>null</c> when nothing has been stored.</returns>
        Task<TokenEnvelope<TSession>?> LoadAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Persists the provided session envelope for future retrieval.
        /// </summary>
        /// <param name="envelope">Session envelope to persist.</param>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        Task SaveAsync(TokenEnvelope<TSession> envelope, CancellationToken cancellationToken = default);

        /// <summary>
        /// Removes any persisted session data.
        /// </summary>
        /// <param name="cancellationToken">Cancellation token for the operation.</param>
        Task ClearAsync(CancellationToken cancellationToken = default);
    }

    /// <summary>
    /// Represents a persisted authentication session together with expiry metadata.
    /// </summary>
    /// <typeparam name="TSession">Type representing the authenticated session token.</typeparam>
    public sealed class TokenEnvelope<TSession>
        where TSession : class
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="TokenEnvelope{TSession}"/> class.
        /// </summary>
        /// <param name="session">Session payload to persist.</param>
        /// <param name="expiresAt">Optional absolute expiry for the session.</param>
        /// <param name="metadata">Optional metadata describing the session.</param>
        public TokenEnvelope(TSession session, DateTime? expiresAt = null, IReadOnlyDictionary<string, string>? metadata = null)
        {
            Session = session ?? throw new ArgumentNullException(nameof(session));
            ExpiresAt = expiresAt;
            Metadata = metadata ?? new Dictionary<string, string>();
        }

        /// <summary>
        /// Gets the session payload.
        /// </summary>
        public TSession Session { get; }

        /// <summary>
        /// Gets the optional expiry timestamp for the session.
        /// </summary>
        public DateTime? ExpiresAt { get; }

        /// <summary>
        /// Gets optional metadata associated with the session.
        /// </summary>
        public IReadOnlyDictionary<string, string> Metadata { get; }
    }
}
