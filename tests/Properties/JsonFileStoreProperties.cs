// <copyright file="JsonFileStoreProperties.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

using System;
using System.IO;
using System.Threading.Tasks;
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Lidarr.Plugin.Common.Services.Storage;

namespace Lidarr.Plugin.Common.Tests.Properties
{
    /// <summary>
    /// FsCheck property tests for <see cref="JsonFileStore{TKey, TValue}"/>.
    /// I/O bounded so MaxTest is reduced; each iteration writes/reads a temp file.
    /// </summary>
    public class JsonFileStoreProperties
    {
        private static string NewTempFile()
        {
            var dir = Path.Combine(Path.GetTempPath(), "LPC.JsonFileStoreProps." + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            return Path.Combine(dir, "store.json");
        }

        private static void TryCleanup(string filePath)
        {
            try
            {
                var dir = Path.GetDirectoryName(filePath);
                if (!string.IsNullOrEmpty(dir) && Directory.Exists(dir))
                {
                    Directory.Delete(dir, recursive: true);
                }
            }
            catch
            {
                // best-effort
            }
        }

        /// <summary>
        /// Round-trip: <c>SetAsync(k, v); GetAsync(k) == v</c> for arbitrary key/value strings.
        /// Verifies the JSON serialize/atomic-replace/deserialize chain preserves data.
        /// </summary>
        [Property(MaxTest = 30)]
        public Property SetThenGet_PreservesValue(NonNull<string> rawKey, NonNull<string> rawValue)
        {
            var key = rawKey.Get;
            var value = rawValue.Get;

            // Empty keys are permitted by Dictionary but System.Text.Json refuses to serialize
            // a Dictionary<string,_> with an empty key. Skip that degenerate case.
            return Prop.When(!string.IsNullOrEmpty(key), () =>
            {
                var path = NewTempFile();
                try
                {
                    var store = new JsonFileStore<string, string>(path);
                    store.SetAsync(key, value).AsTask().GetAwaiter().GetResult();

                    // New instance forces a reload from disk to verify persistence.
                    var fresh = new JsonFileStore<string, string>(path);
                    var loaded = fresh.GetAsync(key).AsTask().GetAwaiter().GetResult();
                    return loaded == value;
                }
                finally
                {
                    TryCleanup(path);
                }
            });
        }

        /// <summary>
        /// LRU eviction: after inserting MaxEntries+1 items, the count never exceeds MaxEntries
        /// and the oldest-inserted key is the one that was evicted.
        /// </summary>
        [Property(MaxTest = 20)]
        public bool MaxEntries_CapEnforced(PositiveInt cap)
        {
            // Constrain the cap to keep test runtime bounded.
            var maxEntries = Math.Min(cap.Get, 8);

            var path = NewTempFile();
            try
            {
                var store = new JsonFileStore<string, string>(
                    path,
                    new JsonFileStoreOptions<string> { MaxEntries = maxEntries });

                for (var i = 0; i < maxEntries + 1; i++)
                {
                    // Slight delay so timestamps differ and LRU ordering is stable.
                    store.SetAsync($"key-{i}", $"val-{i}").AsTask().GetAwaiter().GetResult();
                    System.Threading.Thread.Sleep(1);
                }

                var underCap = store.Count <= maxEntries;
                var firstKeyEvicted = store.GetAsync("key-0").AsTask().GetAwaiter().GetResult() == null;
                var lastKeyKept = store.GetAsync($"key-{maxEntries}").AsTask().GetAwaiter().GetResult() == $"val-{maxEntries}";

                return underCap && firstKeyEvicted && lastKeyKept;
            }
            finally
            {
                TryCleanup(path);
            }
        }

        /// <summary>
        /// TTL: after the TTL elapses, <see cref="JsonFileStore{TKey, TValue}.GetAsync"/> returns the
        /// default value rather than the previously-set value.
        /// </summary>
        [Property(MaxTest = 10)]
        public bool Ttl_ExpiredEntries_ReturnDefault(NonNull<string> rawKey)
        {
            var key = rawKey.Get;
            if (string.IsNullOrEmpty(key)) return true; // skip degenerate

            var path = NewTempFile();
            try
            {
                var store = new JsonFileStore<string, string>(
                    path,
                    new JsonFileStoreOptions<string> { Ttl = TimeSpan.FromMilliseconds(20) });

                store.SetAsync(key, "v").AsTask().GetAwaiter().GetResult();
                System.Threading.Thread.Sleep(60);
                var loaded = store.GetAsync(key).AsTask().GetAwaiter().GetResult();
                return loaded is null;
            }
            finally
            {
                TryCleanup(path);
            }
        }
    }
}
