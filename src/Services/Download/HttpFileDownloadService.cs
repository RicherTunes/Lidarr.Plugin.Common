using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;
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

        /// <summary>
        /// Creates a new HttpFileDownloadService.
        /// </summary>
        /// <param name="httpClient">The HttpClient to use for downloads. Should be configured with appropriate timeouts and connection limits.</param>
        /// <param name="logger">Optional logger for diagnostic output</param>
        public HttpFileDownloadService(HttpClient httpClient, ILogger<HttpFileDownloadService>? logger = null)
        {
            _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
            _logger = logger ?? Microsoft.Extensions.Logging.Abstractions.NullLogger<HttpFileDownloadService>.Instance;
        }

        /// <inheritdoc/>
        public async Task<long> DownloadToFileAsync(string url, string filePath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(url))
                throw new ArgumentException("URL must not be empty.", nameof(url));
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("File path must not be empty.", nameof(filePath));

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

            using var response = await _httpClient.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
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

            await using var responseStream = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);

            var buffer = new byte[131072];
            long totalWritten = existing;
            int read;

            // Explicit scope ensures fileStream is closed before File.Move
            await using (var fileStream = new FileStream(partialPath, FileMode.Append, FileAccess.Write, FileShare.None, 131072, useAsync: true))
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
