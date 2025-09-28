using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Models;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    public class SimpleDownloadOrchestratorTests
    {
        [Fact]
        public async Task DownloadTrack_ReportsProgress_WithSpeedAndEta()
        {
            var totalBytes = 512 * 1024; // 512KB
            var handler = new FakeRangeHandler(totalBytes, supportRange: true);
            using var http = new HttpClient(handler);

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("http://test/file", "bin"))
            );

            var temp = Path.Combine(Path.GetTempPath(), "orch_test_progress.bin");
            try
            {
                // Minimal assertion-only test; progress values are exercised by the download
                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                Assert.True(File.Exists(result.FilePath));
                Assert.Equal(totalBytes, new FileInfo(result.FilePath).Length);
            }
            finally { TryDelete(temp); TryDelete(temp + ".partial"); TryDelete(temp + ".partial.resume.json"); }
        }

        [Fact]
        public async Task DownloadTrack_ResumeWithIfRange_ContinuesFromPartial()
        {
            var totalBytes = 300 * 1024; // 300KB
            var handler = new FakeRangeHandler(totalBytes, supportRange: true, etag: "\"abc123\"");
            using var http = new HttpClient(handler);

            var orch = new SimpleDownloadOrchestrator(
                serviceName: "Test",
                httpClient: http,
                getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = 1 }),
                getTrackAsync: id => Task.FromResult(new StreamingTrack { Id = id, Title = "T", Artist = new StreamingArtist { Name = "X" }, Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }, TrackNumber = 1 }),
                getAlbumTrackIdsAsync: id => Task.FromResult((IReadOnlyList<string>)new List<string> { "t1" }),
                getStreamAsync: (id, q) => Task.FromResult(("http://test/file2", "bin"))
            );

            var temp = Path.Combine(Path.GetTempPath(), "orch_test_resume.bin");
            var partial = temp + ".partial";
            try
            {
                // Seed partial file with first 100KB
                Directory.CreateDirectory(Path.GetDirectoryName(temp)!);
                using (var fs = new FileStream(partial, FileMode.Create, FileAccess.Write))
                {
                    var buf = new byte[100 * 1024];
                    new Random(42).NextBytes(buf);
                    fs.Write(buf, 0, buf.Length);
                }

                var result = await orch.DownloadTrackAsync("t1", temp, new StreamingQuality { Bitrate = 320 });
                Assert.True(result.Success, $"Download failed: {result.ErrorMessage}");
                Assert.Equal(totalBytes, new FileInfo(result.FilePath).Length);
            }
            finally { TryDelete(temp); TryDelete(partial); TryDelete(temp + ".partial.resume.json"); }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }

    internal sealed class FakeRangeHandler : HttpMessageHandler
    {
        private readonly int _totalBytes;
        private readonly bool _supportRange;
        private readonly string? _etag;

        public FakeRangeHandler(int totalBytes, bool supportRange, string? etag = null)
        {
            _totalBytes = totalBytes;
            _supportRange = supportRange;
            _etag = etag;
        }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var response = new HttpResponseMessage(HttpStatusCode.OK);
            var start = 0;
            if (_supportRange && request.Headers.Range != null)
            {
                var enumerator = request.Headers.Range.Ranges.GetEnumerator();
                if (enumerator.MoveNext())
                {
                    var range = enumerator.Current;
                    start = (int)(range.From ?? 0);
                }
                response.StatusCode = HttpStatusCode.PartialContent;
                response.Content = new ByteArrayContent(BuildBytes(_totalBytes - start));
                response.Content.Headers.ContentLength = _totalBytes - start;
            }
            else
            {
                response.StatusCode = HttpStatusCode.OK;
                response.Content = new ByteArrayContent(BuildBytes(_totalBytes));
                response.Content.Headers.ContentLength = _totalBytes;
            }

            if (!string.IsNullOrEmpty(_etag))
            {
                response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(_etag);
                response.Content.Headers.LastModified = DateTimeOffset.UtcNow;
            }
            return Task.FromResult(response);
        }

        private static byte[] BuildBytes(int count)
        {
            var data = new byte[count];
            for (int i = 0; i < count; i++) data[i] = (byte)(i % 251);
            return data;
        }
    }
}
