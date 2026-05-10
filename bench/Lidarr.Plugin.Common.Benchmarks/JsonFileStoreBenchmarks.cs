using System;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Lidarr.Plugin.Common.Services.Storage;

namespace Lidarr.Plugin.Common.Benchmarks;

/// <summary>
/// Baseline for <see cref="JsonFileStore{TKey, TValue}"/> SetAsync + GetAsync round-trip
/// with TTL + LRU caps configured (the typical configuration in adopting plugins).
/// SetAsync hits disk; GetAsync stays in memory after warm-up.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class JsonFileStoreBenchmarks
{
    private string _dir = null!;
    private JsonFileStore<string, BenchValue> _store = null!;
    private int _counter;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lpc-jfs-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        var path = Path.Combine(_dir, "store.json");
        _store = new JsonFileStore<string, BenchValue>(
            path,
            new JsonFileStoreOptions<string>
            {
                Ttl = TimeSpan.FromMinutes(30),
                MaxEntries = 256,
            });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Benchmark(Description = "SetAsync (small payload, TTL+LRU)")]
    public async Task SetAsync()
    {
        var key = $"k-{System.Threading.Interlocked.Increment(ref _counter) & 0xFF}";
        await _store.SetAsync(key, new BenchValue { Id = 1, Name = "bench", Tag = "alpha" }).ConfigureAwait(false);
    }

    [Benchmark(Description = "GetAsync (warm key)")]
    public async Task<BenchValue?> GetAsync()
    {
        return await _store.GetAsync("k-1").ConfigureAwait(false);
    }

    public sealed class BenchValue
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Tag { get; set; }
    }
}
