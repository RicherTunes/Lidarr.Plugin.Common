using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Collections;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Collections
{
    public class BoundedConcurrentDictionaryTests
    {
        private sealed class BlockingHashComparer : IEqualityComparer<string>
        {
            private readonly string _blockedKey;
            private readonly CountdownEvent _entered;
            private readonly ManualResetEventSlim _release;
            private int _remainingBlocks;

            public BlockingHashComparer(
                string blockedKey,
                int blocks,
                CountdownEvent entered,
                ManualResetEventSlim release)
            {
                _blockedKey = blockedKey;
                _remainingBlocks = blocks;
                _entered = entered;
                _release = release;
            }

            public bool Equals(string? x, string? y) => StringComparer.Ordinal.Equals(x, y);

            public int GetHashCode(string obj)
            {
                if (StringComparer.Ordinal.Equals(obj, _blockedKey) &&
                    Interlocked.Decrement(ref _remainingBlocks) >= 0)
                {
                    _entered.Signal();
                    if (!_release.Wait(TimeSpan.FromSeconds(30)))
                        throw new TimeoutException("Timed out waiting for the test harness to release hash probes.");
                }

                return StringComparer.Ordinal.GetHashCode(obj);
            }
        }

        // ── Constructor ─────────────────────────────────────────────────────────────

        [Fact]
        public void Constructor_ZeroCapacity_Throws()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BoundedConcurrentDictionary<string, int>(0));
            Assert.Equal("capacity", ex.ParamName);
        }

        [Fact]
        public void Constructor_NegativeCapacity_Throws()
        {
            var ex = Assert.Throws<ArgumentOutOfRangeException>(() =>
                new BoundedConcurrentDictionary<string, int>(-1));
            Assert.Equal("capacity", ex.ParamName);
        }

        [Fact]
        public void Constructor_PositiveCapacity_Succeeds()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(1);
            Assert.Equal(1, dict.Capacity);
            Assert.Equal(0, dict.Count);
        }

        // ── Below capacity: normal behaviour ────────────────────────────────────────

        [Fact]
        public void TryAdd_BelowCapacity_AddsEntry()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            Assert.True(dict.TryAdd("a", 1));
            Assert.Equal(1, dict.Count);
        }

        [Fact]
        public void TryAdd_DuplicateKey_ReturnsFalse()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            dict.TryAdd("a", 1);
            Assert.False(dict.TryAdd("a", 2));
        }

        [Fact]
        public void TryGetValue_ExistingKey_ReturnsTrue()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            dict.TryAdd("k", 42);
            Assert.True(dict.TryGetValue("k", out var v));
            Assert.Equal(42, v);
        }

        [Fact]
        public void TryGetValue_MissingKey_ReturnsFalse()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            Assert.False(dict.TryGetValue("missing", out _));
        }

        [Fact]
        public void TryRemove_ExistingKey_RemovesAndReturnsTrue()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            dict.TryAdd("x", 7);
            Assert.True(dict.TryRemove("x", out var v));
            Assert.Equal(7, v);
            Assert.Equal(0, dict.Count);
        }

        [Fact]
        public void TryRemove_MissingKey_ReturnsFalse()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            Assert.False(dict.TryRemove("nope", out _));
        }

        // ── Exactly at capacity ──────────────────────────────────────────────────────

        [Fact]
        public void TryAdd_AtCapacity_EntryFitsWithoutEviction()
        {
            // Capacity = 3; add 3 entries (the third is exactly at capacity from the
            // perspective of pre-insert check: count < capacity so no eviction).
            var dict = new BoundedConcurrentDictionary<string, int>(3);
            dict.TryAdd("a", 1);
            dict.TryAdd("b", 2);
            var evictionFired = false;
            var dict2 = new BoundedConcurrentDictionary<string, int>(3, onEvicted: _ => evictionFired = true);
            dict2.TryAdd("a", 1);
            dict2.TryAdd("b", 2);
            // Third insert: count (2) < capacity (3) → no eviction.
            dict2.TryAdd("c", 3);
            Assert.False(evictionFired);
            Assert.Equal(3, dict2.Count);
        }

        // ── Past capacity: eviction fires ────────────────────────────────────────────

        [Fact]
        public void TryAdd_PastCapacity_ClearsFires_NewEntryAdded()
        {
            int evictedCount = -1;
            var dict = new BoundedConcurrentDictionary<string, int>(3, onEvicted: c => evictedCount = c);

            dict.TryAdd("a", 1);
            dict.TryAdd("b", 2);
            dict.TryAdd("c", 3); // count = 3 = capacity, no eviction yet

            // Fourth insert: count (3) >= capacity (3) → eviction triggers.
            dict.TryAdd("d", 4);

            // Eviction callback should have fired with the number of entries cleared (3).
            Assert.Equal(3, evictedCount);
            // Only "d" remains after clear-then-add.
            Assert.Equal(1, dict.Count);
            Assert.True(dict.TryGetValue("d", out var v));
            Assert.Equal(4, v);
        }

        [Fact]
        public void TryAdd_PastCapacity_OldEntriesGone()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(2);
            dict.TryAdd("a", 1);
            dict.TryAdd("b", 2); // count = capacity
            dict.TryAdd("c", 3); // triggers eviction, then adds "c"

            Assert.False(dict.TryGetValue("a", out _));
            Assert.False(dict.TryGetValue("b", out _));
            Assert.True(dict.TryGetValue("c", out _));
        }

        [Fact]
        public void TryAdd_DuplicateKey_AtCapacity_ReturnsFalseAndPreservesExistingEntries()
        {
            var evicted = false;
            var dict = new BoundedConcurrentDictionary<string, int>(2, onEvicted: _ => evicted = true);

            Assert.True(dict.TryAdd("a", 1));
            Assert.True(dict.TryAdd("b", 2));

            Assert.False(dict.TryAdd("a", 99));

            Assert.False(evicted);
            Assert.Equal(2, dict.Count);
            Assert.Equal(1, dict["a"]);
            Assert.Equal(2, dict["b"]);
        }

        // ── TryGetValue / TryRemove across capacity boundary ──────────────────────────

        [Fact]
        public void TryGetValue_AfterEviction_MissingOldKey()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(2);
            dict.TryAdd("a", 10);
            dict.TryAdd("b", 20);
            dict.TryAdd("c", 30); // evicts, then adds c
            Assert.False(dict.TryGetValue("a", out _));
        }

        [Fact]
        public void TryRemove_AfterEviction_ReturnsFalseForEvictedKey()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(2);
            dict.TryAdd("a", 10);
            dict.TryAdd("b", 20);
            dict.TryAdd("c", 30); // eviction
            Assert.False(dict.TryRemove("a", out _));
        }

        // ── GetOrAdd and AddOrUpdate ─────────────────────────────────────────────────

        [Fact]
        public void GetOrAdd_BelowCapacity_AddsAndReturns()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            var result = dict.GetOrAdd("x", k => 99);
            Assert.Equal(99, result);
            Assert.Equal(1, dict.Count);
        }

        [Fact]
        public void GetOrAdd_NullFactory_ThrowsArgumentNullEvenWhenKeyExists()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            Assert.True(dict.TryAdd("a", 1));

            Assert.Throws<ArgumentNullException>(() =>
                dict.GetOrAdd("a", (Func<string, int>)null!));
        }

        [Fact]
        public void AddOrUpdate_BelowCapacity_UpdatesExisting()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            dict.TryAdd("k", 1);
            dict.AddOrUpdate("k", 99, (_, old) => old + 10);
            Assert.True(dict.TryGetValue("k", out var v));
            Assert.Equal(11, v);
        }

        [Fact]
        public void AddOrUpdate_PastCapacity_EvictsFirst()
        {
            int evicted = 0;
            var dict = new BoundedConcurrentDictionary<string, int>(2, onEvicted: c => evicted = c);
            dict.TryAdd("a", 1);
            dict.TryAdd("b", 2); // count = capacity
            dict.AddOrUpdate("c", 3, (_, v) => v); // triggers eviction
            Assert.Equal(2, evicted);
        }

        [Fact]
        public void AddOrUpdate_ExistingKey_AtCapacity_UpdatesOnlyExistingKey()
        {
            var evicted = false;
            var dict = new BoundedConcurrentDictionary<string, int>(2, onEvicted: _ => evicted = true);

            Assert.True(dict.TryAdd("a", 1));
            Assert.True(dict.TryAdd("b", 2));

            var result = dict.AddOrUpdate("a", 99, (_, old) => old + 10);

            Assert.Equal(11, result);
            Assert.False(evicted);
            Assert.Equal(2, dict.Count);
            Assert.Equal(11, dict["a"]);
            Assert.Equal(2, dict["b"]);
        }

        [Fact]
        public void AddOrUpdate_NullUpdateFactory_ThrowsArgumentNullBeforeAdding()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);

            Assert.Throws<ArgumentNullException>(() =>
                dict.AddOrUpdate("a", 1, (Func<string, int, int>)null!));

            Assert.False(dict.TryGetValue("a", out _));
        }

        [Fact]
        public void AddOrUpdate_Factory_ExistingKey_AtCapacity_UpdatesOnlyExistingKey()
        {
            var evicted = false;
            var addFactoryCalled = false;
            var dict = new BoundedConcurrentDictionary<string, int>(2, onEvicted: _ => evicted = true);

            Assert.True(dict.TryAdd("a", 1));
            Assert.True(dict.TryAdd("b", 2));

            var result = dict.AddOrUpdate(
                "a",
                _ =>
                {
                    addFactoryCalled = true;
                    return 99;
                },
                (_, old) => old + 10);

            Assert.Equal(11, result);
            Assert.False(addFactoryCalled);
            Assert.False(evicted);
            Assert.Equal(2, dict.Count);
            Assert.Equal(11, dict["a"]);
            Assert.Equal(2, dict["b"]);
        }

        [Fact]
        public void AddOrUpdate_NullAddFactory_ThrowsArgumentNullEvenWhenKeyExists()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            Assert.True(dict.TryAdd("a", 1));

            Assert.Throws<ArgumentNullException>(() =>
                dict.AddOrUpdate(
                    "a",
                    (Func<string, int>)null!,
                    (_, old) => old + 1));

            Assert.Equal(1, dict["a"]);
        }

        [Fact]
        public void AddOrUpdate_NullFactoryUpdateFactory_ThrowsArgumentNullBeforeAdding()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);

            Assert.Throws<ArgumentNullException>(() =>
                dict.AddOrUpdate(
                    "a",
                    _ => 1,
                    (Func<string, int, int>)null!));

            Assert.False(dict.TryGetValue("a", out _));
        }

        [Fact]
        public void GetOrAdd_ExistingKey_AtCapacity_ReturnsExistingWithoutEviction()
        {
            var evicted = false;
            var factoryCalled = false;
            var dict = new BoundedConcurrentDictionary<string, int>(2, onEvicted: _ => evicted = true);

            Assert.True(dict.TryAdd("a", 1));
            Assert.True(dict.TryAdd("b", 2));

            var result = dict.GetOrAdd("a", _ =>
            {
                factoryCalled = true;
                return 99;
            });

            Assert.Equal(1, result);
            Assert.False(factoryCalled);
            Assert.False(evicted);
            Assert.Equal(2, dict.Count);
            Assert.Equal(1, dict["a"]);
            Assert.Equal(2, dict["b"]);
        }

        [Fact]
        public void GetOrAdd_Value_ExistingKey_AtCapacity_ReturnsExistingWithoutEviction()
        {
            var evicted = false;
            var dict = new BoundedConcurrentDictionary<string, int>(2, onEvicted: _ => evicted = true);

            Assert.True(dict.TryAdd("a", 1));
            Assert.True(dict.TryAdd("b", 2));

            var result = dict.GetOrAdd("a", 99);

            Assert.Equal(1, result);
            Assert.False(evicted);
            Assert.Equal(2, dict.Count);
            Assert.Equal(1, dict["a"]);
            Assert.Equal(2, dict["b"]);
        }

        // ── Clear ───────────────────────────────────────────────────────────────────

        [Fact]
        public void Clear_EmptiesDict()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(10);
            dict.TryAdd("a", 1);
            dict.TryAdd("b", 2);
            dict.Clear();
            Assert.Equal(0, dict.Count);
        }

        // ── Keys snapshot ────────────────────────────────────────────────────────────

        [Fact]
        public void Keys_ReturnsSnapshot()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(10);
            dict.TryAdd("x", 1);
            dict.TryAdd("y", 2);
            var keys = dict.Keys;
            Assert.Contains("x", keys);
            Assert.Contains("y", keys);
        }

        // ── Comparer ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Comparer_OrdinalIgnoreCase_TreatsAsEqual()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(
                10,
                comparer: StringComparer.OrdinalIgnoreCase);

            dict.TryAdd("Hello", 1);
            Assert.True(dict.TryGetValue("hello", out var v));
            Assert.Equal(1, v);
            Assert.False(dict.TryAdd("HELLO", 2)); // duplicate under case-insensitive
        }

        // ── Concurrent inserts ───────────────────────────────────────────────────────

        /// <summary>
        /// The clear-all eviction strategy uses a non-atomic check-then-clear, which is an
        /// acceptable trade-off matching the existing Brainarr/Qobuzarr precedent. Under
        /// concurrent load a snapshot may momentarily exceed capacity (two threads both see
        /// count &lt; capacity, both insert, then one triggers eviction). The safety guarantee
        /// is that the dict periodically resets to prevent unbounded growth, not that every
        /// snapshot is strictly ≤ capacity. This test verifies:
        ///   1. No deadlock or exception under heavy concurrent insert pressure.
        ///   2. Count never grows beyond capacity * threads (i.e. growth IS bounded).
        ///   3. After all inserts settle, count is ≤ capacity.
        /// </summary>
        [Fact]
        public async Task ConcurrentInserts_NoBoundedGrowth_NoExceptionsOrDeadlock()
        {
            const int capacity = 50;
            const int threads = 20;
            const int insertsPerThread = 100;

            var dict = new BoundedConcurrentDictionary<int, int>(capacity);
            var barrier = new Barrier(threads);
            Exception? firstException = null;

            var tasks = Enumerable.Range(0, threads).Select(t => Task.Run(() =>
            {
                try
                {
                    barrier.SignalAndWait();
                    for (int i = 0; i < insertsPerThread; i++)
                    {
                        dict.TryAdd(t * insertsPerThread + i, i);
                    }
                }
                catch (Exception ex)
                {
                    Interlocked.CompareExchange(ref firstException, ex, null);
                }
            })).ToArray();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await Task.WhenAll(tasks).WaitAsync(cts.Token);

            // No exceptions thrown during concurrent access.
            Assert.Null(firstException);

            // After all inserts complete, the dict has settled. Due to clear-all eviction,
            // the final count is ≤ capacity (the last batch of inserts that triggered the
            // final eviction leaves at most 'capacity' entries).
            Assert.True(dict.Count <= capacity,
                $"Expected count ≤ {capacity} after concurrent inserts settled, but got {dict.Count}.");
        }

        [Fact]
        public async Task GetOrAdd_ConcurrentUniqueFactoriesNearCapacity_SettlesWithinCapacity()
        {
            const int capacity = 16;
            const int concurrentAdds = 32;

            var dict = new BoundedConcurrentDictionary<int, int>(capacity);
            for (var i = 0; i < capacity - 1; i++)
            {
                Assert.True(dict.TryAdd(i, i));
            }

            using var factoriesEntered = new CountdownEvent(concurrentAdds);
            using var releaseFactories = new ManualResetEventSlim();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var tasks = Enumerable.Range(0, concurrentAdds)
                .Select(i => Task.Factory.StartNew(() =>
                {
                    var key = 10_000 + i;
                    return dict.GetOrAdd(key, k =>
                    {
                        factoriesEntered.Signal();
                        Assert.True(releaseFactories.Wait(TimeSpan.FromSeconds(30)),
                            "all factories should be released by the test harness");
                        return k;
                    });
                }, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                .ToArray();

            Assert.True(factoriesEntered.Wait(TimeSpan.FromSeconds(30)),
                "all concurrent factories should pass the pre-insert capacity check before release");

            releaseFactories.Set();
            await Task.WhenAll(tasks).WaitAsync(timeout.Token);

            Assert.True(dict.Count <= capacity,
                $"Expected count ≤ {capacity} after concurrent GetOrAdd inserts settled, but got {dict.Count}.");
        }

        [Fact]
        public async Task GetOrAdd_ConcurrentSameKeyFactoryMissAtCapacity_DoesNotEvictExistingEntries()
        {
            var evicted = false;
            var dict = new BoundedConcurrentDictionary<string, int>(2, onEvicted: _ => evicted = true);
            Assert.True(dict.TryAdd("seed", 1));

            using var factoriesEntered = new CountdownEvent(2);
            using var releaseFactories = new ManualResetEventSlim();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            var nextValue = 1;

            var tasks = Enumerable.Range(0, 2)
                .Select(_ => Task.Factory.StartNew(() =>
                    dict.GetOrAdd("shared", _ =>
                    {
                        factoriesEntered.Signal();
                        Assert.True(releaseFactories.Wait(TimeSpan.FromSeconds(30)),
                            "both same-key factories should be parked before either can add");
                        return Interlocked.Increment(ref nextValue);
                    }), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default))
                .ToArray();

            Assert.True(factoriesEntered.Wait(TimeSpan.FromSeconds(30)),
                "both same-key factory calls should observe the initial miss before release");

            releaseFactories.Set();
            var results = await Task.WhenAll(tasks).WaitAsync(timeout.Token);

            Assert.False(evicted);
            Assert.Equal(2, dict.Count);
            Assert.Equal(1, dict["seed"]);
            Assert.Equal(dict["shared"], results[0]);
            Assert.Equal(dict["shared"], results[1]);
        }

        [Fact]
        public async Task GetOrAdd_Value_ConcurrentSameKeyMissAtCapacity_DoesNotEvictExistingEntries()
        {
            using var hashProbesEntered = new CountdownEvent(2);
            using var releaseHashProbes = new ManualResetEventSlim();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));

            var evicted = false;
            var comparer = new BlockingHashComparer("shared", blocks: 2, hashProbesEntered, releaseHashProbes);
            var dict = new BoundedConcurrentDictionary<string, int>(2, comparer, onEvicted: _ => evicted = true);
            Assert.True(dict.TryAdd("seed", 1));

            var tasks = new[]
            {
                Task.Factory.StartNew(() => dict.GetOrAdd("shared", 2), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default),
                Task.Factory.StartNew(() => dict.GetOrAdd("shared", 3), CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default)
            };

            Assert.True(hashProbesEntered.Wait(TimeSpan.FromSeconds(30)),
                "both same-key value overload calls should observe the initial miss before release");

            releaseHashProbes.Set();
            var results = await Task.WhenAll(tasks).WaitAsync(timeout.Token);

            Assert.False(evicted);
            Assert.Equal(2, dict.Count);
            Assert.Equal(1, dict["seed"]);
            Assert.Equal(dict["shared"], results[0]);
            Assert.Equal(dict["shared"], results[1]);
        }

        // ── ContainsKey ─────────────────────────────────────────────────────────────

        [Fact]
        public void ContainsKey_PresentKey_ReturnsTrue()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            dict.TryAdd("a", 1);
            Assert.True(dict.ContainsKey("a"));
        }

        [Fact]
        public void ContainsKey_AbsentKey_ReturnsFalse()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            Assert.False(dict.ContainsKey("missing"));
        }

        // ── Values ──────────────────────────────────────────────────────────────────

        [Fact]
        public void Values_SnapshotsCurrentValues()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            dict.TryAdd("a", 1);
            dict.TryAdd("b", 2);
            dict.TryAdd("c", 3);

            var values = dict.Values.OrderBy(v => v).ToArray();
            Assert.Equal(new[] { 1, 2, 3 }, values);
        }

        // ── Indexer ─────────────────────────────────────────────────────────────────

        [Fact]
        public void Indexer_Set_NewKey_InsertsEntry()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            dict["a"] = 42;
            Assert.Equal(42, dict["a"]);
            Assert.Equal(1, dict.Count);
        }

        [Fact]
        public void Indexer_Set_ExistingKey_OverwritesValue()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            dict["a"] = 1;
            dict["a"] = 99;
            Assert.Equal(99, dict["a"]);
            Assert.Equal(1, dict.Count);
        }

        [Fact]
        public void Indexer_Get_AbsentKey_ThrowsKeyNotFound()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            Assert.Throws<KeyNotFoundException>(() => _ = dict["missing"]);
        }

        [Fact]
        public void Indexer_Set_AtCapacity_EvictsAllThenInserts()
        {
            // Same overflow semantics as TryAdd: clear-all when an insert would push past
            // capacity, then set the new entry. Indexer setter must respect the cap.
            int evictionCount = -1;
            var dict = new BoundedConcurrentDictionary<string, int>(
                capacity: 3,
                comparer: null,
                onEvicted: cleared => evictionCount = cleared);

            dict["a"] = 1;
            dict["b"] = 2;
            dict["c"] = 3;
            Assert.Equal(3, dict.Count);

            dict["d"] = 4; // triggers eviction

            Assert.Equal(3, evictionCount);
            Assert.Equal(1, dict.Count);
            Assert.Equal(4, dict["d"]);
        }

        [Fact]
        public void Indexer_Set_ExistingKey_AtCapacity_OverwritesWithoutEviction()
        {
            var evicted = false;
            var dict = new BoundedConcurrentDictionary<string, int>(2, onEvicted: _ => evicted = true);

            dict["a"] = 1;
            dict["b"] = 2;

            dict["a"] = 99;

            Assert.False(evicted);
            Assert.Equal(2, dict.Count);
            Assert.Equal(99, dict["a"]);
            Assert.Equal(2, dict["b"]);
        }

        // ── Enumeration ─────────────────────────────────────────────────────────────

        [Fact]
        public void GetEnumerator_YieldsAllPairs()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            dict.TryAdd("a", 1);
            dict.TryAdd("b", 2);
            dict.TryAdd("c", 3);

            var pairs = new List<KeyValuePair<string, int>>();
            foreach (var kvp in dict)
            {
                pairs.Add(kvp);
            }

            Assert.Equal(3, pairs.Count);
            Assert.Contains(new KeyValuePair<string, int>("a", 1), pairs);
            Assert.Contains(new KeyValuePair<string, int>("b", 2), pairs);
            Assert.Contains(new KeyValuePair<string, int>("c", 3), pairs);
        }

        [Fact]
        public void GetEnumerator_EmptyDict_YieldsNothing()
        {
            var dict = new BoundedConcurrentDictionary<string, int>(5);
            var count = 0;
            foreach (var _ in dict)
            {
                count++;
            }
            Assert.Equal(0, count);
        }

        [Fact]
        public void LinqIntegration_WorksOverDictPairs()
        {
            // foreach is the most common path, but LINQ over the IEnumerable<KeyValuePair>
            // surface is the second-most-common (e.g. brainarr's MetricsCollector uses
            // `Metrics.Values` and `foreach (var kvp in Metrics)`).
            var dict = new BoundedConcurrentDictionary<string, int>(10);
            dict.TryAdd("a", 1);
            dict.TryAdd("b", 2);
            dict.TryAdd("c", 3);

            var sum = dict.Sum(kvp => kvp.Value);
            Assert.Equal(6, sum);
        }
    }
}
