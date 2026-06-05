using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests.Services.Download;

[Trait("Category", "Unit")]
public class ChunkedHttpAssemblerTests : IDisposable
{
    private readonly string _tempDir;

    public ChunkedHttpAssemblerTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lpc_chunked_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }

    private string OutputPath() => Path.Combine(_tempDir, "out.bin");

    [Fact]
    public async Task AssembleAsync_AssemblesSingleChunk()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("hello world");
        using var http = new HttpClient(new ChunkSourceHandler(("https://test/c0", data)));
        var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });
        var output = OutputPath();

        var result = await sut.AssembleAsync(
            new[] { new ChunkSpec(0, "https://test/c0") },
            output,
            ChunkedAssemblyOptions.Default);

        Assert.True(File.Exists(output));
        Assert.Equal(data.Length, result.TotalBytes);
        Assert.Equal(1, result.ChunkCount);
        Assert.Equal(data, await File.ReadAllBytesAsync(output));
    }

    [Fact]
    public async Task AssembleAsync_AssemblesMultipleChunks_InOrder_Sequential()
    {
        var c0 = System.Text.Encoding.UTF8.GetBytes("AAA");
        var c1 = System.Text.Encoding.UTF8.GetBytes("BBB");
        var c2 = System.Text.Encoding.UTF8.GetBytes("CCC");

        using var http = new HttpClient(new ChunkSourceHandler(
            ("https://test/c0", c0),
            ("https://test/c1", c1),
            ("https://test/c2", c2)));

        var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });
        var output = OutputPath();

        var result = await sut.AssembleAsync(
            new[]
            {
                new ChunkSpec(0, "https://test/c0"),
                new ChunkSpec(1, "https://test/c1"),
                new ChunkSpec(2, "https://test/c2"),
            },
            output,
            new ChunkedAssemblyOptions { MaxConcurrency = 1 });

        Assert.Equal(9, result.TotalBytes);
        Assert.Equal(3, result.ChunkCount);
        Assert.Equal("AAABBBCCC", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task AssembleAsync_AssemblesMultipleChunks_InOrder_Parallel()
    {
        var c0 = System.Text.Encoding.UTF8.GetBytes("first");
        var c1 = System.Text.Encoding.UTF8.GetBytes("second");
        var c2 = System.Text.Encoding.UTF8.GetBytes("third");
        var c3 = System.Text.Encoding.UTF8.GetBytes("fourth");

        using var http = new HttpClient(new ChunkSourceHandler(
            ("https://test/c0", c0),
            ("https://test/c1", c1),
            ("https://test/c2", c2),
            ("https://test/c3", c3)));

        var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });
        var output = OutputPath();

        var result = await sut.AssembleAsync(
            new[]
            {
                new ChunkSpec(0, "https://test/c0"),
                new ChunkSpec(1, "https://test/c1"),
                new ChunkSpec(2, "https://test/c2"),
                new ChunkSpec(3, "https://test/c3"),
            },
            output,
            new ChunkedAssemblyOptions { MaxConcurrency = 4 });

        Assert.Equal(4, result.ChunkCount);
        Assert.Equal("firstsecondthirdfourth", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task AssembleAsync_PreservesChunkOrder_DespiteOutOfOrderInput()
    {
        var c0 = System.Text.Encoding.UTF8.GetBytes("AAA");
        var c1 = System.Text.Encoding.UTF8.GetBytes("BBB");
        var c2 = System.Text.Encoding.UTF8.GetBytes("CCC");

        using var http = new HttpClient(new ChunkSourceHandler(
            ("https://test/c0", c0),
            ("https://test/c1", c1),
            ("https://test/c2", c2)));

        var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });
        var output = OutputPath();

        // Submit in reverse order — should still emit AAABBBCCC.
        await sut.AssembleAsync(
            new[]
            {
                new ChunkSpec(2, "https://test/c2"),
                new ChunkSpec(0, "https://test/c0"),
                new ChunkSpec(1, "https://test/c1"),
            },
            output);

        Assert.Equal("AAABBBCCC", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task AssembleAsync_ReportsProgress()
    {
        var data = new byte[] { 1, 2, 3 };
        using var http = new HttpClient(new ChunkSourceHandler(
            ("https://test/c0", data),
            ("https://test/c1", data),
            ("https://test/c2", data)));

        var progressEvents = new List<ChunkedAssemblyProgress>();
        var progress = new Progress<ChunkedAssemblyProgress>(p => progressEvents.Add(p));

        var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });
        await sut.AssembleAsync(
            new[]
            {
                new ChunkSpec(0, "https://test/c0"),
                new ChunkSpec(1, "https://test/c1"),
                new ChunkSpec(2, "https://test/c2"),
            },
            OutputPath(),
            new ChunkedAssemblyOptions { MaxConcurrency = 1, Progress = progress });

        // Progress<T> dispatches asynchronously; allow a brief settle.
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (progressEvents.Count < 3 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
        }

        Assert.Equal(3, progressEvents.Count);
        Assert.Equal(3, progressEvents[2].CompletedChunks);
        Assert.Equal(3, progressEvents[2].TotalChunks);
    }

    [Fact]
    public async Task AssembleAsync_AtomicRename_NoPartialFileLeft()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("payload");
        using var http = new HttpClient(new ChunkSourceHandler(("https://test/c0", data)));
        var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });
        var output = OutputPath();

        await sut.AssembleAsync(new[] { new ChunkSpec(0, "https://test/c0") }, output);

        Assert.True(File.Exists(output));
        Assert.False(File.Exists(output + ".partial"));
    }

    [Fact]
    public async Task AssembleAsync_OnFailure_RemovesPartialFile()
    {
        using var http = new HttpClient(new AlwaysFailingHandler());
        var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });
        var output = OutputPath();

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await sut.AssembleAsync(new[] { new ChunkSpec(0, "https://test/c0") }, output));

        Assert.False(File.Exists(output + ".partial"));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task AssembleAsync_ThrowsOnEmptyChunkList()
    {
        using var http = new HttpClient(new AlwaysFailingHandler());
        var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });

        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sut.AssembleAsync(Array.Empty<ChunkSpec>(), OutputPath()));
    }

    [Fact]
    public async Task AssembleAsync_RespectsCancellation()
    {
        var data = System.Text.Encoding.UTF8.GetBytes("payload");
        using var http = new HttpClient(new ChunkSourceHandler(
            ("https://test/c0", data),
            ("https://test/c1", data),
            ("https://test/c2", data)));

        var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
            await sut.AssembleAsync(
                new[]
                {
                    new ChunkSpec(0, "https://test/c0"),
                    new ChunkSpec(1, "https://test/c1"),
                    new ChunkSpec(2, "https://test/c2"),
                },
                OutputPath(),
                ChunkedAssemblyOptions.Default,
                cts.Token));
    }

    [Fact]
    public async Task ChunkSpec_RejectsNegativeIndex()
    {
        await Task.CompletedTask;
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChunkSpec(-1, "https://test/c0"));
    }

    [Fact]
    public async Task ChunkSpec_RejectsEmptyUrl()
    {
        await Task.CompletedTask;
        Assert.Throws<ArgumentException>(() => new ChunkSpec(0, ""));
    }

    /// <summary>Returns each registered URL's payload as 200 OK.</summary>
    private sealed class ChunkSourceHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, byte[]> _payloads;

        public ChunkSourceHandler(params (string Url, byte[] Payload)[] entries)
        {
            _payloads = entries.ToDictionary(e => e.Url, e => e.Payload, StringComparer.Ordinal);
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            if (_payloads.TryGetValue(uri, out var payload))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new ByteArrayContent(payload)
                });
            }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
        }
    }

    private sealed class AlwaysFailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
    }
}
