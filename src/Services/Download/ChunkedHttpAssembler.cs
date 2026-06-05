using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>
    /// Specification for a single chunk to be downloaded.
    /// </summary>
    public sealed class ChunkSpec
    {
        /// <summary>0-based ordinal of this chunk in the assembled output.</summary>
        public int Index { get; }

        /// <summary>HTTP URL to GET for this chunk.</summary>
        public string Url { get; }

        /// <summary>Optional opaque tag (e.g. expected SHA, byte length).</summary>
        public string? Tag { get; }

        /// <summary>Creates a new <see cref="ChunkSpec"/>.</summary>
        public ChunkSpec(int index, string url, string? tag = null)
        {
            if (index < 0) throw new ArgumentOutOfRangeException(nameof(index));
            if (string.IsNullOrWhiteSpace(url)) throw new ArgumentException("URL must not be empty.", nameof(url));
            Index = index;
            Url = url;
            Tag = tag;
        }
    }

    /// <summary>Options controlling <see cref="ChunkedHttpAssembler.AssembleAsync"/>.</summary>
    public sealed class ChunkedAssemblyOptions
    {
        /// <summary>Maximum number of chunks downloaded in parallel. Clamped to >= 1.</summary>
        public int MaxConcurrency { get; init; } = 4;

        /// <summary>Optional delay between sequential chunk fetches. Setting this > 0 forces sequential mode (no parallelism).</summary>
        public TimeSpan ChunkDelay { get; init; } = TimeSpan.Zero;

        /// <summary>Buffer size used when streaming chunk bodies to disk.</summary>
        public int BufferSize { get; init; } = 65536;

        /// <summary>Optional progress reporter — called with cumulative completed-chunk counts.</summary>
        public IProgress<ChunkedAssemblyProgress>? Progress { get; init; }

        /// <summary>If true, the temp directory is preserved on failure for diagnostics. Default: false.</summary>
        public bool PreserveTempOnFailure { get; init; }

        /// <summary>Default options.</summary>
        public static ChunkedAssemblyOptions Default => new();
    }

    /// <summary>Progress event payload from <see cref="ChunkedHttpAssembler"/>.</summary>
    public sealed class ChunkedAssemblyProgress
    {
        /// <summary>Total number of chunks in the assembly job.</summary>
        public int TotalChunks { get; init; }

        /// <summary>Number of chunks fully written to the output file so far.</summary>
        public int CompletedChunks { get; init; }

        /// <summary>Cumulative bytes written to the output file.</summary>
        public long BytesWritten { get; init; }

        /// <summary>Convenience: percentage 0..100.</summary>
        public double ProgressPercentage => TotalChunks > 0 ? (double)CompletedChunks / TotalChunks * 100.0 : 0.0;
    }

    /// <summary>Result of a successful <see cref="ChunkedHttpAssembler.AssembleAsync"/> call.</summary>
    public sealed class ChunkedAssemblyResult
    {
        /// <summary>Final output file path (after atomic rename).</summary>
        public string OutputPath { get; init; } = string.Empty;

        /// <summary>Total bytes written to the output file.</summary>
        public long TotalBytes { get; init; }

        /// <summary>Number of chunks successfully assembled.</summary>
        public int ChunkCount { get; init; }
    }

    /// <summary>
    /// Generic chunked-download orchestrator: parallel chunk fetch, ordered assembly,
    /// temp-file bookkeeping, atomic rename to final output. Decryption / format-specific
    /// post-processing stays plugin-local.
    /// </summary>
    /// <remarks>
    /// Fetches each <see cref="ChunkSpec"/>'s URL via the supplied <see cref="HttpClient"/>
    /// (or <see cref="IHttpFileDownloadService"/>'s host client) into per-chunk temp files,
    /// then concatenates them in order and atomically renames the result. Composes naturally
    /// with <see cref="HttpFileDownloadService"/> when a stronger per-chunk pipeline is needed.
    /// </remarks>
    public sealed class ChunkedHttpAssembler
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger _logger;
        private readonly RemoteMediaUriPolicy _mediaUriPolicy;

        /// <summary>Creates a new assembler that uses the supplied <paramref name="httpClient"/> for chunk fetches.</summary>
        /// <param name="httpClient">Host HttpClient used for chunk GETs.</param>
        /// <param name="logger">Optional logger.</param>
        /// <param name="mediaUriPolicy">SSRF policy applied to every chunk URL before fetch. Defaults to
        /// <see cref="RemoteMediaUriPolicy.Strict"/>. Pass a relaxed policy only for explicitly-local providers.</param>
        public ChunkedHttpAssembler(HttpClient httpClient, ILogger<ChunkedHttpAssembler>? logger = null, RemoteMediaUriPolicy? mediaUriPolicy = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = (ILogger?)logger ?? NullLogger.Instance;
            _mediaUriPolicy = mediaUriPolicy ?? RemoteMediaUriPolicy.Strict;
        }

        /// <summary>
        /// Downloads all <paramref name="chunks"/> and writes them, in order, to <paramref name="outputPath"/>.
        /// On success, the file is atomically renamed from <c>{outputPath}.partial</c> to <paramref name="outputPath"/>.
        /// On failure, the temp directory + .partial are removed unless
        /// <see cref="ChunkedAssemblyOptions.PreserveTempOnFailure"/> is true.
        /// </summary>
        public async Task<ChunkedAssemblyResult> AssembleAsync(
            IEnumerable<ChunkSpec> chunks,
            string outputPath,
            ChunkedAssemblyOptions? options = null,
            CancellationToken cancellationToken = default)
        {
            if (chunks is null) throw new ArgumentNullException(nameof(chunks));
            if (string.IsNullOrWhiteSpace(outputPath)) throw new ArgumentException("Output path must not be empty.", nameof(outputPath));

            options ??= ChunkedAssemblyOptions.Default;
            var orderedChunks = chunks.OrderBy(c => c.Index).ToArray();
            if (orderedChunks.Length == 0)
            {
                throw new ArgumentException("Chunk list must contain at least one chunk.", nameof(chunks));
            }

            // SSRF guard: validate every chunk destination before any fetch (chunk URLs come from provider manifests).
            foreach (var chunk in orderedChunks)
            {
                var guard = RemoteMediaUriGuard.Validate(chunk.Url, _mediaUriPolicy);
                if (!guard.IsAllowed)
                    throw new InvalidOperationException($"Refusing to download chunk {chunk.Index} from an unsafe URL: {guard.Reason}");
            }

            var maxConcurrency = Math.Max(1, options.MaxConcurrency);
            // Sequential mode forced if a delay is requested (preserves request-pacing semantics).
            if (options.ChunkDelay > TimeSpan.Zero) maxConcurrency = 1;

            var directory = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var partialPath = outputPath + ".partial";
            var tempDir = Path.Combine(Path.GetTempPath(), $"lpc_chunks_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            int completedChunks = 0;
            long bytesWritten = 0;

            try
            {
                if (maxConcurrency <= 1 || orderedChunks.Length == 1)
                {
                    await using var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        options.BufferSize, useAsync: true);

                    foreach (var chunk in orderedChunks)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        var bytesThisChunk = await DownloadChunkToStreamAsync(chunk, output, options.BufferSize, cancellationToken).ConfigureAwait(false);
                        bytesWritten += bytesThisChunk;
                        completedChunks++;
                        options.Progress?.Report(new ChunkedAssemblyProgress
                        {
                            TotalChunks = orderedChunks.Length,
                            CompletedChunks = completedChunks,
                            BytesWritten = bytesWritten
                        });
                        if (options.ChunkDelay > TimeSpan.Zero)
                        {
                            await Task.Delay(options.ChunkDelay, cancellationToken).ConfigureAwait(false);
                        }
                    }

                    await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                }
                else
                {
                    using var semaphore = new SemaphoreSlim(maxConcurrency, maxConcurrency);
                    using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                    var chunkPaths = new string[orderedChunks.Length];
                    var tasks = new Task[orderedChunks.Length];
                    for (int i = 0; i < orderedChunks.Length; i++)
                    {
                        var chunk = orderedChunks[i];
                        var path = Path.Combine(tempDir, $"{chunk.Index:D6}.chunk");
                        chunkPaths[i] = path;
                        tasks[i] = DownloadChunkToFileAsync(chunk, path, semaphore, options.BufferSize, cts.Token);
                    }

                    try
                    {
                        await using var output = new FileStream(partialPath, FileMode.Create, FileAccess.Write, FileShare.None,
                            options.BufferSize, useAsync: true);

                        for (int i = 0; i < tasks.Length; i++)
                        {
                            cancellationToken.ThrowIfCancellationRequested();
                            await tasks[i].ConfigureAwait(false);

                            await using (var chunkStream = new FileStream(chunkPaths[i], FileMode.Open, FileAccess.Read, FileShare.Read,
                                options.BufferSize, useAsync: true))
                            {
                                await chunkStream.CopyToAsync(output, options.BufferSize, cancellationToken).ConfigureAwait(false);
                                bytesWritten += chunkStream.Length;
                            }

                            try { File.Delete(chunkPaths[i]); }
                            catch (Exception ex) { _logger.LogDebug(ex, "Best-effort cleanup of chunk file failed: {Path}", chunkPaths[i]); }

                            completedChunks++;
                            options.Progress?.Report(new ChunkedAssemblyProgress
                            {
                                TotalChunks = orderedChunks.Length,
                                CompletedChunks = completedChunks,
                                BytesWritten = bytesWritten
                            });
                        }

                        await output.FlushAsync(cancellationToken).ConfigureAwait(false);
                    }
                    catch
                    {
                        try { cts.Cancel(); } catch { /* best effort */ }
                        // Drain inflight tasks to avoid unobserved exceptions.
                        foreach (var t in tasks)
                        {
                            try { await t.ConfigureAwait(false); } catch { /* ignore */ }
                        }
                        throw;
                    }
                }

                // Atomic rename to final output path.
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }
                File.Move(partialPath, outputPath);

                _logger.LogDebug("Chunked assembly complete: {Bytes} bytes / {Chunks} chunks -> {Path}",
                    bytesWritten, orderedChunks.Length, outputPath);

                return new ChunkedAssemblyResult
                {
                    OutputPath = outputPath,
                    TotalBytes = bytesWritten,
                    ChunkCount = orderedChunks.Length
                };
            }
            catch
            {
                if (!options.PreserveTempOnFailure)
                {
                    try { if (File.Exists(partialPath)) File.Delete(partialPath); } catch { /* ignore */ }
                }
                throw;
            }
            finally
            {
                if (!options.PreserveTempOnFailure)
                {
                    try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                    catch (Exception ex) { _logger.LogDebug(ex, "Best-effort cleanup of temp dir failed: {Dir}", tempDir); }
                }
            }
        }

        private async Task<long> DownloadChunkToStreamAsync(ChunkSpec chunk, Stream output, int bufferSize, CancellationToken cancellationToken)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, chunk.Url);
            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();
            await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            long total = 0;
            var buffer = new byte[bufferSize];
            int read;
            while ((read = await content.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
            {
                await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                total += read;
            }
            return total;
        }

        private async Task DownloadChunkToFileAsync(ChunkSpec chunk, string outputPath, SemaphoreSlim semaphore, int bufferSize, CancellationToken cancellationToken)
        {
            await semaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, chunk.Url);
                using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();
                await using var content = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                await using var fs = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, bufferSize, useAsync: true);
                await content.CopyToAsync(fs, bufferSize, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                semaphore.Release();
            }
        }
    }
}
