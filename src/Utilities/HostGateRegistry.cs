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
        private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
        private static Timer? Sweeper;
        private static readonly object SweeperInitLock = new();

        static HostGateRegistry()
        {
            // Background sweeper to dispose idle gates and avoid unbounded growth in long-lived processes.
            Sweeper = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
        }

        public static SemaphoreSlim Get(string? host, int requestedLimit)
        {
            EnsureSweeper();
            host ??= "__unknown__";
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

        public static SemaphoreSlim GetAggregate(string? host, int requestedLimit)
        {
            EnsureSweeper();
            host ??= "__unknown__";
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

            // Do not Dispose() semaphores here; concurrent in-flight operations may still
            // reference them and call Release(), which would throw ObjectDisposedException.
            // Removing from the dictionaries is sufficient; GC will reclaim when unused.
            Gates.TryRemove(host, out _);
            AggregateGates.TryRemove(host, out _);
        }

        private static void Sweep()
        {
            try
            {
                var now = DateTime.UtcNow;
                foreach (var kv in Gates)
                {
                    var state = kv.Value;
                    if (state.Semaphore.CurrentCount == state.Limit && (now - state.LastUsedUtc) > IdleTtl)
                    {
                        // Remove idle entries; do not dispose semaphores to avoid races with
                        // late Release() calls from in-flight operations.
                        Gates.TryRemove(kv.Key, out _);
                    }
                }
                foreach (var kv in AggregateGates)
                {
                    var state = kv.Value;
                    if (state.Semaphore.CurrentCount == state.Limit && (now - state.LastUsedUtc) > IdleTtl)
                    {
                        AggregateGates.TryRemove(kv.Key, out _);
                    }
                }
            }
            catch
            {
                // best-effort cleanup; ignore sweep errors
            }
        }

        /// <summary>
        /// Disposes background timers and clears all gates within this AssemblyLoadContext.
        /// Call from plugin unload paths to avoid collectible ALC retention via Timer roots.
        /// </summary>
        internal static void Shutdown()
        {
            try { Sweeper?.Dispose(); } catch { }
            Sweeper = null;
            try
            {
                foreach (var kv in Gates) { Gates.TryRemove(kv.Key, out _); }
                foreach (var kv in AggregateGates) { AggregateGates.TryRemove(kv.Key, out _); }
            }
            catch { }
        }

        private static void EnsureSweeper()
        {
            if (Sweeper != null) return;
            lock (SweeperInitLock)
            {
                if (Sweeper == null)
                {
                    try { Sweeper = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval); } catch { }
                }
            }
        }
    }
}
