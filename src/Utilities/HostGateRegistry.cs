using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;

namespace Lidarr.Plugin.Common.Utilities
{
    internal static class HostGateRegistry
    {
        // All membership, reference counts, limit changes and retirement are guarded
        // by LifecycleLock. No lock is held while waiting for a permit or sending HTTP.
        internal sealed class GateState
        {
            internal GateState(string key, int limit, Dictionary<string, GateState> owner)
            {
                Key = key;
                Owner = owner;
                Semaphore = new SemaphoreSlim(limit, int.MaxValue);
                Limit = limit;
                LastUsedTimestamp = Stopwatch.GetTimestamp();
            }

            internal string Key { get; }
            internal Dictionary<string, GateState> Owner { get; }
            internal SemaphoreSlim Semaphore { get; }
            internal int Limit { get; set; }
            internal int References { get; set; }
            internal long LastUsedTimestamp { get; set; }
            internal bool RetireWhenIdle { get; set; }
        }

        // A reservation protects both queued and running operations, including the
        // interval between lookup and WaitAsync. Dispose only after permit release.
        internal sealed class Reservation : IDisposable
        {
            private readonly GateState _profile;
            private readonly GateState? _aggregate;
            private int _released;

            internal Reservation(GateState profile, GateState? aggregate)
            {
                _profile = profile;
                _aggregate = aggregate;
            }

