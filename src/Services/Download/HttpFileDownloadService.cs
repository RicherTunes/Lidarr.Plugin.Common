using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
using Lidarr.Plugin.Common.Errors;
using Lidarr.Plugin.Common.Services.Http;
using Lidarr.Plugin.Common.Utilities;
using Microsoft.Extensions.Logging;

namespace Lidarr.Plugin.Common.Services.Download
{
    /// <summary>
    /// Service for downloading files from HTTP URLs with resume support and validation.
    /// </summary>
    public class HttpFileDownloadService : IHttpFileDownloadService
    {
        private readonly HttpClient _httpClient;
        private readonly ILogger<HttpFileDownloadService> _logger;
        private readonly RemoteMediaUriPolicy _mediaUriPolicy;

        /// <summary>
        /// Creates a new HttpFileDownloadService.
        /// </summary>
        /// <param name="httpClient">The HttpClient to use for downloads. Should be configured with appropriate timeouts and connection limits.</param>
        /// <param name="logger">Optional logger for diagnostic output</param>
        /// <param name="mediaUriPolicy">SSRF policy applied to every download URL before fetch. Defaults to
        /// <see cref="RemoteMediaUriPolicy.Strict"/> (https-only, public destinations). Pass a relaxed policy
        /// only for explicitly-local providers (e.g. a self-hosted endpoint).</param>
        public HttpFileDownloadService(HttpClient httpClient, ILogger<HttpFileDownloadService>? logger = null, RemoteMediaUriPolicy? mediaUriPolicy = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpFileDownloadService>.Instance;
            _mediaUriPolicy = mediaUriPolicy ?? RemoteMediaUriPolicy.Strict;
        }

        /// <inheritdoc/>
        public async Task<long> DownloadToFileAsync(string url, string filePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL must not be empty.", nameof(url));
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path must not be empty.", nameof(filePath));

            // SSRF guard: validate the destination before any fetch (provider URLs are hostile-controllable).
            var guard = RemoteMediaUriGuard.Validate(url, _mediaUriPolicy);
            if (!guard.IsAllowed)
                throw new InvalidOperationException($"Refusing to download from an unsafe URL: {guard.Reason}");

            // Stream to a temporary .partial file, then atomic move to final
            var partialPath = filePath + ".partial";
            long existing = 0;
            if (File.Exists(partialPath))
            {
                try { existing = new FileInfo(partialPath).Length; } catch { existing = 0; }
            }

            var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (existing > 0)
            {
                request.Headers.Range = new RangeHeaderValue(existing, null);
            }

            // R2-01: keep the SSRF policy in force across redirects (validate each hop + the final URI).
            using var response = await MediaRedirectSafeSender.SendValidatedAsync(_httpClient, request, _mediaUriPolicy, HttpCompletionOption.ResponseHeadersRead, cancellationToken: cancellationToken).ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            var contentType = response.Content.Headers.ContentType?.MediaType ?? "unknown";
            var contentLength = response.Content.Headers.ContentLength;
            var urlHost = DownloadResponseDiagnostics.TryGetHost(url);

            if (response.StatusCode == System.Net.HttpStatusCode.NoContent || contentLength == 0)
            {
                throw new InvalidOperationException($"Download returned no content (HTTP {(int)response.StatusCode} {response.StatusCode}, Host={urlHost}, Content-Type={contentType}).");
            }

            var directory = Path.GetDirectoryName(filePath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);

            var isPartial = response.StatusCode == System.Net.HttpStatusCode.PartialContent;
            if (!isPartial && File.Exists(partialPath))
            {
                // Server didn't honor range; start fresh
                try { File.Delete(partialPath); } catch { }
                existing = 0;
            }

            // COM-011: Determine the expected total byte count for integrity verification.
            // For 206 Partial Content responses the total is the Content-Range "complete-length" field.
            // For 200 OK responses it is the Content-Length.
            // When neither is present (chunked transfer encoding) we skip the byte-count check.
            long? expectedTotalBytes = null;
            if (isPartial && response.Content.Headers.ContentRange?.Length != null)
            {
                expectedTotalBytes = response.Content.Headers.ContentRange.Length;
            }
            else if (!isPartial && contentLength.HasValue)
            {
                expectedTotalBytes = contentLength.Value;
            }

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var buffer = new byte[131072];
            long totalWritten = existing;
            int read;

            // LOOP-003: on a 200 (server ignored the resume Range) truncate instead of appending, so a stale
            // .partial that the best-effort delete above failed to remove can't be appended onto (silent
            // stale+fresh corruption that the byte-count check below does NOT catch — it counts session bytes).
            var partialWriteMode = PartialFileReset.ResolveWriteMode(serverHonoredRange: isPartial);

            // Explicit scope ensures fileStream is closed before File.Move
            await using (var fileStream = new FileStream(partialPath, partialWriteMode, FileAccess.Write, FileShare.None, 131072, useAsync: true))
            {
                read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                {
                    throw new InvalidOperationException($"Downloaded stream contained no data (Host={urlHost}, Content-Type={contentType}, Content-Length={contentLength?.ToString() ?? "unknown"}).");
                }

                if (DownloadResponseDiagnostics.IsTextLikeContentType(contentType) || DownloadResponseDiagnostics.LooksLikeTextPayload(buffer, read))
                {
                    var snippet = System.Text.Encoding.UTF8.GetString(buffer, 0, Math.Min(read, 512))
                        .Replace("\r", " ")
                        .Replace("\n", " ")
                        .Trim();
                    var safeSnippet = DownloadResponseDiagnostics.GetSafeSnippetForLogging(snippet);
                    throw new InvalidOperationException($"Unexpected content type '{contentType}' when downloading audio (Host={urlHost}). Snippet: {safeSnippet}");
                }

                await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                totalWritten += read;

                while ((read = await responseStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    totalWritten += read;
                }

                await fileStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            // COM-011: Byte-count integrity check — must happen before the atomic move so
            // we can delete the partial file on mismatch rather than leaving corrupt data.
            if (expectedTotalBytes.HasValue && totalWritten != expectedTotalBytes.Value)
            {
                // Delete the partial file so the next attempt starts from scratch.
                try { File.Delete(partialPath); } catch { /* best-effort */ }

                throw new DownloadIntegrityException(
                    actualBytes: totalWritten,
                    expectedBytes: expectedTotalBytes.Value,
                    filePath: filePath);
            }

            File.Move(partialPath, filePath, overwrite: true);

            AudioMagicBytesValidator.ValidateAudioMagicBytes(filePath);

            // Validate file (basic; no size/hash guarantees from server)
            if (!ValidationUtilities.ValidateDownloadedFile(filePath))
            {
                throw new InvalidOperationException($"Downloaded file failed validation: {Path.GetFileName(filePath)}");
            }

            _logger.LogDebug("Download completed: {Bytes} bytes written to {Path}", totalWritten, filePath);
            return totalWritten;
        }
    }
}
