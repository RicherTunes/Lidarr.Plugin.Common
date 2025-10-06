using System;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Interfaces;

namespace Lidarr.Plugin.Common.Services.Authentication
{
    /// <summary>
    /// In-memory token store suitable for tests or transient runtime scenarios.
    /// </summary>
    /// <typeparam name="TSession">Session representation type.</typeparam>
    internal sealed class MemoryTokenStore<TSession> : ITokenStore<TSession>
        where TSession : class
    {
        private readonly object _sync = new();
        private TokenEnvelope<TSession>? _envelope;

        /// <inheritdoc />
        public Task<TokenEnvelope<TSession>?> LoadAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                return Task.FromResult(_envelope);
            }
        }

        /// <inheritdoc />
        public Task SaveAsync(TokenEnvelope<TSession> envelope, CancellationToken cancellationToken = default)
        {
            if (envelope == null)
            {
                throw new ArgumentNullException(nameof(envelope));
            }

            lock (_sync)
            {
                _envelope = envelope;
            }

            return Task.CompletedTask;
        }

        /// <inheritdoc />
        public Task ClearAsync(CancellationToken cancellationToken = default)
        {
            lock (_sync)
            {
                _envelope = null;
            }

            return Task.CompletedTask;
        }
    }
}
