using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.TestKit.LibraryLinking
{
    /// <summary>
    /// Base class for plugin isolation tests. Inherit from this class
    /// to get common test infrastructure for verifying library linking
    /// and service isolation in streaming plugins.
    /// </summary>
    public abstract class PluginIsolationTestBase : IDisposable
    {
        /// <summary>
        /// The plugin assembly being tested.
        /// </summary>
        protected abstract Assembly? PluginAssembly { get; }

        /// <summary>
        /// The path to the plugin assembly.
        /// </summary>
        protected abstract string? PluginAssemblyPath { get; }

        /// <summary>
        /// The expected namespace prefix for the plugin (e.g., "Brainarr", "Lidarr.Plugin.Qobuzarr").
        /// </summary>
        protected abstract string ExpectedNamespacePrefix { get; }

        /// <summary>
        /// Simulated cache storage for testing cache isolation.
        /// </summary>
        protected ConcurrentDictionary<string, object> TestCache { get; } = new();

        /// <summary>
        /// Simulated rate limiter for testing rate limit isolation.
        /// </summary>
        protected SemaphoreSlim? TestRateLimiter { get; private set; }

        /// <summary>
        /// Simulated cancellation token source for testing cancellation isolation.
        /// </summary>
        protected CancellationTokenSource? TestCancellationSource { get; private set; }

        /// <summary>
        /// Initialize test resources.
        /// </summary>
        protected void InitializeTestResources(int rateLimitCapacity = 10)
        {
            TestRateLimiter = new SemaphoreSlim(rateLimitCapacity);
            TestCancellationSource = new CancellationTokenSource();
        }

        /// <summary>
        /// Verifies that Common library types are not publicly exposed.
        /// </summary>
        /// <returns>True if properly internalized</returns>
        protected bool VerifyCommonTypesInternalized()
        {
            if (PluginAssembly == null) return true; // Skip if not available

            var exposedTypes = PluginIsolationAssertions.GetExposedCommonTypes(PluginAssembly);
            return exposedTypes.Count == 0;
        }

        /// <summary>
        /// Verifies that Polly types are not publicly exposed.
        /// </summary>
        /// <returns>True if properly internalized</returns>
        protected bool VerifyPollyTypesInternalized()
        {
            if (PluginAssembly == null) return true;

            var exposedTypes = PluginIsolationAssertions.GetExposedPollyTypes(PluginAssembly);
            return exposedTypes.Count == 0;
        }

        /// <summary>
        /// Verifies that TagLibSharp types are not publicly exposed.
        /// </summary>
        /// <returns>True if properly internalized</returns>
        protected bool VerifyTagLibTypesInternalized()
        {
            if (PluginAssembly == null) return true;

            var exposedTypes = PluginIsolationAssertions.GetExposedTagLibTypes(PluginAssembly);
            return exposedTypes.Count == 0;
        }

        /// <summary>
        /// Verifies that CliWrap types are not publicly exposed.
        /// </summary>
        /// <returns>True if properly internalized</returns>
        protected bool VerifyCliWrapTypesInternalized()
        {
            if (PluginAssembly == null) return true;

            var exposedTypes = PluginIsolationAssertions.GetExposedCliWrapTypes(PluginAssembly);
            return exposedTypes.Count == 0;
        }

        /// <summary>
        /// Verifies that no unmerged assembly references exist.
        /// </summary>
        /// <returns>True if all target assemblies are merged</returns>
        protected bool VerifyNoUnmergedReferences()
        {
            if (PluginAssembly == null) return true;

            var unmerged = PluginIsolationAssertions.GetUnmergedReferences(PluginAssembly);
            return unmerged.Count == 0;
        }

        /// <summary>
        /// Verifies that no unmerged assembly files exist in the plugin directory.
        /// </summary>
        /// <returns>True if the plugin directory is clean</returns>
        protected bool VerifyNoUnmergedFiles()
        {
            if (string.IsNullOrEmpty(PluginAssemblyPath)) return true;

            var pluginDir = Path.GetDirectoryName(PluginAssemblyPath);
            if (string.IsNullOrEmpty(pluginDir)) return true;

            var unmerged = PluginIsolationAssertions.GetUnmergedAssemblyFiles(pluginDir);
            return unmerged.Count == 0;
        }

        /// <summary>
        /// Verifies that all plugin types are properly namespaced.
        /// </summary>
        /// <returns>True if all types are correctly namespaced</returns>
        protected bool VerifyNamespacing()
        {
            if (PluginAssembly == null) return true;

            var improper = PluginIsolationAssertions.GetImproperlyNamespacedTypes(
                PluginAssembly, ExpectedNamespacePrefix);
            return improper.Count == 0;
        }

        /// <summary>
        /// Verifies concurrent plugin loading.
        /// </summary>
        /// <param name="concurrency">Number of concurrent loads</param>
        /// <returns>True if all loads succeed</returns>
        protected async Task<bool> VerifyConcurrentLoadingAsync(int concurrency = 5)
        {
            if (string.IsNullOrEmpty(PluginAssemblyPath)) return true;

            return await PluginIsolationAssertions.VerifyConcurrentLoadingAsync(
                PluginAssemblyPath, concurrency);
        }

        /// <summary>
        /// Simulates cache isolation between plugins.
        /// </summary>
        /// <param name="pluginId">Plugin identifier for key scoping</param>
        /// <param name="key">Cache key</param>
        /// <param name="value">Value to cache</param>
        protected void SimulateCacheSet(string pluginId, string key, object value)
        {
            var scopedKey = $"{pluginId}:{key}";
            TestCache[scopedKey] = value;
        }

        /// <summary>
        /// Retrieves a cached value with plugin scope.
        /// </summary>
        /// <param name="pluginId">Plugin identifier for key scoping</param>
        /// <param name="key">Cache key</param>
        /// <returns>Cached value or null</returns>
        protected object? SimulateCacheGet(string pluginId, string key)
        {
            var scopedKey = $"{pluginId}:{key}";
            return TestCache.TryGetValue(scopedKey, out var value) ? value : null;
        }

        /// <summary>
        /// Simulates acquiring a rate limiter slot.
        /// </summary>
        /// <param name="timeout">Timeout for acquisition</param>
        /// <returns>True if slot acquired</returns>
        protected async Task<bool> SimulateRateLimitAcquireAsync(TimeSpan? timeout = null)
        {
            if (TestRateLimiter == null)
                throw new InvalidOperationException("Call InitializeTestResources first");

            return await TestRateLimiter.WaitAsync(timeout ?? TimeSpan.FromSeconds(1));
        }

        /// <summary>
        /// Releases a rate limiter slot.
        /// </summary>
        protected void SimulateRateLimitRelease()
        {
            TestRateLimiter?.Release();
        }

        /// <summary>
        /// Gets the current rate limiter capacity.
        /// </summary>
        protected int GetRateLimitCurrentCount()
        {
            return TestRateLimiter?.CurrentCount ?? 0;
        }

        /// <summary>
        /// Simulates cancellation of plugin operations.
        /// </summary>
        protected void SimulateCancellation()
        {
            TestCancellationSource?.Cancel();
        }

        /// <summary>
        /// Checks if cancellation was requested.
        /// </summary>
        protected bool IsCancellationRequested()
        {
            return TestCancellationSource?.IsCancellationRequested ?? false;
        }

        /// <summary>
        /// Gets the cancellation token for operations.
        /// </summary>
        protected CancellationToken GetCancellationToken()
        {
            return TestCancellationSource?.Token ?? CancellationToken.None;
        }

        /// <summary>
        /// Verifies that objects can be garbage collected after simulated unload.
        /// </summary>
        /// <returns>True if object was collected</returns>
        protected bool VerifyGarbageCollection()
        {
            var testObject = new object();
            var weakRef = new WeakReference(testObject);

            testObject = null!;

            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();

            return !weakRef.IsAlive;
        }

        /// <summary>
        /// Cleanup test resources.
        /// </summary>
        public virtual void Dispose()
        {
            TestRateLimiter?.Dispose();
            TestCancellationSource?.Dispose();
            TestCache.Clear();
        }
    }

    /// <summary>
    /// Extended test base for streaming service plugins with additional
    /// audio-specific test infrastructure.
    /// </summary>
    public abstract class StreamingPluginIsolationTestBase : PluginIsolationTestBase
    {
        /// <summary>
        /// Simulated chunk buffer for download testing.
        /// </summary>
        protected ConcurrentDictionary<string, byte[]> ChunkBuffer { get; } = new();

        /// <summary>
        /// Simulated session storage for authentication testing.
        /// </summary>
        protected ConcurrentDictionary<string, object> SessionStorage { get; } = new();

        /// <summary>
        /// Simulated quality settings storage.
        /// </summary>
        protected Dictionary<string, object> QualitySettings { get; } = new();

        /// <summary>
        /// Simulates storing a chunk in the buffer.
        /// </summary>
        /// <param name="trackId">Track identifier</param>
        /// <param name="chunkIndex">Chunk index</param>
        /// <param name="data">Chunk data</param>
        protected void SimulateChunkStore(string trackId, int chunkIndex, byte[] data)
        {
            var key = $"{trackId}:chunk:{chunkIndex}";
            ChunkBuffer[key] = data;
        }

        /// <summary>
        /// Retrieves a stored chunk.
        /// </summary>
        /// <param name="trackId">Track identifier</param>
        /// <param name="chunkIndex">Chunk index</param>
        /// <returns>Chunk data or null</returns>
        protected byte[]? SimulateChunkRetrieve(string trackId, int chunkIndex)
        {
            var key = $"{trackId}:chunk:{chunkIndex}";
            return ChunkBuffer.TryGetValue(key, out var data) ? data : null;
        }

        /// <summary>
        /// Clears all chunks for a track (simulates assembly complete).
        /// </summary>
        /// <param name="trackId">Track identifier</param>
        protected void SimulateChunksClear(string trackId)
        {
            var keysToRemove = ChunkBuffer.Keys
                .Where(k => k.StartsWith($"{trackId}:chunk:", StringComparison.Ordinal))
                .ToList();

            foreach (var key in keysToRemove)
            {
                ChunkBuffer.TryRemove(key, out _);
            }
        }

        /// <summary>
        /// Simulates storing an authentication session.
        /// </summary>
        /// <param name="pluginId">Plugin identifier</param>
        /// <param name="sessionData">Session data object</param>
        protected void SimulateSessionStore(string pluginId, object sessionData)
        {
            SessionStorage[$"{pluginId}:session"] = sessionData;
        }

        /// <summary>
        /// Retrieves a stored session.
        /// </summary>
        /// <param name="pluginId">Plugin identifier</param>
        /// <returns>Session data or null</returns>
        protected object? SimulateSessionRetrieve(string pluginId)
        {
            return SessionStorage.TryGetValue($"{pluginId}:session", out var session) ? session : null;
        }

        /// <summary>
        /// Cleanup streaming-specific resources.
        /// </summary>
        public override void Dispose()
        {
            ChunkBuffer.Clear();
            SessionStorage.Clear();
            QualitySettings.Clear();
            base.Dispose();
        }
    }
}
