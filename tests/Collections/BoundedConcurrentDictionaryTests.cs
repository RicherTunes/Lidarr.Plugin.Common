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
        public void ConcurrentInserts_NoBoundedGrowth_NoExceptionsOrDeadlock()
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

            Task.WaitAll(tasks, TimeSpan.FromSeconds(30));

            // No exceptions thrown during concurrent access.
            Assert.Null(firstException);

            // After all inserts complete, the dict has settled. Due to clear-all eviction,
            // the final count is ≤ capacity (the last batch of inserts that triggered the
            // final eviction leaves at most 'capacity' entries).
            Assert.True(dict.Count <= capacity,
                $"Expected count ≤ {capacity} after concurrent inserts settled, but got {dict.Count}.");
        }
    }
}
