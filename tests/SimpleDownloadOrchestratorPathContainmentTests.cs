using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Abstractions.Models;
using Lidarr.Plugin.Common.Interfaces;
using Lidarr.Plugin.Common.Services.Download;
using Xunit;

namespace Lidarr.Plugin.Common.Tests
{
    /// <summary>
    /// Containment contract for the <c>BuildTrackOutputPath</c> naming seam: the album loops must
    /// reject an override that returns a path resolving OUTSIDE the album's output directory
    /// (write-anywhere hazard), recording a single-track failure instead of writing outside the
    /// root or aborting the whole album. Legitimate nested subpaths (multi-disc
    /// "Disc 01/01 - Title.flac") must remain allowed — the guard is containment, not flattening.
    /// </summary>
    public class SimpleDownloadOrchestratorPathContainmentTests
    {
        private sealed class NamingOrchestrator : SimpleDownloadOrchestrator
        {
            public Func<string, StreamingTrack?, string>? NameBuilder { get; set; }

            public NamingOrchestrator(HttpClient httpClient, IReadOnlyList<string> trackIds, int maxConcurrentTracks = 1)
                : base(
                    "ContainmentTest",
                    httpClient,
                    getAlbumAsync: id => Task.FromResult(new StreamingAlbum { Id = id, Title = "A", Artist = new StreamingArtist { Name = "X" }, TrackCount = trackIds.Count }),
                    getTrackAsync: id => Task.FromResult(new StreamingTrack
                    {
                        Id = id,
                        Title = $"T{id.TrimStart('t')}",
                        TrackNumber = int.Parse(id.TrimStart('t')),
                        Artist = new StreamingArtist { Name = "X" },
                        Album = new StreamingAlbum { Title = "A", Artist = new StreamingArtist { Name = "X" } }
                    }),
                    getAlbumTrackIdsAsync: _ => Task.FromResult(trackIds),
                    getStreamAsync: (id, q) => Task.FromResult(("https://93.184.216.34/file", "bin")),
                    maxConcurrentTracks,
                    streamProvider: null,
                    metadataApplier: new NoopMetadataApplier(),
                    logger: null,
                    postProcessor: null,
                    telemetrySink: null)
            {
            }

            protected override string BuildTrackOutputPath(string outputDirectory, StreamingTrack? track)
            {
                return NameBuilder != null
                    ? NameBuilder(outputDirectory, track)
                    : base.BuildTrackOutputPath(outputDirectory, track);
            }

            private sealed class NoopMetadataApplier : IAudioMetadataApplier
            {
                public Task ApplyAsync(string filePath, StreamingTrack metadata, CancellationToken cancellationToken = default)
                    => Task.CompletedTask;
            }
        }

        private static (string Sandbox, string AlbumDir) MakeDirs(string label)
        {
            var sandbox = Path.Combine(Path.GetTempPath(), $"orch_contain_{label}_{Guid.NewGuid():N}");
            var albumDir = Path.Combine(sandbox, "album");
            Directory.CreateDirectory(albumDir);
            return (sandbox, albumDir);
        }

        [Fact]
        public async Task SequentialLoop_EscapingOverride_FailsThatTrackOnly_NothingWrittenOutsideRoot()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var orch = new NamingOrchestrator(http, new List<string> { "t1", "t2" });
            // t1 escapes the album root via "..", t2 is legitimate — the loop must keep going after
            // the containment failure and let t2 succeed.
            orch.NameBuilder = (dir, track) => track?.Id == "t1"
                ? Path.Combine(dir, "..", "escape.bin")
                : Path.Combine(dir, "inside_02.bin");

            var (sandbox, albumDir) = MakeDirs("seq");
            var escaped = Path.GetFullPath(Path.Combine(albumDir, "..", "escape.bin"));
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", albumDir, new StreamingQuality { Bitrate = 320 });

