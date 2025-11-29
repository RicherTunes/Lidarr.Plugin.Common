// Licensed under the MIT License.

using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Utilities
{
    /// <summary>
    /// Provides per-host concurrency control using semaphores.
    /// Ensures that requests to the same host are throttled to prevent overwhelming the server.
    /// </summary>
    /// <remarks>
    /// This pattern is extracted from Qobuzarr's ConcurrencyManager and generalized for use
    /// across all streaming plugins. It provides a simple way to limit concurrent requests
    /// to any given host without complex adaptive logic.
    /// </remarks>
    public sealed class HostConcurrencyGate : IDisposable
    {
        private readonly ConcurrentDictionary<string, SemaphoreSlim> _hostSemaphores = new();
        private readonly int _maxConcurrencyPerHost;
        private volatile bool _disposed;

        /// <summary>
        /// Initializes a new instance of the <see cref="HostConcurrencyGate"/> class.
        /// </summary>
        /// <param name="maxConcurrencyPerHost">Maximum concurrent requests allowed per host.</param>
        /// <exception cref="ArgumentOutOfRangeException">Thrown when <paramref name="maxConcurrencyPerHost"/> is less than 1.</exception>
        public HostConcurrencyGate(int maxConcurrencyPerHost = 4)
        {
            if (maxConcurrencyPerHost < 1)
            {
                throw new ArgumentOutOfRangeException(nameof(maxConcurrencyPerHost), "Concurrency limit must be at least 1.");
            }

            _maxConcurrencyPerHost = maxConcurrencyPerHost;
        }

        /// <summary>
        /// Gets the maximum concurrency limit per host.
        /// </summary>
        public int MaxConcurrencyPerHost => _maxConcurrencyPerHost;

        /// <summary>
        /// Gets the number of hosts currently being tracked.
        /// </summary>
        public int TrackedHostCount => _hostSemaphores.Count;

        /// <summary>
        /// Acquires a concurrency slot for the specified host.
        /// </summary>
        /// <param name="host">The host to acquire a slot for.</param>
        /// <param name="cancellationToken">Cancellation token.</param>
        /// <returns>A disposable that releases the slot when disposed.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when the gate has been disposed.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="host"/> is null or whitespace.</exception>
        public async Task<IDisposable> AcquireAsync(string host, CancellationToken cancellationToken = default)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Host cannot be null or whitespace.", nameof(host));
            }

            var normalizedHost = NormalizeHost(host);
            var semaphore = _hostSemaphores.GetOrAdd(normalizedHost, _ => new SemaphoreSlim(_maxConcurrencyPerHost, _maxConcurrencyPerHost));

            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);

            return new ConcurrencySlotReleaser(semaphore);
        }

        /// <summary>
        /// Attempts to acquire a concurrency slot for the specified host without waiting.
        /// </summary>
        /// <param name="host">The host to acquire a slot for.</param>
        /// <param name="slot">The acquired slot, or null if no slot was available.</param>
        /// <returns>True if a slot was acquired; otherwise, false.</returns>
        /// <exception cref="ObjectDisposedException">Thrown when the gate has been disposed.</exception>
        /// <exception cref="ArgumentException">Thrown when <paramref name="host"/> is null or whitespace.</exception>
        public bool TryAcquire(string host, out IDisposable? slot)
        {
            ThrowIfDisposed();

            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("Host cannot be null or whitespace.", nameof(host));
            }

            var normalizedHost = NormalizeHost(host);
            var semaphore = _hostSemaphores.GetOrAdd(normalizedHost, _ => new SemaphoreSlim(_maxConcurrencyPerHost, _maxConcurrencyPerHost));

            if (semaphore.Wait(0))
            {
                slot = new ConcurrencySlotReleaser(semaphore);
                return true;
            }

            slot = null;
            return false;
        }

        /// <summary>
        /// Gets the number of available slots for the specified host.
        /// </summary>
        /// <param name="host">The host to check.</param>
        /// <returns>The number of available slots.</returns>
        public int GetAvailableSlots(string host)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                return _maxConcurrencyPerHost;
            }

            var normalizedHost = NormalizeHost(host);
            return _hostSemaphores.TryGetValue(normalizedHost, out var semaphore)
                ? semaphore.CurrentCount
                : _maxConcurrencyPerHost;
        }

        /// <summary>
        /// Gets statistics for all tracked hosts.
        /// </summary>
        /// <returns>A dictionary of host to available slot count.</returns>
        public ConcurrentDictionary<string, int> GetStatistics()
        {
            var stats = new ConcurrentDictionary<string, int>();

            foreach (var kvp in _hostSemaphores)
            {
                stats[kvp.Key] = kvp.Value.CurrentCount;
            }

            return stats;
        }

        private static string NormalizeHost(string host)
        {
            // Normalize to lowercase and remove any port specification
            var normalized = host.ToLowerInvariant();

            // Handle URI format
            if (Uri.TryCreate(host, UriKind.Absolute, out var uri))
            {
                return uri.Host.ToLowerInvariant();
            }

            // Remove port if present
            var colonIndex = normalized.LastIndexOf(':');
            if (colonIndex > 0 && colonIndex < normalized.Length - 1)
            {
                var potentialPort = normalized.Substring(colonIndex + 1);
                if (int.TryParse(potentialPort, out _))
                {
                    normalized = normalized.Substring(0, colonIndex);
                }
            }

            return normalized;
        }

        private void ThrowIfDisposed()
        {
            if (_disposed)
            {
                throw new ObjectDisposedException(nameof(HostConcurrencyGate));
            }
        }

        /// <inheritdoc/>
        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            foreach (var semaphore in _hostSemaphores.Values)
            {
                try
                {
                    semaphore.Dispose();
                }
                catch
                {
                    // Best-effort disposal
                }
            }

            _hostSemaphores.Clear();
        }

        private sealed class ConcurrencySlotReleaser : IDisposable
        {
            private readonly SemaphoreSlim _semaphore;
            private volatile bool _disposed;

            public ConcurrencySlotReleaser(SemaphoreSlim semaphore)
            {
                _semaphore = semaphore ?? throw new ArgumentNullException(nameof(semaphore));
            }

            public void Dispose()
            {
                if (_disposed)
                {
                    return;
                }

                _disposed = true;

                try
                {
                    _semaphore.Release();
                }
                catch (ObjectDisposedException)
                {
                    // Semaphore was disposed, ignore
                }
                catch (SemaphoreFullException)
                {
                    // Semaphore is full (shouldn't happen normally), ignore
                }
            }
        }
    }
}
