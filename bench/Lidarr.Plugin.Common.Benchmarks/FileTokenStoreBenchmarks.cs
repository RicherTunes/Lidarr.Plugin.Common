using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Authentication;

namespace Lidarr.Plugin.Common.Benchmarks;

/// <summary>
/// Baseline for <see cref="FileTokenStore{TSession}"/> SaveAsync + LoadAsync round-trip.
/// Measures combined disk write + read costs (envelope serialization, token protection,
/// named-mutex acquisition).
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class FileTokenStoreBenchmarks
{
    private string _dir = null!;
    private FileTokenStore<BenchSession> _store = null!;
    private TokenEnvelope<BenchSession> _envelope = null!;

    [GlobalSetup]
    public void Setup()
    {
        _dir = Path.Combine(Path.GetTempPath(), "lpc-fts-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
        _store = new FileTokenStore<BenchSession>(Path.Combine(_dir, "session.json"));
        _envelope = new TokenEnvelope<BenchSession>(
            new BenchSession { AccessToken = new string('a', 96), RefreshToken = new string('b', 64), UserId = 12345 },
            DateTime.UtcNow.AddHours(1),
            new Dictionary<string, string> { ["region"] = "us", ["product"] = "lossless" });
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Benchmark(Description = "SaveAsync + LoadAsync round-trip")]
    public async Task<bool> RoundTrip()
    {
        await _store.SaveAsync(_envelope).ConfigureAwait(false);
        var loaded = await _store.LoadAsync().ConfigureAwait(false);
        return loaded?.Session is not null;
    }

    public sealed class BenchSession
    {
        public string? AccessToken { get; set; }
        public string? RefreshToken { get; set; }
        public int UserId { get; set; }
    }
}
