using System;
using System.Collections.Concurrent;
using System.Threading;

namespace Lidarr.Plugin.Common.Utilities
{
    internal static class HostGateRegistry
    {
        private sealed class GateState
        {
            private readonly object _lock = new();

            public GateState(int limit)
            {
                Semaphore = new SemaphoreSlim(limit, int.MaxValue);
                Limit = limit;
                LastUsedUtc = DateTime.UtcNow;
            }

            public SemaphoreSlim Semaphore { get; }
            public int Limit { get; private set; }
            public DateTime LastUsedUtc { get; private set; }

            public void EnsureLimit(int requestedLimit)
            {
                if (requestedLimit <= Limit)
                {
                    return;
                }

                lock (_lock)
                {
                    if (requestedLimit <= Limit)
                    {
                        return;
                    }

                    var delta = requestedLimit - Limit;
                    Semaphore.Release(delta);
                    Limit = requestedLimit;
                }
            }

            public void Touch()
            {
                LastUsedUtc = DateTime.UtcNow;
            }
        }

        private static readonly ConcurrentDictionary<string, GateState> Gates = new();
        private static readonly ConcurrentDictionary<string, GateState> AggregateGates = new();
        private static readonly object LifecycleLock = new();
        private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
        // Nullable so Shutdown() can null it via Interlocked.Exchange (readonly would prevent that).
        private static Timer? _sweeper;

        static HostGateRegistry()
        {
            // Background sweeper to dispose idle gates and avoid unbounded growth in long-lived processes.
            _sweeper = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
        }

        // Test/diagnostic observability: whether the idle-gate sweeper timer is currently armed.
        internal static bool IsSweeperActive => Volatile.Read(ref _sweeper) is not null;

        // Re-arm the sweeper lazily after a prior Shutdown() nulled it. Lidarr keeps the host
        // process alive across plugin reloads, so without this the first unload's Shutdown()
        // would kill idle-gate eviction permanently and every subsequently-added gate (each
        // owning a SemaphoreSlim) would accumulate forever.
        private static void EnsureSweeper()
        {
            if (Volatile.Read(ref _sweeper) is not null)
            {
                return;
            }

            var timer = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
            // Only install if still null; if another thread won the race, dispose ours.
            if (Interlocked.CompareExchange(ref _sweeper, timer, null) is not null)
            {
                timer.Dispose();
            }
        }

        public static SemaphoreSlim Get(string? host, int requestedLimit)
        {
            host ??= "__unknown__";
            lock (LifecycleLock)
            {
                EnsureSweeper();
                var gate = Gates.AddOrUpdate(
                    host,
                    _ => new GateState(requestedLimit),
                    (_, existing) =>
                    {
                        existing.EnsureLimit(requestedLimit);
                        return existing;
                    });

                gate.Touch();
                return gate.Semaphore;
            }
        }

        public static SemaphoreSlim GetAggregate(string? host, int requestedLimit)
        {
            host ??= "__unknown__";
            lock (LifecycleLock)
            {
                EnsureSweeper();
                var gate = AggregateGates.AddOrUpdate(
                    host,
                    _ => new GateState(requestedLimit),
                    (_, existing) =>
                    {
                        existing.EnsureLimit(requestedLimit);
                        return existing;
                    });

                gate.Touch();
                return gate.Semaphore;
            }
        }

        public static bool TryGetState(string? host, out (SemaphoreSlim Semaphore, int Limit) state)
        {
            host ??= "__unknown__";

            if (Gates.TryGetValue(host, out var gate))
            {
                state = (gate.Semaphore, gate.Limit);
                return true;
            }

            state = default;
            return false;
        }

        public static void Clear(string? host)
        {
            host ??= "__unknown__";

            if (Gates.TryRemove(host, out var gate))
            {
                gate.Semaphore.Dispose();
            }
            if (AggregateGates.TryRemove(host, out var agg))
            {
                agg.Semaphore.Dispose();
            }
        }

        /// <summary>
        /// Disposes the background sweeper Timer, clears all gate entries, and resets internal
        /// state so the registry is safe to use again after the next call to <see cref="Get"/>
        /// or <see cref="GetAggregate"/> (the timer is re-created lazily on first access).
        /// <para>
        /// Call this during plugin unload / Dispose to prevent the static Timer from continuing
        /// to fire against an already-unloaded AssemblyLoadContext.  Safe to call multiple times
        /// (idempotent) and safe to call even if the registry has never been accessed.
        /// </para>
        /// </summary>
        public static void Shutdown()
        {
            lock (LifecycleLock)
            {
                // Swap the sweeper with a sentinel null so concurrent Sweep() calls are no-ops.
                var sweeper = Interlocked.Exchange(ref _sweeper, null);
                sweeper?.Dispose();

                // Drain Gates
                foreach (var kv in Gates)
                {
                    if (Gates.TryRemove(kv.Key, out var removed))
                    {
                        try { removed.Semaphore.Dispose(); } catch { /* best-effort */ }
                    }
                }

                // Drain AggregateGates
                foreach (var kv in AggregateGates)
                {
                    if (AggregateGates.TryRemove(kv.Key, out var removed))
                    {
                        try { removed.Semaphore.Dispose(); } catch { /* best-effort */ }
                    }
                }
            }
        }

        private static void Sweep()
        {
            // Guard: Shutdown() may have nulled out the sweeper reference.
            if (_sweeper is null)
            {
                return;
            }

            try
            {
                var now = DateTime.UtcNow;
                foreach (var kv in Gates)
                {
                    var state = kv.Value;
                    if (state.Semaphore.CurrentCount == state.Limit && (now - state.LastUsedUtc) > IdleTtl)
                    {
                        if (Gates.TryRemove(kv.Key, out var removed))
                        {
                            removed.Semaphore.Dispose();
                        }
                    }
                }
                foreach (var kv in AggregateGates)
                {
                    var state = kv.Value;
                    if (state.Semaphore.CurrentCount == state.Limit && (now - state.LastUsedUtc) > IdleTtl)
                    {
                        if (AggregateGates.TryRemove(kv.Key, out var removed))
                        {
                            removed.Semaphore.Dispose();
                        }
                    }
                }
            }
            catch
            {
                // best-effort cleanup; ignore sweep errors
            }
        }
    }
}
