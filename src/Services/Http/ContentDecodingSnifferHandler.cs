using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace Lidarr.Plugin.Common.Services.Http
{
    /// <summary>
    /// Safety net handler that inflates mislabelled gzip payloads where servers fail to emit Content-Encoding.
    /// </summary>
    public sealed class ContentDecodingSnifferHandler : DelegatingHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request == null) throw new ArgumentNullException(nameof(request));

            var response = await base.SendAsync(request, cancellationToken).ConfigureAwait(false);
            if (response.Content == null)
            {
                return response;
            }

            var encodings = response.Content.Headers.ContentEncoding;
            if (encodings != null && encodings.Count > 0)
            {
                // Server already declared the encoding; trust the handler pipeline.
                return response;
            }

            var originalHeaders = response.Content.Headers.Select(h => h).ToList();
#if NET8_0_OR_GREATER
            var payload = await response.Content.ReadAsByteArrayAsync(cancellationToken).ConfigureAwait(false);
#else
            var payload = await response.Content.ReadAsByteArrayAsync().ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
#endif

            // Always reset the response content to a seekable stream after inspection.
            var bufferStream = new MemoryStream(payload, writable: false);
            SetContent(response, bufferStream, originalHeaders);

            if (payload.Length < 2)
            {
                return response;
            }

            var magic0 = payload[0];
            var magic1 = payload[1];
            if (magic0 != 0x1F || magic1 != 0x8B)
            {
                return response;
            }

            bufferStream.Position = 0;
            using var gzip = new GZipStream(bufferStream, CompressionMode.Decompress, leaveOpen: true);
            var inflated = new MemoryStream();
            await gzip.CopyToAsync(inflated, cancellationToken).ConfigureAwait(false);
            inflated.Position = 0;

            SetContent(response, inflated, originalHeaders, treatAsJson: true);
            return response;
        }

        private static void SetContent(HttpResponseMessage response, Stream stream, List<KeyValuePair<string, IEnumerable<string>>> originalHeaders, bool treatAsJson = false)
        {
            var newContent = new StreamContent(stream);
            foreach (var header in originalHeaders)
            {
                if (header.Key.Equals("Content-Length", StringComparison.OrdinalIgnoreCase) ||
                    header.Key.Equals("Content-Encoding", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                newContent.Headers.TryAddWithoutValidation(header.Key, header.Value);
            }

            if (treatAsJson && newContent.Headers.ContentType == null)
            {
                newContent.Headers.ContentType = new MediaTypeHeaderValue("application/json");
            }

            response.Content = newContent;
        }
    }
}