                Assert.False(File.Exists(escaped), $"track file must NOT be written outside the album root: {escaped}");
                Assert.False(result.Success, "an album with a rejected track must not report success");
                Assert.Equal(2, result.TrackResults.Count);
                var failed = Assert.Single(result.TrackResults, tr => !tr.Success);
                Assert.Equal("t1", failed.TrackId);
                Assert.Contains("escapes the album root", failed.ErrorMessage);
                var ok = Assert.Single(result.TrackResults, tr => tr.Success);
                Assert.Equal("t2", ok.TrackId);
                var goodFile = Assert.Single(result.FilePaths);
                Assert.True(File.Exists(goodFile), $"the non-escaping sibling track must still download: {goodFile}");
            }
            finally
            {
                TryDeleteDir(sandbox);
                TryDelete(escaped);
            }
        }

        [Fact]
        public async Task ParallelLoop_EscapingOverride_FailsThatTrackOnly_NothingWrittenOutsideRoot()
        {
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var orch = new NamingOrchestrator(http, new List<string> { "t1", "t2" }, maxConcurrentTracks: 2);
            orch.NameBuilder = (dir, track) => track?.Id == "t2"
                ? Path.Combine(dir, "..", "escape.bin")
                : Path.Combine(dir, "inside_01.bin");

            var (sandbox, albumDir) = MakeDirs("par");
            var escaped = Path.GetFullPath(Path.Combine(albumDir, "..", "escape.bin"));
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", albumDir, new StreamingQuality { Bitrate = 320 });

                Assert.False(File.Exists(escaped), $"track file must NOT be written outside the album root: {escaped}");
                Assert.False(result.Success);
                Assert.Equal(2, result.TrackResults.Count);
                var failed = Assert.Single(result.TrackResults, tr => !tr.Success);
                Assert.Equal("t2", failed.TrackId);
                Assert.Contains("escapes the album root", failed.ErrorMessage);
                var goodFile = Assert.Single(result.FilePaths);
                Assert.True(File.Exists(goodFile));
            }
            finally
            {
                TryDeleteDir(sandbox);
                TryDelete(escaped);
            }
        }

        [Fact]
        public async Task NestedSubdirectoryOverride_IsAllowed_NotOverBlocked()
        {
            // qobuz-style multi-disc naming returns a SUBPATH under the album root — containment
            // must allow nested descendants, not flatten to direct children.
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var orch = new NamingOrchestrator(http, new List<string> { "t1" });
            orch.NameBuilder = (dir, track) => Path.Combine(dir, "Disc 01", "01 - T.flac");

            var (sandbox, albumDir) = MakeDirs("nested");
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", albumDir, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success, $"nested subpath must be allowed: {result.ErrorMessage}");
                var expected = Path.Combine(albumDir, "Disc 01", "01 - T.bin"); // engine swaps to the resolved stream extension
                var file = Assert.Single(result.FilePaths);
                Assert.Equal(expected, file);
                Assert.True(File.Exists(expected));
            }
            finally
            {
                TryDeleteDir(sandbox);
            }
        }

        [Fact]
        public async Task AlbumRootItselfViaDotSegments_IsAllowed()
        {
            // A path that lexically wanders ("./sub/../name.bin") but RESOLVES inside the root must
            // pass — the guard is canonical-form containment, not a lexical ".." rejection.
            using var http = new HttpClient(new FakeRangeHandler(totalBytes: 4, supportRange: false));
            var orch = new NamingOrchestrator(http, new List<string> { "t1" });
            orch.NameBuilder = (dir, track) => Path.Combine(dir, "sub", "..", "01 - T.flac");

            var (sandbox, albumDir) = MakeDirs("dots");
            try
            {
                var result = await orch.DownloadAlbumAsync("a1", albumDir, new StreamingQuality { Bitrate = 320 });

                Assert.True(result.Success, $"a path resolving inside the root must be allowed: {result.ErrorMessage}");
                var file = Assert.Single(result.FilePaths);
                Assert.True(File.Exists(file));
            }
            finally
            {
                TryDeleteDir(sandbox);
            }
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        private static void TryDeleteDir(string dir)
        {
            try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { }
        }
    }
}