            internal SemaphoreSlim Profile => _profile.Semaphore;
            internal SemaphoreSlim? Aggregate => _aggregate?.Semaphore;

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _released, 1) != 0) return;
                lock (LifecycleLock)
                {
                    Return(_profile);
                    if (_aggregate is not null) Return(_aggregate);
                }
            }
        }

        private static readonly Dictionary<string, GateState> Gates = new(StringComparer.Ordinal);
        private static readonly Dictionary<string, GateState> AggregateGates = new(StringComparer.Ordinal);
        private static readonly object LifecycleLock = new();
        private static readonly TimeSpan IdleTtl = TimeSpan.FromMinutes(10);
        private static readonly TimeSpan SweepInterval = TimeSpan.FromMinutes(5);
        private static Timer? _sweeper;
        private static object? _sweepGeneration;

        internal static bool IsSweeperActive => Volatile.Read(ref _sweeper) is not null;

        private static void EnsureSweeper()
        {
            if (_sweeper is not null) return;
            var generation = new object();
            var timer = new Timer(Sweep, generation, SweepInterval, SweepInterval);
            _sweepGeneration = generation;
            _sweeper = timer;
        }

        private static GateState GetOrCreate(Dictionary<string, GateState> owner, string? host, int requestedLimit)
        {
            if (requestedLimit < 1) throw new ArgumentOutOfRangeException(nameof(requestedLimit));
            host ??= "__unknown__";
            EnsureSweeper();
            if (!owner.TryGetValue(host, out var state))
            {
                state = new GateState(host, requestedLimit, owner);
                owner.Add(host, state);
            }
            else if (requestedLimit > state.Limit)
            {
                state.Semaphore.Release(requestedLimit - state.Limit);
                state.Limit = requestedLimit;
            }
            state.LastUsedTimestamp = Stopwatch.GetTimestamp();
            return state;
        }

        // Raw access is retained for existing diagnostics and test fixtures only.
        // Production users must reserve through HostGateLease before any await.
        public static SemaphoreSlim Get(string? host, int requestedLimit)
        {
            lock (LifecycleLock) return GetOrCreate(Gates, host, requestedLimit).Semaphore;
        }

        public static SemaphoreSlim GetAggregate(string? host, int requestedLimit)
        {
            lock (LifecycleLock) return GetOrCreate(AggregateGates, host, requestedLimit).Semaphore;
        }

        internal static Reservation Reserve(string? profileHost, int profileLimit, string? aggregateHost = null, int? aggregateLimit = null)
        {
            lock (LifecycleLock)
            {
                if (profileLimit < 1) throw new ArgumentOutOfRangeException(nameof(profileLimit));
                if (aggregateLimit.HasValue && aggregateLimit.Value < 1) throw new ArgumentOutOfRangeException(nameof(aggregateLimit));
                var profile = GetOrCreate(Gates, profileHost, profileLimit);
                var aggregate = aggregateLimit.HasValue ? GetOrCreate(AggregateGates, aggregateHost, aggregateLimit.Value) : null;
                var reservation = new Reservation(profile, aggregate);
                var profileReferences = checked(profile.References + 1);
                var aggregateReferences = aggregate is null ? 0 : checked(aggregate.References + 1);
                profile.References = profileReferences;
                if (aggregate is not null) aggregate.References = aggregateReferences;
                return reservation;
            }
        }

        private static void Return(GateState state)
        {
            state.References--;
            if (state.References != 0) return;
            state.LastUsedTimestamp = Stopwatch.GetTimestamp();
            if (state.RetireWhenIdle) RemoveIfQuiescent(state);
        }

        private static void RemoveIfQuiescent(GateState state)
        {
            // CurrentCount additionally protects manually held diagnostic permits.
            // Real operations are protected by References, not a semaphore snapshot.
            if (state.References != 0 || state.Semaphore.CurrentCount != state.Limit) return;
            if (state.Owner.TryGetValue(state.Key, out var current) && ReferenceEquals(current, state))
            {
                state.Owner.Remove(state.Key);
                state.Semaphore.Dispose();
            }
        }

        public static bool TryGetState(string? host, out (SemaphoreSlim Semaphore, int Limit) state)
        {
            lock (LifecycleLock)
            {
                if (Gates.TryGetValue(host ?? "__unknown__", out var gate))
                {
                    state = (gate.Semaphore, gate.Limit);
                    return true;
                }
                state = default;
                return false;
            }
        }

        private static void Retire(GateState state)
        {
            state.RetireWhenIdle = true;
            RemoveIfQuiescent(state);
        }

        public static void Clear(string? host)
        {
            host ??= "__unknown__";
            lock (LifecycleLock)
            {
                if (Gates.TryGetValue(host, out var gate)) Retire(gate);
                if (AggregateGates.TryGetValue(host, out var aggregate)) Retire(aggregate);
            }
        }

        /// <summary>
        /// Stops idle sweeping and retires entries. Existing reservations can drain;
        /// their semaphores are disposed on the last return, never under a waiter.
        /// Reuse restarts sweeping. Same-key traffic shares any draining gate rather
        /// than creating a second concurrency budget during a reload.
        /// </summary>
        public static void Shutdown()
        {
            lock (LifecycleLock)
            {
                var timer = _sweeper;
                _sweeper = null;
                _sweepGeneration = null;
                timer?.Dispose();
                foreach (var gate in Gates.Values.ToArray()) Retire(gate);
                foreach (var gate in AggregateGates.Values.ToArray()) Retire(gate);
            }
        }

        // Exercise exactly the production sweep under deterministic age thresholds.
        internal static void SweepIdle(TimeSpan minimumIdleAge)
        {
            if (minimumIdleAge < TimeSpan.Zero) throw new ArgumentOutOfRangeException(nameof(minimumIdleAge));
            lock (LifecycleLock) SweepIdleUnderLock(minimumIdleAge);
        }

        private static void SweepIdleUnderLock(TimeSpan minimumIdleAge)
        {
            foreach (var gate in Gates.Values.Concat(AggregateGates.Values).ToArray())
            {
                if (gate.References == 0 && Stopwatch.GetElapsedTime(gate.LastUsedTimestamp) >= minimumIdleAge)
                {
                    RemoveIfQuiescent(gate);
                }
            }
        }

        private static void Sweep(object? generation)
        {
            lock (LifecycleLock)
            {
                // A callback queued before Shutdown must not operate on a later
                // timer generation, even if Get has already rearmed the registry.
                if (_sweeper is null || !ReferenceEquals(generation, _sweepGeneration)) return;
                SweepIdleUnderLock(IdleTtl);
            }
        }
    }
}
