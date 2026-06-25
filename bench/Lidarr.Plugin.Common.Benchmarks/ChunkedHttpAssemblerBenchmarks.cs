using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Jobs;
using Lidarr.Plugin.Common.Services.Download;

namespace Lidarr.Plugin.Common.Benchmarks;

/// <summary>
/// Baseline for <see cref="ChunkedHttpAssembler.AssembleAsync"/> across small and large
/// chunk counts. Uses an in-memory <see cref="HttpMessageHandler"/> so timings reflect
/// orchestration + temp-file IO, not network.
/// </summary>
[MemoryDiagnoser]
[SimpleJob(RuntimeMoniker.Net80)]
public class ChunkedHttpAssemblerBenchmarks
{
    private const int BytesPerChunk = 4096;

    private HttpClient _client = null!;
    private string _outputDir = null!;
    private ChunkSpec[] _smallChunks = null!;
    private ChunkSpec[] _largeChunks = null!;
    private static readonly RemoteMediaUriPolicy BenchmarkMediaUriPolicy = new()
    {
        ResolveDns = false,
    };

    [GlobalSetup]
    public void Setup()
    {
        var payload = new byte[BytesPerChunk];
        new Random(42).NextBytes(payload);

        var handler = new InMemoryByteHandler(payload);
        _client = new HttpClient(handler) { BaseAddress = new Uri("https://bench.example.test/") };

        _outputDir = Path.Combine(Path.GetTempPath(), "lpc-bench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_outputDir);

        _smallChunks = BuildChunks(8);
        _largeChunks = BuildChunks(64);
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        try { Directory.Delete(_outputDir, recursive: true); } catch { /* best effort */ }
        _client.Dispose();
    }

    [Benchmark(Description = "AssembleAsync — 8 chunks (4KB each)")]
    public async Task<long> SmallChunkCount()
    {
        var output = Path.Combine(_outputDir, $"small-{Guid.NewGuid():N}.bin");
        var assembler = new ChunkedHttpAssembler(_client, BenchmarkMediaUriPolicy);
        var result = await assembler.AssembleAsync(_smallChunks, output, new ChunkedAssemblyOptions { MaxConcurrency = 4 }).ConfigureAwait(false);
        File.Delete(output);
        return result.TotalBytes;
    }

    [Benchmark(Description = "AssembleAsync — 64 chunks (4KB each)")]
    public async Task<long> LargeChunkCount()
    {
        var output = Path.Combine(_outputDir, $"large-{Guid.NewGuid():N}.bin");
        var assembler = new ChunkedHttpAssembler(_client, BenchmarkMediaUriPolicy);
        var result = await assembler.AssembleAsync(_largeChunks, output, new ChunkedAssemblyOptions { MaxConcurrency = 8 }).ConfigureAwait(false);
        File.Delete(output);
        return result.TotalBytes;
    }

    private static ChunkSpec[] BuildChunks(int count)
    {
        var chunks = new ChunkSpec[count];
        for (int i = 0; i < count; i++)
        {
            chunks[i] = new ChunkSpec(i, $"https://bench.example.test/chunks/{i}");
        }
        return chunks;
    }

    private sealed class InMemoryByteHandler : HttpMessageHandler
    {
        private readonly byte[] _payload;

        public InMemoryByteHandler(byte[] payload) { _payload = payload; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, System.Threading.CancellationToken cancellationToken)
        {
            var resp = new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(_payload)
            };
            return Task.FromResult(resp);
        }
    }
}
