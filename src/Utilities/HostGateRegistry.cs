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
        private static readonly Timer Sweeper;

        static HostGateRegistry()
        {
            // Background sweeper to dispose idle gates and avoid unbounded growth in long-lived processes.
            Sweeper = new Timer(_ => Sweep(), null, SweepInterval, SweepInterval);
        }

        public static SemaphoreSlim Get(string? host, int requestedLimit)
        {
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

            if (Gates.TryRemove(host, out var gate))
            {
                gate.Semaphore.Dispose();
            }
            if (AggregateGates.TryRemove(host, out var agg))
            {
                agg.Semaphore.Dispose();
            }
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
