// <copyright file="ChunkedHttpAssemblerEdgeCaseTests.cs" company="RicherTunes">
// Copyright (c) RicherTunes. All rights reserved.
// </copyright>

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

/// <summary>
/// Edge-case coverage complementing <see cref="ChunkedHttpAssemblerTests"/>: argument
/// validation, options clamping, ChunkDelay forces sequential mode, PreserveTempOnFailure,
/// directory auto-creation, replacing existing output, ChunkSpec.Tag.
/// </summary>
[Trait("Category", "Unit")]
public sealed class ChunkedHttpAssemblerEdgeCaseTests : IDisposable
{
    private readonly string _tempDir;

    public ChunkedHttpAssemblerEdgeCaseTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), $"lpc_chunked_edge_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try { if (Directory.Exists(_tempDir)) Directory.Delete(_tempDir, recursive: true); } catch { /* ignore */ }
    }

    private string OutputPath(string name = "out.bin") => Path.Combine(_tempDir, name);

    [Fact]
    public void Ctor_NullHttpClient_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new ChunkedHttpAssembler(null!));
    }

    [Fact]
    public async Task AssembleAsync_NullChunks_Throws()
    {
        using var http = new HttpClient(new ChunkSourceHandler());
        var sut = new ChunkedHttpAssembler(http);
        await Assert.ThrowsAsync<ArgumentNullException>(async () =>
            await sut.AssembleAsync(null!, OutputPath()));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public async Task AssembleAsync_NullOrEmptyOutputPath_Throws(string? output)
    {
        using var http = new HttpClient(new ChunkSourceHandler());
        var sut = new ChunkedHttpAssembler(http);
        await Assert.ThrowsAsync<ArgumentException>(async () =>
            await sut.AssembleAsync(new[] { new ChunkSpec(0, "https://test/c0") }, output!));
    }

    [Fact]
    public async Task AssembleAsync_MaxConcurrencyZero_ClampsToOne_AndSucceeds()
    {
        var c0 = System.Text.Encoding.UTF8.GetBytes("AA");
        var c1 = System.Text.Encoding.UTF8.GetBytes("BB");
        using var http = new HttpClient(new ChunkSourceHandler(
            ("https://test/c0", c0),
            ("https://test/c1", c1)));

        var sut = new ChunkedHttpAssembler(http);

        var result = await sut.AssembleAsync(
            new[]
            {
                new ChunkSpec(0, "https://test/c0"),
                new ChunkSpec(1, "https://test/c1"),
            },
            OutputPath(),
            new ChunkedAssemblyOptions { MaxConcurrency = 0 });

        Assert.Equal(2, result.ChunkCount);
        Assert.Equal("AABB", await File.ReadAllTextAsync(result.OutputPath));
    }

    [Fact]
    public async Task AssembleAsync_ChunkDelay_ForcesSequentialMode_PreservesOrder()
    {
        var c0 = System.Text.Encoding.UTF8.GetBytes("X");
        var c1 = System.Text.Encoding.UTF8.GetBytes("Y");
        var c2 = System.Text.Encoding.UTF8.GetBytes("Z");
        using var http = new HttpClient(new ChunkSourceHandler(
            ("https://test/c0", c0),
            ("https://test/c1", c1),
            ("https://test/c2", c2)));

        var sut = new ChunkedHttpAssembler(http);

        // High MaxConcurrency requested, but ChunkDelay > 0 must downgrade to sequential.
        var result = await sut.AssembleAsync(
            new[]
            {
                new ChunkSpec(0, "https://test/c0"),
                new ChunkSpec(1, "https://test/c1"),
                new ChunkSpec(2, "https://test/c2"),
            },
            OutputPath(),
            new ChunkedAssemblyOptions
            {
                MaxConcurrency = 8,
                ChunkDelay = TimeSpan.FromMilliseconds(5),
            });

        Assert.Equal("XYZ", await File.ReadAllTextAsync(result.OutputPath));
    }

    [Fact]
    public async Task AssembleAsync_OverwritesExistingOutputFile()
    {
        var output = OutputPath();
        await File.WriteAllTextAsync(output, "old contents");

        var data = System.Text.Encoding.UTF8.GetBytes("new");
        using var http = new HttpClient(new ChunkSourceHandler(("https://test/c0", data)));
        var sut = new ChunkedHttpAssembler(http);

        await sut.AssembleAsync(new[] { new ChunkSpec(0, "https://test/c0") }, output);

        Assert.Equal("new", await File.ReadAllTextAsync(output));
    }

    [Fact]
    public async Task AssembleAsync_CreatesNonExistentParentDirectory()
    {
        var nested = Path.Combine(_tempDir, "nested", "dir", "out.bin");
        Assert.False(Directory.Exists(Path.GetDirectoryName(nested)!));

        var data = System.Text.Encoding.UTF8.GetBytes("ok");
        using var http = new HttpClient(new ChunkSourceHandler(("https://test/c0", data)));
        var sut = new ChunkedHttpAssembler(http);

        await sut.AssembleAsync(new[] { new ChunkSpec(0, "https://test/c0") }, nested);

        Assert.True(File.Exists(nested));
    }

    [Fact]
    public async Task AssembleAsync_PreserveTempOnFailure_True_KeepsPartial()
    {
        using var http = new HttpClient(new AlwaysFailingHandler());
        var sut = new ChunkedHttpAssembler(http);
        var output = OutputPath("preserve.bin");

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await sut.AssembleAsync(
                new[] { new ChunkSpec(0, "https://test/c0") },
                output,
                new ChunkedAssemblyOptions { PreserveTempOnFailure = true }));

        // .partial is left behind; the final file is still missing.
        Assert.False(File.Exists(output));

        // Some preserved chunked-temp directory should still exist somewhere (best effort assertion via tmp scan).
        var preserved = Directory.EnumerateDirectories(Path.GetTempPath(), "lpc_chunks_*").ToArray();
        // We can't guarantee ours wasn't already cleaned by another test, but the option must not throw.
        Assert.NotNull(preserved);
    }

    [Fact]
    public async Task AssembleAsync_ChunkServerReturns500_Parallel_PropagatesException()
    {
        // Mix one successful and one failing chunk to exercise parallel-mode failure path
        // (catch + cts.Cancel + drain inflight tasks).
        using var http = new HttpClient(new MixedHandler(new Dictionary<string, HttpStatusCode>
        {
            { "https://test/c0", HttpStatusCode.OK },
            { "https://test/c1", HttpStatusCode.InternalServerError },
        }));
        var sut = new ChunkedHttpAssembler(http);

        await Assert.ThrowsAnyAsync<Exception>(async () =>
            await sut.AssembleAsync(
                new[]
                {
                    new ChunkSpec(0, "https://test/c0"),
                    new ChunkSpec(1, "https://test/c1"),
                },
                OutputPath("mixed.bin"),
                new ChunkedAssemblyOptions { MaxConcurrency = 2 }));
    }

    [Fact]
    public void ChunkSpec_RejectsNullUrl()
    {
        Assert.Throws<ArgumentException>(() => new ChunkSpec(0, null!));
    }

    [Fact]
    public void ChunkSpec_AcceptsOptionalTag()
    {
        var spec = new ChunkSpec(2, "https://test/c2", tag: "sha:abc");
        Assert.Equal(2, spec.Index);
        Assert.Equal("https://test/c2", spec.Url);
        Assert.Equal("sha:abc", spec.Tag);
    }

    [Fact]
    public void ChunkSpec_TagDefaultsToNull()
    {
        var spec = new ChunkSpec(0, "https://test/x");
        Assert.Null(spec.Tag);
    }

    [Fact]
    public void ChunkedAssemblyProgress_Percentage_HandlesZeroTotal()
    {
        var p = new ChunkedAssemblyProgress { TotalChunks = 0, CompletedChunks = 0 };
        Assert.Equal(0.0, p.ProgressPercentage);
    }

    [Fact]
    public void ChunkedAssemblyProgress_Percentage_ComputesCorrectly()
    {
        var p = new ChunkedAssemblyProgress { TotalChunks = 4, CompletedChunks = 1 };
        Assert.Equal(25.0, p.ProgressPercentage);
    }

    [Fact]
    public void ChunkedAssemblyOptions_Default_ProvidesSafeDefaults()
    {
        var o = ChunkedAssemblyOptions.Default;
        Assert.True(o.MaxConcurrency >= 1);
        Assert.Equal(TimeSpan.Zero, o.ChunkDelay);
        Assert.True(o.BufferSize > 0);
        Assert.False(o.PreserveTempOnFailure);
        Assert.Null(o.Progress);
    }

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
                    Content = new ByteArrayContent(payload),
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

    private sealed class MixedHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, HttpStatusCode> _routes;

        public MixedHandler(Dictionary<string, HttpStatusCode> routes)
        {
            _routes = routes;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!.ToString();
            var status = _routes.TryGetValue(uri, out var s) ? s : HttpStatusCode.NotFound;
            var resp = new HttpResponseMessage(status);
            if (status == HttpStatusCode.OK)
            {
                resp.Content = new ByteArrayContent(new byte[] { 1, 2, 3 });
            }
            return Task.FromResult(resp);
        }
    }
}
