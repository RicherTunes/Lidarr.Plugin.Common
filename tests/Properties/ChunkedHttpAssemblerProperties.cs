// <copyright file="ChunkedHttpAssemblerProperties.cs" company="RicherTunes">
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
using FsCheck;
using FsCheck.Fluent;
using FsCheck.Xunit;
using Lidarr.Plugin.Common.Services.Download;

namespace Lidarr.Plugin.Common.Tests.Properties
{
    /// <summary>
    /// FsCheck property tests for <see cref="ChunkedHttpAssembler"/>.
    /// I/O bounded; MaxTest reduced. Wave 28 extension of wave 27 properties.
    /// </summary>
    public class ChunkedHttpAssemblerProperties
    {
        private static string NewTempDir()
        {
            var dir = Path.Combine(Path.GetTempPath(), "LPC.ChunkAsmProps." + Guid.NewGuid().ToString("n"));
            Directory.CreateDirectory(dir);
            return dir;
        }

        private static void TryCleanup(string dir)
        {
            try
            {
                if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true);
            }
            catch
            {
                // best-effort
            }
        }

        /// <summary>
        /// Build N deterministic chunk payloads from a seed byte array. Each chunk is keyed by
        /// its index so the URL-keyed handler returns the matching bytes regardless of fetch
        /// order. The expected concatenation is bytes(0) || bytes(1) || ... in index order.
        /// </summary>
        private static (ChunkSpec[] Specs, byte[] Expected, (string Url, byte[] Payload)[] Entries)
            BuildChunks(byte[] seed, int chunkCount)
        {
            var specs = new ChunkSpec[chunkCount];
            var entries = new (string, byte[])[chunkCount];
            using var ms = new MemoryStream();
            for (var i = 0; i < chunkCount; i++)
            {
                // Deterministic per-index payload of length 1..16.
                var len = ((seed.Length == 0 ? (byte)1 : seed[i % seed.Length]) % 16) + 1;
                var payload = new byte[len];
                for (var j = 0; j < len; j++)
                {
                    payload[j] = (byte)((i * 31 + j) & 0xFF);
                }
                var url = $"https://test/chunk/{i}";
                specs[i] = new ChunkSpec(i, url);
                entries[i] = (url, payload);
                ms.Write(payload, 0, payload.Length);
            }
            return (specs, ms.ToArray(), entries);
        }

        /// <summary>
        /// Order preservation: regardless of the order in which chunk specs are submitted to
        /// AssembleAsync, the assembled file's bytes equal the concatenation in INDEX order.
        /// Critical guarantee under parallel fetch.
        /// </summary>
        [Property(MaxTest = 15)]
        public Property OrderPreserved_AcrossArbitrarySubmissionOrder(byte[] seed, byte rawCount, byte rawShuffle)
        {
            var count = (rawCount % 8) + 2; // 2..9 chunks
            var (specs, expected, entries) = BuildChunks(seed ?? Array.Empty<byte>(), count);

            // Deterministic shuffle: simple swap-based permutation driven by seed.
            var shuffled = specs.ToArray();
            var rngState = (int)rawShuffle + 1;
            for (var i = shuffled.Length - 1; i > 0; i--)
            {
                rngState = (rngState * 1103515245 + 12345) & 0x7FFFFFFF;
                var j = rngState % (i + 1);
                (shuffled[i], shuffled[j]) = (shuffled[j], shuffled[i]);
            }

            var dir = NewTempDir();
            try
            {
                using var http = new HttpClient(new UrlPayloadHandler(entries));
                var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });
                var output = Path.Combine(dir, "out.bin");

                var result = sut.AssembleAsync(
                    shuffled,
                    output,
                    new ChunkedAssemblyOptions { MaxConcurrency = 4 }).GetAwaiter().GetResult();

