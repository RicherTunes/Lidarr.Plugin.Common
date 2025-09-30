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
            }

            public SemaphoreSlim Semaphore { get; }
            public int Limit { get; private set; }

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
        }

        private static readonly ConcurrentDictionary<string, GateState> Gates = new();

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

            return gate.Semaphore;
        }
    }
}