                var actual = File.ReadAllBytes(output);
                var ok = actual.SequenceEqual(expected)
                    && result.ChunkCount == count
                    && result.TotalBytes == expected.Length;
                return ok.ToProperty();
            }
            finally
            {
                TryCleanup(dir);
            }
        }

        /// <summary>
        /// Atomic-rename invariant: on success the final output file exists and the .partial
        /// sibling does NOT. On failure (with PreserveTempOnFailure=false) the .partial is
        /// cleaned up and the final output does not exist.
        /// </summary>
        [Property(MaxTest = 12)]
        public Property AtomicRename_OnSuccess_NoPartialLeftover(byte[] seed, byte rawCount)
        {
            var count = (rawCount % 6) + 1; // 1..6
            var (specs, expected, entries) = BuildChunks(seed ?? Array.Empty<byte>(), count);

            var dir = NewTempDir();
            try
            {
                using var http = new HttpClient(new UrlPayloadHandler(entries));
                var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });
                var output = Path.Combine(dir, "final.bin");
                var partial = output + ".partial";

                _ = sut.AssembleAsync(specs, output, new ChunkedAssemblyOptions { MaxConcurrency = 2 })
                    .GetAwaiter().GetResult();

                var ok = File.Exists(output) && !File.Exists(partial)
                    && File.ReadAllBytes(output).SequenceEqual(expected);
                return ok.ToProperty();
            }
            finally
            {
                TryCleanup(dir);
            }
        }

        /// <summary>
        /// Failure path: when chunk fetch fails (handler returns 500), neither the final
        /// output nor the .partial remain when PreserveTempOnFailure is false.
        /// </summary>
        [Property(MaxTest = 8)]
        public Property OnFailure_NoStaleArtifactsLeftover(byte rawCount)
        {
            var count = (rawCount % 4) + 1;
            var specs = Enumerable.Range(0, count)
                .Select(i => new ChunkSpec(i, $"https://test/fail/{i}"))
                .ToArray();

            var dir = NewTempDir();
            try
            {
                using var http = new HttpClient(new AlwaysFailingHandler());
                var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });
                var output = Path.Combine(dir, "final.bin");
                var partial = output + ".partial";

                var threw = false;
                try
                {
                    _ = sut.AssembleAsync(specs, output, new ChunkedAssemblyOptions { MaxConcurrency = 2, PreserveTempOnFailure = false })
                        .GetAwaiter().GetResult();
                }
                catch
                {
                    threw = true;
                }

                var ok = threw && !File.Exists(output) && !File.Exists(partial);
                return ok.ToProperty();
            }
            finally
            {
                TryCleanup(dir);
            }
        }

        /// <summary>
        /// MaxConcurrency clamp: passing 0 or negative values must clamp to >= 1 internally
        /// (no division-by-zero, no thread starvation). The assembler should still complete
        /// successfully and produce the expected output.
        /// </summary>
        [Property(MaxTest = 10)]
        public Property MaxConcurrency_NonPositive_ClampsToOne(byte rawConcurrency, byte rawCount)
        {
            // Map raw byte to a non-positive concurrency value: -128..0
            var concurrency = -(rawConcurrency % 129);
            var count = (rawCount % 4) + 1;
            var (specs, expected, entries) = BuildChunks(new byte[] { rawCount }, count);

            var dir = NewTempDir();
            try
            {
                using var http = new HttpClient(new UrlPayloadHandler(entries));
                var sut = new ChunkedHttpAssembler(http, mediaUriPolicy: new RemoteMediaUriPolicy { AllowPrivateNetworks = true });
                var output = Path.Combine(dir, "out.bin");

                var result = sut.AssembleAsync(specs, output, new ChunkedAssemblyOptions { MaxConcurrency = concurrency })
                    .GetAwaiter().GetResult();

                var ok = File.Exists(output)
                    && File.ReadAllBytes(output).SequenceEqual(expected)
                    && result.ChunkCount == count;
                return ok.ToProperty();
            }
            finally
            {
                TryCleanup(dir);
            }
        }

        private sealed class UrlPayloadHandler : HttpMessageHandler
        {
            private readonly Dictionary<string, byte[]> _payloads;

            public UrlPayloadHandler((string Url, byte[] Payload)[] entries)
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
}
